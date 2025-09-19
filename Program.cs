using KubeOps.Operator;
using AzDORunner.Services;
using k8s;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddKubernetesOperator()
    .AddCrdInstaller(c =>
    {
        c.OverwriteExisting = false;
        c.DeleteOnShutdown = false;
    })
    .RegisterComponents();

// Register the official Kubernetes client
builder.Services.AddSingleton<IKubernetes>(provider =>
{
    var config = KubernetesClientConfiguration.InClusterConfig();
    return new Kubernetes(config);
});

builder.Services.AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// Register the official Kubernetes client
builder.Services.AddSingleton<IKubernetes>(provider =>
{
    var config = KubernetesClientConfiguration.InClusterConfig();
    return new Kubernetes(config);
});

builder.Services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();
builder.Services.AddSingleton<KubernetesPodService>();

builder.Services.AddSingleton<AzDORunner.Services.WebhookCertificateManager>();
builder.Services.AddSingleton<AzDORunner.Services.WebhookCertificateBackgroundService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AzDORunner.Services.WebhookCertificateBackgroundService>());

builder.Services.AddSingleton<AzureDevOpsPollingService>(provider =>
{
    var pollingService = new AzureDevOpsPollingService(
        provider.GetRequiredService<ILogger<AzureDevOpsPollingService>>(),
        provider.GetRequiredService<IAzureDevOpsService>(),
        provider.GetRequiredService<KubernetesPodService>(),
        provider.GetRequiredService<IKubernetes>());
    return pollingService;
});
builder.Services.AddHostedService(provider => provider.GetRequiredService<AzureDevOpsPollingService>());

var app = builder.Build();

app.UseRouting();

app.MapControllers();

await app.RunAsync();