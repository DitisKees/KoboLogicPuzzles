namespace KoboNonogram.Server;

public sealed class CatalogRefreshWorker(
    PuzzleStore store, PuzzleLibrary library, TimeProvider time, ILogger<CatalogRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var previous = store.GetCatalogRefreshState();
                var delay = previous.CompletedAt is { } completedAt
                    ? Interval - (time.GetUtcNow() - completedAt)
                    : TimeSpan.Zero;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay < Interval ? delay : Interval, time, stoppingToken);
                    continue;
                }

                var count = await library.RefreshAsync(previous, stoppingToken);
                if (count is not null)
                    logger.LogInformation("WebPBN catalog refreshed with {PuzzleCount} puzzles.", count);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "WebPBN refresh failed. The previous catalog remains available. Retrying in one hour.");
                await Task.Delay(RetryDelay, time, stoppingToken);
            }
        }
    }
}
