using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KoboNonogram.Server.Hashi;

namespace KoboNonogram.Server;

public sealed record PuzzleFilter(PuzzleGame Game, int? Width, int? Height, decimal? WebPbnDifficulty, int? WebPbnSolvability,
    HashiDifficulty? HashiDifficulty, HashiSolvability? HashiSolvability)
{
    internal IReadOnlyList<HashiDifficulty>? PreferredDifficulties { get; init; }
    internal IReadOnlyList<HashiSolvability>? PreferredSolvabilities { get; init; }

    public static bool TryParseGame(string? value, out PuzzleGame game) =>
        TryParseName(value, out game);

    public static PuzzleFilter? Parse(string? game, int? width, int? height, string? difficulty, string? solvability)
    {
        if (!TryParseGame(game, out var kind) || width is < 1 or > 100 || height is < 1 or > 100)
            return null;
        if (kind == PuzzleGame.Nonogram)
        {
            var stars = 0m;
            if (difficulty is not null && (!decimal.TryParse(difficulty, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out stars) || stars is < 1 or > 5 || stars * 4 % 1 != 0))
                return null;
            var level = 0;
            if (solvability is not null && (!int.TryParse(solvability, NumberStyles.None, CultureInfo.InvariantCulture,
                    out level) || level is < 1 or > 6))
                return null;
            return new PuzzleFilter(kind, width, height, difficulty is null ? null : stars,
                solvability is null ? null : level, null, null);
        }

        HashiDifficulty rating = default;
        HashiSolvability classification = default;
        if ((difficulty is not null && !TryParseName(difficulty, out rating)) ||
            (solvability is not null && !TryParseName(solvability, out classification)))
            return null;
        return new PuzzleFilter(kind, width, height, null, null, difficulty is null ? null : rating,
            solvability is null ? null : classification);
    }

    private static bool TryParseName<T>(string? value, out T result) where T : struct, Enum
    {
        foreach (var member in Enum.GetValues<T>())
        {
            if (JsonNamingPolicy.SnakeCaseLower.ConvertName(member.ToString()) == value)
            {
                result = member;
                return true;
            }
        }
        result = default;
        return false;
    }

    internal bool Matches(PuzzleDefinition puzzle) => DimensionsMatch(puzzle.Width, puzzle.Height) &&
        (WebPbnDifficulty is null || WebPbnDifficulty == puzzle.Metadata?.Difficulty) &&
        (WebPbnSolvability is null || WebPbnSolvability == puzzle.Metadata?.Solvability);

    internal bool Matches(StoredHashiPuzzle puzzle) => DimensionsMatch(puzzle.Puzzle.Width, puzzle.Puzzle.Height) &&
        (HashiDifficulty is null || HashiDifficulty == puzzle.Metadata.Rating.Difficulty) &&
        (HashiSolvability is null || HashiSolvability == puzzle.Metadata.Rating.Solvability);

    private bool DimensionsMatch(int width, int height) => (Width is null || Width == width) && (Height is null || Height == height);
}

public sealed record GamePuzzleSummary(string Id, string Game, int Width, int Height, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Author = null)
{
    internal static string PlayStatus(bool? completed) => completed switch { true => "solved", false => "started", null => "unstarted" };
}

public sealed record GameCatalogPage(IReadOnlyList<GamePuzzleSummary> Puzzles, string? NextBeforeId);

public sealed record GameOfflineQueueRequest(string? Game, [property: JsonRequired] int TargetCount,
    string[]? CachedUnstartedPuzzleIds, string[]? ExcludedPuzzleIds, int? Width, int? Height, string? Difficulty, string? Solvability);

public sealed record GameOfflineQueueResponse(IReadOnlyList<GamePuzzleDefinition> Puzzles, int Remaining);

