using AzDORunner.Entities;
using AzDORunner.Model.Domain;
using k8s;
using k8s.Models;
using System.Collections.Concurrent;

namespace AzDORunner.Services;

public class AzureDevOpsPollingService : BackgroundService
{
    private readonly ILogger<AzureDevOpsPollingService> _logger;
    private readonly IAzureDevOpsService _azureDevOpsService;
    private readonly KubernetesPodService _kubernetesPodService;
    private readonly IKubernetes _kubernetesClient;
    private readonly IRunnerPoolStatusService _statusService;
    private readonly ConcurrentDictionary<string, PoolPollInfo> _poolsToMonitor = new();

    public AzureDevOpsPollingService(
        ILogger<AzureDevOpsPollingService> logger,
        IAzureDevOpsService azureDevOpsService,
        KubernetesPodService kubernetesPodService,
        IKubernetes kubernetesClient,
        IRunnerPoolStatusService statusService)
    {
        _logger = logger;
        _azureDevOpsService = azureDevOpsService;
        _kubernetesPodService = kubernetesPodService;
        _kubernetesClient = kubernetesClient;
        _statusService = statusService;
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
        var connectionStatus = "Disconnected";
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

            // If we successfully polled everything, set status to Connected
            connectionStatus = "Connected";

            // 0. Clean up error pods immediately and retry if needed
            await CleanupErrorPodsAsync(entity, pat, allPods);

            // 1. Clean up completed agents/pods (Failed/Completed pods are deleted immediately)
            await CleanupCompletedAgentsAsync(entity, pat, azureAgents, allPods);

            // 2. Clean up idle running agents based on TtlIdleSeconds configuration
            await CleanupIdleAgentsAsync(entity, pat, azureAgents, allPods);

            // 3. Ensure minimum agents are running
            await EnsureMinimumAgentsAsync(entity, pat);

            // 4. Optimize minimum agents for required capabilities
            if (queuedJobs > 0)
            {
                await OptimizeMinAgentsForCapabilitiesAsync(entity, pat);
            }

            // 5. Ensure maximum agents limit is respected
            await EnsureMaximumAgentsLimitAsync(entity, pat);

            // 6. Scale up if needed - get fresh pod list after cleanup operations
            if (queuedJobs > 0)
            {
                var freshActivePods = await _kubernetesPodService.GetActivePodsAsync(entity);
                await ScaleUpForQueuedWorkAsync(entity, pat, queuedJobs, azureAgents, freshActivePods.Count);
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

        // First, clean up job-request-id labels from running pods whose jobs have completed
        await CleanupCompletedJobLabelsAsync(entity, allPods, jobRequests);

        // Handle Succeeded and Failed pods based on TTL settings
        var completedPods = allPods.Where(pod =>
            pod.Status?.Phase == "Succeeded" ||
            pod.Status?.Phase == "Failed").ToList();

        foreach (var completedPod in completedPods)
        {
            try
            {
                var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == completedPod.Metadata.Name);

                // Double-check: Never delete a pod if agent still has an active job (safety check)
                bool agentHasActiveJob = false;
                if (correspondingAgent != null)
                {
                    var activeJob = jobRequests.FirstOrDefault(j => j.Result == null && j.AgentId == correspondingAgent.Id);
                    if (activeJob != null)
                    {
                        agentHasActiveJob = true;
                    }
                }

                // Also check pod labels for job-request-id
                if (!agentHasActiveJob && completedPod.Metadata.Labels != null && completedPod.Metadata.Labels.TryGetValue("job-request-id", out var jobRequestId) && !string.IsNullOrEmpty(jobRequestId))
                {
                    var labelJob = jobRequests.FirstOrDefault(j => j.RequestId.ToString() == jobRequestId);
                    if (labelJob != null && labelJob.Result == null)
                    {
                        agentHasActiveJob = true;
                    }
                }

                // CRITICAL: Skip if agent still has an active job (this should not happen for Failed/Succeeded pods, but safety first)
                if (agentHasActiveJob)
                {
                    _logger.LogWarning("PROTECTED: Not cleaning up {Phase} pod '{PodName}' because agent still has an active job (this is unusual for a {Phase} pod)", completedPod.Status?.Phase, completedPod.Metadata.Name, completedPod.Status?.Phase);
                    continue;
                }

                bool shouldCleanup = false;
                string reason = "";
                var ttlIdleSeconds = entity.Spec.TtlIdleSeconds;

                if (ttlIdleSeconds == 0)
                {
                    // TTL = 0: Clean up completed pods immediately
                    shouldCleanup = true;
                    reason = $"TTL=0 (immediate cleanup after completion)";
                }
                else if (ttlIdleSeconds > 0 && completedPod.Metadata.CreationTimestamp.HasValue)
                {
                    // TTL > 0: Clean up after pod has existed for ttlIdleSeconds
                    var ttlThreshold = completedPod.Metadata.CreationTimestamp.Value.AddSeconds(ttlIdleSeconds);
                    if (DateTime.UtcNow > ttlThreshold)
                    {
                        shouldCleanup = true;
                        reason = $"pod in {completedPod.Status?.Phase} state for more than {ttlIdleSeconds}s (created: {completedPod.Metadata.CreationTimestamp})";
                    }
                    else
                    {
                        var remainingTime = ttlThreshold - DateTime.UtcNow;
                        _logger.LogDebug("Keeping {Phase} pod '{PodName}' for {RemainingSeconds}s more (TTL: {TtlIdleSeconds}s)",
                            completedPod.Status?.Phase, completedPod.Metadata.Name, (int)remainingTime.TotalSeconds, ttlIdleSeconds);
                    }
                }

                if (shouldCleanup)
                {
                    // Unregister the agent from Azure DevOps if it exists
                    if (correspondingAgent != null)
                    {
                        bool isOperatorManaged = IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name);
                        _logger.LogInformation("{Phase} agent '{AgentName}' - IsOperatorManaged: {IsOperatorManaged}, AgentId: {AgentId}, Status: {Status}",
                            completedPod.Status?.Phase, correspondingAgent.Name, isOperatorManaged, correspondingAgent.Id, correspondingAgent.Status);

                        if (isOperatorManaged)
                        {
                            _logger.LogInformation("Unregistering {Phase} agent '{AgentName}' (ID: {AgentId}) from Azure DevOps - {Reason}",
                                completedPod.Status?.Phase, correspondingAgent.Name, correspondingAgent.Id, reason);

                            var unregistered = await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);

                            if (unregistered)
                            {
                                _logger.LogInformation("Successfully unregistered {Phase} agent '{AgentName}' from Azure DevOps", completedPod.Status?.Phase, correspondingAgent.Name);
                            }
                            else
                            {
                                _logger.LogWarning("Failed to unregister {Phase} agent '{AgentName}' from Azure DevOps (but will still delete pod)", completedPod.Status?.Phase, correspondingAgent.Name);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Skipping Azure DevOps unregistration for {Phase} agent '{AgentName}' - not operator managed", completedPod.Status?.Phase, correspondingAgent.Name);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No corresponding Azure DevOps agent found for {Phase} pod '{PodName}' - proceeding with pod deletion only", completedPod.Status?.Phase, completedPod.Metadata.Name);
                    }

                    // Delete the pod
                    await _kubernetesPodService.DeletePodAsync(completedPod.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default");
                    _logger.LogInformation("Deleted {Phase} pod '{PodName}' after TTL - {Reason}", completedPod.Status?.Phase, completedPod.Metadata.Name, reason);
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

    private async Task CleanupCompletedJobLabelsAsync(V1AzDORunnerEntity entity, List<V1Pod> allPods, List<JobRequest> jobRequests)
    {
        // Find running pods that have job-request-id labels for completed jobs
        var runningPodsWithJobLabels = allPods.Where(pod =>
            pod.Status?.Phase == "Running" &&
            pod.Metadata.Labels != null &&
            pod.Metadata.Labels.ContainsKey("job-request-id")).ToList();

        foreach (var pod in runningPodsWithJobLabels)
        {
            try
            {
                var jobRequestId = pod.Metadata.Labels["job-request-id"];
                var job = jobRequests.FirstOrDefault(j => j.RequestId.ToString() == jobRequestId);

                // If job is completed (has a Result), clear the job-request-id label to make agent reusable
                if (job != null && job.Result != null)
                {
                    await _kubernetesPodService.UpdatePodLabelsAsync(pod.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default",
                        new Dictionary<string, string> { { "job-request-id", "" } });

                    _logger.LogInformation("Cleared job-request-id label from pod '{PodName}' as job {JobRequestId} completed with result: {Result}",
                        pod.Metadata.Name, jobRequestId, job.Result);
                }
                // If job no longer exists (was deleted), also clear the label
                else if (job == null)
                {
                    await _kubernetesPodService.UpdatePodLabelsAsync(pod.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default",
                        new Dictionary<string, string> { { "job-request-id", "" } });

                    _logger.LogInformation("Cleared job-request-id label from pod '{PodName}' as job {JobRequestId} no longer exists",
                        pod.Metadata.Name, jobRequestId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup job label for pod '{PodName}'", pod.Metadata.Name);
            }
        }
    }

    private async Task CleanupErrorPodsAsync(V1AzDORunnerEntity entity, string pat, List<V1Pod> allPods)
    {
        // Find pods with Error status or stuck in problematic states (IMMEDIATELY clean up these)
        var errorPods = allPods.Where(pod =>
            pod.Status?.Phase == "Error" ||
            (pod.Status?.Phase == "Pending" &&
             pod.Status?.ContainerStatuses?.Any(cs => cs.State?.Waiting?.Reason == "ImagePullBackOff" ||
                                                     cs.State?.Waiting?.Reason == "ErrImagePull" ||
                                                     cs.State?.Waiting?.Reason == "CrashLoopBackOff" ||
                                                     cs.State?.Waiting?.Reason == "InvalidImageName" ||
                                                     cs.State?.Waiting?.Reason == "ImageInspectError") == true) ||
            // Only consider ContainerCreating as stuck if it's been more than 15 minutes (increased tolerance)
            (pod.Status?.Phase == "Pending" &&
             pod.Metadata.CreationTimestamp.HasValue &&
             DateTime.UtcNow - pod.Metadata.CreationTimestamp.Value > TimeSpan.FromMinutes(15) &&
             pod.Status?.ContainerStatuses?.Any(cs => cs.State?.Waiting?.Reason == "ContainerCreating") == true)).ToList();

        foreach (var errorPod in errorPods)
        {
            try
            {
                var podName = errorPod.Metadata.Name;
                var namespaceName = entity.Metadata.NamespaceProperty ?? "default";

                _logger.LogWarning("Found error pod '{PodName}' with status '{Phase}'. Deleting immediately (error states ignore TTL).",
                    podName, errorPod.Status?.Phase);

                // Log container status details for debugging
                if (errorPod.Status?.ContainerStatuses != null)
                {
                    foreach (var containerStatus in errorPod.Status.ContainerStatuses)
                    {
                        if (containerStatus.State?.Waiting != null)
                        {
                            _logger.LogWarning("Container '{ContainerName}' in pod '{PodName}' is waiting with reason: {Reason}, message: {Message}",
                                containerStatus.Name, podName, containerStatus.State.Waiting.Reason, containerStatus.State.Waiting.Message);
                        }
                        if (containerStatus.State?.Terminated != null)
                        {
                            _logger.LogWarning("Container '{ContainerName}' in pod '{PodName}' terminated with reason: {Reason}, exit code: {ExitCode}",
                                containerStatus.Name, podName, containerStatus.State.Terminated.Reason, containerStatus.State.Terminated.ExitCode);
                        }
                    }
                }

                // Try to unregister the agent from Azure DevOps if it exists
                try
                {
                    await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, podName, pat);
                    _logger.LogInformation("Unregistered failed agent '{AgentName}' from Azure DevOps", podName);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not unregister agent '{AgentName}' - it may not be registered yet", podName);
                }

                // Delete the error pod
                await _kubernetesPodService.DeletePodAsync(podName, namespaceName);
                _logger.LogInformation("Deleted error pod '{PodName}'. New pod will be created automatically if needed.", podName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup error pod '{PodName}'", errorPod.Metadata.Name);
            }
        }
    }

    private async Task CleanupIdleAgentsAsync(V1AzDORunnerEntity entity, string pat, List<Agent> azureAgents, List<V1Pod> pods)
    {
        var ttlIdleSeconds = entity.Spec.TtlIdleSeconds;
        var queuedJobs = await _azureDevOpsService.GetQueuedJobsCountAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

        // Get minimum agent pods to protect them from cleanup
        var minAgentPods = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
        var minAgentNames = minAgentPods.Select(pod => pod.Metadata.Name).ToHashSet();

        // Fetch all job requests for the pool to determine if an agent is running a job
        var jobRequests = await _azureDevOpsService.GetJobRequestsAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, pat);

        // Get running pods that are not minimum agents
        var runningPods = pods.Where(pod => pod.Status?.Phase == "Running").ToList();

        foreach (var pod in runningPods)
        {
            try
            {
                // Skip minimum agents
                if (minAgentNames.Contains(pod.Metadata.Name))
                {
                    _logger.LogDebug("Skipping cleanup of minimum agent '{PodName}'", pod.Metadata.Name);
                    continue;
                }

                // Grace period: do not delete pod if it is less than 2 minutes old (allow time for registration)
                if (pod.Metadata.CreationTimestamp.HasValue &&
                    DateTime.UtcNow - pod.Metadata.CreationTimestamp.Value < TimeSpan.FromMinutes(2))
                {
                    _logger.LogDebug("Skipping deletion of pod '{PodName}' because it is in registration grace period", pod.Metadata.Name);
                    continue;
                }

                var correspondingAgent = azureAgents.FirstOrDefault(agent => agent.Name == pod.Metadata.Name);

                // Check if agent has an active job by multiple methods
                bool agentHasActiveJob = false;
                string jobCheckReason = "";

                // Method 1: Check if agent is assigned to an incomplete job in Azure DevOps
                if (correspondingAgent != null)
                {
                    var activeJob = jobRequests.FirstOrDefault(j => j.Result == null && j.AgentId == correspondingAgent.Id);
                    if (activeJob != null)
                    {
                        agentHasActiveJob = true;
                        jobCheckReason = $"agent {correspondingAgent.Id} assigned to active job {activeJob.RequestId}";
                    }
                }

                // Method 2: Check if pod has job-request-id label for an active job
                if (!agentHasActiveJob && pod.Metadata.Labels != null && pod.Metadata.Labels.TryGetValue("job-request-id", out var jobRequestId) && !string.IsNullOrEmpty(jobRequestId))
                {
                    var labelJob = jobRequests.FirstOrDefault(j => j.RequestId.ToString() == jobRequestId);
                    if (labelJob != null && labelJob.Result == null)
                    {
                        agentHasActiveJob = true;
                        jobCheckReason = $"pod has job-request-id label {jobRequestId} for active job";
                    }
                }

                // NEVER kill an agent that has an active job
                if (agentHasActiveJob)
                {
                    _logger.LogInformation("PROTECTED: Not cleaning up agent '{AgentName}' because it has an active job - {JobCheckReason}", pod.Metadata.Name, jobCheckReason);
                    continue;
                }

                bool shouldCleanup = false;
                string reason = "";

                if (ttlIdleSeconds == 0)
                {
                    // TTL = 0: Clean up immediately when no jobs are queued
                    if (queuedJobs == 0)
                    {
                        shouldCleanup = true;
                        reason = "no queued jobs and TTL=0 (immediate cleanup)";
                    }
                }
                else if (ttlIdleSeconds > 0)
                {
                    // TTL > 0: Clean up after agent has been idle for ttlIdleSeconds
                    // Use agent's LastActive time if available (more accurate than pod creation)
                    if (correspondingAgent?.LastActive != null)
                    {
                        var idleThreshold = correspondingAgent.LastActive.Value.AddSeconds(ttlIdleSeconds);
                        if (DateTime.UtcNow > idleThreshold)
                        {
                            shouldCleanup = true;
                            reason = $"agent idle for more than {ttlIdleSeconds}s (last active: {correspondingAgent.LastActive})";
                        }
                        else
                        {
                            var remainingTime = idleThreshold - DateTime.UtcNow;
                            _logger.LogDebug("Keeping idle agent '{AgentName}' for {RemainingSeconds}s more (TTL: {TtlIdleSeconds}s, last active: {LastActive})",
                                pod.Metadata.Name, (int)remainingTime.TotalSeconds, ttlIdleSeconds, correspondingAgent.LastActive);
                        }
                    }
                    else if (pod.Metadata.CreationTimestamp.HasValue)
                    {
                        // Fallback: Use pod creation time if no LastActive available
                        var idleThreshold = pod.Metadata.CreationTimestamp.Value.AddSeconds(ttlIdleSeconds);
                        if (DateTime.UtcNow > idleThreshold)
                        {
                            shouldCleanup = true;
                            reason = $"pod created more than {ttlIdleSeconds}s ago and agent has no LastActive time";
                        }
                        else
                        {
                            var remainingTime = idleThreshold - DateTime.UtcNow;
                            _logger.LogDebug("Keeping agent '{AgentName}' for {RemainingSeconds}s more (TTL: {TtlIdleSeconds}s, created: {CreationTime})",
                                pod.Metadata.Name, (int)remainingTime.TotalSeconds, ttlIdleSeconds, pod.Metadata.CreationTimestamp);
                        }
                    }
                }

                if (shouldCleanup)
                {
                    _logger.LogInformation("Cleaning up idle agent '{AgentName}' - {Reason}", pod.Metadata.Name, reason);

                    // Unregister from Azure DevOps first
                    if (correspondingAgent != null)
                    {
                        bool isOperatorManaged = IsOperatorManagedAgent(correspondingAgent.Name, entity.Metadata.Name);
                        _logger.LogInformation("Agent '{AgentName}' - IsOperatorManaged: {IsOperatorManaged}, AgentId: {AgentId}, Status: {Status}",
                            correspondingAgent.Name, isOperatorManaged, correspondingAgent.Id, correspondingAgent.Status);

                        if (isOperatorManaged)
                        {
                            _logger.LogInformation("Unregistering agent '{AgentName}' (ID: {AgentId}) from Azure DevOps pool '{Pool}'",
                                correspondingAgent.Name, correspondingAgent.Id, entity.Spec.Pool);

                            var unregistered = await _azureDevOpsService.UnregisterAgentAsync(entity.Spec.AzDoUrl, entity.Spec.Pool, correspondingAgent.Name, pat);

                            if (unregistered)
                            {
                                _logger.LogInformation("Successfully unregistered agent '{AgentName}' from Azure DevOps", correspondingAgent.Name);
                            }
                            else
                            {
                                _logger.LogWarning("Failed to unregister agent '{AgentName}' from Azure DevOps (but will still delete pod)", correspondingAgent.Name);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Skipping Azure DevOps unregistration for agent '{AgentName}' - not operator managed", correspondingAgent.Name);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No corresponding Azure DevOps agent found for pod '{PodName}' - proceeding with pod deletion only", pod.Metadata.Name);
                    }

                    // Delete the pod
                    await _kubernetesPodService.DeletePodAsync(pod.Metadata.Name,
                        entity.Metadata.NamespaceProperty ?? "default");

                    _logger.LogInformation("Successfully cleaned up idle agent pod '{AgentName}'", pod.Metadata.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup idle agent/pod '{PodName}'", pod.Metadata.Name);
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

        // For every queued job not assigned to an agent or pod, try to reuse existing idle agents first
        var jobsWithoutAgentOrPod = jobRequests.Where(j =>
            (j.Result == null || (j.Result != null && j.Result.ToLower() == "inprogress")) &&
            !operatorManagedAgents.Any(a => a.Id == j.AgentId) &&
            !allPods.Any(pod =>
                pod.Metadata.Labels != null &&
                pod.Metadata.Labels.TryGetValue("job-request-id", out var val) &&
                val == j.RequestId.ToString())
        ).ToList();

        // Try to reuse idle agents first before creating new ones
        var jobsToSpawn = new List<JobRequest>();
        foreach (var job in jobsWithoutAgentOrPod)
        {
            var reusedAgent = await TryReuseIdleAgentAsync(entity, pat, job, operatorManagedAgents, allPods, jobRequests);
            if (!reusedAgent)
            {
                jobsToSpawn.Add(job);
            }
        }

        var availableSlots = entity.Spec.MaxAgents - totalAgentCount;
        jobsToSpawn = jobsToSpawn.Take(availableSlots).ToList();

        if (jobsToSpawn.Count > 0)
        {
            _logger.LogInformation("PENDING WORK DETECTED: Spawning {NeededAgents} agents for {JobsWithoutAgent} unassigned queued jobs (max agents: {MaxAgents}, total agents: {TotalAgentCount})",
                jobsToSpawn.Count, jobsWithoutAgentOrPod.Count, entity.Spec.MaxAgents, totalAgentCount);

            foreach (var job in jobsToSpawn)
            {
                bool podExists = allPods.Any(pod =>
                    pod.Metadata.Labels != null &&
                    pod.Metadata.Labels.TryGetValue("job-request-id", out var val) &&
                    val == job.RequestId.ToString());
                if (podExists)
                {
                    _logger.LogInformation("Pod already exists for job-request-id {JobRequestId}, skipping agent spawn.", job.RequestId);
                    continue;
                }
                var extraLabels = new Dictionary<string, string> { { "job-request-id", job.RequestId.ToString() } };
                if (entity.Spec.CapabilityAware)
                {
                    await SpawnCapabilityAwareAgentsFromJobDemands(entity, pat, new List<JobRequest> { job }, extraLabels);
                }
                else
                {
                    var agentIndex = _kubernetesPodService.GetNextAvailableAgentIndex(entity);
                    await _kubernetesPodService.CreateAgentPodAsync(entity, pat, agentIndex, false, null, extraLabels);
                }
            }

            _logger.LogInformation("Created {NeededAgents} agent pods for pending work", jobsToSpawn.Count);
        }
        else if (jobsWithoutAgentOrPod.Count == 0)
        {
            _logger.LogInformation("No new agents needed: all queued jobs are already assigned to agents or pods are starting up.");
        }
        else
        {
            _logger.LogInformation("All {JobCount} unassigned jobs were handled by reusing idle agents.", jobsWithoutAgentOrPod.Count);
        }
    }

    private async Task SpawnCapabilityAwareAgentsFromJobDemands(V1AzDORunnerEntity entity, string pat, List<JobRequest> jobsToSpawn, Dictionary<string, string>? extraLabels = null)
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
                var labels = extraLabels != null ? new Dictionary<string, string>(extraLabels) : new Dictionary<string, string>();
                labels["job-request-id"] = job.RequestId.ToString();
                var agentIndex = _kubernetesPodService.GetNextAvailableAgentIndex(entity);
                await _kubernetesPodService.CreateAgentPodAsync(entity, pat, agentIndex, false, capability, labels);
                _logger.LogInformation("Spawned agent with capability '{Capability}' for job {JobId} (labels: {Labels})", capability, job.RequestId, string.Join(",", labels.Select(kv => $"{kv.Key}={kv.Value}")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn capability-aware agents, falling back to regular spawning");
            foreach (var job in jobsToSpawn)
            {
                var agentIndex = _kubernetesPodService.GetNextAvailableAgentIndex(entity);
                await _kubernetesPodService.CreateAgentPodAsync(entity, pat, agentIndex);
            }
        }
    }

    private async Task<bool> TryReuseIdleAgentAsync(V1AzDORunnerEntity entity, string pat, JobRequest job, List<Agent> operatorManagedAgents, List<V1Pod> allPods, List<JobRequest> jobRequests)
    {
        try
        {
            // Only try to reuse agents if TtlIdleSeconds > 0 (reuse is only meaningful with a grace period)
            if (entity.Spec.TtlIdleSeconds <= 0)
            {
                return false;
            }

            var ttlIdleSeconds = entity.Spec.TtlIdleSeconds;
            var idleThreshold = DateTime.UtcNow.AddSeconds(-ttlIdleSeconds);

            // Get minimum agent pods to protect them from being reassigned
            var minAgentPods = await _kubernetesPodService.GetMinAgentPodsAsync(entity);
            var minAgentNames = minAgentPods.Select(pod => pod.Metadata.Name).ToHashSet();

            // Find idle agents that are eligible for reuse
            var eligibleIdleAgents = operatorManagedAgents.Where(agent =>
                // Agent must be online or running (but not busy)
                (agent.Status?.ToLower() == "online" || agent.Status?.ToLower() == "running") &&
                IsOperatorManagedAgent(agent.Name, entity.Metadata.Name) &&
                // Don't reuse minimum agents
                !minAgentNames.Contains(agent.Name) &&
                // Agent must be within the idle time window (not yet expired)
                (agent.LastActive == null || agent.LastActive > idleThreshold) &&
                // Agent must not be running a job
                !jobRequests.Any(j => j.Result == null && j.AgentId == agent.Id)
            ).ToList();

            // Find the corresponding pods for these agents that are truly idle (no active job-request-id)
            var eligibleIdlePods = allPods.Where(pod =>
                pod.Status?.Phase == "Running" &&
                eligibleIdleAgents.Any(agent => agent.Name == pod.Metadata.Name) &&
                // Pod must not have an active job-request-id (or have an empty one)
                (pod.Metadata.Labels?.TryGetValue("job-request-id", out var jobId) != true || string.IsNullOrEmpty(jobId))
            ).ToList();

            // Determine required capability for the job
            string requiredCapability = "base";
            if (entity.Spec.CapabilityAware && job.Demands != null && entity.Spec.CapabilityImages != null)
            {
                var matched = job.Demands.FirstOrDefault(d => entity.Spec.CapabilityImages.ContainsKey(d));
                if (!string.IsNullOrEmpty(matched))
                {
                    requiredCapability = matched;
                }
            }

            // Find a pod that matches the required capability and is truly idle
            V1Pod? reusablePod = null;
            if (entity.Spec.CapabilityAware)
            {
                reusablePod = eligibleIdlePods.FirstOrDefault(pod =>
                    pod.Metadata.Labels != null &&
                    pod.Metadata.Labels.TryGetValue("capability", out var capability) &&
                    capability == requiredCapability);
            }
            else
            {
                // If not capability-aware, any eligible idle pod can be reused
                reusablePod = eligibleIdlePods.FirstOrDefault();
            }

            if (reusablePod != null)
            {
                // Update the pod's job-request-id label to the new job
                await _kubernetesPodService.UpdatePodLabelsAsync(reusablePod.Metadata.Name,
                    entity.Metadata.NamespaceProperty ?? "default",
                    new Dictionary<string, string> { { "job-request-id", job.RequestId.ToString() } });

                _logger.LogInformation("Reused idle agent '{AgentName}' for job {JobRequestId} with capability '{Capability}'",
                    reusablePod.Metadata.Name, job.RequestId, requiredCapability);

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reuse idle agent for job {JobRequestId}", job.RequestId);
            return false;
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
                    var agentIndex = _kubernetesPodService.GetNextAvailableAgentIndex(entity);
                    await _kubernetesPodService.CreateAgentPodAsync(entity, pat, agentIndex, true, capabilityToAdd);
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

    private async void UpdateEntityStatus(V1AzDORunnerEntity entity, List<Agent> azureAgents, List<V1Pod> pods, int queuedJobs, string connectionStatus = "Disconnected", string? lastError = null)
    {
        try
        {
            // Get the latest version of the entity from Kubernetes
            var freshEntity = await _statusService.GetRunnerPoolAsync(entity.Metadata.Name, entity.Metadata.NamespaceProperty ?? "default");
            if (freshEntity != null)
            {
                // Filter to only count operator-managed agents for status
                var operatorManagedAgents = azureAgents.Where(a => IsOperatorManagedAgent(a.Name, entity.Metadata.Name)).ToList();

                var runningPods = pods.Count(p => p.Status?.Phase == "Running");
                var pendingPods = pods.Count(p => p.Status?.Phase == "Pending");
                var containerCreatingPods = pods.Count(p =>
                    p.Status?.Phase == "Pending" &&
                    p.Status?.ContainerStatuses?.Any(cs => cs.State?.Waiting?.Reason == "ContainerCreating") == true);
                var activePods = runningPods + pendingPods;
                var availableAgents = operatorManagedAgents.Count(a => a.Status?.ToLower() == "online" || a.Status?.ToLower() == "idle" || a.Status?.ToLower() == "running");
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
                    var podStatusMessage = containerCreatingPods > 0
                        ? $"{activePods} pods ({runningPods} running, {pendingPods} pending, {containerCreatingPods} starting)"
                        : $"{activePods} pods ({runningPods} running, {pendingPods} pending)";

                    freshEntity.Status.Conditions.Add(new V1AzDORunnerEntity.StatusCondition
                    {
                        Type = "Ready",
                        Status = "True",
                        Reason = "Reconciled",
                        Message = $"Pool has {operatorManagedAgents.Count} operator-managed agents ({availableAgents} available, {offlineAgents} offline), {podStatusMessage}, {queuedJobs} queued jobs",
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

                // Update the status using our status service
                await _statusService.UpdateStatusAsync(freshEntity);
                _logger.LogDebug("Updated RunnerPool status: {ConnectionStatus}, {AgentCount} agents, {QueuedJobs} queued jobs, {RunningPods} running pods, {PendingPods} pending pods",
                    connectionStatus, azureAgents.Count, queuedJobs, runningPods, pendingPods);
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

        // Check if suffix is a numeric index (e.g., "0", "1", "2", etc.)
        if (int.TryParse(suffix, out var index) && index >= 0)
        {
            return true;
        }

        // Also support 8-character alphanumeric suffixes for backward compatibility
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
                    var agentIndex = _kubernetesPodService.GetNextAvailableAgentIndex(entity);
                    await _kubernetesPodService.CreateAgentPodAsync(entity, pat, agentIndex, true);
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
                    var jobRequestId = podToRemove.Metadata.Labels != null && podToRemove.Metadata.Labels.TryGetValue("job-request-id", out var labelVal) ? labelVal : null;
                    bool isLinkedToActiveJob = false;
                    if (!string.IsNullOrEmpty(jobRequestId))
                    {
                        isLinkedToActiveJob = jobRequests.Any(j => j.RequestId.ToString() == jobRequestId && j.Result == null);
                    }
                    if (isLinkedToActiveJob)
                    {
                        _logger.LogInformation("Skipping removal of pod '{PodName}' because it is linked to an active or queued job (job-request-id: {JobRequestId})", podToRemove.Metadata.Name, jobRequestId);
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
