using KubeOps.Operator.Web.Webhooks.Admission.Mutation;
using AzDORunner.Entities;

namespace AzDORunner.Webhooks;

[MutationWebhook(typeof(V1AzDORunnerEntity))]
public class V1RunnerPoolMutationWebhook : MutationWebhook<V1AzDORunnerEntity>
{
    private bool MutateEntity(V1AzDORunnerEntity entity)
    {
        bool modified = false;

        entity.Metadata.Labels ??= new Dictionary<string, string>();
        if (!entity.Metadata.Labels.ContainsKey("managed-by"))
        {
            entity.Metadata.Labels["managed-by"] = "azdo-runner-operator";
            modified = true;
        }

        if (string.IsNullOrWhiteSpace(entity.Spec.Pool))
        {
            entity.Spec.Pool = "default";
            modified = true;
        }

        if (string.IsNullOrWhiteSpace(entity.Spec.Image))
        {
            entity.Spec.Image = "ghcr.io/mahmoudk1000/azdo-runner-operator/agent:latest";
            modified = true;
        }

        if (string.IsNullOrWhiteSpace(entity.Spec.ImagePullPolicy))
        {
            entity.Spec.ImagePullPolicy = "IfNotPresent";
            modified = true;
        }

        if (entity.Spec.TtlIdleSeconds == 0)
        {
            entity.Spec.TtlIdleSeconds = 300; // 5 minutes
            modified = true;
        }

        if (entity.Spec.MinAgents == 0 && entity.Spec.MaxAgents == 0)
        {
            entity.Spec.MinAgents = 0;
            entity.Spec.MaxAgents = 5;
            modified = true;
        }
        else if (entity.Spec.MaxAgents == 0)
        {
            entity.Spec.MaxAgents = Math.Max(1, entity.Spec.MinAgents);
            modified = true;
        }

        if (entity.Spec.ExtraEnv == null)
        {
            entity.Spec.ExtraEnv = new List<V1AzDORunnerEntity.ExtraEnvVar>();
            modified = true;
        }

        if (entity.Spec.Pvcs == null)
        {
            entity.Spec.Pvcs = new List<V1AzDORunnerEntity.PvcSpec>();
            modified = true;
        }

        foreach (var pvc in entity.Spec.Pvcs)
        {
            if (pvc.CreatePvc && string.IsNullOrWhiteSpace(pvc.Storage))
            {
                pvc.Storage = "1Gi";
                modified = true;
            }
        }

        return modified;
    }

    public override MutationResult<V1AzDORunnerEntity> Create(V1AzDORunnerEntity entity, bool dryRun)
    {
        bool modified = MutateEntity(entity);
        return modified ? Modified(entity) : NoChanges();
    }

    public override MutationResult<V1AzDORunnerEntity> Update(V1AzDORunnerEntity oldEntity, V1AzDORunnerEntity newEntity, bool dryRun)
    {
        bool modified = MutateEntity(newEntity);
        return modified ? Modified(newEntity) : NoChanges();
    }
}

