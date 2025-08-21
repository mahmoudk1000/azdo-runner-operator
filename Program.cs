using KubeOps.Operator;
using AzDORunner.Services;
using KubeOps.KubernetesClient;

var builder = WebApplication.CreateBuilder(args);

// KubeOps automatically registers IKubernetesClient when adding the operator
builder.Services
    .AddKubernetesOperator()
    .AddCrdInstaller(c =>
    {
        c.OverwriteExisting = true;
        c.DeleteOnShutdown = false;
    })
    .RegisterComponents();

// Add standard ASP.NET Core MVC services
builder.Services.AddControllers();
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

var app = builder.Build();

// Enable ASP.NET Core routing
app.UseRouting();

// Map controllers (including webhook endpoints)
app.MapControllers();

// Run the operator and web host
await app.RunAsync();