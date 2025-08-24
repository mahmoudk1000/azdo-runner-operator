using KubeOps.Operator;
using AzDORunner.Services;
using KubeOps.KubernetesClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddKubernetesOperator()
    .AddCrdInstaller(c =>
    {
        c.OverwriteExisting = false;
        c.DeleteOnShutdown = false;
    })
    .RegisterComponents();

builder.Services.AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);
builder.Services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();
builder.Services.AddSingleton<IKubernetesPodService, KubernetesPodService>();

builder.Services.AddSingleton<AzDORunner.Services.WebhookCertificateManager>();
builder.Services.AddSingleton<AzDORunner.Services.WebhookCertificateBackgroundService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AzDORunner.Services.WebhookCertificateBackgroundService>());

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

builder.Services.AddHealthChecks()
    .AddCheck<OperatorHealthCheck>("operator");

var app = builder.Build();

app.UseRouting();

app.MapControllers();

await app.RunAsync();