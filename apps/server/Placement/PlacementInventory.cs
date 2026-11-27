using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace KoboLogicPuzzles.Server.Placement;

internal sealed record PlacementStock(int Size, string Difficulty, string Solvability, int Unstarted);

internal sealed class PlacementOptions
{
    public bool Enabled { get; init; }
    public string? ExecutablePath { get; init; }
    public int[]? Sizes { get; init; }
    public string[]? Difficulties { get; init; }
    public bool IncludeUnclassified { get; init; }
    [Range(1, 100)] public int TargetPerDifficulty { get; init; } = 3;
    [Range(1, 1800)] public int TimeoutSeconds { get; init; } = 10;
    [Range(1, 30)] public int AttemptsPerSize { get; init; } = 3;
    [Range(1, 86400)] public int PollIntervalSeconds { get; init; } = 300;
}

internal sealed class PlacementInventory
{
    private readonly PuzzleStore store;
    private readonly PlacementCollections collections;
    private readonly Dictionary<PuzzleGame, PlacementOptions> options;
    private readonly Dictionary<PuzzleGame, GenerationActivity> activities;

    public PlacementInventory(PuzzleStore store, PlacementCollections collections, IConfiguration config, TimeProvider time)
    {
        this.store = store;
        this.collections = collections;
        options = PlacementBoard.Games.ToDictionary(game => game, game =>
            config.GetSection($"{game}Generation").Get<PlacementOptions>() ?? new());
        activities = PlacementBoard.Games.ToDictionary(game => game, _ => new GenerationActivity(time));
        foreach (var game in PlacementBoard.Games)
        {
            var settings = Options(game);
            Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
            var sizes = Sizes(game);
            var levels = Difficulties(game);
            if (sizes.Length == 0 || !sizes.All(PlacementGenerator.Sizes(game).Contains) || sizes.Distinct().Count() != sizes.Length ||
                levels.Length == 0 || !levels.All(PlacementBoard.Difficulties.Contains) || levels.Distinct().Count() != levels.Length)
                throw new ValidationException($"Use distinct tested sizes and difficulty tiers in {game}Generation.");
        }
    }

    internal PlacementOptions Options(PuzzleGame game) => options[game];
    internal int[] Sizes(PuzzleGame game) => Options(game).Sizes ??
        (game == PuzzleGame.Tents ? PlacementGenerator.Sizes(game) : [20, 25, 30]);
    internal string[] Difficulties(PuzzleGame game) => Options(game).Difficulties ?? ["hard", "expert"];

    internal bool Available(PuzzleGame game) => game == PuzzleGame.Battleships ||
        File.Exists(PlacementGenerator.TentsExecutable(Options(game).ExecutablePath));

