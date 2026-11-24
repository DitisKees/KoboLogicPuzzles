using System.Text.Json;

namespace KoboNonogram.Server;

public interface IPuzzleProvider
{
    PuzzleGame Game { get; }

    ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken);
}

public sealed class PuzzleProviders(IEnumerable<IPuzzleProvider> providers)
{
    private readonly Dictionary<PuzzleGame, IPuzzleProvider> byGame = providers.ToDictionary(provider => provider.Game);

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution,
        CancellationToken cancellationToken) => byGame[id.Game].GetDefinitionAsync(id, includeSolution, cancellationToken);
}

internal sealed class WebPbnPuzzleProvider(PuzzleLibrary library) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.Nonogram;

    public async ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution,
        CancellationToken cancellationToken)
    {
        var puzzle = await library.GetPuzzleAsync(id.WebPbnId!.Value, cancellationToken, includeSolution);
        return puzzle is null ? null : new NonogramPuzzleDefinition(id.Value, puzzle.Width, puzzle.Height,
            puzzle.Title, puzzle.Author, puzzle.RowClues, puzzle.ColumnClues,
            includeSolution ? puzzle.Solution : null, puzzle.Metadata);
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
        if (stored is null)
            return ValueTask.FromResult<GamePuzzleDefinition?>(null);

        var puzzle = stored.Puzzle;
        var rating = stored.Metadata.Rating;
        var metadata = new HashiDefinitionMetadata(JsonNamingPolicy.SnakeCaseLower.ConvertName(rating.Solvability.ToString()),
            stored.Metadata.IslandCount, stored.Metadata.ActualDensity,
            rating.Difficulty is { } difficulty ? JsonNamingPolicy.SnakeCaseLower.ConvertName(difficulty.ToString()) : null,
            rating.DifficultyScore);
        return ValueTask.FromResult<GamePuzzleDefinition?>(new HashiPuzzleDefinition(id.Value, puzzle.Width, puzzle.Height,
            puzzle.Islands, puzzle.Edges, metadata, includeSolution ? stored.Solution : null));
    }
}
