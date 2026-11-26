using System.Security.Cryptography;

namespace KoboLogicPuzzles.Server.Slitherlink;

internal sealed class SlitherlinkInventory(PuzzleStore store, LoopyGenerator generator, SlitherlinkOptions options)
{
    public object GetStatus()
    {
        var counts = store.CountUnstartedSlitherlink();
        return new
        {
            options.Enabled,
            options.TimeoutSeconds,
            Sources = store.ListSlitherlinkSources(),
            Stock = options.Buckets().Select(bucket => new
            {
                bucket.Size,
                bucket.Tiling,
                bucket.Difficulty,
                Unstarted = counts.GetValueOrDefault(bucket),
                Target = options.TargetPerCombination,
            }).ToArray(),
        };
    }

    internal IEnumerable<SlitherlinkBucket> Shortfalls()
    {
        var counts = store.CountUnstartedSlitherlink();
        return options.Buckets().Where(bucket => counts.GetValueOrDefault(bucket) < options.TargetPerCombination)
            .OrderBy(bucket => counts.GetValueOrDefault(bucket));
    }

    internal async Task<(string Status, string? Id)> GenerateAsync(SlitherlinkBucket bucket, string seed,
        bool diagnostic, CancellationToken token)
    {
        using var generation = store.TryLockSlitherlinkGeneration();
        if (generation is null)
            return ("busy", null);
        if (!diagnostic && store.CountUnstartedSlitherlink().GetValueOrDefault(bucket) >= options.TargetPerCombination)
            return ("full", null);
        var puzzle = await generator.GenerateAsync(bucket, seed, token);
        token.ThrowIfCancellationRequested();
        return (diagnostic ? "verified" : store.SaveSlitherlink(puzzle) ? "stored" : "duplicate", puzzle.Id);
    }

    internal static string NewSeed() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

internal sealed class SlitherlinkInventoryWorker(SlitherlinkInventory inventory, SlitherlinkOptions options, TimeProvider time,
    ILogger<SlitherlinkInventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;
        var interval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var added = 0;
            try
            {
                foreach (var bucket in inventory.Shortfalls())
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        var (status, _) = await inventory.GenerateAsync(bucket, SlitherlinkInventory.NewSeed(), false, stoppingToken);
                        if (status == "busy")
                            break;
                        if (status == "stored")
                            added++;
                    }
                    catch (Exception exception) when (exception is TimeoutException or InvalidDataException)
                    {
                        logger.LogWarning("{Size}×{Size} {Tiling} {Difficulty} Slitherlink attempt failed. {Error}",
                            bucket.Size, bucket.Size, bucket.Tiling, bucket.Difficulty, exception.Message);
                    }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Slitherlink inventory could not complete a pass.");
            }
            if (added == 0)
                await Task.Delay(interval, time, stoppingToken);
        }
    }
}
