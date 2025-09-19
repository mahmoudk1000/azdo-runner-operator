﻿using k8s.Models;
using AzDORunner.Entities;
using KubeOps.KubernetesClient;
using static AzDORunner.Entities.V1AzDORunnerEntity;

namespace AzDORunner.Services;

public interface IKubernetesPodService
{
    Task<V1Pod> CreateAgentPodAsync(V1AzDORunnerEntity runnerPool, string pat, int agentIndex, bool isMinAgent = false, string? requiredCapability = null, Dictionary<string, string>? extraLabels = null);
    Task<List<V1Pod>> GetActivePodsAsync(V1AzDORunnerEntity runnerPool);
    Task<List<V1Pod>> GetAllRunnerPodsAsync(V1AzDORunnerEntity runnerPool);
    Task<List<V1Pod>> GetMinAgentPodsAsync(V1AzDORunnerEntity runnerPool);
    Task DeletePodAsync(string podName, string namespaceName);
    Task DeleteAgentAsync(V1AzDORunnerEntity runnerPool, int agentIndex);
    Task DeleteCompletedPodsAsync(V1AzDORunnerEntity runnerPool);
    Task<V1PersistentVolumeClaim?> CreatePvcAsync(V1AzDORunnerEntity runnerPool, PvcSpec pvcSpec, int agentIndex);
    Task<V1PersistentVolumeClaim?> TryGetExistingPvcAsync(V1AzDORunnerEntity runnerPool, int agentIndex, PvcSpec pvcSpec);
    Task DeletePvcAsync(string pvcName, string namespaceName);
    int GetNextAvailableAgentIndex(V1AzDORunnerEntity runnerPool);
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

