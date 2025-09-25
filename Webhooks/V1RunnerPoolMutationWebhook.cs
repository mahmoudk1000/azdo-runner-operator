using KubeOps.Operator.Web.Webhooks.Admission.Mutation;
using AzDORunner.Entities;

namespace AzDORunner.Webhooks;

[MutationWebhook(typeof(V1AzDORunnerEntity))]
public class V1RunnerPoolMutationWebhook : MutationWebhook<V1AzDORunnerEntity>
{
    public override MutationResult<V1AzDORunnerEntity> Create(V1AzDORunnerEntity entity, bool dryRun)
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
            entity.Spec.Image = "mcr.microsoft.com/azure-pipelines/vsts-agent:ubuntu-20.04";
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

        return modified ? Modified(entity) : NoChanges();
    }

    public override MutationResult<V1AzDORunnerEntity> Update(V1AzDORunnerEntity oldEntity, V1AzDORunnerEntity newEntity, bool dryRun)
    {
        bool modified = false;

        newEntity.Metadata.Labels ??= new Dictionary<string, string>();
        if (!newEntity.Metadata.Labels.ContainsKey("managed-by"))
        {
            newEntity.Metadata.Labels["managed-by"] = "azdo-runner-operator";
            modified = true;
        }

        if (string.IsNullOrWhiteSpace(newEntity.Spec.Pool))
        {
            newEntity.Spec.Pool = "default";
            modified = true;
        }

        if (string.IsNullOrWhiteSpace(newEntity.Spec.Image))
        {
            newEntity.Spec.Image = "mcr.microsoft.com/azure-pipelines/vsts-agent:ubuntu-20.04";
            modified = true;
        }

        if (string.IsNullOrWhiteSpace(newEntity.Spec.ImagePullPolicy))
        {
            newEntity.Spec.ImagePullPolicy = "IfNotPresent";
            modified = true;
        }

        if (newEntity.Spec.TtlIdleSeconds == 0)
        {
            newEntity.Spec.TtlIdleSeconds = 300; // 5 minutes
            modified = true;
        }

        if (newEntity.Spec.MinAgents == 0 && newEntity.Spec.MaxAgents == 0)
        {
            newEntity.Spec.MinAgents = 0;
            newEntity.Spec.MaxAgents = 5;
            modified = true;
        }
        else if (newEntity.Spec.MaxAgents == 0)
        {
            newEntity.Spec.MaxAgents = Math.Max(1, newEntity.Spec.MinAgents);
            modified = true;
        }

        if (newEntity.Spec.ExtraEnv == null)
        {
            newEntity.Spec.ExtraEnv = new List<V1AzDORunnerEntity.ExtraEnvVar>();
            modified = true;
        }

        if (newEntity.Spec.Pvcs == null)
        {
            newEntity.Spec.Pvcs = new List<V1AzDORunnerEntity.PvcSpec>();
            modified = true;
        }

        foreach (var pvc in newEntity.Spec.Pvcs)
        {
            if (pvc.CreatePvc && string.IsNullOrWhiteSpace(pvc.Storage))
            {
                pvc.Storage = "1Gi";
                modified = true;
            }
        }

        return modified ? Modified(newEntity) : NoChanges();
    }
}
