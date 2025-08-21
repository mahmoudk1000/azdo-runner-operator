using KubeOps.Operator.Web.Webhooks.Admission.Mutation;
using AzDORunner.Entities;

namespace AzDORunner.Webhooks;

[MutationWebhook(typeof(V1AzDORunnerEntity))]
public class V1RunnerPoolMutationWebhook : MutationWebhook<V1AzDORunnerEntity>
{
    public override MutationResult<V1AzDORunnerEntity> Create(V1AzDORunnerEntity entity, bool dryRun)
    {
        bool modified = false;

        // Add a default label to identify operator-managed resources
        entity.Metadata.Labels ??= new Dictionary<string, string>();
        if (!entity.Metadata.Labels.ContainsKey("managed-by"))
        {
            entity.Metadata.Labels["managed-by"] = "azdo-runner-operator";
            modified = true;
        }

        // Set default ImagePullPolicy if not specified
        if (string.IsNullOrWhiteSpace(entity.Spec.ImagePullPolicy))
        {
            entity.Spec.ImagePullPolicy = "IfNotPresent";
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

        // Set default ImagePullPolicy if not specified
        if (string.IsNullOrWhiteSpace(newEntity.Spec.ImagePullPolicy))
        {
            newEntity.Spec.ImagePullPolicy = "IfNotPresent";
            modified = true;
        }

        return modified ? Modified(newEntity) : NoChanges();
    }
}
