using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace KoboLogicPuzzles.Server.Hashi;

internal sealed record HashiStockCount(int Size, HashiDifficulty? Difficulty, HashiSolvability Solvability, int Total, int Unstarted,
    int MeetsDensityFloor);

internal readonly record struct HashiStockKey(int Size, HashiStockRating Rating);

public sealed record HashiInventoryBucket(int Size, string? Difficulty, string Solvability, int Total, int Unstarted);

public sealed record HashiInventoryTarget(int Size, string Difficulty, string Solvability, int Unstarted, int Target);

public sealed record HashiInventoryStatus(bool Enabled, IReadOnlyList<HashiInventoryBucket> Stock,
    IReadOnlyList<HashiInventoryTarget> Targets, IReadOnlyDictionary<string, long> Outcomes, HashiGenerationOutcome? LastAttempt,
    GenerationActivitySnapshot Activity);

public sealed record HashiGenerationOutcome(string Status, int Size, HashiGenerationParameters Parameters, string? PuzzleId = null,
    string? Validation = null, int? ExactNodes = null, string? Difficulty = null, string? Solvability = null,
    int? IslandCount = null, double? GenerationMilliseconds = null, double? VerificationMilliseconds = null,
    long? PeakRssKiB = null, string? Detail = null);

public sealed class HashiInventory(PuzzleStore store, IHashiCandidateGenerator generator, HashiGenerationOptions options,
    TimeProvider time, ILogger<HashiInventory> logger)
{
    private readonly GenerationActivity activity = new(time);
    private readonly HashSet<HashiStockKey> pendingRefills = [];
    private HashiGenerationOutcome? lastAttempt;
    private int cycleNumber;

    public HashiInventoryStatus GetStatus()
    {
        var stock = ReadStock();
        var buckets = stock.Select(item => new HashiInventoryBucket(item.Size, Name(item.Difficulty), Name(item.Solvability)!,
            item.Total, item.Unstarted)).ToArray();
        var eligible = CountEligible(stock);
        var targets = TargetKeys(eligible).Select(key => new HashiInventoryTarget(key.Size, Name(key.Rating.Difficulty)!,
            Name(key.Rating.Solvability)!, eligible.GetValueOrDefault(key), options.TargetPerDifficulty)).ToArray();
        var snapshot = activity.Snapshot();
        return new HashiInventoryStatus(options.Enabled, buckets, targets, snapshot.Outcomes,
            Volatile.Read(ref lastAttempt), snapshot);
    }

    // Returns true if a productive pass should be followed immediately by another.
    public async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var gate = store.TryLockHashiGeneration();
        if (gate is null)
            return false;

        var cycle = (uint)(Interlocked.Increment(ref cycleNumber) - 1);
        var stock = ReadStock();
        var eligible = CountEligible(stock);
        // The generation lock serializes inserts. Concurrent progress sync can only reduce unstarted stock.
        var unstarted = stock.GroupBy(item => item.Size).ToDictionary(group => group.Key, group => group.Sum(item => item.Unstarted));
        var targetCount = options.TargetPerDifficulty;
        var lowWatermark = cycle == 0 ? targetCount - 1 : options.RefillThreshold;
        pendingRefills.UnionWith(TargetKeys(eligible).Where(key => eligible.GetValueOrDefault(key) <= lowWatermark));
        pendingRefills.RemoveWhere(key => eligible.GetValueOrDefault(key) >= targetCount);
        var sizes = options.SelectedSizes.Where(size => pendingRefills.Any(key => key.Size == size)).ToArray();
        var stored = 0;

        for (var round = 0; round < options.AttemptsPerSize; round++)
        {
            foreach (var size in sizes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (unstarted.GetValueOrDefault(size) >= options.MaxUnstartedPerSize)
                    continue;

                var missing = pendingRefills.Where(key => key.Size == size && eligible.GetValueOrDefault(key) < targetCount)
                    .OrderBy(key => key.Rating.Difficulty).ThenBy(key => key.Rating.Solvability).ToArray();
                if (missing.Length == 0)
                    continue;

                var attempt = cycle + (uint)round;
                var target = missing[(int)(attempt % missing.Length)].Rating;
                var densities = options.DensitiesFor(size, target.Difficulty);
                // Try all densities while rotating through the missing rating combinations.
                var density = densities[(int)((attempt / (uint)missing.Length) % densities.Count)];
                var upstream = options.UpstreamDifficulty ?? (target switch
                {
                    (HashiDifficulty.Easy, HashiSolvability.BasicLogic) => 0,
                    (HashiDifficulty.Medium, HashiSolvability.BasicLogic) => 1,
                    _ => 2,
                });
                var parameters = new HashiGenerationParameters(NewSeed(), density, options.Expansion, upstream);
                HashiStockKey admitted = default;
                var outcome = await AttemptAsync(size, parameters, diagnostic: false, rating =>
                {
                    if (rating.Difficulty is not { } tier)
                        return false;
                    var candidate = new HashiStockRating(tier, rating.Solvability);
                    admitted = new HashiStockKey(size, candidate);
                    return options.IncludesRating(candidate) && eligible.GetValueOrDefault(admitted) < targetCount;
                }, cancellationToken, target);
                if (outcome.Status == "stored")
                {
                    eligible[admitted] = eligible.GetValueOrDefault(admitted) + 1;
                    unstarted[size] = unstarted.GetValueOrDefault(size) + 1;
                    pendingRefills.Add(admitted);
                    stored++;
                }
            }
        }

        pendingRefills.RemoveWhere(key => eligible.GetValueOrDefault(key) >= targetCount);
        if (pendingRefills.Count > 0)
            logger.LogInformation("Hashi inventory stored {Stored} puzzles. {Shortfalls} rating combinations are still below target.",
                stored, pendingRefills.Count);
        return stored > 0 && pendingRefills.Any(key => unstarted.GetValueOrDefault(key.Size) < options.MaxUnstartedPerSize);
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
        Func<HashiPuzzleRating, bool> acceptsRating, CancellationToken cancellationToken, HashiStockRating? target = null)
    {
        activity.Begin(size, Name(target?.Difficulty), Name(target?.Solvability));
        try
        {
            return await GenerateAndVerifyAsync(size, parameters, diagnostic, acceptsRating, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            activity.Finish("generation_failed");
            throw;
        }
        finally
        {
            activity.Clear();
        }
    }

    private async Task<HashiGenerationOutcome> GenerateAndVerifyAsync(int size, HashiGenerationParameters parameters,
        bool diagnostic, Func<HashiPuzzleRating, bool> acceptsRating, CancellationToken cancellationToken)
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

        activity.Verifying();
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

    private static Dictionary<HashiStockKey, int> CountEligible(IReadOnlyList<HashiStockCount> stock) =>
        stock.Where(item => item.Difficulty.HasValue).ToDictionary(
            item => new HashiStockKey(item.Size, new HashiStockRating(item.Difficulty!.Value, item.Solvability)),
            item => item.MeetsDensityFloor);

    private IEnumerable<HashiStockKey> TargetKeys(IReadOnlyDictionary<HashiStockKey, int> eligible)
    {
        var startingRatings = options.StartingRatings();
        foreach (var size in options.SelectedSizes)
        {
            var discovered = eligible.Keys.Where(key => key.Size == size && options.IncludesRating(key.Rating))
                .Select(key => key.Rating);
            foreach (var rating in startingRatings.Concat(discovered).Distinct().OrderBy(rating => rating.Difficulty)
                         .ThenBy(rating => rating.Solvability))
                yield return new HashiStockKey(size, rating);
        }
    }

    private IReadOnlyList<HashiStockCount> ReadStock() => store.ReadHashiStock(options.MinIslands, options.MinDensityPercent);

    private HashiGenerationOutcome Record(HashiGenerationOutcome result)
    {
        Volatile.Write(ref lastAttempt, result);
        activity.Finish(result.Status, result.Difficulty, result.Solvability);
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
