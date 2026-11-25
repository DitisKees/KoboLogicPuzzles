using System.Text.Json;
using KoboLogicPuzzles.Server.Hashi;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal void SaveMixedProgress(IReadOnlyList<ProgressUpdate> nonograms, IReadOnlyList<HashiProgressUpdate> hashi,
        DateTimeOffset backedUpAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (nonograms.Count > 0)
            WriteNonogramProgress(connection, transaction, nonograms, backedUpAt);
        if (hashi.Count > 0)
            WriteHashiProgress(connection, transaction, hashi, backedUpAt);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public IReadOnlyList<GameSavedBoard> RestoreGameProgress(PuzzleGame? game)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var boards = new List<GameSavedBoard>();
        if (game is null or PuzzleGame.Nonogram)
        {
            foreach (var (puzzle, progress) in ReadNonogramProgress(connection, transaction))
            {
                var definition = WebPbnPuzzleProvider.ToDefinition(puzzle, includeSolution: true);
                boards.Add(ToSavedBoard(definition, progress.Marks, progress.Completed, progress.BackedUpAt));
            }
        }
        if (game is null or PuzzleGame.Hashi)
        {
            foreach (var (puzzle, progress) in ReadHashiProgress(connection, transaction))
            {
                var definition = LocalHashiPuzzleProvider.ToDefinition(puzzle, includeSolution: true);
                boards.Add(ToSavedBoard(definition, progress.Marks, progress.Completed, progress.BackedUpAt));
            }
        }
        transaction.Commit();
        return boards;
    }

    private static GameSavedBoard ToSavedBoard<T>(GamePuzzleDefinition definition, T marks, bool completed, DateTimeOffset backedUpAt) =>
        new(definition, new GameSavedProgress(definition.Id, definition.SchemaVersion,
            JsonSerializer.SerializeToElement(marks, Json), completed, backedUpAt));
}
