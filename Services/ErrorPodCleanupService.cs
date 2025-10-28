using AzDORunner.Entities;
using k8s;
using k8s.Models;
using System.Collections.Concurrent;

namespace AzDORunner.Services;

public class ErrorPodCleanupService : BackgroundService
{
    #region Fields

    private readonly ILogger<ErrorPodCleanupService> _logger;
    private readonly KubernetesPodService _kubernetesPodService;
    private readonly IAzureDevOpsService _azureDevOpsService;
    private readonly IKubernetes _kubernetesClient;
    private readonly ConcurrentDictionary<string, ErrorPodMonitorInfo> _poolsToMonitor = new();
    private readonly TimeSpan _errorPodCheckInterval = TimeSpan.FromSeconds(10); // Check every 10 seconds

    #endregion

    #region Constructor

    public ErrorPodCleanupService(
        ILogger<ErrorPodCleanupService> logger,
        KubernetesPodService kubernetesPodService,
        IAzureDevOpsService azureDevOpsService,
        IKubernetes kubernetesClient)
    {
        _logger = logger;
        _kubernetesPodService = kubernetesPodService;
        _azureDevOpsService = azureDevOpsService;
        _kubernetesClient = kubernetesClient;
    }

    #endregion

    #region Public Methods

    public void RegisterPool(V1AzDORunnerEntity entity, string pat)
    {
        var poolName = entity.Metadata.Name;
        _poolsToMonitor.AddOrUpdate(poolName, (key) => new ErrorPodMonitorInfo
        {
            Entity = entity,
            Pat = pat
        }, (key, old) =>
        {
            old.Entity = entity;
            old.Pat = pat;
            return old;
        });
        _logger.LogInformation("Registered pool '{PoolName}' for error pod monitoring", poolName);
    }

    public void UnregisterPool(string poolName)
    {
        if (_poolsToMonitor.TryRemove(poolName, out _))
        {
            _logger.LogInformation("Unregistered pool '{PoolName}' from error pod monitoring", poolName);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Error Pod Cleanup Service started - checking for Error/Failed/Succeeded pods every {IntervalSeconds}s (ContainerCreating/Pending pods are tolerated)",
            _errorPodCheckInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndCleanupErrorPods();
                await Task.Delay(_errorPodCheckInterval, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in error pod cleanup service main loop");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Wait longer on error
            }
        }

        _logger.LogInformation("Error Pod Cleanup Service stopped");
    }

    #endregion

    #region Private Methods

    private async Task CheckAndCleanupErrorPods()
    {
        if (_poolsToMonitor.IsEmpty)
        {
            return;
        }

        var currentTime = DateTime.UtcNow;
        var poolsSnapshot = _poolsToMonitor.Values.ToList();

        foreach (var monitorInfo in poolsSnapshot)
        {
            try
            {
                // Rate limit: only check each pool every 10 seconds to avoid excessive API calls
                if (currentTime - monitorInfo.LastChecked < _errorPodCheckInterval)
                {
                    continue;
                }

                await CleanupErrorPodsForPool(monitorInfo);
                monitorInfo.LastChecked = currentTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up error pods for pool '{PoolName}'",
                    monitorInfo.Entity.Metadata.Name);
                // Still update LastChecked to avoid hammering a problematic pool
                monitorInfo.LastChecked = currentTime;
            }
        }
    }
    private async Task CleanupErrorPodsForPool(ErrorPodMonitorInfo monitorInfo)
    {
        var entity = monitorInfo.Entity;
        var pat = monitorInfo.Pat;
        var poolName = entity.Metadata.Name;

        // Get all pods for this pool
        var allPods = await _kubernetesPodService.GetAllRunnerPodsAsync(entity);

        // Find pods with definitive error or completed states that need immediate cleanup
        var errorPods = allPods.Where(pod =>
            pod.Status?.Phase == "Error" ||
            pod.Status?.Phase == "Failed" ||
            pod.Status?.Phase == "Succeeded" ||
            // Only handle severe image/container errors, not normal startup delays
            (pod.Status?.Phase == "Pending" &&
             pod.Status?.ContainerStatuses?.Any(cs =>
                 cs.State?.Waiting?.Reason == "ImagePullBackOff" ||
                 cs.State?.Waiting?.Reason == "ErrImagePull" ||
                 cs.State?.Waiting?.Reason == "CrashLoopBackOff" ||
                 cs.State?.Waiting?.Reason == "InvalidImageName" ||
                 cs.State?.Waiting?.Reason == "ImageInspectError") == true)).ToList();

        // Note: ContainerCreating and normal Pending states are tolerated and handled by the main polling service

        if (!errorPods.Any())
        {
            _logger.LogDebug("No error pods found in pool '{PoolName}'", poolName);
            return; // No error pods found
        }

        _logger.LogWarning("Found {ErrorPodCount} completed/error pods in pool '{PoolName}' - cleaning up immediately",
            errorPods.Count, poolName);

        foreach (var errorPod in errorPods)
        {
            try
            {
                await CleanupSingleErrorPod(errorPod, entity, pat);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup error pod '{PodName}' in pool '{PoolName}'",
                    errorPod.Metadata.Name, poolName);
            }
        }
    }

    private async Task CleanupSingleErrorPod(V1Pod errorPod, V1AzDORunnerEntity entity, string pat)
    {
        var podName = errorPod.Metadata.Name;
        var namespaceName = entity.Metadata.NamespaceProperty ?? "default";
        var poolName = entity.Metadata.Name;

        _logger.LogWarning("Cleaning up completed/error pod '{PodName}' in pool '{PoolName}' with status '{Phase}'",
            podName, poolName, errorPod.Status?.Phase);

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
            _logger.LogInformation("Unregistered failed agent '{AgentName}' from Azure DevOps pool '{PoolName}'",
                podName, poolName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not unregister agent '{AgentName}' from pool '{PoolName}' - it may not be registered yet",
                podName, poolName);
        }

        // Delete the completed/error pod
        await _kubernetesPodService.DeletePodAsync(podName, namespaceName);
        _logger.LogInformation("Deleted completed/error pod '{PodName}' from pool '{PoolName}'. New pod will be created automatically if needed.",
            podName, poolName);
    }

    #endregion

    #region Helper Classes

    private class ErrorPodMonitorInfo
    {
        public V1AzDORunnerEntity Entity { get; set; } = null!;
        public string Pat { get; set; } = null!;
        public DateTime LastChecked { get; set; } = DateTime.MinValue;
    }

    #endregion
}