    internal Dictionary<(int Size, string Difficulty), int> Counts(PuzzleGame game, PlacementStock[]? stock = null) =>
        (stock ?? store.CountUnstartedPlacement(game)).Where(row => Options(game).IncludeUnclassified || row.Solvability != "unknown")
            .GroupBy(row => (row.Size, row.Difficulty))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Unstarted));

    internal Dictionary<string, PuzzleSource> Sources(PuzzleGame game, IEnumerable<DashboardCatalogCount> catalog)
    {
        var sources = catalog.Where(row => row.Game == PlacementBoard.Slug(game)).ToDictionary(row => row.Source,
            row => new PuzzleSource(row.SourceName, row.SourceKind), StringComparer.Ordinal);
        if (Options(game).Enabled && Available(game))
            sources.TryAdd(PlacementGenerator.Source(game), new(PlacementGenerator.SourceName(game), "generated"));
        if (collections.Enabled(game))
            sources.TryAdd("janko", new("Janko", "collection"));
        return sources;
    }

    internal object GetStatus(PuzzleGame game)
    {
        var settings = Options(game);
        var stock = store.CountUnstartedPlacement(game);
        var counts = Counts(game, stock);
        var available = Available(game);
        var refresh = store.GetPlacementRefresh(game);
        return new
        {
            Enabled = settings.Enabled && available,
            Available = available,
            settings.TimeoutSeconds,
            Stock = Sizes(game).SelectMany(size => Difficulties(game).Select(difficulty => new
            {
                Size = size, Difficulty = difficulty, Target = settings.Enabled && available ? settings.TargetPerDifficulty : 0,
                Unstarted = counts.GetValueOrDefault((size, difficulty)),
            })).ToArray(),
            ActualStock = stock,
            Collection = new
            {
                Source = "janko",
                Enabled = collections.Enabled(game),
                Cursor = refresh.Cursor,
                CheckedAt = refresh.CheckedAt == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(refresh.CheckedAt),
            },
            Activity = activities[game].Snapshot(),
        };
    }

    internal async Task<(string Status, string? Id, PlacementProof? Proof)> GenerateAsync(PuzzleGame game, int size, int seed,
        string target, bool diagnostic, bool enforceTarget, CancellationToken token)
    {
        var activity = activities[game];
        var settings = Options(game);
        var timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(timeout);
        activity.Begin(size, target);
        var stage = "generation";
        try
        {
            if (!Available(game))
                return Finish("generator_unavailable");
            PlacementBoard board;
            PlacementProof proof;
            if (game == PuzzleGame.Tents)
            {
                var (layout, expected) = await PlacementGenerator.TentsAsync(
                    PlacementGenerator.TentsExecutable(settings.ExecutablePath), size, seed, budget.Token);
                stage = "verification";
                activity.Verifying();
                (board, proof) = PlacementGenerator.TuneTents(layout, expected, seed, target, timeout - clock.Elapsed, budget.Token);
            }
            else
            {
                stage = "verification";
                activity.Verifying();
                (board, proof) = PlacementGenerator.Battleships(size, seed, target, timeout, budget.Token);
            }

            if (proof.Count != 1 || proof.Solution is null || !board.IsSolved(proof.Solution))
                return Finish(proof.Count > 1 ? "multiple_solutions" : "invalid", proof);
            if (enforceTarget && (proof.Difficulty != target || !settings.IncludeUnclassified && proof.Solvability == "unknown"))
                return Finish("outside_target", proof);

            budget.Token.ThrowIfCancellationRequested();
            var hash = board.Hash();
            var seedId = seed.ToString(CultureInfo.InvariantCulture);
            var tents = game == PuzzleGame.Tents;
            var metadata = new PlacementMetadata(PlacementGenerator.Source(game), PlacementGenerator.SourceName(game), "generated",
                $"{size}/{target}/{seedId}", tents ? PlacementGenerator.TentsUrl : null,
                "MIT", seedId, tents ? PlacementGenerator.TentsVersion : PlacementGenerator.BattleshipsVersion,
                proof.Difficulty!, proof.Solvability!, hash);
            var definition = board.ToDefinition(metadata, null, null, proof.Solution);
            return Finish(diagnostic ? "verified" : store.SavePlacement(definition) ? "stored" : "duplicate", proof, definition.Id);
        }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is TimeoutException or OperationCanceledException)
        {
            return Finish(stage + "_timeout");
        }
        catch (Exception exception) when (exception is InvalidDataException or PlacementSearchLimitException)
        {
            return Finish("invalid");
        }
        finally
        {
            activity.Clear();
        }

        (string, string?, PlacementProof?) Finish(string status, PlacementProof? proof = null, string? id = null)
        {
            activity.Finish(status, proof?.Difficulty, proof?.Solvability);
            return (status, id, proof);
        }
    }
}

