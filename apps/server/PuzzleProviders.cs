using System.Text.Json;
using KoboLogicPuzzles.Server.Hashi;

namespace KoboLogicPuzzles.Server;

public interface IPuzzleProvider
{
    PuzzleGame Game { get; }

    ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken);

    ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken);

    IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly);

    bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter);

    IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids);
}

public sealed class PuzzleProviders(IEnumerable<IPuzzleProvider> providers)
{
    private readonly Dictionary<PuzzleGame, IPuzzleProvider> byGame = providers.ToDictionary(provider => provider.Game);

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution,
        CancellationToken cancellationToken) => For(id.Game).GetDefinitionAsync(id, includeSolution, cancellationToken);

    internal IPuzzleProvider For(PuzzleGame game) => byGame[game];
}

internal sealed class WebPbnPuzzleProvider(PuzzleLibrary library, PuzzleStore store) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.Nonogram;

    public async ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution,
        CancellationToken cancellationToken)
    {
        if (id.WebPbnId is not { } number)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imported = store.FindCollectionNonogram(id.Value);
            return includeSolution || imported is null ? imported : imported with { Solution = null };
        }
        var puzzle = await library.GetPuzzleAsync(number, cancellationToken, includeSolution);
        return puzzle is null ? null : ToDefinition(puzzle, includeSolution);
    }

    public async ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken)
    {
        if (id.WebPbnId is not { } number)
            return await GetDefinitionAsync(id, includeSolution: true, cancellationToken);
        var puzzle = await library.GetPuzzleAsync(number, cancellationToken);
        return puzzle is null ? null : ToDefinition(puzzle, includeSolution: true);
    }

    internal static NonogramPuzzleDefinition ToDefinition(PuzzleDefinition puzzle, bool includeSolution) =>
        new(PuzzleId.FromWebPbn(puzzle.Id).Value, puzzle.Width, puzzle.Height,
            puzzle.Title, puzzle.Author, puzzle.RowClues, puzzle.ColumnClues,
            includeSolution ? puzzle.Solution : null, puzzle.Metadata);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly)
    {
        if (before?.WebPbnId is { } webPbnBefore)
            return filter.IncludesSource("webpbn") ? store.ListNonogramCatalog(filter, webPbnBefore, limit, unstartedOnly) : [];

        var collection = store.ListCollectionNonograms(filter, before?.Value, limit, unstartedOnly);
        if (collection.Count == limit || !filter.IncludesSource("webpbn"))
            return collection;
        return [.. collection, .. store.ListNonogramCatalog(filter, null, limit - collection.Count, unstartedOnly)];
    }

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        id.WebPbnId is { } number
            ? store.FindUnstartedNonogram(number) is { } puzzle && filter.Matches(puzzle)
            : store.FindCollectionNonogram(id.Value, unstartedOnly: true) is { } imported && filter.Matches(imported);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids)
    {
        var metadata = store.ListMetadata(ids.Where(id => id.WebPbnId is not null).Select(id => id.WebPbnId!.Value).ToArray())
            .ToDictionary(pair => PuzzleId.FromWebPbn(pair.Key).Value, pair => (object)pair.Value, StringComparer.Ordinal);
        store.AddCollectionNonogramMetadata(ids, metadata);
        return metadata;
    }
}

internal sealed class LocalHashiPuzzleProvider(PuzzleStore store) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.Hashi;

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = store.FindHashiPuzzle(id.ContentHash!);
        return ValueTask.FromResult<GamePuzzleDefinition?>(stored is null ? null : ToDefinition(stored, includeSolution));
    }

    public ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken) =>
        GetDefinitionAsync(id, includeSolution: true, cancellationToken);

    internal static HashiDefinitionMetadata ToMetadata(HashiPuzzleMetadata stored) =>
        new(JsonNamingPolicy.SnakeCaseLower.ConvertName(stored.Rating.Solvability.ToString()),
            stored.IslandCount, stored.ActualDensity,
            stored.Rating.Difficulty is { } difficulty ? JsonNamingPolicy.SnakeCaseLower.ConvertName(difficulty.ToString()) : null,
            stored.Rating.DifficultyScore, stored.Attribution?.Source ?? "bridges",
            stored.Attribution?.SourceName ?? "Bridges", stored.Attribution?.SourceKind ?? "generated",
            stored.Attribution?.SourceId, stored.Attribution?.SourceUrl, stored.Attribution?.License);

    internal static HashiPuzzleDefinition ToDefinition(StoredHashiPuzzle stored, bool includeSolution)
    {
        var puzzle = stored.Puzzle;
        return new HashiPuzzleDefinition(PuzzleId.FromHashi(puzzle.ContentHash).Value, puzzle.Width, puzzle.Height,
            puzzle.Islands, puzzle.Edges, ToMetadata(stored.Metadata), includeSolution ? stored.Solution : null,
            stored.Metadata.Attribution?.Title, stored.Metadata.Attribution?.Author);
    }

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListHashiCatalog(filter, before?.ContentHash, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindUnstartedHashi(id.ContentHash!) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) =>
        store.ListHashiMetadata(ids.Select(id => id.ContentHash!).ToArray());
}
