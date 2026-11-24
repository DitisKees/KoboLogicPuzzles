namespace KoboNonogram.Server.Hashi;

public sealed class HashiInventoryWorker(HashiInventory inventory, HashiGenerationOptions options, TimeProvider time,
    ILogger<HashiInventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var interval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await inventory.RunCycleAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Hashi inventory replenishment failed. Retrying in {Seconds} seconds.",
                    options.PollIntervalSeconds);
            }
            await Task.Delay(interval, time, stoppingToken);
        }
    }
}
