using FactoryQuoteApi.Services;

namespace FactoryQuoteApi.Services;

public class TelegramOutboxBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramOutboxBackgroundService> _logger;

    public TelegramOutboxBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<TelegramOutboxBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telegram Outbox Background Service started.");

        // Initial short delay on startup
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var deliveryService = scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>();
                int processed = await deliveryService.ProcessPendingOutboxAsync(cancellationToken: stoppingToken);
                if (processed > 0)
                {
                    _logger.LogInformation("Telegram outbox worker processed {Count} deliveries.", processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error occurred in Telegram outbox background worker.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Telegram Outbox Background Service stopped.");
    }
}
