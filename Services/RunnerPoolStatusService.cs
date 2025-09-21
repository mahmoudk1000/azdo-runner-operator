using AzDORunner.Entities;
using k8s;
using k8s.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzDORunner.Services;

public interface IRunnerPoolStatusService
{
    Task<V1AzDORunnerEntity?> GetRunnerPoolAsync(string name, string namespaceName = "default");
    Task UpdateStatusAsync(V1AzDORunnerEntity entity);
}

public class RunnerPoolStatusService : IRunnerPoolStatusService
{
    private readonly IKubernetes _kubernetesClient;
    private readonly ILogger<RunnerPoolStatusService> _logger;

    private const string Group = "devops.opentools.mf";
    private const string Version = "v1";
    private const string Plural = "runnerpools";

    public RunnerPoolStatusService(IKubernetes kubernetesClient, ILogger<RunnerPoolStatusService> logger)
    {
        _kubernetesClient = kubernetesClient;
        _logger = logger;
    }

    public async Task<V1AzDORunnerEntity?> GetRunnerPoolAsync(string name, string namespaceName = "default")
    {
        try
        {
            var response = await _kubernetesClient.CustomObjects.GetNamespacedCustomObjectAsync(
                group: Group,
                version: Version,
                namespaceParameter: namespaceName,
                plural: Plural,
                name: name);

            // Convert the response to our custom entity
            if (response is JsonElement jsonElement)
            {
                var json = jsonElement.GetRawText();
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                var entity = JsonSerializer.Deserialize<V1AzDORunnerEntity>(json, options);
                return entity;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get RunnerPool {Name} from namespace {Namespace}", name, namespaceName);
            return null;
        }
    }

    public async Task UpdateStatusAsync(V1AzDORunnerEntity entity)
    {
        try
        {
            var namespaceName = entity.Metadata.NamespaceProperty ?? "default";
            var name = entity.Metadata.Name;

            // First, get the current version of the resource to ensure we have the latest
            var currentEntity = await GetRunnerPoolAsync(name, namespaceName);
            if (currentEntity == null)
            {
                _logger.LogWarning("Could not retrieve current RunnerPool {Name} for status update", name);
                return;
            }

            // Update only the status, preserve everything else
            currentEntity.Status = entity.Status;

            // Prepare the status subresource update
            var statusUpdate = new
            {
                status = currentEntity.Status
            };

            var json = JsonSerializer.Serialize(statusUpdate, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            // Update the status subresource
            await _kubernetesClient.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
                body: json,
                group: Group,
                version: Version,
                namespaceParameter: namespaceName,
                plural: Plural,
                name: name);

            _logger.LogDebug("Successfully updated status for RunnerPool {Name} in namespace {Namespace}", name, namespaceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update status for RunnerPool {Name}", entity.Metadata.Name);
            throw;
        }
    }
}