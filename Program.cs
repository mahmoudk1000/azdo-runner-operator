using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection;
using AzDORunner.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KubeOps.KubernetesClient;

var builder = Host.CreateApplicationBuilder(args);

// Add services to the container
// KubeOps automatically registers IKubernetesClient when adding the operator
builder.Services
    .AddKubernetesOperator()
#if DEBUG
    .AddCrdInstaller(c =>
    {
        // Careful, this can be very destructive.
        // c.OverwriteExisting = true;
        // c.DeleteOnShutdown = true;
    })
#endif
    .RegisterComponents();

// Register custom services
builder.Services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();
builder.Services.AddSingleton<IKubernetesPodService, KubernetesPodService>();
builder.Services.AddSingleton<AzureDevOpsPollingService>(provider =>
{
    var pollingService = new AzureDevOpsPollingService(
        provider.GetRequiredService<ILogger<AzureDevOpsPollingService>>(),
        provider.GetRequiredService<IAzureDevOpsService>(),
        provider.GetRequiredService<IKubernetesPodService>(),
        provider.GetRequiredService<IKubernetesClient>());
    return pollingService;
});
builder.Services.AddHostedService(provider => provider.GetRequiredService<AzureDevOpsPollingService>());

// Add health checks
builder.Services.AddHealthChecks()
    .AddCheck<OperatorHealthCheck>("operator");

var host = builder.Build();

// Run as console application (no web server, no browser)
await host.RunAsync();
