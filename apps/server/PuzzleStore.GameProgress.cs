using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using KoboLogicPuzzles.Server.Hashi;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal void SaveMixedProgress(IReadOnlyList<ProgressUpdate> nonograms, IReadOnlyList<CellProgressUpdate> collectionNonograms,
        IReadOnlyList<HashiProgressUpdate> hashi,
        IReadOnlyList<FillAPixProgressUpdate> fillAPix, IReadOnlyList<CellProgressUpdate> slitherlink,
        IReadOnlyList<CellProgressUpdate> nurikabe, IReadOnlyList<CellProgressUpdate> galaxies,
        IReadOnlyList<CellProgressUpdate> placement, IReadOnlyList<CellProgressUpdate> link, DateTimeOffset backedUpAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (nonograms.Count > 0)
            WriteNonogramProgress(connection, transaction, nonograms, backedUpAt);
        if (collectionNonograms.Count > 0)
            WriteCellProgress(connection, transaction, "nonogram_collection_progress", collectionNonograms, backedUpAt);
        if (hashi.Count > 0)
            WriteHashiProgress(connection, transaction, hashi, backedUpAt);
        if (fillAPix.Count > 0)
            WriteFillAPixProgress(connection, transaction, fillAPix, backedUpAt);
        if (slitherlink.Count > 0)
            WriteCellProgress(connection, transaction, "slitherlink_progress", slitherlink, backedUpAt);
        if (nurikabe.Count > 0)
            WriteCellProgress(connection, transaction, "nurikabe_progress", nurikabe, backedUpAt);
        if (galaxies.Count > 0)
            WriteCellProgress(connection, transaction, "galaxies_progress", galaxies, backedUpAt);
        if (placement.Count > 0)
            WriteCellProgress(connection, transaction, "placement_progress", placement, backedUpAt);
        if (link.Count > 0)
            WriteCellProgress(connection, transaction, "link_a_pix_progress", link, backedUpAt);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static void WriteCellProgress(SqliteConnection connection, SqliteTransaction transaction, string table,
        IReadOnlyList<CellProgressUpdate> boards, DateTimeOffset backedUpAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table} (puzzle_id, marks, completed, backed_up_at) VALUES ($id, $marks, $completed, $backedUpAt)
            ON CONFLICT(puzzle_id) DO UPDATE SET marks = excluded.marks, completed = excluded.completed, backed_up_at = excluded.backed_up_at
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var marks = command.Parameters.Add("$marks", SqliteType.Text);
        var completed = command.Parameters.Add("$completed", SqliteType.Integer);
        command.Parameters.AddWithValue("$backedUpAt", backedUpAt.ToString("O", CultureInfo.InvariantCulture));
        foreach (var board in boards)
        {
            id.Value = board.Id.Value;
            marks.Value = board.Marks;
            completed.Value = board.Completed;
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<GameSavedBoard> RestoreGameProgress(PuzzleGame? game, bool includeCollections = true)
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
            if (includeCollections)
                RestoreCollectionNonograms(connection, transaction, boards);
        }
        if (game is null or PuzzleGame.Hashi)
        {
            foreach (var (puzzle, progress) in ReadHashiProgress(connection, transaction))
            {
                if (!includeCollections && puzzle.Metadata.Attribution is not null)
                    continue;
                var definition = LocalHashiPuzzleProvider.ToDefinition(puzzle, includeSolution: true);
                boards.Add(ToSavedBoard(definition, progress.Marks, progress.Completed, progress.BackedUpAt));
            }
        }
        if (game is null or PuzzleGame.FillAPix)
            RestoreFillAPixProgress(connection, transaction, boards);
        if (game is null or PuzzleGame.Slitherlink)
            RestoreSlitherlinkProgress(connection, transaction, boards);
        if (game is null or PuzzleGame.Nurikabe)
            RestoreNurikabeProgress(connection, transaction, boards);
        if (game is null or PuzzleGame.Galaxies)
            RestoreGalaxiesProgress(connection, transaction, boards);
        if (game is null or PuzzleGame.Tents or PuzzleGame.Battleships)
            RestorePlacementProgress(connection, transaction, game, boards);
        if (game is null or PuzzleGame.LinkAPix)
            RestoreLinkAPixProgress(connection, transaction, boards);
        transaction.Commit();
        return boards;
    }

    private static GameSavedBoard ToSavedBoard<T>(GamePuzzleDefinition definition, T marks, bool completed, DateTimeOffset backedUpAt) =>
        new(definition, new GameSavedProgress(definition.Id, definition.SchemaVersion,
            JsonSerializer.SerializeToElement(marks, Json), completed, backedUpAt));
}
