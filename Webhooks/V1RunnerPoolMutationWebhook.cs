using KubeOps.Operator.Web.Webhooks.Admission.Mutation;
using AzDORunner.Entities;

namespace AzDORunner.Webhooks;

[MutationWebhook(typeof(V1AzDORunnerEntity))]
public class V1RunnerPoolMutationWebhook : MutationWebhook<V1AzDORunnerEntity>
{
    public override MutationResult<V1AzDORunnerEntity> Create(V1AzDORunnerEntity entity, bool dryRun)
    {
        bool modified = false;

        // Set default image if not provided
        if (string.IsNullOrWhiteSpace(entity.Spec.Image))
        {
            entity.Spec.Image = "mcr.microsoft.com/dotnet/aspnet:8.0";
            modified = true;
        }

        // Set default TtlIdleSeconds if not set
        if (entity.Spec.TtlIdleSeconds == 0)
        {
            entity.Spec.TtlIdleSeconds = 300; // 5 minutes default
            modified = true;
        }

        // Set default MaxAgents if not reasonable
        if (entity.Spec.MaxAgents <= 0)
        {
            entity.Spec.MaxAgents = 10;
            modified = true;
        }

        // Ensure MinAgents is not greater than MaxAgents
        if (entity.Spec.MinAgents > entity.Spec.MaxAgents)
        {
            entity.Spec.MinAgents = entity.Spec.MaxAgents;
            modified = true;
        }

        // Add a default label to identify operator-managed resources
        entity.Metadata.Labels ??= new Dictionary<string, string>();
        if (!entity.Metadata.Labels.ContainsKey("managed-by"))
        {
            entity.Metadata.Labels["managed-by"] = "azdo-runner-operator";
            modified = true;
        }

        return modified ? Modified(entity) : NoChanges();
    }

    public override MutationResult<V1AzDORunnerEntity> Update(V1AzDORunnerEntity oldEntity, V1AzDORunnerEntity newEntity, bool dryRun)
    {
        bool modified = false;

        // Ensure the managed-by label is preserved
        newEntity.Metadata.Labels ??= new Dictionary<string, string>();
        if (!newEntity.Metadata.Labels.ContainsKey("managed-by"))
        {
            newEntity.Metadata.Labels["managed-by"] = "azdo-runner-operator";
            modified = true;
        }

        // Ensure MinAgents is not greater than MaxAgents on updates
        if (newEntity.Spec.MinAgents > newEntity.Spec.MaxAgents)
        {
            newEntity.Spec.MinAgents = newEntity.Spec.MaxAgents;
            modified = true;
        }

        return modified ? Modified(newEntity) : NoChanges();
    }
}
