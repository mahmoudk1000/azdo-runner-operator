namespace AzDORunner.Services
{
    public class WebhookCertificateBackgroundService : BackgroundService
    {
        private readonly WebhookCertificateManager _manager;
        private readonly ILogger<WebhookCertificateBackgroundService> _logger;

        public WebhookCertificateBackgroundService(WebhookCertificateManager manager, ILogger<WebhookCertificateBackgroundService> logger)
        {
            _manager = manager;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting WebhookCertificateManager background reconciliation loop.");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _manager.Reconcile();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during webhook certificate reconciliation");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // Reconcile every 10 seconds
            }
        }
    }
}