public sealed class GameCatalog(PuzzleProviders providers, HashiGenerationOptions hashiOptions)
{
    public GameCatalogPage List(PuzzleFilter filter, PuzzleId? before, int limit)
    {
        var entries = providers.For(filter.Game).ListCatalog(filter, before, limit + 1, unstartedOnly: false);
        return entries.Count <= limit ? new GameCatalogPage(entries, null) :
            new GameCatalogPage(entries.Take(limit).ToArray(), entries[limit - 1].Id);
    }

    public async Task<GameOfflineQueueResponse> TopUpAsync(GameOfflineQueueRequest request, CancellationToken cancellationToken)
    {
        var filter = PuzzleFilter.Parse(request.Game, request.Width, request.Height, request.Difficulty, request.Solvability)
            ?? throw new ArgumentException("Supply a supported game and valid dimension, difficulty, and solvability filters.");
        if (request.TargetCount is < 0 or > 100 || request.CachedUnstartedPuzzleIds is { Length: > 100 } ||
            request.ExcludedPuzzleIds is { Length: > 1000 })
            throw new ArgumentException("Use a targetCount of 0–100, at most 100 cached IDs, and at most 1000 excluded IDs.");

        var provider = providers.For(filter.Game);
        var cached = ParseIds(request.CachedUnstartedPuzzleIds, filter.Game);
        var excluded = ParseIds(request.ExcludedPuzzleIds, filter.Game);
        var needed = request.TargetCount - cached.Count(id => !excluded.Contains(id) &&
            provider.IsMatchingUnstartedCache(id, filter));
        if (needed <= 0)
            return new GameOfflineQueueResponse([], 0);

        excluded.UnionWith(cached);
        var puzzles = new List<GamePuzzleDefinition>();
        if (filter.Game == PuzzleGame.Hashi && request.Difficulty is null && request.Solvability is null)
        {
            var preferred = filter with
            {
                PreferredDifficulties = hashiOptions.SelectedDifficulties,
                PreferredSolvabilities = hashiOptions.SelectedSolvabilities,
            };
            await FillAsync(provider, preferred, needed, excluded, puzzles, cancellationToken);
        }
        if (puzzles.Count < needed)
            await FillAsync(provider, filter, needed, excluded, puzzles, cancellationToken);

        return new GameOfflineQueueResponse(puzzles, needed - puzzles.Count);
    }

    private static async Task FillAsync(IPuzzleProvider provider, PuzzleFilter filter, int needed, HashSet<PuzzleId> excluded,
        List<GamePuzzleDefinition> puzzles, CancellationToken cancellationToken)
    {
        PuzzleId? before = null;
        const int pageSize = 100;
        IReadOnlyList<GamePuzzleSummary> page;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = provider.ListCatalog(filter, before, pageSize, unstartedOnly: true);
            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PuzzleId.TryParse(item.Id, out before))
                    throw new InvalidOperationException("The catalog returned an invalid puzzle ID.");
                if (!excluded.Add(before))
                    continue;
                var puzzle = await provider.GetOfflineDefinitionAsync(before, cancellationToken);
                if (puzzle is null)
                    continue;
                puzzles.Add(puzzle);
                if (puzzles.Count == needed)
                    return;
            }
        } while (page.Count == pageSize);
    }

    public IReadOnlyDictionary<string, object> ListMetadata(string[] values)
    {
        if (values.Length > 100)
            throw new ArgumentException("Supply at most 100 puzzle IDs.");
        var ids = ParseIds(values, null);
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var group in ids.GroupBy(id => id.Game))
            foreach (var (id, rating) in providers.For(group.Key).ListMetadata(group.ToArray()))
                metadata.Add(id, rating);
        return metadata;
    }

    private static HashSet<PuzzleId> ParseIds(string[]? values, PuzzleGame? game)
    {
        var ids = new HashSet<PuzzleId>();
        foreach (var value in values ?? [])
        {
            if (!PuzzleId.TryParse(value, out var id) || game is { } kind && id.Game != kind)
                throw new ArgumentException("Supply valid namespaced puzzle IDs for the requested game.");
            ids.Add(id);
        }
        return ids;
    }
}