internal sealed class PlacementInventoryWorker(PuzzleStore store, PlacementInventory inventory, TimeProvider time,
    ILogger<PlacementInventoryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(PlacementBoard.Games.Where(game => inventory.Options(game).Enabled).Select(game => RunAsync(game, stoppingToken)));

    private async Task RunAsync(PuzzleGame game, CancellationToken token)
    {
        var options = inventory.Options(game);
        if (!inventory.Available(game))
        {
            logger.LogWarning("{Game} generator is enabled but its executable is unavailable.", game);
            return;
        }
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var generation = store.TryLockPlacement(game, "generation");
                if (generation is not null)
                {
                    var counts = inventory.Counts(game);
                    foreach (var size in inventory.Sizes(game))
                        foreach (var difficulty in inventory.Difficulties(game))
                            for (var attempt = 0; counts.GetValueOrDefault((size, difficulty)) < options.TargetPerDifficulty &&
                                attempt < options.AttemptsPerSize; attempt++)
                            {
                                token.ThrowIfCancellationRequested();
                                var result = await inventory.GenerateAsync(game, size, RandomNumberGenerator.GetInt32(int.MaxValue),
                                    difficulty, diagnostic: false, enforceTarget: true, token);
                                if (result.Status == "stored")
                                    counts = inventory.Counts(game);
                            }
                }
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(exception, "{Game} inventory could not complete a pass.", game);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), time, token);
        }
    }
}

internal static class PlacementCli
{
    internal static async Task<int> RunAsync(string command, WebApplication app, CancellationToken token)
    {
        var inventory = app.Services.GetRequiredService<PlacementInventory>();
        var config = app.Configuration;
        if (!PuzzleFilter.TryParseGame(config["game"], out var game) || !PlacementBoard.Games.Contains(game))
        {
            Console.Error.WriteLine($"{command} requires --game tents|battleships.");
            return 2;
        }
        if (command == "placement-status")
        {
            Console.WriteLine(JsonSerializer.Serialize(inventory.GetStatus(game), JsonSerializerOptions.Web));
            return 0;
        }
        if (command == "placement-refresh")
        {
            if (!config.GetValue("allow-non-commercial", config.GetValue("PlacementCollections:AllowNonCommercial", false)))
            {
                Console.Error.WriteLine("Janko requires a noncommercial deployment. Use --allow-non-commercial true to import.");
                return 2;
            }
            var (added, more) = await app.Services.GetRequiredService<PlacementCollections>().RefreshAsync(game, true, token);
            Console.WriteLine($"Imported {added} Janko {PlacementBoard.Name(game)} puzzles." +
                (more ? " Run placement-refresh again to continue." : ""));
            return 0;
        }
        if (!int.TryParse(config["size"], out var size) || !PlacementGenerator.Sizes(game).Contains(size) ||
            config["seed"] is { } value && (!int.TryParse(value, out _) || value.StartsWith('-')))
        {
            Console.Error.WriteLine($"Use --size {string.Join('|', PlacementGenerator.Sizes(game))} and an optional nonnegative integer --seed.");
            return 2;
        }
        var seed = config.GetValue("seed", RandomNumberGenerator.GetInt32(int.MaxValue));
        var difficulty = config["difficulty"] ?? "hard";
        if (!PlacementBoard.Difficulties.Contains(difficulty))
        {
            Console.Error.WriteLine("Use --difficulty easy|medium|hard|expert.");
            return 2;
        }
        using var generation = app.Services.GetRequiredService<PuzzleStore>().TryLockPlacement(game, "generation");
        if (generation is null)
        {
            Console.Error.WriteLine("An inventory pass is already running for this game.");
            return 1;
        }
        var clock = Stopwatch.StartNew();
        var result = await inventory.GenerateAsync(game, size, seed, difficulty, config.GetValue("dry-run", false),
            enforceTarget: config.GetValue("require-difficulty", false), token);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Status, result.Id, Count = result.Proof?.Count, Difficulty = result.Proof?.Difficulty,
            Solvability = result.Proof?.Solvability, Nodes = result.Proof?.Nodes,
            ElapsedMilliseconds = clock.ElapsedMilliseconds, Seed = seed,
        }, JsonSerializerOptions.Web));
        return result.Status is "stored" or "duplicate" or "verified" ? 0 : 1;
    }
}
