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
                _logger.LogDebug("Polling cycle - checking all registered pools");
                await PollAllRegisteredPools();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // Check every 5 seconds
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
        var completedPods = allPods.Where(pod =>
            pod.Status?.Phase == "Succeeded" ||
            pod.Status?.Phase == "Failed").ToList();

        foreach (var completedPod in completedPods)
        {
            try
            {
                var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == completedPod.Metadata.Name);
                if (correspondingAgent != null && IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name))
                {
                    _logger.LogInformation("Cleaning up agent '{AgentName}' for completed pod", correspondingAgent.Name);
                    await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);
                }

                await _kubernetesPodService.DeletePodAsync(completedPod.Metadata.Name,
                    entity.Metadata.NamespaceProperty ?? "default");
                _logger.LogInformation("Deleted completed pod '{PodName}'", completedPod.Metadata.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup completed agent/pod '{PodName}'", completedPod.Metadata.Name);
            }
        }

        // Clean up offline operator-managed agents without active pods
        var operatorOfflineAgents = azureAgents.Where(agent =>
            agent.Status.ToLower() == "offline" &&
            IsOperatorManagedAgent(agent.Name, entity.Metadata.Name) &&
            !allPods.Any(pod => pod.Metadata.Name == agent.Name &&
                         (pod.Status?.Phase == "Running" || pod.Status?.Phase == "Pending"))).ToList();

        foreach (var offlineAgent in operatorOfflineAgents)
        {
            try
            {
                _logger.LogInformation("Cleaning up offline agent '{AgentName}' with no active pod", offlineAgent.Name);
                await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, offlineAgent.Name, pat);
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
                !jobRequests.Any(j => j.Result == null && j.AgentId == agent.Id)
            ).ToList();
        }

        foreach (var idleAgent in operatorOnlineIdleAgents)
        {
            try
            {
                _logger.LogInformation("Cleaning up idle agent '{AgentName}' - no queued work (TtlIdleSeconds: {TtlIdleSeconds})",
                    idleAgent.Name, ttlIdleSeconds);
                await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, idleAgent.Name, pat);

                var pod = pods.FirstOrDefault(p => p.Metadata.Name == idleAgent.Name);
                if (pod != null)
                {
                    await _kubernetesPodService.DeletePodAsync(pod.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default");
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

        // Only count online/idle operator-managed agents as available capacity
        var availableAgents = operatorManagedAgents.Count(a => a.Status?.ToLower() == "online" || a.Status?.ToLower() == "idle");

        // Get all pods to check for recently created ones that might be starting up
        var allPods = await _kubernetesPodService.GetAllRunnerPodsAsync(entity);
        var recentPods = allPods.Where(pod =>
            pod.Metadata.CreationTimestamp.HasValue &&
            DateTime.UtcNow - pod.Metadata.CreationTimestamp.Value < TimeSpan.FromMinutes(2)
        ).ToList();

        var totalAvailableCapacity = availableAgents + activePods;

        _logger.LogInformation("Pool '{PoolName}': {QueuedJobs} queued jobs, {TotalAgents} total agents ({OperatorAgents} operator-managed, {AvailableAgents} available), {ActivePods} active pods, {RecentPods} recent pods (starting up)",
            entity.Name(), queuedJobs, agents.Count, operatorManagedAgents.Count, availableAgents, activePods, recentPods.Count);

        // If we have recent pods that are still starting up, be more conservative about spawning new ones
        if (recentPods.Count > 0 && queuedJobs <= (totalAvailableCapacity + recentPods.Count))
        {
            _logger.LogInformation("No new agents needed: {QueuedJobs} queued jobs, {TotalCapacity} available capacity + {RecentPods} recent pods starting up",
                queuedJobs, totalAvailableCapacity, recentPods.Count);
            return;
        }

        if (queuedJobs <= totalAvailableCapacity)
        {
            _logger.LogInformation("No new agents needed: {QueuedJobs} queued jobs, {TotalCapacity} available capacity ({AvailableAgents} available agents + {ActivePods} active pods)",
                queuedJobs, totalAvailableCapacity, availableAgents, activePods);
            return;
        }

        var neededAgents = Math.Min(queuedJobs - totalAvailableCapacity, entity.Spec.MaxAgents - totalAvailableCapacity);

        if (neededAgents > 0)
        {
            _logger.LogInformation("PENDING WORK DETECTED: Spawning {NeededAgents} agents for {QueuedJobs} queued jobs (available capacity: {AvailableAgents} operator agents + {ActivePods} pods = {TotalCapacity})",
                neededAgents, queuedJobs, availableAgents, activePods, totalAvailableCapacity);

            for (int i = 0; i < neededAgents; i++)
            {
                await _kubernetesPodService.CreateAgentPodAsync(entity, pat);
            }

            _logger.LogInformation("Created {NeededAgents} agent pods for pending work", neededAgents);
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
                freshEntity.Status.Active = connectionStatus == "Connected";
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
            // Get Azure agents to find corresponding agents to unregister
            var azureAgents = await _azureDevOpsService.GetPoolAgentsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

            // Sort minimum agents by creation time (oldest first) to ensure stable removal
            var agentsToRemove = currentMinAgents
                .OrderBy(pod => pod.Metadata.CreationTimestamp)
                .Take(countToRemove)
                .ToList();

            foreach (var podToRemove in agentsToRemove)
            {
                try
                {
                    // Find corresponding Azure DevOps agent and unregister it
                    var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == podToRemove.Metadata.Name);
                    if (correspondingAgent != null && IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name))
                    {
                        _logger.LogInformation("Unregistering excess minimum agent '{AgentName}' from Azure DevOps", correspondingAgent.Name);
                        await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);
                    }

                    // Delete the pod
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

            // Get minimum agent pods to protect them if possible
            var minAgentPods = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
            var minAgentNames = minAgentPods.Select(pod => pod.Metadata.Name).ToHashSet();

            // Prioritize removing non-minimum agents first
            var nonMinAgents = activePods.Where(pod => !minAgentNames.Contains(pod.Metadata.Name)).ToList();
            var minAgents = activePods.Where(pod => minAgentNames.Contains(pod.Metadata.Name)).ToList();

            // Sort by creation time (oldest first) for stable removal
            var agentsToRemove = new List<V1Pod>();

            // First, remove non-minimum agents
            var nonMinAgentsToRemove = Math.Min(excessAgents, nonMinAgents.Count);
            agentsToRemove.AddRange(nonMinAgents
                .OrderBy(pod => pod.Metadata.CreationTimestamp)
                .Take(nonMinAgentsToRemove));

            // If we still need to remove more agents, remove minimum agents
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

            foreach (var podToRemove in agentsToRemove)
            {
                try
                {
                    // Find corresponding Azure DevOps agent and unregister it
                    var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == podToRemove.Metadata.Name);
                    if (correspondingAgent != null && IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name))
                    {
                        var isMinAgent = podToRemove.Metadata.Labels?.ContainsKey("min-agent") == true &&
                                        podToRemove.Metadata.Labels["min-agent"] == "true";
                        var agentType = isMinAgent ? "minimum" : "regular";

                        _logger.LogInformation("Unregistering excess {AgentType} agent '{AgentName}' from Azure DevOps for MaxAgents compliance",
                            agentType, correspondingAgent.Name);
                        await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);
                    }

                    // Delete the pod
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
