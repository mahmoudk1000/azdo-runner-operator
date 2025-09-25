using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using AzDORunner.Entities;

namespace AzDORunner.Webhooks;

[ValidationWebhook(typeof(V1AzDORunnerEntity))]
public class V1RunnerPoolValidationWebhook : ValidationWebhook<V1AzDORunnerEntity>
{
    public override ValidationResult Create(V1AzDORunnerEntity entity, bool dryRun)
    {
        // Only require absolutely essential fields
        if (string.IsNullOrWhiteSpace(entity.Spec.AzDoUrl))
            return Fail("AzDoUrl is required and cannot be empty", 422);

        if (string.IsNullOrWhiteSpace(entity.Spec.PatSecretName))
            return Fail("PatSecretName is required and cannot be empty", 422);

        // Smart validation - validate business logic for provided fields
        var result = ValidateBusinessLogic(entity);
        if (result != null)
            return result;

        // Validate ExtraEnv fields (only if provided)
        result = ValidateExtraEnv(entity.Spec.ExtraEnv);
        if (result != null)
            return result;

        // Validate PVC fields (smart conditional validation)
        result = ValidatePvcs(entity.Spec.Pvcs);
        if (result != null)
            return result;

        return Success();
    }

    public override ValidationResult Update(V1AzDORunnerEntity oldEntity, V1AzDORunnerEntity newEntity, bool dryRun)
    {
        // Only require absolutely essential fields
        if (string.IsNullOrWhiteSpace(newEntity.Spec.AzDoUrl))
            return Fail("AzDoUrl is required and cannot be empty");

        if (string.IsNullOrWhiteSpace(newEntity.Spec.PatSecretName))
            return Fail("PatSecretName is required and cannot be empty");

        // Smart validation - validate business logic for provided fields
        var result = ValidateBusinessLogic(newEntity);
        if (result != null)
            return result;

        // Validate ExtraEnv fields (only if provided)
        result = ValidateExtraEnv(newEntity.Spec.ExtraEnv);
        if (result != null)
            return result;

        // Validate PVC fields (smart conditional validation)
        result = ValidatePvcs(newEntity.Spec.Pvcs);
        if (result != null)
            return result;

        return Success();
    }

