using AzDORunner.Entities;
using AzDORunner.Model.Domain;
using KubeOps.KubernetesClient;
using k8s.Models;
using System.Collections.Concurrent;

namespace AzDORunner.Services;

public class AzureDevOpsPollingService : BackgroundService
{
    private readonly ILogger<AzureDevOpsPollingService> _logger;
    private readonly IAzureDevOpsService _azureDevOpsService;
    private readonly IKubernetesPodService _kubernetesPodService;
    private readonly IKubernetesClient _kubernetesClient;
    private readonly ConcurrentDictionary<string, PoolPollInfo> _poolsToMonitor = new();

    public AzureDevOpsPollingService(
        ILogger<AzureDevOpsPollingService> logger,
        IAzureDevOpsService azureDevOpsService,
        IKubernetesPodService kubernetesPodService,
        IKubernetesClient kubernetesClient)
    {
        _logger = logger;
        _azureDevOpsService = azureDevOpsService;
        _kubernetesPodService = kubernetesPodService;
        _kubernetesClient = kubernetesClient;
    }

    public void RegisterPool(V1AzDORunnerEntity entity, string pat)
    {
        var poolName = entity.Metadata.Name;
        var pollInterval = entity.Spec.PollIntervalSeconds > 5 ? entity.Spec.PollIntervalSeconds : 5;
        _poolsToMonitor.AddOrUpdate(poolName, (key) =>
        {
            var pollInfo = new PoolPollInfo
            {
                Entity = entity,
                Pat = pat,
                PollIntervalSeconds = pollInterval
            };
            pollInfo.LastPolled = DateTime.UtcNow.AddSeconds(-pollInfo.PollIntervalSeconds - 1); // Force immediate poll
            return pollInfo;
        }, (key, old) =>
        {
            old.Entity = entity;
            old.Pat = pat;
            old.PollIntervalSeconds = pollInterval;
            old.LastPolled = DateTime.UtcNow.AddSeconds(-pollInterval - 1);
            return old;
        });
        _logger.LogInformation("Registered/updated pool '{PoolName}' for Azure DevOps monitoring with {IntervalSeconds}s interval (immediate poll scheduled)",
            poolName, pollInterval);
    }

