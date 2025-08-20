using AzDORunner.Entities;
using AzDORunner.Services;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;

namespace AzDORunner.Controller;

[EntityRbac(typeof(V1AzDORunnerEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Pod), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.List)]
public class RunnerPoolController : IEntityController<V1AzDORunnerEntity>
{
    private readonly ILogger<RunnerPoolController> _logger;
    private readonly IAzureDevOpsService _azureDevOpsService;
    private readonly IKubernetesPodService _kubernetesPodService;
    private readonly IKubernetesClient _kubernetesClient;
    private readonly AzureDevOpsPollingService _pollingService;

    public RunnerPoolController(
        ILogger<RunnerPoolController> logger,
        IAzureDevOpsService azureDevOpsService,
        IKubernetesPodService kubernetesPodService,
        IKubernetesClient kubernetesClient,
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

    private void UpdateStatus(V1AzDORunnerEntity entity, string status, string? error)
    {
        try
        {
            var freshEntity = _kubernetesClient.Get<V1AzDORunnerEntity>(entity.Metadata.Name, entity.Metadata.NamespaceProperty ?? "default");
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

                _kubernetesClient.UpdateStatus(freshEntity);
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
            var secret = _kubernetesClient.Get<V1Secret>(entity.Spec.PatSecretName, namespaceName);

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
