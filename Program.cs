using KubeOps.Operator;
using AzDORunner.Services;
using k8s;

var builder = WebApplication.CreateBuilder(args);
var kubernetesClientConfig = KubernetesClientConfiguration.BuildDefaultConfig();

builder.Services
    .AddKubernetesOperator()
    .AddCrdInstaller(c =>
    {
        c.OverwriteExisting = false;
        c.DeleteOnShutdown = false;
    })
    .RegisterComponents();

builder.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(kubernetesClientConfig));

builder.Services.AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

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
