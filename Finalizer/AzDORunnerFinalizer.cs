using AzDORunner.Entities;
using AzDORunner.Services;
using KubeOps.Abstractions.Finalizer;

namespace AzDORunner.Finalizer;

public class RunnerPoolFinalizer : IEntityFinalizer<V1AzDORunnerEntity>
{
    private readonly ILogger<RunnerPoolFinalizer> _logger;
    private readonly IKubernetesPodService _kubernetesPodService;

    public RunnerPoolFinalizer(ILogger<RunnerPoolFinalizer> logger, IKubernetesPodService kubernetesPodService)
    {
        _logger = logger;
        _kubernetesPodService = kubernetesPodService;
    }

    public async Task FinalizeAsync(V1AzDORunnerEntity entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Finalizing RunnerPool {Name}, cleaning up all agent pods", entity.Metadata.Name);

        try
        {
            var namespaceName = entity.Metadata.NamespaceProperty ?? "default";

            // 1. First, bulk delete all completed pods (Succeeded and Failed phases)
            // This is equivalent to: kubectl delete pod --field-selector=status.phase==Succeeded,status.phase==Failed
            await _kubernetesPodService.DeleteCompletedPodsAsync(entity);

            // 2. Get remaining active pods and delete them individually
            var activePods = await _kubernetesPodService.GetActivePodsAsync(entity);

            if (activePods.Count > 0)
            {
                _logger.LogInformation("Deleting {ActivePodCount} remaining active pods for RunnerPool {Name}",
                    activePods.Count, entity.Metadata.Name);

                foreach (var pod in activePods)
                {
                    await _kubernetesPodService.DeletePodAsync(pod.Metadata.Name, namespaceName);
                    _logger.LogInformation("Deleted active pod {PodName} (Phase: {Phase}) during RunnerPool finalization",
                        pod.Metadata.Name, pod.Status?.Phase);
                }
            }

            // 3. Double-check and clean up any remaining pods
            var remainingPods = await _kubernetesPodService.GetAllRunnerPodsAsync(entity);
            if (remainingPods.Count > 0)
            {
                _logger.LogWarning("Found {RemainingPodCount} remaining pods after cleanup, force deleting...", remainingPods.Count);

                foreach (var pod in remainingPods)
                {
                    await _kubernetesPodService.DeletePodAsync(pod.Metadata.Name, namespaceName);
                    _logger.LogInformation("Force deleted remaining pod {PodName} (Phase: {Phase})",
                        pod.Metadata.Name, pod.Status?.Phase);
                }
            }

            _logger.LogInformation("Successfully finalized RunnerPool {Name} - all agent pods cleaned up", entity.Metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizing RunnerPool {Name}", entity.Metadata.Name);
            throw;
        }
    }
}
