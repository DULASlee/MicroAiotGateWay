using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeviceSimulator;

public sealed class PlaceholderWorker(ILogger<PlaceholderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DeviceSimulator placeholder worker started.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("DeviceSimulator placeholder worker stopped.");
        }
    }
}
