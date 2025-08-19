using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzDORunner.Services;

public class OperatorHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Simple health check - can be extended with more sophisticated checks
        return Task.FromResult(HealthCheckResult.Healthy("Operator is running"));
    }
}