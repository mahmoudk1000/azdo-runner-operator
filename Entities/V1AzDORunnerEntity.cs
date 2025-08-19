using k8s.Models;
using KubeOps.Abstractions.Entities;
using AzDORunner.Model.Domain;
using DataAnnotationsRequired = System.ComponentModel.DataAnnotations.RequiredAttribute;
using KubeOps.Abstractions.Entities.Attributes;

namespace AzDORunner.Entities;

[KubernetesEntity(Group = "devops.atos.net", ApiVersion = "v1alpha", Kind = "RunnerPool")]
[GenericAdditionalPrinterColumn(".status.connectionStatus", "Status", "string")]
[GenericAdditionalPrinterColumn(".status.organizationName", "Organization", "string")]
[GenericAdditionalPrinterColumn(".status.queuedJobs", "Queued", "integer")]
[GenericAdditionalPrinterColumn(".status.agentsSummary", "Agents", "string")]
[GenericAdditionalPrinterColumn(".status.runningAgents", "Running", "integer")]
public class V1RunnerPoolEntity : CustomKubernetesEntity<V1RunnerPoolEntity.V1RunnerPoolEntitySpec, V1RunnerPoolEntity.V1RunnerPoolEntityStatus>
{
    public class V1RunnerPoolEntitySpec
    {
        [DataAnnotationsRequired]
        public string AzDoUrl { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string Pool { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string PatSecretName { get; set; } = string.Empty;

        [DataAnnotationsRequired]
        public string Image { get; set; } = string.Empty;

        public int TtlSecondsAfterFinished { get; set; } = 3600;
        public int TtlIdleSeconds { get; set; } = 300;
        public int MaxAgents { get; set; } = 10;
    }

    public class V1RunnerPoolEntityStatus
    {
        public string ConnectionStatus { get; set; } = "Unknown";
        public string OrganizationName { get; set; } = string.Empty;
        public string AgentsSummary { get; set; } = "0/0";
        public bool Active { get; set; } = false;
        public int QueuedJobs { get; set; } = 0;
        public int RunningAgents { get; set; } = 0;
        public DateTime? LastPolled { get; set; }
        public string? LastError { get; set; }
        public List<Agent> Agents { get; set; } = new();
        public List<StatusCondition> Conditions { get; set; } = new();
    }

    public class StatusCondition
    {
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime LastTransitionTime { get; set; } = DateTime.UtcNow;
    }
}
