using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using AzDORunner.Entities;

namespace AzDORunner.Webhooks;

[ValidationWebhook(typeof(V1AzDORunnerEntity))]
public class V1RunnerPoolValidationWebhook : ValidationWebhook<V1AzDORunnerEntity>
{
    public override ValidationResult Create(V1AzDORunnerEntity entity, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(entity.Spec.AzDoUrl))
            return Fail("AzDoUrl is required and cannot be empty", 422);

        if (string.IsNullOrWhiteSpace(entity.Spec.Pool))
            return Fail("Pool is required and cannot be empty", 422);

        if (string.IsNullOrWhiteSpace(entity.Spec.PatSecretName))
            return Fail("PatSecretName is required and cannot be empty", 422);

        if (string.IsNullOrWhiteSpace(entity.Spec.Image))
            return Fail("Image is required and cannot be empty", 422);

        if (entity.Spec.TtlIdleSeconds < 0)
            return Fail("TtlIdleSeconds must be a non-negative value", 422);

        if (entity.Spec.MinAgents < 0)
            return Fail("MinAgents must be a non-negative value", 422);

        if (entity.Spec.MaxAgents < 1)
            return Fail("MaxAgents must be at least 1", 422);

        if (entity.Spec.MinAgents > entity.Spec.MaxAgents)
            return Fail($"MinAgents ({entity.Spec.MinAgents}) cannot be greater than MaxAgents ({entity.Spec.MaxAgents})", 422);

        if (!string.IsNullOrWhiteSpace(entity.Spec.AzDoUrl))
        {
            if (!Uri.TryCreate(entity.Spec.AzDoUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http"))
                return Fail("AzDoUrl must be a valid HTTP or HTTPS URL", 422);
        }

        if (!string.IsNullOrWhiteSpace(entity.Spec.Image))
        {
            if (entity.Spec.Image.Contains(" ") || entity.Spec.Image.Contains("\t"))
                return Fail("Image cannot contain spaces or tabs", 422);
        }

        if (!string.IsNullOrWhiteSpace(entity.Spec.ImagePullPolicy))
        {
            var validImagePullPolicies = new[] { "Always", "IfNotPresent", "Never" };
            if (!validImagePullPolicies.Contains(entity.Spec.ImagePullPolicy))
                return Fail($"ImagePullPolicy must be one of: {string.Join(", ", validImagePullPolicies)}", 422);
        }

        return Success();
    }

    public override ValidationResult Update(V1AzDORunnerEntity oldEntity, V1AzDORunnerEntity newEntity, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(newEntity.Spec.AzDoUrl))
            return Fail("AzDoUrl is required and cannot be empty");

        if (string.IsNullOrWhiteSpace(newEntity.Spec.Pool))
            return Fail("Pool is required and cannot be empty");

        if (string.IsNullOrWhiteSpace(newEntity.Spec.PatSecretName))
            return Fail("PatSecretName is required and cannot be empty");

        if (string.IsNullOrWhiteSpace(newEntity.Spec.Image))
            return Fail("Image is required and cannot be empty");

        if (newEntity.Spec.TtlIdleSeconds < 0)
            return Fail("TtlIdleSeconds must be a non-negative value");

        if (newEntity.Spec.MinAgents < 0)
            return Fail("MinAgents must be a non-negative value");

        if (newEntity.Spec.MaxAgents < 1)
            return Fail("MaxAgents must be at least 1");

        if (newEntity.Spec.MinAgents > newEntity.Spec.MaxAgents)
            return Fail($"MinAgents ({newEntity.Spec.MinAgents}) cannot be greater than MaxAgents ({newEntity.Spec.MaxAgents})");

        if (!string.IsNullOrWhiteSpace(newEntity.Spec.AzDoUrl))
        {
            if (!Uri.TryCreate(newEntity.Spec.AzDoUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http"))
                return Fail("AzDoUrl must be a valid HTTP or HTTPS URL");
        }

        if (!string.IsNullOrWhiteSpace(newEntity.Spec.Image))
        {
            if (newEntity.Spec.Image.Contains(" ") || newEntity.Spec.Image.Contains("\t"))
                return Fail("Image cannot contain spaces or tabs");
        }

        if (!string.IsNullOrWhiteSpace(newEntity.Spec.ImagePullPolicy))
        {
            var validImagePullPolicies = new[] { "Always", "IfNotPresent", "Never" };
            if (!validImagePullPolicies.Contains(newEntity.Spec.ImagePullPolicy))
                return Fail($"ImagePullPolicy must be one of: {string.Join(", ", validImagePullPolicies)}");
        }

        return Success();
    }
}
