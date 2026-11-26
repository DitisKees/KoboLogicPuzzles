using System.Security.Cryptography;

namespace KoboLogicPuzzles.Server.Slitherlink;

internal sealed class SlitherlinkInventory(PuzzleStore store, LoopyGenerator generator, SlitherlinkOptions options, TimeProvider time)
{
    private readonly GenerationActivity activity = new(time);

    public object GetStatus()
    {
        var counts = store.CountUnstartedSlitherlink();
        var buckets = options.Buckets().ToArray();
        return new
        {
            options.Enabled,
            options.TimeoutSeconds,
            Sources = store.ListSlitherlinkSources(),
            Stock = buckets.Select(bucket => new
            {
                bucket.Size,
                bucket.Tiling,
                bucket.Difficulty,
                Unstarted = counts.GetValueOrDefault(bucket),
                Target = options.TargetPerCombination,
            }).ToArray(),
            UntargetedStock = counts.Where(pair => !buckets.Contains(pair.Key)).Select(pair => new
            {
                pair.Key.Size,
                pair.Key.Tiling,
                pair.Key.Difficulty,
                Unstarted = pair.Value,
            }).ToArray(),
            Activity = activity.Snapshot(),
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
        activity.Begin(bucket.Size, bucket.Difficulty, tiling: bucket.Tiling);
        try
        {
            var puzzle = await generator.GenerateAsync(bucket, seed, token);
            token.ThrowIfCancellationRequested();
            var status = diagnostic ? "verified" : store.SaveSlitherlink(puzzle) ? "stored" : "duplicate";
            activity.Finish(status);
            return (status, puzzle.Id);
        }
        catch (TimeoutException)
        {
            activity.Finish("generation_timeout");
            throw;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            activity.Finish("generation_failed");
            throw;
        }
        finally
        {
            activity.Clear();
        }
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
