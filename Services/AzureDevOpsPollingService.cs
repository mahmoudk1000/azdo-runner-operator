using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    public void RegisterPool(V1RunnerPoolEntity entity, string pat)
    {
        var poolName = entity.Metadata.Name;
        var pollInfo = new PoolPollInfo
        {
            Entity = entity,
            Pat = pat
        };
        pollInfo.LastPolled = DateTime.UtcNow.AddSeconds(-pollInfo.PollIntervalSeconds - 1); // Force immediate poll

        _poolsToMonitor.AddOrUpdate(poolName, pollInfo, (key, old) => pollInfo);
        _logger.LogInformation("Registered pool '{PoolName}' for Azure DevOps monitoring with {IntervalSeconds}s interval (immediate poll scheduled)",
            poolName, pollInfo.PollIntervalSeconds);
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
                _logger.LogInformation("Polling cycle - checking all registered pools");
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

        _logger.LogInformation("Polling Azure DevOps for pool '{PoolName}'", poolName);

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

        // 4. Update status
        UpdateEntityStatus(entity, azureAgents, activePods, queuedJobs);
    }

    private async Task CleanupCompletedAgentsAsync(V1RunnerPoolEntity entity, string pat, List<Agent> azureAgents, List<V1Pod> allPods)
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

    private async Task CleanupIdleAgentsAsync(V1RunnerPoolEntity entity, string pat, List<Agent> azureAgents, List<V1Pod> pods)
    {
        // If TtlIdleSeconds is 0, remove agents immediately when no work is queued
        // If TtlIdleSeconds > 0, only remove agents that have been idle for that many seconds
        var ttlIdleSeconds = entity.Spec.TtlIdleSeconds;

        // Get minimum agent pods to protect them from cleanup
        var minAgentPods = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
        var minAgentNames = minAgentPods.Select(pod => pod.Metadata.Name).ToHashSet();

        List<Agent> operatorOnlineIdleAgents;

        if (ttlIdleSeconds == 0)
        {
            // Remove all online operator-managed agents immediately when no work is queued (except minimum agents)
            operatorOnlineIdleAgents = azureAgents.Where(agent =>
                agent.Status == "Online" &&
                IsOperatorManagedAgent(agent.Name, entity.Metadata.Name) &&
                !minAgentNames.Contains(agent.Name)) // Protect minimum agents
                .ToList();
        }
        else
        {
            // Only remove agents that have been idle for the specified duration (except minimum agents)
            var idleThreshold = DateTime.UtcNow.AddSeconds(-ttlIdleSeconds);
            operatorOnlineIdleAgents = azureAgents.Where(agent =>
                agent.Status == "Online" &&
                IsOperatorManagedAgent(agent.Name, entity.Metadata.Name) &&
                !minAgentNames.Contains(agent.Name) && // Protect minimum agents
                (agent.LastActive == null || agent.LastActive < idleThreshold))
                .ToList();
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

    private async Task ScaleUpForQueuedWorkAsync(V1RunnerPoolEntity entity, string pat, int queuedJobs, List<Agent> agents, int activePods)
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

    private void UpdateEntityStatus(V1RunnerPoolEntity entity, List<Agent> azureAgents, List<V1Pod> pods, int queuedJobs)
    {
        try
        {
            var freshEntity = _kubernetesClient.Get<V1RunnerPoolEntity>(entity.Metadata.Name, entity.Metadata.NamespaceProperty ?? "default");
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
                freshEntity.Status.Active = true;
                freshEntity.Status.ConnectionStatus = "Connected";
                freshEntity.Status.OrganizationName = _azureDevOpsService.ExtractOrganizationName(freshEntity.Spec.AzDoUrl);
                freshEntity.Status.AgentsSummary = $"{operatorManagedAgents.Count}/{freshEntity.Spec.MaxAgents}";
                freshEntity.Status.Agents = operatorManagedAgents; // Only show operator-managed agents

                freshEntity.Status.Conditions.Clear();
                freshEntity.Status.Conditions.Add(new V1RunnerPoolEntity.StatusCondition
                {
                    Type = "Ready",
                    Status = "True",
                    Reason = "Reconciled",
                    Message = $"Pool has {operatorManagedAgents.Count} operator-managed agents ({availableAgents} available, {offlineAgents} offline), {activePods} active pods, {queuedJobs} queued jobs",
                    LastTransitionTime = DateTime.UtcNow
                });

                _kubernetesClient.UpdateStatus(freshEntity);
                _logger.LogDebug("Updated RunnerPool status: {AgentCount} agents, {QueuedJobs} queued jobs, {ActivePods} active pods",
                    azureAgents.Count, queuedJobs, activePods);
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

    private async Task EnsureMinimumAgentsAsync(V1RunnerPoolEntity entity, string pat)
    {
        if (entity.Spec.MinAgents <= 0)
        {
            return; // No minimum agents required
        }

        try
        {
            var currentMinAgents = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
            var neededMinAgents = entity.Spec.MinAgents - currentMinAgents.Count;

            if (neededMinAgents > 0)
            {
                _logger.LogInformation("Creating {NeededMinAgents} minimum agents for pool '{PoolName}' (current: {CurrentMinAgents}, required: {MinAgents})",
                    neededMinAgents, entity.Metadata.Name, currentMinAgents.Count, entity.Spec.MinAgents);

                for (int i = 0; i < neededMinAgents; i++)
                {
                    await _kubernetesPodService.CreateAgentPodAsync(entity, pat, isMinAgent: true);
                }
            }
            else
            {
                _logger.LogDebug("Minimum agent requirement satisfied for pool '{PoolName}' ({CurrentMinAgents}/{MinAgents})",
                    entity.Metadata.Name, currentMinAgents.Count, entity.Spec.MinAgents);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure minimum agents for pool '{PoolName}'", entity.Metadata.Name);
        }
    }
}