    public void UnregisterPool(string poolName)
    {
        if (_poolsToMonitor.TryRemove(poolName, out _))
        {
            _logger.LogInformation("Unregistered pool '{PoolName}' from Azure DevOps monitoring", poolName);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Azure DevOps Polling Service started - will continuously monitor pools");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pollStart = DateTime.UtcNow;
                await PollAllRegisteredPools();

                int minPollInterval = 5;
                if (!_poolsToMonitor.IsEmpty)
                {
                    minPollInterval = _poolsToMonitor.Values.Min(p => p.PollIntervalSeconds);
                }

                var elapsed = DateTime.UtcNow - pollStart;
                var delay = TimeSpan.FromSeconds(minPollInterval) - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Azure DevOps polling service main loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); // Wait longer on error
            }
        }

        _logger.LogInformation("Azure DevOps Polling Service stopped");
    }

    private async Task PollAllRegisteredPools()
    {
        if (_poolsToMonitor.IsEmpty)
        {
            _logger.LogDebug("No pools registered for monitoring yet");
            return;
        }

        var currentTime = DateTime.UtcNow;
        var poolsToPoll = _poolsToMonitor.Values
            .Where(info => currentTime.Subtract(info.LastPolled).TotalSeconds >= info.PollIntervalSeconds)
            .ToList();

        _logger.LogDebug("Checking {TotalPools} registered pools, {PollablePools} ready to poll",
            _poolsToMonitor.Count, poolsToPoll.Count);

        foreach (var pollInfo in poolsToPoll)
        {
            try
            {
                await PollSinglePool(pollInfo);
                pollInfo.LastPolled = currentTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling pool '{PoolName}' - will retry on next cycle",
                    pollInfo.Entity.Metadata.Name);
            }
        }
    }

    private async Task PollSinglePool(PoolPollInfo pollInfo)
    {
        var entity = pollInfo.Entity;
        var pat = pollInfo.Pat;
        var poolName = entity.Metadata.Name;
        var connectionStatus = "Connected";
        string? lastError = null;

        _logger.LogInformation("Polling Azure DevOps for pool '{PoolName}'", poolName);

        try
        {
            // Get current Azure DevOps state
            var queuedJobs = await _azureDevOpsService.GetQueuedJobsCountAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);
            var azureAgents = await _azureDevOpsService.GetPoolAgentsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);
            var activePods = await _kubernetesPodService.GetActivePodsAsync(entity);
            var allPods = await _kubernetesPodService.GetAllRunnerPodsAsync(entity);

            _logger.LogInformation("Pool '{PoolName}': {QueuedJobs} queued jobs, {AzureAgents} Azure agents, {ActivePods} active pods",
                poolName, queuedJobs, azureAgents.Count, activePods.Count);

            // 1. Clean up completed agents/pods
            await CleanupCompletedAgentsAsync(entity, pat, azureAgents, allPods);

            // 2. Ensure minimum agents are running
            await EnsureMinimumAgentsAsync(entity, pat);

            // 2.1. Optimize minimum agents for required capabilities
            if (queuedJobs > 0)
            {
                await OptimizeMinAgentsForCapabilitiesAsync(entity, pat);
            }

            // 2.5. Ensure maximum agents limit is respected
            await EnsureMaximumAgentsLimitAsync(entity, pat);

            // 3. Clean up idle agents based on TtlIdleSeconds configuration
            if (entity.Spec.TtlIdleSeconds == 0)
            {
                // For immediate cleanup mode (--once), only clean up when no work is queued
                if (queuedJobs == 0)
                {
                    await CleanupIdleAgentsAsync(entity, pat, azureAgents, activePods);
                }
            }
            else
            {
                // For continuous mode, clean up agents that have exceeded TtlIdleSeconds regardless of queue status
                await CleanupIdleAgentsAsync(entity, pat, azureAgents, activePods);
            }

            // 4. Scale up if needed - but consider both Azure agents AND running pods
            if (queuedJobs > 0)
            {
                await ScaleUpForQueuedWorkAsync(entity, pat, queuedJobs, azureAgents, activePods.Count);
            }

            // 5. Update status with successful connection
            UpdateEntityStatus(entity, azureAgents, activePods, queuedJobs, connectionStatus, lastError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to poll Azure DevOps for pool '{PoolName}' - marking as disconnected", poolName);
            connectionStatus = "Disconnected";
            lastError = ex.Message;

            try
            {
                var activePods = await _kubernetesPodService.GetActivePodsAsync(entity);
                UpdateEntityStatus(entity, new List<Agent>(), activePods, 0, connectionStatus, lastError);
            }
            catch (Exception statusEx)
            {
                _logger.LogError(statusEx, "Failed to update status for disconnected pool '{PoolName}'", poolName);
            }
        }
    }

    private async Task CleanupCompletedAgentsAsync(V1AzDORunnerEntity entity, string pat, List<Agent> azureAgents, List<V1Pod> allPods)
    {
        // Get all job requests to check if any agent is running a job
        var jobRequests = await _azureDevOpsService.GetJobRequestsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

        var completedPods = allPods.Where(pod =>
            pod.Status?.Phase == "Succeeded" ||
            pod.Status?.Phase == "Failed").ToList();

        foreach (var completedPod in completedPods)
        {
            try
            {
                // Grace period: do not delete pod if it is less than 2 minutes old
                if (completedPod.Metadata.CreationTimestamp.HasValue &&
                    DateTime.UtcNow - completedPod.Metadata.CreationTimestamp.Value < TimeSpan.FromMinutes(2))
                {
                    _logger.LogInformation("Skipping deletion of pod '{PodName}' because it is in registration grace period", completedPod.Metadata.Name);
                    continue;
                }
                var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == completedPod.Metadata.Name);
                bool agentIsBusy = correspondingAgent != null && jobRequests.Any(j => j.Result == null && j.AgentId == correspondingAgent.Id);
                if (correspondingAgent != null && IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name) && !agentIsBusy)
                {
                    _logger.LogInformation("Cleaning up agent '{AgentName}' for completed pod", correspondingAgent.Name);
                    await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);
                }

                // Only delete pod if agent is not busy
                if (!agentIsBusy)
                {
                    await _kubernetesPodService.DeletePodAsync(completedPod.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default");
                    _logger.LogInformation("Deleted completed pod '{PodName}'", completedPod.Metadata.Name);
                }
                else
                {
                    _logger.LogInformation("Skipping deletion of completed pod '{PodName}' because agent is still running a job", completedPod.Metadata.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup completed agent/pod '{PodName}'", completedPod.Metadata.Name);
            }
        }

        // Clean up offline operator-managed agents without active pods and not running jobs
        var operatorOfflineAgents = azureAgents.Where(agent =>
            agent.Status.ToLower() == "offline" &&
            IsOperatorManagedAgent(agent.Name, entity.Metadata.Name) &&
            !allPods.Any(pod => pod.Metadata.Name == agent.Name &&
                         (pod.Status?.Phase == "Running" || pod.Status?.Phase == "Pending"))
        ).ToList();

        foreach (var offlineAgent in operatorOfflineAgents)
        {
            try
            {
                // If the agent is offline, and has a recent LastActive, and has a job assigned, treat as stuck and remove
                bool isStuck = false;
                var stuckJob = jobRequests.FirstOrDefault(j => j.Result == null && j.AgentId == offlineAgent.Id);
                if (stuckJob != null && offlineAgent.LastActive != null)
                {
                    // Consider stuck if LastActive is within the last 10 minutes (configurable if needed)
                    var lastActiveThreshold = DateTime.UtcNow.AddMinutes(-10);
                    if (offlineAgent.LastActive > lastActiveThreshold)
                    {
                        isStuck = true;
                    }
                }
                if (!isStuck && stuckJob == null)
                {
                    // Not stuck, not running a job, safe to remove
                    _logger.LogInformation("Cleaning up offline agent '{AgentName}' with no active pod", offlineAgent.Name);
                    await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, offlineAgent.Name, pat);
                }
                else if (isStuck)
                {
                    _logger.LogWarning("Deleting stuck offline agent '{AgentName}' with recent LastActive and assigned job (jobId={JobId})", offlineAgent.Name, stuckJob?.RequestId);
                    await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, offlineAgent.Name, pat);
                }
                else
                {
                    _logger.LogInformation("Skipping offline agent '{AgentName}' because it is still assigned to a job but not considered stuck", offlineAgent.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup offline agent '{AgentName}'", offlineAgent.Name);
            }
        }
    }

    private async Task CleanupIdleAgentsAsync(V1AzDORunnerEntity entity, string pat, List<Agent> azureAgents, List<V1Pod> pods)
    {
        // If TtlIdleSeconds is 0, remove agents immediately when no work is queued
        // If TtlIdleSeconds > 0, only remove agents that have been idle for that many seconds
        var ttlIdleSeconds = entity.Spec.TtlIdleSeconds;

        // Get minimum agent pods to protect them from cleanup
        var minAgentPods = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
        var minAgentNames = minAgentPods.Select(pod => pod.Metadata.Name).ToHashSet();

        // Fetch all job requests for the pool to determine if an agent is running a job
        var jobRequests = await _azureDevOpsService.GetJobRequestsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

        // Only remove agents that have been idle for the specified duration (except minimum agents and those running a job)
        List<Agent> operatorOnlineIdleAgents = new();
        if (ttlIdleSeconds > 0)
        {
            var idleThreshold = DateTime.UtcNow.AddSeconds(-ttlIdleSeconds);
            operatorOnlineIdleAgents = azureAgents.Where(agent =>
                agent.Status == "Online" &&
                IsOperatorManagedAgent(agent.Name, entity.Metadata.Name) &&
                !minAgentNames.Contains(agent.Name) &&
                (agent.LastActive == null || agent.LastActive < idleThreshold) &&
                // Do not remove if agent is running a job
                !jobRequests.Any(j => j.Result == null && j.AgentId == agent.Id)
            ).ToList();
        }

        foreach (var idleAgent in operatorOnlineIdleAgents)
        {
            try
            {
                // Only remove pod if agent is not running a job
                var pod = pods.FirstOrDefault(p => p.Metadata.Name == idleAgent.Name);
                bool agentIsBusy = jobRequests.Any(j => j.Result == null && j.AgentId == idleAgent.Id);
                // Grace period: do not delete pod if it is less than 2 minutes old
                if (pod != null && pod.Metadata.CreationTimestamp.HasValue &&
                    DateTime.UtcNow - pod.Metadata.CreationTimestamp.Value < TimeSpan.FromMinutes(2))
                {
                    _logger.LogInformation("Skipping deletion of pod '{PodName}' (idle cleanup) because it is in registration grace period", pod.Metadata.Name);
                    continue;
                }
                if (!agentIsBusy)
                {
                    _logger.LogInformation("Cleaning up idle agent '{AgentName}' - no queued work (TtlIdleSeconds: {TtlIdleSeconds})",
                        idleAgent.Name, ttlIdleSeconds);
                    await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, idleAgent.Name, pat);

                    if (pod != null)
                    {
                        await _kubernetesPodService.DeletePodAsync(pod.Metadata.Name,
                            entity.Metadata.NamespaceProperty ?? "default");
                    }
                }
                else
                {
                    _logger.LogInformation("Skipping cleanup of idle agent '{AgentName}' because it is running a job", idleAgent.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup idle agent '{AgentName}'", idleAgent.Name);
            }
        }
    }

    private async Task ScaleUpForQueuedWorkAsync(V1AzDORunnerEntity entity, string pat, int queuedJobs, List<Agent> agents, int activePods)
    {
        // Filter to only count operator-managed agents (ignore external agents like "Labby")
        var operatorManagedAgents = agents.Where(a => IsOperatorManagedAgent(a.Name, entity.Metadata.Name)).ToList();

        // Fetch all job requests for the pool to determine if an agent is running a job
        var jobRequests = await _azureDevOpsService.GetJobRequestsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

        // Count all operator-managed agents and pods (including offline) for max agent check
        var allPods = await _kubernetesPodService.GetAllRunnerPodsAsync(entity);
        var totalAgentCount = operatorManagedAgents.Count + allPods.Count;

        // For every queued job not assigned to an agent or pod, spawn a new agent (up to MaxAgents)
        var jobsWithoutAgentOrPod = jobRequests.Where(j =>
            j.Result == null &&
            !operatorManagedAgents.Any(a => a.Id == j.AgentId) &&
            !allPods.Any(pod =>
                pod.Metadata.Annotations != null &&
                pod.Metadata.Annotations.TryGetValue("jobRequestId", out var val) &&
                val == j.RequestId.ToString())
        ).ToList();
        var availableSlots = entity.Spec.MaxAgents - totalAgentCount;
        var jobsToSpawn = jobsWithoutAgentOrPod.Take(availableSlots).ToList();

        if (jobsToSpawn.Count > 0)
        {
            _logger.LogInformation("PENDING WORK DETECTED: Spawning {NeededAgents} agents for {JobsWithoutAgent} unassigned queued jobs (max agents: {MaxAgents}, total agents: {TotalAgentCount})",
                jobsToSpawn.Count, jobsWithoutAgentOrPod.Count, entity.Spec.MaxAgents, totalAgentCount);

            foreach (var job in jobsToSpawn)
            {
                bool podExists = allPods.Any(pod =>
                    pod.Metadata.Annotations != null &&
                    pod.Metadata.Annotations.TryGetValue("jobRequestId", out var val) &&
                    val == job.RequestId.ToString());
                if (podExists)
                {
                    _logger.LogInformation("Pod already exists for jobRequestId {JobRequestId}, skipping agent spawn.", job.RequestId);
                    continue;
                }
                if (entity.Spec.CapabilityAware)
                {
                    await SpawnCapabilityAwareAgentsFromJobDemands(entity, pat, new List<JobRequest> { job });
                }
                else
                {
                    await _kubernetesPodService.CreateAgentPodAsync(entity, pat);
                }
            }

            _logger.LogInformation("Created {NeededAgents} agent pods for pending work", jobsToSpawn.Count);
        }
        else
        {
            _logger.LogInformation("No new agents needed: all queued jobs are already assigned to agents or pods are starting up.");
        }
    }

    private async Task SpawnCapabilityAwareAgentsFromJobDemands(V1AzDORunnerEntity entity, string pat, List<JobRequest> jobsToSpawn)
    {
        try
        {
            foreach (var job in jobsToSpawn)
            {
                string capability = "base";
                if (job.Demands != null && entity.Spec.CapabilityImages != null)
                {
                    // Use the first demand that matches a capability image
                    var matched = job.Demands.FirstOrDefault(d => entity.Spec.CapabilityImages.ContainsKey(d));
                    if (!string.IsNullOrEmpty(matched))
                    {
                        capability = matched;
                    }
                }
                await _kubernetesPodService.CreateAgentPodAsync(entity, pat, false, capability);
                _logger.LogInformation("Spawned agent with capability '{Capability}' for job {JobId}", capability, job.RequestId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn capability-aware agents, falling back to regular spawning");
            foreach (var job in jobsToSpawn)
            {
                await _kubernetesPodService.CreateAgentPodAsync(entity, pat);
            }
        }
    }

    private async Task OptimizeMinAgentsForCapabilitiesAsync(V1AzDORunnerEntity entity, string pat)
    {
        try
        {
            // Only optimize if capability-aware mode is enabled and we have min agents
            if (!entity.Spec.CapabilityAware || entity.Spec.MinAgents <= 0)
            {
                return;
            }

            _logger.LogDebug("Checking if minimum agents need capability optimization for pool '{PoolName}'", entity.Metadata.Name);

            // Get current min agents and queued jobs with capabilities
            var currentMinAgents = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
            if (currentMinAgents.Count == 0)
            {
                return;
            }

            // Get queued jobs with their capability requirements
            var queuedJobsWithCapabilities = await _azureDevOpsService.GetQueuedJobsWithCapabilitiesAsync(
                entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

            if (!queuedJobsWithCapabilities.Any())
            {
                return;
            }

            // Group jobs by required capability and find capabilities we need but don't have
            var requiredCapabilities = queuedJobsWithCapabilities
                .Where(job => !string.IsNullOrEmpty(job.RequiredCapability))
                .GroupBy(job => job.RequiredCapability!)
                .Where(g => entity.Spec.CapabilityImages.ContainsKey(g.Key)) // Only consider configured capabilities
                .ToDictionary(g => g.Key, g => g.Count());

            if (!requiredCapabilities.Any())
            {
                _logger.LogDebug("No configured capabilities required by queued jobs");
                return;
            }

            // Check which capabilities are missing from current min agents
            var currentMinAgentCapabilities = currentMinAgents
                .Select(pod => pod.Metadata.Labels?.TryGetValue("capability", out var cap) == true ? cap : "base")
                .ToList();

            var missingCapabilities = requiredCapabilities.Keys
                .Where(capability => !currentMinAgentCapabilities.Contains(capability))
                .ToList();

            if (!missingCapabilities.Any())
            {
                _logger.LogDebug("All required capabilities are covered by existing minimum agents");
                return;
            }

            _logger.LogInformation("Pool '{PoolName}': Required capabilities {RequiredCapabilities}, Current min agent capabilities {CurrentCapabilities}, Missing {MissingCapabilities}",
                entity.Metadata.Name,
                string.Join(", ", requiredCapabilities.Keys),
                string.Join(", ", currentMinAgentCapabilities),
                string.Join(", ", missingCapabilities));

            // Replace base agents with capability-specific agents
            var baseAgents = currentMinAgents
                .Where(pod =>
                {
                    if (pod.Metadata.Labels?.TryGetValue("capability", out var cap) == true)
                    {
                        return cap == "base";
                    }
                    return true; // No capability label means it's a base agent
                })
                .OrderBy(pod => pod.Metadata.CreationTimestamp)
                .ToList();

            var replacementsNeeded = Math.Min(missingCapabilities.Count, baseAgents.Count);

            if (replacementsNeeded == 0)
            {
                _logger.LogInformation("No base minimum agents available to replace with capability-specific agents");
                return;
            }

            _logger.LogInformation("Replacing {ReplacementsNeeded} base minimum agents with capability-specific agents", replacementsNeeded);

            // Replace base agents with required capability agents
            for (int i = 0; i < replacementsNeeded; i++)
            {
                var baseAgentToReplace = baseAgents[i];
                var capabilityToAdd = missingCapabilities[i];

                try
                {
                    // Create new capability-specific min agent
                    await _kubernetesPodService.CreateAgentPodAsync(entity, pat, isMinAgent: true, capabilityToAdd);
                    _logger.LogInformation("Created capability-specific minimum agent with capability '{Capability}'", capabilityToAdd);

                    // Remove the base agent
                    await RemoveSpecificMinAgentAsync(entity, pat, baseAgentToReplace);
                    _logger.LogInformation("Replaced base minimum agent {AgentName} with {Capability}-capable agent",
                        baseAgentToReplace.Metadata.Name, capabilityToAdd);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to replace base minimum agent {AgentName} with {Capability}-capable agent",
                        baseAgentToReplace.Metadata.Name, capabilityToAdd);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to optimize minimum agents for capabilities in pool '{PoolName}'", entity.Metadata.Name);
        }
    }

    private async Task RemoveSpecificMinAgentAsync(V1AzDORunnerEntity entity, string pat, V1Pod podToRemove)
    {
        try
        {
            // Try to unregister from Azure DevOps first
            var azureAgents = await _azureDevOpsService.GetPoolAgentsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);
            var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == podToRemove.Metadata.Name);

            if (correspondingAgent != null && IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name))
            {
                var unregistered = await _azureDevOpsService.UnregisterAgentAsync(
                    entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);

                if (unregistered)
                {
                    _logger.LogInformation("Unregistered minimum agent '{AgentName}' from Azure DevOps", correspondingAgent.Name);
                }
                else
                {
                    _logger.LogWarning("Failed to unregister minimum agent '{AgentName}' from Azure DevOps, but continuing with pod deletion",
                        correspondingAgent.Name);
                }
            }

            // Delete the pod
            await _kubernetesPodService.DeletePodAsync(podToRemove.Metadata.Name,
                podToRemove.Metadata.NamespaceProperty ?? "default");
            _logger.LogInformation("Deleted minimum agent pod '{PodName}'", podToRemove.Metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove specific minimum agent '{PodName}'", podToRemove.Metadata.Name);
        }
    }

    private void UpdateEntityStatus(V1AzDORunnerEntity entity, List<Agent> azureAgents, List<V1Pod> pods, int queuedJobs, string connectionStatus = "Connected", string? lastError = null)
    {
        try
        {
            var freshEntity = _kubernetesClient.Get<V1AzDORunnerEntity>(entity.Metadata.Name, entity.Metadata.NamespaceProperty ?? "default");
            if (freshEntity != null)
            {
                // Filter to only count operator-managed agents for status
                var operatorManagedAgents = azureAgents.Where(a => IsOperatorManagedAgent(a.Name, entity.Metadata.Name)).ToList();

                var activePods = pods.Count(p => p.Status?.Phase == "Running" || p.Status?.Phase == "Pending");
                var availableAgents = operatorManagedAgents.Count(a => a.Status?.ToLower() == "online" || a.Status?.ToLower() == "idle");
                var offlineAgents = operatorManagedAgents.Count(a => a.Status?.ToLower() == "offline");

                freshEntity.Status.QueuedJobs = queuedJobs;
                freshEntity.Status.RunningAgents = operatorManagedAgents.Count;
                freshEntity.Status.LastPolled = DateTime.UtcNow;
                freshEntity.Status.Active = connectionStatus == "Disconnected";
                freshEntity.Status.ConnectionStatus = connectionStatus;
                freshEntity.Status.LastError = lastError;
                freshEntity.Status.OrganizationName = _azureDevOpsService.ExtractOrganizationName(freshEntity.Spec.AzDoUrl);
                freshEntity.Status.AgentsSummary = $"{operatorManagedAgents.Count}/{freshEntity.Spec.MaxAgents}";
                freshEntity.Status.Agents = operatorManagedAgents; // Only show operator-managed agents

                freshEntity.Status.Conditions.Clear();

                if (connectionStatus == "Connected")
                {
                    freshEntity.Status.Conditions.Add(new V1AzDORunnerEntity.StatusCondition
                    {
                        Type = "Ready",
                        Status = "True",
                        Reason = "Reconciled",
                        Message = $"Pool has {operatorManagedAgents.Count} operator-managed agents ({availableAgents} available, {offlineAgents} offline), {activePods} active pods, {queuedJobs} queued jobs",
                        LastTransitionTime = DateTime.UtcNow
                    });
                }
                else
                {
                    freshEntity.Status.Conditions.Add(new V1AzDORunnerEntity.StatusCondition
                    {
                        Type = "Error",
                        Status = "True",
                        Reason = "Disconnected",
                        Message = $"Failed to connect to Azure DevOps: {lastError ?? "Unknown error"}",
                        LastTransitionTime = DateTime.UtcNow
                    });
                }

                _kubernetesClient.UpdateStatus(freshEntity);
                _logger.LogDebug("Updated RunnerPool status: {ConnectionStatus}, {AgentCount} agents, {QueuedJobs} queued jobs, {ActivePods} active pods",
                    connectionStatus, azureAgents.Count, queuedJobs, activePods);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update status for pool '{PoolName}' - continuing operation", entity.Metadata.Name);
        }
    }

    private static bool IsOperatorManagedAgent(string agentName, string runnerPoolName)
    {
        var expectedPrefix = $"{runnerPoolName}-agent-";
        if (!agentName.StartsWith(expectedPrefix))
        {
            return false;
        }

        var suffix = agentName.Substring(expectedPrefix.Length);
        return suffix.Length == 8 && suffix.All(c => char.IsLetterOrDigit(c));
    }

    private async Task EnsureMinimumAgentsAsync(V1AzDORunnerEntity entity, string pat)
    {
        try
        {
            var currentMinAgents = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
            var currentMinAgentCount = currentMinAgents.Count;
            var requiredMinAgents = Math.Max(0, entity.Spec.MinAgents); // Ensure non-negative

            // Ensure MinAgents doesn't exceed MaxAgents
            var maxAgents = Math.Max(0, entity.Spec.MaxAgents);
            if (requiredMinAgents > maxAgents)
            {
                _logger.LogWarning("MinAgents ({MinAgents}) exceeds MaxAgents ({MaxAgents}) for pool '{PoolName}'. Adjusting MinAgents to MaxAgents.",
                    requiredMinAgents, maxAgents, entity.Metadata.Name);
                requiredMinAgents = maxAgents;
            }

            if (requiredMinAgents == 0)
            {
                // If MinAgents is 0, remove all existing minimum agents
                if (currentMinAgentCount > 0)
                {
                    _logger.LogInformation("Removing all {CurrentMinAgents} minimum agents for pool '{PoolName}' (required: {MinAgents})",
                        currentMinAgentCount, entity.Metadata.Name, requiredMinAgents);

                    await RemoveExcessMinimumAgentsAsync(entity, pat, currentMinAgents, currentMinAgentCount);
                }
                return;
            }

            var neededMinAgents = requiredMinAgents - currentMinAgentCount;

            if (neededMinAgents > 0)
            {
                // Scale up: Create additional minimum agents
                _logger.LogInformation("Creating {NeededMinAgents} minimum agents for pool '{PoolName}' (current: {CurrentMinAgents}, required: {MinAgents})",
                    neededMinAgents, entity.Metadata.Name, currentMinAgentCount, requiredMinAgents);

                for (int i = 0; i < neededMinAgents; i++)
                {
                    await _kubernetesPodService.CreateAgentPodAsync(entity, pat, isMinAgent: true);
                }
            }
            else if (neededMinAgents < 0)
            {
                // Scale down: Remove excess minimum agents
                var excessMinAgents = Math.Abs(neededMinAgents);
                _logger.LogInformation("Removing {ExcessMinAgents} excess minimum agents for pool '{PoolName}' (current: {CurrentMinAgents}, required: {MinAgents})",
                    excessMinAgents, entity.Metadata.Name, currentMinAgentCount, requiredMinAgents);

                await RemoveExcessMinimumAgentsAsync(entity, pat, currentMinAgents, excessMinAgents);
            }
            else
            {
                _logger.LogDebug("Minimum agent requirement satisfied for pool '{PoolName}' ({CurrentMinAgents}/{MinAgents})",
                    entity.Metadata.Name, currentMinAgentCount, requiredMinAgents);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure minimum agents for pool '{PoolName}'", entity.Metadata.Name);
        }
    }

    private async Task RemoveExcessMinimumAgentsAsync(V1AzDORunnerEntity entity, string pat, List<V1Pod> currentMinAgents, int countToRemove)
    {
        try
        {
            var azureAgents = await _azureDevOpsService.GetPoolAgentsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

            var agentsToRemove = currentMinAgents
                .OrderBy(pod => pod.Metadata.CreationTimestamp)
                .Take(countToRemove)
                .ToList();

            var jobRequests = await _azureDevOpsService.GetJobRequestsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

            foreach (var podToRemove in agentsToRemove)
            {
                try
                {
                    var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == podToRemove.Metadata.Name);
                    bool agentIsBusy = correspondingAgent != null && jobRequests.Any(j => j.Result == null && j.AgentId == correspondingAgent.Id);

                    if (agentIsBusy)
                    {
                        _logger.LogInformation("Skipping removal of minimum agent '{AgentName}' because it is running a job", podToRemove.Metadata.Name);
                        continue;
                    }

                    if (podToRemove.Metadata.CreationTimestamp.HasValue &&
                        DateTime.UtcNow - podToRemove.Metadata.CreationTimestamp.Value < TimeSpan.FromMinutes(3))
                    {
                        _logger.LogInformation("Skipping removal of minimum agent pod '{PodName}' because it is in registration grace period", podToRemove.Metadata.Name);
                        continue;
                    }

                    if (correspondingAgent != null && IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name))
                    {
                        _logger.LogInformation("Unregistering excess minimum agent '{AgentName}' from Azure DevOps", correspondingAgent.Name);
                        await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);
                    }

                    await _kubernetesPodService.DeletePodAsync(podToRemove.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default");
                    _logger.LogInformation("Removed excess minimum agent pod '{PodName}' for pool '{PoolName}'",
                        podToRemove.Metadata.Name, entity.Metadata.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove excess minimum agent pod '{PodName}' for pool '{PoolName}'",
                        podToRemove.Metadata.Name, entity.Metadata.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove excess minimum agents for pool '{PoolName}'", entity.Metadata.Name);
        }
    }

    private async Task EnsureMaximumAgentsLimitAsync(V1AzDORunnerEntity entity, string pat)
    {
        try
        {
            var allPods = await _kubernetesPodService.GetAllRunnerPodsAsync(entity);
            var activePods = allPods.Where(pod =>
                pod.Status?.Phase == "Running" || pod.Status?.Phase == "Pending").ToList();

            var currentActiveCount = activePods.Count;
            var maxAgents = Math.Max(0, entity.Spec.MaxAgents); // Ensure non-negative

            if (currentActiveCount <= maxAgents)
            {
                _logger.LogDebug("Agent count within maximum limit for pool '{PoolName}' ({CurrentActive}/{MaxAgents})",
                    entity.Metadata.Name, currentActiveCount, maxAgents);
                return;
            }

            var excessAgents = currentActiveCount - maxAgents;
            _logger.LogInformation("Removing {ExcessAgents} excess agents for pool '{PoolName}' to respect MaxAgents limit (current: {CurrentActive}, max: {MaxAgents})",
                excessAgents, entity.Metadata.Name, currentActiveCount, maxAgents);

            var minAgentPods = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
            var minAgentNames = minAgentPods.Select(pod => pod.Metadata.Name).ToHashSet();

            var nonMinAgents = activePods.Where(pod => !minAgentNames.Contains(pod.Metadata.Name)).ToList();
            var minAgents = activePods.Where(pod => minAgentNames.Contains(pod.Metadata.Name)).ToList();

            var agentsToRemove = new List<V1Pod>();

            var nonMinAgentsToRemove = Math.Min(excessAgents, nonMinAgents.Count);
            agentsToRemove.AddRange(nonMinAgents
                .OrderBy(pod => pod.Metadata.CreationTimestamp)
                .Take(nonMinAgentsToRemove));

            var remainingToRemove = excessAgents - nonMinAgentsToRemove;
            if (remainingToRemove > 0)
            {
                _logger.LogWarning("Need to remove {RemainingToRemove} minimum agents to respect MaxAgents limit for pool '{PoolName}'",
                    remainingToRemove, entity.Metadata.Name);

                agentsToRemove.AddRange(minAgents
                    .OrderBy(pod => pod.Metadata.CreationTimestamp)
                    .Take(remainingToRemove));
            }

            await RemoveExcessAgentsAsync(entity, pat, agentsToRemove);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure maximum agents limit for pool '{PoolName}'", entity.Metadata.Name);
        }
    }

    private async Task RemoveExcessAgentsAsync(V1AzDORunnerEntity entity, string pat, List<V1Pod> agentsToRemove)
    {
        try
        {
            // Get Azure agents to find corresponding agents to unregister
            var azureAgents = await _azureDevOpsService.GetPoolAgentsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

            // Get all job requests to check if any agent is running a job
            var jobRequests = await _azureDevOpsService.GetJobRequestsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

            foreach (var podToRemove in agentsToRemove)
            {
                try
                {
                    // Grace period: do not delete pod if it is less than 2 minutes old
                    if (podToRemove.Metadata.CreationTimestamp.HasValue &&
                        DateTime.UtcNow - podToRemove.Metadata.CreationTimestamp.Value < TimeSpan.FromMinutes(2))
                    {
                        _logger.LogInformation("Skipping removal of agent pod '{PodName}' for MaxAgents compliance because it is in registration grace period", podToRemove.Metadata.Name);
                        continue;
                    }
                    var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == podToRemove.Metadata.Name);
                    bool agentIsBusy = correspondingAgent != null && jobRequests.Any(j => j.Result == null && j.AgentId == correspondingAgent.Id);
                    if (agentIsBusy)
                    {
                        _logger.LogInformation("Skipping removal of agent '{AgentName}' for MaxAgents compliance because it is running a job", podToRemove.Metadata.Name);
                        continue;
                    }
                    if (correspondingAgent != null && IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name))
                    {
                        var isMinAgent = podToRemove.Metadata.Labels?.ContainsKey("min-agent") == true &&
                                        podToRemove.Metadata.Labels["min-agent"] == "true";
                        var agentType = isMinAgent ? "minimum" : "regular";

                        _logger.LogInformation("Unregistering excess {AgentType} agent '{AgentName}' from Azure DevOps for MaxAgents compliance",
                            agentType, correspondingAgent.Name);
                        await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);
                    }
                    await _kubernetesPodService.DeletePodAsync(podToRemove.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default");
                    _logger.LogInformation("Removed excess agent pod '{PodName}' for MaxAgents compliance in pool '{PoolName}'",
                        podToRemove.Metadata.Name, entity.Metadata.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove excess agent pod '{PodName}' for pool '{PoolName}'",
                        podToRemove.Metadata.Name, entity.Metadata.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove excess agents for pool '{PoolName}'", entity.Metadata.Name);
        }
    }
}
