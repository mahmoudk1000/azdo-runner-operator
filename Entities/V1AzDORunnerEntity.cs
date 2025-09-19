using k8s.Models;
using KubeOps.Abstractions.Entities;
using AzDORunner.Model.Domain;
using DataAnnotationsRequired = System.ComponentModel.DataAnnotations.RequiredAttribute;
using KubeOps.Abstractions.Entities.Attributes;
using System.ComponentModel.DataAnnotations;

namespace AzDORunner.Entities;

[KubernetesEntity(Group = "devops.opentools.mf", ApiVersion = "v1", Kind = "RunnerPool")]
[GenericAdditionalPrinterColumn(".status.connectionStatus", "Status", "string")]
[GenericAdditionalPrinterColumn(".spec.pool", "Pool", "string")]
[GenericAdditionalPrinterColumn(".status.organizationName", "Organization", "string")]
[GenericAdditionalPrinterColumn(".status.queuedJobs", "Queued", "integer")]
[GenericAdditionalPrinterColumn(".status.agentsSummary", "Agents", "string")]
[GenericAdditionalPrinterColumn(".status.runningAgents", "Running", "integer")]
public class V1AzDORunnerEntity : CustomKubernetesEntity<V1AzDORunnerEntity.V1AzDORunnerEntitySpec, V1AzDORunnerEntity.V1AzDORunnerEntityStatus>
{
    public class ExtraEnvVar
    {
        [DataAnnotationsRequired]
        public string Name { get; set; } = string.Empty;

        public string? Value { get; set; }

        public V1EnvVarSource? ValueFrom { get; set; }
    }

    public class PvcSpec
    {
        [DataAnnotationsRequired]
        public string Name { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string MountPath { get; set; } = string.Empty;

        public string StorageClass { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string Storage { get; set; } = string.Empty;

        public bool CreatePvc { get; set; } = true;

        public bool Optional { get; set; } = false;

        public bool DeleteWithAgent { get; set; } = false;
    }
    public class V1AzDORunnerEntitySpec : IValidatableObject
    {
        [DataAnnotationsRequired]
        public string AzDoUrl { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string Pool { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string PatSecretName { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string Image { get; set; } = string.Empty;

        public string ImagePullPolicy { get; set; } = "IfNotPresent";

        public bool CapabilityAware { get; set; } = false;

        public Dictionary<string, string> CapabilityImages { get; set; } = new();

        [Range(0, int.MaxValue, ErrorMessage = "TtlIdleSeconds must be a non-negative value")]
        public int TtlIdleSeconds { get; set; } = 0;

        [Range(0, int.MaxValue, ErrorMessage = "MinAgents must be a non-negative value")]
        public int MinAgents { get; set; } = 0;

        [Range(1, int.MaxValue, ErrorMessage = "MaxAgents must be at least 1")]
        public int MaxAgents { get; set; } = 10;

        public int PollIntervalSeconds { get; set; } = 5;

        public List<ExtraEnvVar> ExtraEnv { get; set; } = new();

        public List<PvcSpec> Pvcs { get; set; } = new();

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            var validImagePullPolicies = new[] { "Always", "IfNotPresent", "Never" };
            if (!string.IsNullOrEmpty(ImagePullPolicy) && !validImagePullPolicies.Contains(ImagePullPolicy))
            {
                yield return new ValidationResult(
                    $"ImagePullPolicy must be one of: {string.Join(", ", validImagePullPolicies)}",
                    new[] { nameof(ImagePullPolicy) });
            }

            if (PollIntervalSeconds < 5)
            {
                yield return new ValidationResult(
                    "PollIntervalSeconds must be at least 5 seconds",
                    new[] { nameof(PollIntervalSeconds) });
            }

            if (MinAgents > MaxAgents)
            {
                yield return new ValidationResult(
                    $"MinAgents ({MinAgents}) cannot be greater than MaxAgents ({MaxAgents})",
                    new[] { nameof(MinAgents), nameof(MaxAgents) });
            }

            if (MinAgents < 0)
            {
                yield return new ValidationResult(
                    "MinAgents cannot be negative",
                    new[] { nameof(MinAgents) });
            }

            if (MaxAgents < 1)
            {
                yield return new ValidationResult(
                    "MaxAgents must be at least 1",
                    new[] { nameof(MaxAgents) });
            }

            // Validate ExtraEnv
            foreach (var envVar in ExtraEnv)
            {
                if (string.IsNullOrWhiteSpace(envVar.Name))
                {
                    yield return new ValidationResult(
                        "ExtraEnv entries must have a non-empty Name",
                        new[] { nameof(ExtraEnv) });
                }

                if (string.IsNullOrEmpty(envVar.Value) && envVar.ValueFrom == null)
                {
                    yield return new ValidationResult(
                        $"ExtraEnv entry '{envVar.Name}' must have either Value or ValueFrom specified",
                        new[] { nameof(ExtraEnv) });
                }
            }

            // Validate PVCs
            foreach (var pvc in Pvcs)
            {
                if (string.IsNullOrWhiteSpace(pvc.Name))
                {
                    yield return new ValidationResult(
                        "PVC entries must have a non-empty Name",
                        new[] { nameof(Pvcs) });
                }

                if (string.IsNullOrWhiteSpace(pvc.MountPath))
                {
                    yield return new ValidationResult(
                        $"PVC '{pvc.Name}' must have a non-empty MountPath",
                        new[] { nameof(Pvcs) });
                }

                if (string.IsNullOrWhiteSpace(pvc.Storage))
                {
                    yield return new ValidationResult(
                        $"PVC '{pvc.Name}' must have a non-empty Storage value",
                        new[] { nameof(Pvcs) });
                }
            }
        }
    }

    public class V1AzDORunnerEntityStatus
    {
        public string ConnectionStatus { get; set; } = "Disconnected";
        public string OrganizationName { get; set; } = string.Empty;
        public string AgentsSummary { get; set; } = "0/0";
        public bool Active { get; set; } = false;
        public int QueuedJobs { get; set; } = 0;
        public int RunningAgents { get; set; } = 0;
        public int CurrentAgentIndex { get; set; } = 0;
        public DateTime? LastPolled { get; set; }
        public string? LastError { get; set; }
        public List<Agent> Agents { get; set; } = new();
        public List<StatusCondition> Conditions { get; set; } = new();
        public Dictionary<int, AgentIndexInfo> AgentIndexes { get; set; } = new();
    }

    public class StatusCondition
    {
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime LastTransitionTime { get; set; } = DateTime.UtcNow;
    }

    public class AgentIndexInfo
    {
        public string PodName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsMinAgent { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<string> PvcNames { get; set; } = new();
    }
}
