using k8s.Models;
using AzDORunner.Entities;
using KubeOps.KubernetesClient;

namespace AzDORunner.Services;

public interface IKubernetesPodService
{
    Task<V1Pod> CreateAgentPodAsync(V1AzDORunnerEntity runnerPool, string pat, bool isMinAgent = false, string? requiredCapability = null);
    Task<List<V1Pod>> GetActivePodsAsync(V1AzDORunnerEntity runnerPool);
    Task<List<V1Pod>> GetAllRunnerPodsAsync(V1AzDORunnerEntity runnerPool);
    Task<List<V1Pod>> GetMinAgentPodsAsync(V1AzDORunnerEntity runnerPool);
    Task DeletePodAsync(string podName, string namespaceName);
    Task DeleteCompletedPodsAsync(V1AzDORunnerEntity runnerPool);
}

public class KubernetesPodService : IKubernetesPodService
{
    private readonly IKubernetesClient _kubernetesClient;
    private readonly ILogger<KubernetesPodService> _logger;

    public KubernetesPodService(IKubernetesClient kubernetesClient, ILogger<KubernetesPodService> logger)
    {
        _kubernetesClient = kubernetesClient;
        _logger = logger;
    }

    public Task<V1Pod> CreateAgentPodAsync(V1AzDORunnerEntity runnerPool, string pat, bool isMinAgent = false, string? requiredCapability = null)
    {
        var podName = $"{runnerPool.Metadata.Name}-agent-{Guid.NewGuid().ToString("N")[..8]}";
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        // Determine which image to use based on capability requirements
        var imageToUse = DetermineImageForCapability(runnerPool, requiredCapability);
        var capabilityLabel = requiredCapability ?? "base";

        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = "azdo-runner",
                    ["runner-pool"] = runnerPool.Metadata.Name,
                    ["managed-by"] = "azdo-runner-operator",
                    ["atos"] = "devops",
                    ["min-agent"] = isMinAgent.ToString().ToLower(),
                    ["capability"] = capabilityLabel,
                    ["capability-aware"] = runnerPool.Spec.CapabilityAware.ToString().ToLower()
                },
                OwnerReferences = new List<V1OwnerReference>
                {
                    new()
                    {
                        ApiVersion = runnerPool.ApiVersion,
                        Kind = runnerPool.Kind,
                        Name = runnerPool.Metadata.Name,
                        Uid = runnerPool.Metadata.Uid,
                        Controller = true,
                        BlockOwnerDeletion = true
                    }
                }
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                TerminationGracePeriodSeconds = 30,
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = "agent",
                        Image = imageToUse,
                        ImagePullPolicy = runnerPool.Spec.ImagePullPolicy,
                        // Min agents never use --once, regular agents use --once only when TtlIdleSeconds is 0
                        Args = (!isMinAgent && runnerPool.Spec.TtlIdleSeconds == 0) ? new List<string> { "--once" } : null,
                        Env = new List<V1EnvVar>
                        {
                            new()
                            {
                                Name = "AZP_URL",
                                Value = runnerPool.Spec.AzDoUrl
                            },
                            new()
                            {
                                Name = "AZP_POOL",
                                Value = runnerPool.Spec.Pool
                            },
                            new()
                            {
                                Name = "AZP_TOKEN",
                                ValueFrom = new V1EnvVarSource
                                {
                                    SecretKeyRef = new V1SecretKeySelector
                                    {
                                        Name = runnerPool.Spec.PatSecretName,
                                        Key = "token"
                                    }
                                }
                            },
                            new()
                            {
                                Name = "AZP_AGENT_NAME",
                                Value = podName
                            },
                            new()
                            {
                                Name = "AZP_CAPABILITY",
                                Value = capabilityLabel
                            }
                        },
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity>
                            {
                                ["cpu"] = new("100m"),
                                ["memory"] = new("256Mi")
                            },
                            Limits = new Dictionary<string, ResourceQuantity>
                            {
                                ["cpu"] = new("2"),
                                ["memory"] = new("4Gi")
                            }
                        },
                        Lifecycle = new V1Lifecycle
                        {
                            PreStop = new V1LifecycleHandler
                            {
                                Exec = new V1ExecAction
                                {
                                    Command = new List<string> { "/bin/bash", "-c", "echo 'PreStop hook triggered'; pkill -TERM Agent.Listener || true; sleep 5" }
                                }
                            }
                        }
                    }
                }
            }
        };

        try
        {
            var createdPod = _kubernetesClient.Create(pod);
            var agentType = isMinAgent ? "minimum" : "regular";
            var mode = (!isMinAgent && runnerPool.Spec.TtlIdleSeconds == 0) ? "one-time (--once)" : "continuous";
            _logger.LogInformation("Created {AgentType} agent pod {PodName} in namespace {Namespace} (Mode: {Mode}, TtlIdleSeconds: {TtlIdleSeconds}, Capability: {Capability}, Image: {Image}, ImagePullPolicy: {ImagePullPolicy})",
                agentType, podName, namespaceName, mode, runnerPool.Spec.TtlIdleSeconds, capabilityLabel, imageToUse, runnerPool.Spec.ImagePullPolicy);
            return Task.FromResult(createdPod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent pod {PodName}", podName);
            throw;
        }
    }

    public Task<List<V1Pod>> GetActivePodsAsync(V1AzDORunnerEntity runnerPool)
    {
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        try
        {
            var allPods = _kubernetesClient.List<V1Pod>(namespaceName);

            // Only include truly active pods (Running or Pending)
            var activePods = allPods.Where(pod =>
                pod.Metadata.Labels?.ContainsKey("runner-pool") == true &&
                pod.Metadata.Labels["runner-pool"] == runnerPool.Metadata.Name &&
                (pod.Status?.Phase == "Running" || pod.Status?.Phase == "Pending")).ToList();

            return Task.FromResult(activePods);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active pods for runner pool {RunnerPoolName}", runnerPool.Metadata.Name);
            return Task.FromResult(new List<V1Pod>());
        }
    }

    public Task<List<V1Pod>> GetAllRunnerPodsAsync(V1AzDORunnerEntity runnerPool)
    {
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        try
        {
            var allPods = _kubernetesClient.List<V1Pod>(namespaceName);

            // Include ALL pods belonging to this runner pool (for cleanup purposes)
            var allRunnerPods = allPods.Where(pod =>
                pod.Metadata.Labels?.ContainsKey("runner-pool") == true &&
                pod.Metadata.Labels["runner-pool"] == runnerPool.Metadata.Name).ToList();

            return Task.FromResult(allRunnerPods);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all pods for runner pool {RunnerPoolName}", runnerPool.Metadata.Name);
            return Task.FromResult(new List<V1Pod>());
        }
    }

    public Task<List<V1Pod>> GetMinAgentPodsAsync(V1AzDORunnerEntity runnerPool)
    {
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        try
        {
            var allPods = _kubernetesClient.List<V1Pod>(namespaceName);

            // Get only minimum agent pods that are active
            var minAgentPods = allPods.Where(pod =>
                pod.Metadata.Labels?.ContainsKey("runner-pool") == true &&
                pod.Metadata.Labels["runner-pool"] == runnerPool.Metadata.Name &&
                pod.Metadata.Labels?.ContainsKey("min-agent") == true &&
                pod.Metadata.Labels["min-agent"] == "true" &&
                (pod.Status?.Phase == "Running" || pod.Status?.Phase == "Pending")).ToList();

            return Task.FromResult(minAgentPods);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get minimum agent pods for runner pool {RunnerPoolName}", runnerPool.Metadata.Name);
            return Task.FromResult(new List<V1Pod>());
        }
    }

    public Task DeletePodAsync(string podName, string namespaceName)
    {
        try
        {
            _kubernetesClient.Delete<V1Pod>(podName, namespaceName);
            _logger.LogInformation("Deleted pod {PodName} in namespace {Namespace}", podName, namespaceName);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete pod {PodName}", podName);
            return Task.CompletedTask;
        }
    }

    public Task DeleteCompletedPodsAsync(V1AzDORunnerEntity runnerPool)
    {
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        try
        {
            _logger.LogInformation("🧹 Deleting completed pods for RunnerPool {Name} (Succeeded and Failed phases)", runnerPool.Metadata.Name);

            // Get all pods for this runner pool
            var allPods = _kubernetesClient.List<V1Pod>(namespaceName);

            // Filter completed pods that belong to this runner pool
            // Equivalent to: kubectl delete pod --field-selector=status.phase==Succeeded,status.phase==Failed
            var completedPods = allPods.Where(pod =>
                pod.Metadata.Labels?.ContainsKey("runner-pool") == true &&
                pod.Metadata.Labels["runner-pool"] == runnerPool.Metadata.Name &&
                (pod.Status?.Phase == "Succeeded" || pod.Status?.Phase == "Failed")).ToList();

            var deletedCount = 0;
            foreach (var pod in completedPods)
            {
                try
                {
                    _kubernetesClient.Delete<V1Pod>(pod.Metadata.Name, namespaceName);
                    _logger.LogInformation("Bulk deleted completed pod {PodName} (Phase: {Phase})",
                        pod.Metadata.Name, pod.Status?.Phase);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete completed pod {PodName}", pod.Metadata.Name);
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Bulk deleted {DeletedCount} completed pods for RunnerPool {Name}",
                    deletedCount, runnerPool.Metadata.Name);
            }
            else
            {
                _logger.LogDebug("No completed pods found to delete for RunnerPool {Name}", runnerPool.Metadata.Name);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete completed pods for RunnerPool {Name}", runnerPool.Metadata.Name);
            return Task.CompletedTask;
        }
    }

    private string DetermineImageForCapability(V1AzDORunnerEntity runnerPool, string? requiredCapability)
    {
        // If capability-aware mode is disabled, always use the default image
        if (!runnerPool.Spec.CapabilityAware)
        {
            return runnerPool.Spec.Image;
        }

        // If no specific capability is required, use the default image
        if (string.IsNullOrEmpty(requiredCapability))
        {
            return runnerPool.Spec.Image;
        }

        // Check if we have a specific image for the required capability
        if (runnerPool.Spec.CapabilityImages.TryGetValue(requiredCapability, out var capabilityImage))
        {
            _logger.LogInformation("Using capability-specific image {Image} for capability {Capability}",
                capabilityImage, requiredCapability);
            return capabilityImage;
        }

        // Fallback to default image if no specific capability image is configured
        _logger.LogWarning("No specific image configured for capability {Capability}, using default image {Image}",
            requiredCapability, runnerPool.Spec.Image);
        return runnerPool.Spec.Image;
    }
}