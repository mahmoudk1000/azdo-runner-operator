using AzDORunner.Entities;
using AzDORunner.Services;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Rbac;
using k8s;

namespace AzDORunner.Controller;

[EntityRbac(typeof(V1AzDORunnerEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Pod), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1PersistentVolumeClaim), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.List)]
public class RunnerPoolController : IEntityController<V1AzDORunnerEntity>
{
    private readonly ILogger<RunnerPoolController> _logger;
    private readonly IAzureDevOpsService _azureDevOpsService;
    private readonly KubernetesPodService _kubernetesPodService;
    private readonly IKubernetes _kubernetesClient;
    private readonly AzureDevOpsPollingService _pollingService;

    public RunnerPoolController(
        ILogger<RunnerPoolController> logger,
        IAzureDevOpsService azureDevOpsService,
        KubernetesPodService kubernetesPodService,
        IKubernetes kubernetesClient,
        AzureDevOpsPollingService pollingService)
    {
        _logger = logger;
        _azureDevOpsService = azureDevOpsService;
        _kubernetesPodService = kubernetesPodService;
        _kubernetesClient = kubernetesClient;
        _pollingService = pollingService;
    }

    public async Task ReconcileAsync(V1AzDORunnerEntity entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reconciling RunnerPool {Name}", entity.Metadata.Name);

        try
        {
            var pat = await GetPatFromSecretAsync(entity);
            if (string.IsNullOrEmpty(pat))
            {
                UpdateStatus(entity, "Error", "Failed to get PAT from secret");
                return;
            }

            if (!await _azureDevOpsService.TestConnectionAsync(entity.Spec.AzDoUrl, pat))
            {
                UpdateStatus(entity, "Disconnected", "Failed to connect to Azure DevOps");
                return;
            }

            UpdateStatus(entity, "Connected", null);

            // Update agent index tracking
            await UpdateAgentIndexTracking(entity);

            // Register with the polling service for continuous monitoring
            _pollingService.RegisterPool(entity, pat);

            _logger.LogInformation("Registered RunnerPool {Name} with Azure DevOps polling service", entity.Metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconciling RunnerPool {Name}", entity.Metadata.Name);
            UpdateStatus(entity, "Error", ex.Message);
        }
    }

    public Task DeletedAsync(V1AzDORunnerEntity entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("RunnerPool {Name} deleted, cleaning up resources", entity.Metadata.Name);
        _pollingService.UnregisterPool(entity.Metadata.Name);
        return Task.CompletedTask;
    }

    private async Task UpdateAgentIndexTracking(V1AzDORunnerEntity entity)
    {
        try
        {
            var namespaceName = entity.Metadata.NamespaceProperty ?? "default";
            var allPods = await _kubernetesPodService.GetAllRunnerPodsAsync(entity);
            var allPvcs = _kubernetesClient.CoreV1.ListNamespacedPersistentVolumeClaim(namespaceName).Items
                .Where(pvc => pvc.Metadata.Labels?.ContainsKey("runner-pool") == true &&
                             pvc.Metadata.Labels["runner-pool"] == entity.Metadata.Name)
                .ToList();

            // TODO: Implement custom resource retrieval using official Kubernetes client
            // For now, use the passed entity instead of fetching a fresh copy
            var freshEntity = entity;
            if (freshEntity?.Status == null)
            {
                _logger.LogWarning("Cannot update agent index tracking - freshEntity or Status is null");
                return;
            }

            if (freshEntity.Status.AgentIndexes == null)
            {
                freshEntity.Status.AgentIndexes = new Dictionary<int, V1AzDORunnerEntity.AgentIndexInfo>();
            }

            // Clear existing index tracking and rebuild from current state
            freshEntity.Status.AgentIndexes.Clear();

            foreach (var pod in allPods)
            {
                var podName = pod.Metadata.Name;
                var expectedPrefix = $"{entity.Metadata.Name}-agent-";

                if (podName.StartsWith(expectedPrefix))
                {
                    var indexStr = podName.Substring(expectedPrefix.Length);
                    if (int.TryParse(indexStr, out var index))
                    {
                        var isMinAgent = pod.Metadata.Labels?.ContainsKey("min-agent") == true &&
                                        pod.Metadata.Labels["min-agent"] == "true";

                        var associatedPvcs = allPvcs
                            .Where(pvc => pvc.Metadata.Labels?.ContainsKey("agent-index") == true &&
                                         pvc.Metadata.Labels["agent-index"] == index.ToString())
                            .Select(pvc => pvc.Metadata.Name)
                            .ToList();

                        freshEntity.Status.AgentIndexes[index] = new V1AzDORunnerEntity.AgentIndexInfo
                        {
                            PodName = podName,
                            Status = pod.Status?.Phase ?? "Unknown",
                            IsMinAgent = isMinAgent,
                            CreatedAt = pod.Metadata.CreationTimestamp?.ToUniversalTime() ?? DateTime.UtcNow,
                            PvcNames = associatedPvcs
                        };
                    }
                }
            }

            // Update the current agent index to be the next available index
            try
            {
                freshEntity.Status.CurrentAgentIndex = _kubernetesPodService.GetNextAvailableAgentIndex(entity);
            }
            catch (InvalidOperationException)
            {
                // Max agents reached, keep current index
            }

            // TODO: Implement custom resource status update using official Kubernetes client
            // _kubernetesClient.UpdateStatus(freshEntity);
            _logger.LogDebug("Updated agent index tracking for RunnerPool {Name}. Tracked indexes: {Indexes} (Status update temporarily disabled)",
                entity.Metadata.Name, string.Join(", ", freshEntity.Status.AgentIndexes.Keys));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update agent index tracking for RunnerPool {Name}", entity.Metadata.Name);
        }
    }

    private void UpdateStatus(V1AzDORunnerEntity entity, string status, string? error)
    {
        try
        {
            // TODO: Implement custom resource retrieval using official Kubernetes client
            // var freshEntity = _kubernetesClient.Get<V1AzDORunnerEntity>(entity.Metadata.Name, entity.Metadata.NamespaceProperty ?? "default");
            var freshEntity = entity; // Temporary: use the passed entity instead of fetching fresh
            if (freshEntity != null)
            {
                freshEntity.Status.ConnectionStatus = status;
                freshEntity.Status.LastError = error;
                freshEntity.Status.LastPolled = DateTime.UtcNow;
                freshEntity.Status.OrganizationName = _azureDevOpsService.ExtractOrganizationName(freshEntity.Spec.AzDoUrl);

                if (!string.IsNullOrEmpty(error))
                {
                    freshEntity.Status.Conditions.Clear();
                    freshEntity.Status.Conditions.Add(new V1AzDORunnerEntity.StatusCondition
                    {
                        Type = "Error",
                        Status = "True",
                        Reason = status,
                        Message = error,
                        LastTransitionTime = DateTime.UtcNow
                    });
                }

                // TODO: Implement custom resource status update using official Kubernetes client
                // _kubernetesClient.UpdateStatus(freshEntity);
                _logger.LogDebug("Would update status for RunnerPool {Name}: {Status} (Status update temporarily disabled)",
                                entity.Metadata.Name, status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update status for RunnerPool {Name}", entity.Metadata.Name);
        }
    }

    private Task<string?> GetPatFromSecretAsync(V1AzDORunnerEntity entity)
    {
        try
        {
            var namespaceName = entity.Metadata.NamespaceProperty ?? "default";
            var secret = _kubernetesClient.CoreV1.ReadNamespacedSecret(entity.Spec.PatSecretName, namespaceName);

            if (secret?.Data?.TryGetValue("token", out var tokenBytes) == true)
            {
                return Task.FromResult<string?>(System.Text.Encoding.UTF8.GetString(tokenBytes));
            }

            _logger.LogError("Secret {SecretName} does not contain 'token' key", entity.Spec.PatSecretName);
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PAT from secret {SecretName}", entity.Spec.PatSecretName);
            return Task.FromResult<string?>(null);
        }
    }
}