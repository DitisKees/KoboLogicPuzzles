using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace KoboLogicPuzzles.Server.Hashi;

internal sealed record HashiStockCount(int Size, HashiDifficulty? Difficulty, HashiSolvability Solvability, int Total, int Unstarted,
    int MeetsDensityFloor);

public sealed record HashiInventoryBucket(int Size, string? Difficulty, string Solvability, int Total, int Unstarted);

public sealed record HashiInventoryTarget(int Size, string Difficulty, int Unstarted, int Target);

public sealed record HashiInventoryStatus(bool Enabled, IReadOnlyList<HashiInventoryBucket> Stock,
    IReadOnlyList<HashiInventoryTarget> Targets, IReadOnlyDictionary<string, long> Outcomes, HashiGenerationOutcome? LastAttempt);

public sealed record HashiGenerationOutcome(string Status, int Size, HashiGenerationParameters Parameters, string? PuzzleId = null,
    string? Validation = null, int? ExactNodes = null, string? Difficulty = null, string? Solvability = null,
    int? IslandCount = null, double? GenerationMilliseconds = null, double? VerificationMilliseconds = null,
    long? PeakRssKiB = null, string? Detail = null);

public sealed class HashiInventory(PuzzleStore store, IHashiCandidateGenerator generator, HashiGenerationOptions options,
    TimeProvider time, ILogger<HashiInventory> logger)
{
    private readonly ConcurrentDictionary<string, long> outcomes = new(StringComparer.Ordinal);
    private readonly HashSet<int> pendingRefills = [];
    private HashiGenerationOutcome? lastAttempt;
    private int cycleNumber;

    public HashiInventoryStatus GetStatus()
    {
        var stock = ReadStock();
        var buckets = stock.Select(item => new HashiInventoryBucket(item.Size, Name(item.Difficulty), Name(item.Solvability)!,
            item.Total, item.Unstarted)).ToArray();
        var targets = (from size in options.SelectedSizes
                       from tier in options.SelectedDifficulties
                       select new HashiInventoryTarget(size, Name(tier)!, EligibleStock(stock, size, tier), options.TargetPerDifficulty))
            .ToArray();
        return new HashiInventoryStatus(options.Enabled, buckets, targets, new Dictionary<string, long>(outcomes),
            Volatile.Read(ref lastAttempt));
    }

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = store.TryLockHashiGeneration();
        if (gate is null)
            return;

        var cycle = (uint)(Interlocked.Increment(ref cycleNumber) - 1);
        var stock = ReadStock();
        var lowWatermark = cycle == 0 ? options.TargetPerDifficulty - 1 : options.LowWatermark;
        pendingRefills.UnionWith(options.SelectedSizes.Where(size => options.SelectedDifficulties.Any(tier =>
            EligibleStock(stock, size, tier) <= lowWatermark)));
        var sizes = options.SelectedSizes.Where(pendingRefills.Contains).ToArray();
        if (sizes.Length == 0)
            return;

        for (var round = 0; round < options.AttemptsPerSize; round++)
        {
            foreach (var size in sizes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stock = ReadStock();
                if (AtCapacity(stock, size))
                    continue;

                var missing = options.SelectedDifficulties.Where(tier =>
                    EligibleStock(stock, size, tier) < options.TargetPerDifficulty).ToArray();
                if (missing.Length == 0)
                    continue;

                var attempt = cycle + (uint)round;
                var target = missing[(int)(attempt % missing.Length)];
                var densities = options.DensitiesFor(size, target);
                // Rotate through the missing tiers and try a different density on each trip through the list.
                var density = densities[(int)((attempt / (uint)missing.Length) % densities.Count)];
                var upstream = options.UpstreamDifficulty ?? (target switch
                {
                    HashiDifficulty.Easy => 0,
                    HashiDifficulty.Medium => 1,
                    _ => 2,
                });
                var parameters = new HashiGenerationParameters(NewSeed(), density, options.Expansion, upstream);
                await AttemptAsync(size, parameters, diagnostic: false, rating => rating.Difficulty is { } tier &&
                    missing.Contains(tier) && options.SelectedSolvabilities.Contains(rating.Solvability), cancellationToken);
            }
        }

        var shortfalls = GetStatus().Targets.Where(target => sizes.Contains(target.Size) && target.Unstarted < target.Target).ToArray();
        pendingRefills.IntersectWith(shortfalls.Select(target => target.Size));
        if (shortfalls.Length > 0)
            logger.LogInformation("Hashi inventory shortfall {Shortfalls}. Retrying in {Seconds} seconds.",
                string.Join(", ", shortfalls.Select(target => $"{target.Size}/{target.Difficulty} {target.Unstarted}/{target.Target}")),
                options.PollIntervalSeconds);
    }

    public async Task<HashiGenerationOutcome> GenerateOnceAsync(int size, HashiGenerationParameters parameters, bool diagnostic,
        CancellationToken cancellationToken)
    {
        if (!HashiGenerationOptions.IsSupportedSize(size) || !parameters.IsValid())
            throw new ArgumentException("Supply a supported Hashi size and valid generator parameters.");
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = store.TryLockHashiGeneration();
        if (gate is null)
            return new HashiGenerationOutcome("busy", size, parameters, Detail: "Another process is generating puzzles for this database.");
        if (!diagnostic && AtCapacity(ReadStock(), size))
            return new HashiGenerationOutcome("capacity", size, parameters, Detail: "The unstarted inventory for this size is full.");
        return await AttemptAsync(size, parameters, diagnostic, _ => true, cancellationToken);
    }

    private async Task<HashiGenerationOutcome> AttemptAsync(int size, HashiGenerationParameters parameters, bool diagnostic,
        Func<HashiPuzzleRating, bool> acceptsRating, CancellationToken cancellationToken)
    {
        GeneratedHashiCandidate generated;
        try
        {
            generated = await generator.GenerateAsync(size, parameters, options.StageTimeout, cancellationToken);
            if (generated.Candidate.Puzzle.Width != size || generated.Candidate.Puzzle.Height != size)
                throw new InvalidDataException("Bridges returned the wrong dimensions.");
        }
        catch (TimeoutException exception)
        {
            return Record(new HashiGenerationOutcome("generation_timeout", size, parameters, Detail: exception.Message));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or Win32Exception or FormatException)
        {
            return Record(new HashiGenerationOutcome("generation_failed", size, parameters, Detail: exception.Message));
        }

        var started = Stopwatch.GetTimestamp();
        var (validation, verified) = VerifiedHashiPuzzle.Verify(generated.Candidate, parameters, options.StageTimeout,
            time.GetUtcNow(), cancellationToken);
        var metadata = verified?.Metadata;
        var result = new HashiGenerationOutcome("validation_rejected", size, parameters, Validation: Name(validation.Status),
            ExactNodes: validation.Nodes, Difficulty: Name(metadata?.Rating.Difficulty), Solvability: Name(metadata?.Rating.Solvability),
            IslandCount: generated.Candidate.Puzzle.Islands.Count, GenerationMilliseconds: generated.GenerationMilliseconds,
            VerificationMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds, PeakRssKiB: generated.PeakRssKiB);
        if (verified is null)
            return Record(result);

        cancellationToken.ThrowIfCancellationRequested();
        result = result with { PuzzleId = PuzzleId.FromHashi(verified.Puzzle.ContentHash).Value };
        if (diagnostic)
            return Record(result with { Status = "verified" });
        if (metadata!.IslandCount < options.MinIslands || metadata.ActualDensity < options.MinDensityPercent)
            return Record(result with { Status = "density_rejected" });
        if (!acceptsRating(metadata.Rating))
            return Record(result with { Status = "rating_rejected" });
        return Record(result with { Status = store.SaveHashiPuzzle(verified) ? "stored" : "duplicate" });
    }

    private bool AtCapacity(IReadOnlyList<HashiStockCount> stock, int size) =>
        stock.Where(item => item.Size == size).Sum(item => item.Unstarted) >= options.MaxUnstartedPerSize;

    private int EligibleStock(IReadOnlyList<HashiStockCount> stock, int size, HashiDifficulty difficulty) =>
        stock.Where(item => item.Size == size && item.Difficulty == difficulty && options.SelectedSolvabilities.Contains(item.Solvability))
            .Sum(item => item.MeetsDensityFloor);

    private IReadOnlyList<HashiStockCount> ReadStock() => store.ReadHashiStock(options.MinIslands, options.MinDensityPercent);

    private HashiGenerationOutcome Record(HashiGenerationOutcome result)
    {
        outcomes.AddOrUpdate(result.Status, 1, (_, count) => count + 1);
        Volatile.Write(ref lastAttempt, result);
        if (result.Detail is { } detail)
            logger.LogWarning("Hashi generation for size {Size} ended with {Status}: {Detail}", result.Size, result.Status, detail);
        else
            logger.LogDebug("Hashi generation for size {Size} ended with {Status} and validation {Validation}",
                result.Size, result.Status, result.Validation);
        return result;
    }

    internal static string NewSeed() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static string? Name<T>(T? value) where T : struct, Enum => value is { } member
        ? JsonNamingPolicy.SnakeCaseLower.ConvertName(member.ToString()) : null;

    private static string? Name<T>(T value) where T : struct, Enum => Name((T?)value);
}
