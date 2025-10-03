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

        if (string.IsNullOrWhiteSpace(entity.Spec.PatSecretName))
            return Fail("PatSecretName is required and cannot be empty", 422);

        var result = ValidateBusinessLogic(entity);
        if (result != null)
            return result;

        result = ValidateExtraEnv(entity.Spec.ExtraEnv);
        if (result != null)
            return result;

        result = ValidatePvcs(entity.Spec.Pvcs);
        if (result != null)
            return result;

        result = ValidateCertTrustStore(entity.Spec.CertTrustStore);
        if (result != null)
            return result;

        return Success();
    }

    public override ValidationResult Update(V1AzDORunnerEntity oldEntity, V1AzDORunnerEntity newEntity, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(newEntity.Spec.AzDoUrl))
            return Fail("AzDoUrl is required and cannot be empty");

        if (string.IsNullOrWhiteSpace(newEntity.Spec.PatSecretName))
            return Fail("PatSecretName is required and cannot be empty");

        var result = ValidateBusinessLogic(newEntity);
        if (result != null)
            return result;

        result = ValidateExtraEnv(newEntity.Spec.ExtraEnv);
        if (result != null)
            return result;

        result = ValidatePvcs(newEntity.Spec.Pvcs);
        if (result != null)
            return result;

        result = ValidateCertTrustStore(newEntity.Spec.CertTrustStore);
        if (result != null)
            return result;

        return Success();
    }

    private ValidationResult? ValidateBusinessLogic(V1AzDORunnerEntity entity)
    {
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
            if (string.IsNullOrWhiteSpace(pvc.Name))
                return Fail("PVC entries must have a non-empty Name", 422);

            if (!pvcNames.Add(pvc.Name))
                return Fail($"Duplicate PVC name '{pvc.Name}' found in Pvcs", 422);

            if (!IsValidKubernetesName(pvc.Name))
                return Fail($"Invalid PVC name '{pvc.Name}'. Must be a valid Kubernetes name (RFC 1123)", 422);

            if (string.IsNullOrWhiteSpace(pvc.MountPath))
                return Fail($"PVC '{pvc.Name}' must have a MountPath specified", 422);

            if (!mountPaths.Add(pvc.MountPath))
                return Fail($"Duplicate mount path '{pvc.MountPath}' found in Pvcs", 422);

            if (!pvc.MountPath.StartsWith("/"))
                return Fail($"PVC '{pvc.Name}' mount path '{pvc.MountPath}' must be an absolute path (start with '/')", 422);

            if (pvc.CreatePvc)
            {
                if (string.IsNullOrWhiteSpace(pvc.Storage))
                    return Fail($"PVC '{pvc.Name}' has CreatePvc=true but no Storage specified. Storage is required when creating a PVC", 422);

                if (!IsValidStorageQuantity(pvc.Storage))
                    return Fail($"PVC '{pvc.Name}' has invalid storage quantity '{pvc.Storage}'. Must use units: Gi, Mi, or Ki (e.g., '1Gi', '500Mi')", 422);
            }

            if (!string.IsNullOrWhiteSpace(pvc.StorageClass) && !IsValidKubernetesName(pvc.StorageClass))
                return Fail($"PVC '{pvc.Name}' has invalid storage class name '{pvc.StorageClass}'. Must be a valid Kubernetes name", 422);

            if (!pvc.CreatePvc && pvc.DeleteWithAgent)
                return Fail($"PVC '{pvc.Name}' has CreatePvc=false but DeleteWithAgent=true. Cannot delete a PVC that wasn't created by this operator", 422);
        }

        return null;
    }

    private static bool IsValidEnvVarName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (char.IsDigit(name[0]))
            return false;

        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool IsValidKubernetesName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 63)
            return false;

        if (!char.IsLetter(name[0]) || !char.IsLower(name[0]))
            return false;

        if (!(char.IsLower(name[^1]) || char.IsDigit(name[^1])))
            return false;

        return name.All(c => (char.IsLower(c) && char.IsLetter(c)) || char.IsDigit(c) || c == '-');
    }

    private ValidationResult? ValidateCertTrustStore(List<V1AzDORunnerEntity.CertTrustStore> certTrustStore)
    {
        var secretNames = new HashSet<string>();

        foreach (var cert in certTrustStore)
        {
            if (string.IsNullOrWhiteSpace(cert.SecretName))
                return Fail("CertTrustStore entries must have a non-empty SecretName", 422);

            if (!secretNames.Add(cert.SecretName))
                return Fail($"Duplicate secret name '{cert.SecretName}' found in CertTrustStore", 422);

            if (!IsValidKubernetesName(cert.SecretName))
                return Fail($"Invalid secret name '{cert.SecretName}'. Must be a valid Kubernetes name (RFC 1123)", 422);
        }

        return null;
    }

    private static bool IsValidStorageQuantity(string quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
            return false;

        var validUnits = new[] { "Gi", "Mi", "Ki" };

        if (long.TryParse(quantity, out _))
            return true;

        return validUnits.Any(unit => quantity.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                                     long.TryParse(quantity.Substring(0, quantity.Length - unit.Length), out _));
    }
}
