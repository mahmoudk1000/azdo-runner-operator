using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using AzDORunner.Entities;

namespace AzDORunner.Webhooks;

[ValidationWebhook(typeof(V1AzDORunnerEntity))]
public class V1RunnerPoolValidationWebhook : ValidationWebhook<V1AzDORunnerEntity>
{
    public override ValidationResult Create(V1AzDORunnerEntity entity, bool dryRun)
    {
        return ValidateEntity(entity);
    }

    public override ValidationResult Update(V1AzDORunnerEntity oldEntity, V1AzDORunnerEntity newEntity, bool dryRun)
    {
        return ValidateEntity(newEntity);
    }

    private ValidationResult ValidateEntity(V1AzDORunnerEntity entity)
    {
        var validationErrors = new List<string>();

        // Validate required fields
        if (string.IsNullOrWhiteSpace(entity.Spec.AzDoUrl))
        {
            validationErrors.Add("AzDoUrl is required and cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(entity.Spec.Pool))
        {
            validationErrors.Add("Pool is required and cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(entity.Spec.PatSecretName))
        {
            validationErrors.Add("PatSecretName is required and cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(entity.Spec.Image))
        {
            validationErrors.Add("Image is required and cannot be empty");
        }

        // Validate range constraints
        if (entity.Spec.TtlIdleSeconds < 0)
        {
            validationErrors.Add("TtlIdleSeconds must be a non-negative value");
        }

        if (entity.Spec.MinAgents < 0)
        {
            validationErrors.Add("MinAgents must be a non-negative value");
        }

        if (entity.Spec.MaxAgents < 1)
        {
            validationErrors.Add("MaxAgents must be at least 1");
        }

        // Validate business logic
        if (entity.Spec.MinAgents > entity.Spec.MaxAgents)
        {
            validationErrors.Add($"MinAgents ({entity.Spec.MinAgents}) cannot be greater than MaxAgents ({entity.Spec.MaxAgents})");
        }

        // Validate AzDoUrl format
        if (!string.IsNullOrWhiteSpace(entity.Spec.AzDoUrl))
        {
            if (!Uri.TryCreate(entity.Spec.AzDoUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                validationErrors.Add("AzDoUrl must be a valid HTTP or HTTPS URL");
            }
        }

        // Validate Image format (basic container image validation)
        if (!string.IsNullOrWhiteSpace(entity.Spec.Image))
        {
            if (entity.Spec.Image.Contains(" ") || entity.Spec.Image.Contains("\t"))
            {
                validationErrors.Add("Image cannot contain spaces or tabs");
            }
        }

        if (validationErrors.Any())
        {
            var errorMessage = string.Join("; ", validationErrors);
            return Fail(errorMessage, 422);
        }

        return Success();
    }
}
