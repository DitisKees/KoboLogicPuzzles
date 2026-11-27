using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;

namespace KoboLogicPuzzles.Server.Nurikabe;

internal readonly record struct NurikabeBucket(int Size, string Difficulty, string Solvability);

public sealed class NurikabeOptions : IValidatableObject
{
    public bool Enabled { get; init; }
    private static readonly string[] PreferredDifficulties = ["hard", "expert"];

    public int[]? Sizes { get; init; }
    public string[]? Difficulties { get; init; }
    public bool IncludeRequiresSearch { get; init; }
    [Range(1, 100)] public int TargetPerDifficulty { get; init; } = 5;
    [Range(1, 30)] public int ProofTimeoutSeconds { get; init; } = 2;
    [Range(1, 30)] public int AttemptsPerSize { get; init; } = 5;
    [Range(1, 86400)] public int PollIntervalSeconds { get; init; } = 60;

    internal int[] SelectedSizes => Sizes ?? NurikabeGenerator.Sizes;
    internal string[] SelectedDifficulties => Difficulties ?? PreferredDifficulties;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SelectedSizes.Length == 0 || SelectedSizes.Any(size => !NurikabeGenerator.Sizes.Contains(size)) ||
            SelectedSizes.Distinct().Count() != SelectedSizes.Length)
            yield return new ValidationResult("NurikabeGeneration sizes must be distinct tested sizes from 25 to 50 in increments of five.");
        if (SelectedDifficulties.Length == 0 || SelectedDifficulties.Any(tier => !NurikabeGenerator.Difficulties.Contains(tier)) ||
            SelectedDifficulties.Distinct().Count() != SelectedDifficulties.Length)
            yield return new ValidationResult("NurikabeGeneration difficulties must be distinct valid tiers.");
    }
}

internal sealed class NurikabeInventory(PuzzleStore store, NurikabeOptions options, NurikabeCollectionOptions collections, TimeProvider time)
{
    private readonly GenerationActivity activity = new(time);

    internal Dictionary<string, PuzzleSource> Sources()
    {
        var sources = store.ListNurikabeSources();
        if (options.Enabled)
            sources.TryAdd(NurikabeGenerator.Source, new(NurikabeGenerator.SourceName, "generated"));
        return sources;
    }

    internal object GetStatus()
    {
        var stock = store.CountUnstartedNurikabe();
        var counts = TargetCounts(stock);
        var refresh = store.GetNurikabeRefresh();
        return new
        {
            options.Enabled,
            TimeoutSeconds = options.ProofTimeoutSeconds,
            Sources = Sources(),
            Stock = options.SelectedSizes.SelectMany(size => options.SelectedDifficulties.Select(difficulty => new
            {
                Size = size, Difficulty = difficulty,
                Target = options.TargetPerDifficulty, Unstarted = counts.GetValueOrDefault((size, difficulty)),
            })).ToArray(),
            ActualStock = stock.Select(pair => new
            {
                pair.Key.Size, pair.Key.Difficulty, pair.Key.Solvability, Unstarted = pair.Value,
            }).ToArray(),
            Collection = new
            {
                Source = "janko", Enabled = collections.RefreshEnabled, refresh.NextIndex,
                CheckedAt = refresh.CheckedAt == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(refresh.CheckedAt),
            },
            Activity = activity.Snapshot(),
        };
    }

    internal Dictionary<(int Size, string Difficulty), int> Counts() => TargetCounts(store.CountUnstartedNurikabe());

    private Dictionary<(int Size, string Difficulty), int> TargetCounts(Dictionary<NurikabeBucket, int> stock) => stock
        .Where(pair => options.IncludeRequiresSearch || pair.Key.Solvability != "requires_search")
        .GroupBy(pair => (pair.Key.Size, pair.Key.Difficulty))
        .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));

    internal (string Status, string? Id) Generate(int size, int seed, string[]? requested, bool diagnostic, CancellationToken token)
    {
        if (!NurikabeGenerator.Sizes.Contains(size))
            throw new ArgumentException("Use a size of 25, 30, 35, 40, 45 or 50.");
        activity.Begin(size, requested is [var difficulty] ? difficulty : null);
        try
        {
            var (board, solution) = NurikabeGenerator.Candidate(size, seed, token);
            activity.Verifying();
            var proof = new NurikabeSolver(board, TimeSpan.FromSeconds(options.ProofTimeoutSeconds), token).CountSolutions();
            if (proof.Count != 1 || proof.Solution != solution || !board.IsSolved(solution))
                return Finish(proof.Count > 1 ? "multiple_solutions" : "invalid");
            if (requested is not null && (!requested.Contains(proof.Difficulty!) ||
                !options.IncludeRequiresSearch && proof.Solvability == "requires_search"))
                return Finish("outside_target", proof: proof);

            token.ThrowIfCancellationRequested();
            var id = board.Id();
            var metadata = new NurikabeMetadata(NurikabeGenerator.Source, NurikabeGenerator.SourceName, "generated",
                $"{size}/{seed.ToString(CultureInfo.InvariantCulture)}", null, null, seed.ToString(CultureInfo.InvariantCulture),
                NurikabeGenerator.Version, proof.Difficulty!, proof.Solvability!);
            var stored = !diagnostic && store.SaveNurikabe(new(id, size, size, board.Clues, $"Nurikabe {id[^6..]}", null, metadata, solution));
            return Finish(diagnostic ? "verified" : stored ? "stored" : "duplicate", id, proof);
        }
        catch (TimeoutException)
        {
            return Finish("verification_timeout");
        }
        finally
        {
            activity.Clear();
        }

        (string, string?) Finish(string status, string? id = null, NurikabeProof? proof = null)
        {
            activity.Finish(status, proof?.Difficulty, proof?.Solvability);
            return (status, id);
        }
    }

    internal static int NewSeed() => RandomNumberGenerator.GetInt32(int.MaxValue);
}

internal sealed class NurikabeInventoryWorker(PuzzleStore store, NurikabeInventory inventory, NurikabeOptions options,
    TimeProvider time, ILogger<NurikabeInventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var generation = store.TryLockNurikabe("generation");
                if (generation is not null)
                {
                    var counts = inventory.Counts();
                    foreach (var size in options.SelectedSizes)
                        for (var attempt = 0; attempt < options.AttemptsPerSize; attempt++)
                        {
                            stoppingToken.ThrowIfCancellationRequested();
                            var shortfalls = options.SelectedDifficulties.Where(tier =>
                                counts.GetValueOrDefault((size, tier)) < options.TargetPerDifficulty).ToArray();
                            if (shortfalls.Length == 0)
                                break;

                            if (inventory.Generate(size, NurikabeInventory.NewSeed(), shortfalls, false, stoppingToken).Status == "stored")
                                counts = inventory.Counts();
                        }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Nurikabe inventory could not complete a pass.");
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), time, stoppingToken);
        }
    }
}