    public async Task<V1Pod> CreateAgentPodAsync(V1AzDORunnerEntity runnerPool, string pat, int agentIndex, bool isMinAgent = false, string? requiredCapability = null, Dictionary<string, string>? extraLabels = null)
    {
        var podName = $"{runnerPool.Metadata.Name}-agent-{agentIndex}";
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        // Determine which image to use based on capability requirements
        var imageToUse = DetermineImageForCapability(runnerPool, requiredCapability);
        var capabilityLabel = requiredCapability ?? "base";

        var labels = new Dictionary<string, string>
        {
            ["app"] = "azdo-runner",
            ["runner-pool"] = runnerPool.Metadata.Name,
            ["managed-by"] = "azdo-runner-operator",
            ["min-agent"] = isMinAgent.ToString().ToLower(),
            ["capability"] = capabilityLabel,
            ["capability-aware"] = runnerPool.Spec.CapabilityAware.ToString().ToLower()
        };
        if (extraLabels != null)
        {
            foreach (var kv in extraLabels)
            {
                labels[kv.Key] = kv.Value;
            }
        }
        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = namespaceName,
                Labels = labels,
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
                        }.Concat(
                            runnerPool.Spec.ExtraEnv.Select(env => new V1EnvVar
                            {
                                Name = env.Name,
                                Value = env.Value,
                                ValueFrom = env.ValueFrom
                            })
                        ).ToList(),
                        VolumeMounts = runnerPool.Spec.Pvcs.Select(pvc => new V1VolumeMount
                        {
                            Name = $"{runnerPool.Metadata.Name}-agent-{agentIndex}-{pvc.Name}",
                            MountPath = pvc.MountPath
                        }).ToList(),
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
                },
                Volumes = runnerPool.Spec.Pvcs.Select(pvc => new V1Volume
                {
                    Name = $"{runnerPool.Metadata.Name}-agent-{agentIndex}-{pvc.Name}",
                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                    {
                        ClaimName = $"{runnerPool.Metadata.Name}-agent-{agentIndex}-{pvc.Name}"
                    }
                }).ToList()
            }
        };

        try
        {
            // Create or reuse PVCs first if needed
            var createdPvcNames = new List<string>();
            foreach (var pvcSpec in runnerPool.Spec.Pvcs)
            {
                var expectedPvcName = $"{runnerPool.Metadata.Name}-agent-{agentIndex}-{pvcSpec.Name}";

                if (pvcSpec.CreatePvc)
                {
                    try
                    {
                        // Check if PVC already exists (for reuse case)
                        var existingPvc = await TryGetExistingPvcAsync(runnerPool, agentIndex, pvcSpec);
                        if (existingPvc != null)
                        {
                            _logger.LogInformation("Reusing existing PVC {PvcName} for agent {PodName}", existingPvc.Metadata.Name, podName);
                            createdPvcNames.Add(existingPvc.Metadata.Name);
                        }
                        else
                        {
                            var pvc = await CreatePvcAsync(runnerPool, pvcSpec, agentIndex);
                            if (pvc != null)
                            {
                                createdPvcNames.Add(pvc.Metadata.Name);
                                _logger.LogInformation("Created new PVC {PvcName} for agent {PodName}", pvc.Metadata.Name, podName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!pvcSpec.Optional)
                        {
                            _logger.LogError(ex, "Failed to create required PVC {PvcName} for agent {PodName}", expectedPvcName, podName);
                            throw;
                        }
                        _logger.LogWarning(ex, "Failed to create optional PVC {PvcName} for agent {PodName}", expectedPvcName, podName);
                    }
                }
                else
                {
                    // For existing PVCs, just check if they exist
                    var existingPvc = await TryGetExistingPvcAsync(runnerPool, agentIndex, pvcSpec);
                    if (existingPvc != null)
                    {
                        _logger.LogInformation("Using existing PVC {PvcName} for agent {PodName}", existingPvc.Metadata.Name, podName);
                        createdPvcNames.Add(existingPvc.Metadata.Name);
                    }
                    else if (!pvcSpec.Optional)
                    {
                        throw new InvalidOperationException($"Required PVC {expectedPvcName} does not exist and CreatePvc is false");
                    }
                    else
                    {
                        _logger.LogWarning("Optional PVC {PvcName} does not exist and CreatePvc is false, skipping", expectedPvcName);
                    }
                }
            }

            var createdPod = _kubernetesClient.Create(pod);
            var agentType = isMinAgent ? "minimum" : "regular";
            var mode = (!isMinAgent && runnerPool.Spec.TtlIdleSeconds == 0) ? "one-time (--once)" : "continuous";
            _logger.LogInformation("Created {AgentType} agent pod {PodName} in namespace {Namespace} (Mode: {Mode}, TtlIdleSeconds: {TtlIdleSeconds}, Capability: {Capability}, Image: {Image}, ImagePullPolicy: {ImagePullPolicy})",
                agentType, podName, namespaceName, mode, runnerPool.Spec.TtlIdleSeconds, capabilityLabel, imageToUse, runnerPool.Spec.ImagePullPolicy);
            return createdPod;
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

    public async Task DeleteAgentAsync(V1AzDORunnerEntity runnerPool, int agentIndex)
    {
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";
        var podName = $"{runnerPool.Metadata.Name}-agent-{agentIndex}";

        try
        {
            // Delete the pod
            await DeletePodAsync(podName, namespaceName);

            // Delete PVCs if they should be deleted with the agent
            foreach (var pvcSpec in runnerPool.Spec.Pvcs.Where(p => p.DeleteWithAgent))
            {
                var pvcName = $"{runnerPool.Metadata.Name}-agent-{agentIndex}-{pvcSpec.Name}";
                await DeletePvcAsync(pvcName, namespaceName);
            }

            _logger.LogInformation("Deleted agent {AgentIndex} and associated resources for RunnerPool {Name}",
                agentIndex, runnerPool.Metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete agent {AgentIndex} for RunnerPool {Name}",
                agentIndex, runnerPool.Metadata.Name);
            throw;
        }
    }

    public Task<V1PersistentVolumeClaim?> TryGetExistingPvcAsync(V1AzDORunnerEntity runnerPool, int agentIndex, PvcSpec pvcSpec)
    {
        var pvcName = $"{runnerPool.Metadata.Name}-agent-{agentIndex}-{pvcSpec.Name}";
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        try
        {
            var existingPvc = _kubernetesClient.Get<V1PersistentVolumeClaim>(pvcName, namespaceName);

            // Verify that this PVC belongs to our runner pool and has the correct labels
            if (existingPvc?.Metadata?.Labels != null &&
                existingPvc.Metadata.Labels.ContainsKey("runner-pool") &&
                existingPvc.Metadata.Labels["runner-pool"] == runnerPool.Metadata.Name &&
                existingPvc.Metadata.Labels.ContainsKey("agent-index") &&
                existingPvc.Metadata.Labels["agent-index"] == agentIndex.ToString() &&
                existingPvc.Metadata.Labels.ContainsKey("pvc-name") &&
                existingPvc.Metadata.Labels["pvc-name"] == pvcSpec.Name)
            {
                _logger.LogDebug("Found existing PVC {PvcName} for agent index {AgentIndex}", pvcName, agentIndex);
                return Task.FromResult<V1PersistentVolumeClaim?>(existingPvc);
            }
            else
            {
                _logger.LogDebug("PVC {PvcName} exists but has incorrect labels, not reusing", pvcName);
                return Task.FromResult<V1PersistentVolumeClaim?>(null);
            }
        }
        catch (Exception ex)
        {
            // PVC doesn't exist or we can't access it
            _logger.LogDebug(ex, "PVC {PvcName} not found or not accessible", pvcName);
            return Task.FromResult<V1PersistentVolumeClaim?>(null);
        }
    }

    public Task<V1PersistentVolumeClaim?> CreatePvcAsync(V1AzDORunnerEntity runnerPool, PvcSpec pvcSpec, int agentIndex)
    {
        var pvcName = $"{runnerPool.Metadata.Name}-agent-{agentIndex}-{pvcSpec.Name}";
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        var pvc = new V1PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = new V1ObjectMeta
            {
                Name = pvcName,
                NamespaceProperty = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = "azdo-runner",
                    ["runner-pool"] = runnerPool.Metadata.Name,
                    ["managed-by"] = "azdo-runner-operator",
                    ["agent-index"] = agentIndex.ToString(),
                    ["pvc-name"] = pvcSpec.Name
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
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = new List<string> { "ReadWriteOnce" },
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["storage"] = new(pvcSpec.Storage)
                    }
                },
                StorageClassName = string.IsNullOrEmpty(pvcSpec.StorageClass) ? null : pvcSpec.StorageClass
            }
        };

        try
        {
            var createdPvc = _kubernetesClient.Create(pvc);
            _logger.LogInformation("Created PVC {PvcName} with storage {Storage} for agent {AgentIndex}",
                pvcName, pvcSpec.Storage, agentIndex);
            return Task.FromResult<V1PersistentVolumeClaim?>(createdPvc);
        }
        catch (Exception ex) when (ex.Message.Contains("already exists"))
        {
            // PVC already exists, try to get it instead
            _logger.LogWarning("PVC {PvcName} already exists, attempting to reuse", pvcName);
            try
            {
                var existingPvc = _kubernetesClient.Get<V1PersistentVolumeClaim>(pvcName, namespaceName);
                return Task.FromResult(existingPvc);
            }
            catch (Exception getEx)
            {
                _logger.LogError(getEx, "Failed to get existing PVC {PvcName}", pvcName);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create PVC {PvcName}", pvcName);
            throw;
        }
    }

    public Task DeletePvcAsync(string pvcName, string namespaceName)
    {
        try
        {
            _kubernetesClient.Delete<V1PersistentVolumeClaim>(pvcName, namespaceName);
            _logger.LogInformation("Deleted PVC {PvcName} in namespace {Namespace}", pvcName, namespaceName);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete PVC {PvcName}", pvcName);
            return Task.CompletedTask;
        }
    }

    public int GetNextAvailableAgentIndex(V1AzDORunnerEntity runnerPool)
    {
        var namespaceName = runnerPool.Metadata.NamespaceProperty ?? "default";

        try
        {
            var allPods = _kubernetesClient.List<V1Pod>(namespaceName);
            var runnerPods = allPods.Where(pod =>
                pod.Metadata.Labels?.ContainsKey("runner-pool") == true &&
                pod.Metadata.Labels["runner-pool"] == runnerPool.Metadata.Name).ToList();

            var usedIndexes = new HashSet<int>();
            foreach (var pod in runnerPods)
            {
                if (pod.Metadata.Name.StartsWith($"{runnerPool.Metadata.Name}-agent-"))
                {
                    var indexStr = pod.Metadata.Name.Substring($"{runnerPool.Metadata.Name}-agent-".Length);
                    if (int.TryParse(indexStr, out var index))
                    {
                        usedIndexes.Add(index);
                    }
                }
            }

            // Find the first available index starting from 0
            for (int i = 0; i < runnerPool.Spec.MaxAgents; i++)
            {
                if (!usedIndexes.Contains(i))
                {
                    return i;
                }
            }

            // If we reach here, we're at max capacity
            throw new InvalidOperationException($"No available agent indexes. Max agents: {runnerPool.Spec.MaxAgents}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get next available agent index for RunnerPool {Name}", runnerPool.Metadata.Name);
            throw;
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