    private ValidationResult? ValidateBusinessLogic(V1AzDORunnerEntity entity)
    {
        // Validate AzDoUrl if provided (mutation webhook will set default if null)
        if (!string.IsNullOrWhiteSpace(entity.Spec.AzDoUrl))
        {
            if (!Uri.TryCreate(entity.Spec.AzDoUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http"))
                return Fail("AzDoUrl must be a valid HTTP or HTTPS URL", 422);
        }

        // Validate Image if provided (mutation webhook will set default if null)
        if (!string.IsNullOrWhiteSpace(entity.Spec.Image))
        {
            if (entity.Spec.Image.Contains(" ") || entity.Spec.Image.Contains("\t"))
                return Fail("Image cannot contain spaces or tabs", 422);
        }

        // Validate ImagePullPolicy if provided
        if (!string.IsNullOrWhiteSpace(entity.Spec.ImagePullPolicy))
        {
            var validImagePullPolicies = new[] { "Always", "IfNotPresent", "Never" };
            if (!validImagePullPolicies.Contains(entity.Spec.ImagePullPolicy))
                return Fail($"ImagePullPolicy must be one of: {string.Join(", ", validImagePullPolicies)}", 422);
        }

        // Validate numeric values (mutation webhook will set defaults if null)
        if (entity.Spec.TtlIdleSeconds < 0)
            return Fail("TtlIdleSeconds must be a non-negative value", 422);

        if (entity.Spec.MinAgents < 0)
            return Fail("MinAgents must be a non-negative value", 422);

        if (entity.Spec.MaxAgents < 1)
            return Fail("MaxAgents must be at least 1", 422);

        if (entity.Spec.MinAgents > entity.Spec.MaxAgents)
            return Fail($"MinAgents ({entity.Spec.MinAgents}) cannot be greater than MaxAgents ({entity.Spec.MaxAgents})", 422);

        return null;
    }

    private ValidationResult? ValidateExtraEnv(List<V1AzDORunnerEntity.ExtraEnvVar> extraEnv)
    {
        var envNames = new HashSet<string>();

        foreach (var envVar in extraEnv)
        {
            if (string.IsNullOrWhiteSpace(envVar.Name))
                return Fail("ExtraEnv entries must have a non-empty Name", 422);

            if (!envNames.Add(envVar.Name))
                return Fail($"Duplicate environment variable name '{envVar.Name}' found in ExtraEnv", 422);

            if (!IsValidEnvVarName(envVar.Name))
                return Fail($"Invalid environment variable name '{envVar.Name}'. Must contain only alphanumeric characters and underscores, and cannot start with a digit", 422);

            var hasValue = !string.IsNullOrEmpty(envVar.Value);
            var hasValueFrom = envVar.ValueFrom != null;

            if (!hasValue && !hasValueFrom)
                return Fail($"ExtraEnv entry '{envVar.Name}' must have either Value or ValueFrom specified", 422);

            if (hasValue && hasValueFrom)
                return Fail($"ExtraEnv entry '{envVar.Name}' cannot have both Value and ValueFrom specified", 422);
        }

        return null;
    }

    private ValidationResult? ValidatePvcs(List<V1AzDORunnerEntity.PvcSpec> pvcs)
    {
        var pvcNames = new HashSet<string>();
        var mountPaths = new HashSet<string>();

        foreach (var pvc in pvcs)
        {
            // Only require Name - this is the basic identifier
            if (string.IsNullOrWhiteSpace(pvc.Name))
                return Fail("PVC entries must have a non-empty Name", 422);

            if (!pvcNames.Add(pvc.Name))
                return Fail($"Duplicate PVC name '{pvc.Name}' found in Pvcs", 422);

            if (!IsValidKubernetesName(pvc.Name))
                return Fail($"Invalid PVC name '{pvc.Name}'. Must be a valid Kubernetes name (lowercase alphanumeric characters or '-', and must start and end with an alphanumeric character)", 422);

            // Smart conditional validation: If PVC is specified, MountPath is required
            if (string.IsNullOrWhiteSpace(pvc.MountPath))
                return Fail($"PVC '{pvc.Name}' must have a MountPath specified", 422);

            if (!mountPaths.Add(pvc.MountPath))
                return Fail($"Duplicate mount path '{pvc.MountPath}' found in Pvcs", 422);

            if (!pvc.MountPath.StartsWith("/"))
                return Fail($"PVC '{pvc.Name}' mount path '{pvc.MountPath}' must be an absolute path (start with '/')", 422);

            // Smart conditional validation: If CreatePvc is true, then Storage is required
            if (pvc.CreatePvc)
            {
                if (string.IsNullOrWhiteSpace(pvc.Storage))
                    return Fail($"PVC '{pvc.Name}' has CreatePvc=true but no Storage specified. Storage is required when creating a PVC", 422);

                if (!IsValidStorageQuantity(pvc.Storage))
                    return Fail($"PVC '{pvc.Name}' has invalid storage quantity '{pvc.Storage}'. Must use units: Gi, Mi, or Ki (e.g., '1Gi', '500Mi')", 422);
            }

            // Validate StorageClass format if provided (optional unless creating)
            if (!string.IsNullOrWhiteSpace(pvc.StorageClass) && !IsValidKubernetesName(pvc.StorageClass))
                return Fail($"PVC '{pvc.Name}' has invalid storage class name '{pvc.StorageClass}'. Must be a valid Kubernetes name", 422);

            // Business logic validation: Cannot delete what wasn't created
            if (!pvc.CreatePvc && pvc.DeleteWithAgent)
                return Fail($"PVC '{pvc.Name}' has CreatePvc=false but DeleteWithAgent=true. Cannot delete a PVC that wasn't created by this operator", 422);
        }

        return null;
    }

    private static bool IsValidEnvVarName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        // Environment variable names must contain only alphanumeric characters and underscores
        // and cannot start with a digit
        if (char.IsDigit(name[0]))
            return false;

        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool IsValidKubernetesName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 253)
            return false;

        // Kubernetes names must be lowercase and contain only alphanumeric characters or '-'
        // Must start and end with an alphanumeric character
        if (!char.IsLetterOrDigit(name[0]) || !char.IsLetterOrDigit(name[^1]))
            return false;

        return name.All(c => (char.IsLetterOrDigit(c) && char.IsLower(c)) || c == '-');
    }

    private static bool IsValidStorageQuantity(string quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
            return false;

        var validUnits = new[] { "Gi", "Mi", "Ki" };

        if (long.TryParse(quantity, out _))
            return true;

        // Check if it ends with a valid unit
        return validUnits.Any(unit => quantity.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                                     long.TryParse(quantity.Substring(0, quantity.Length - unit.Length), out _));
    }
}
