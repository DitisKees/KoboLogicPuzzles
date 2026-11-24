using System.Text.Json;
using KoboNonogram.Server.Hashi;

namespace KoboNonogram.Server;

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
        var puzzle = await library.GetPuzzleAsync(id.WebPbnId!.Value, cancellationToken, includeSolution);
        return puzzle is null ? null : ToDefinition(puzzle, includeSolution);
    }

    public async ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken)
    {
        var puzzle = await library.GetPuzzleAsync(id.WebPbnId!.Value, cancellationToken);
        return puzzle is null ? null : ToDefinition(puzzle, includeSolution: true);
    }

    internal static NonogramPuzzleDefinition ToDefinition(PuzzleDefinition puzzle, bool includeSolution) =>
        new(PuzzleId.FromWebPbn(puzzle.Id).Value, puzzle.Width, puzzle.Height,
            puzzle.Title, puzzle.Author, puzzle.RowClues, puzzle.ColumnClues,
            includeSolution ? puzzle.Solution : null, puzzle.Metadata);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListNonogramCatalog(filter, before?.WebPbnId, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindUnstartedNonogram(id.WebPbnId!.Value) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) =>
        store.ListMetadata(ids.Select(id => id.WebPbnId!.Value).ToArray()).ToDictionary(
            pair => PuzzleId.FromWebPbn(pair.Key).Value, pair => (object)pair.Value, StringComparer.Ordinal);
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
            stored.Rating.DifficultyScore);

    internal static HashiPuzzleDefinition ToDefinition(StoredHashiPuzzle stored, bool includeSolution)
    {
        var puzzle = stored.Puzzle;
        return new HashiPuzzleDefinition(PuzzleId.FromHashi(puzzle.ContentHash).Value, puzzle.Width, puzzle.Height,
            puzzle.Islands, puzzle.Edges, ToMetadata(stored.Metadata), includeSolution ? stored.Solution : null);
    }

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListHashiCatalog(filter, before?.ContentHash, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindUnstartedHashi(id.ContentHash!) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) =>
        store.ListHashiMetadata(ids.Select(id => id.ContentHash!).ToArray());
}
