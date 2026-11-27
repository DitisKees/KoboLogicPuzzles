using System.Globalization;
using System.Text.Json;
using KoboLogicPuzzles.Server.Placement;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal bool SavePlacement(PlacementDefinition puzzle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO placement_puzzles
                (id, game, content_hash, width, height, source, source_name, source_kind, difficulty, solvability, title, author, metadata, definition)
            VALUES ($id, $game, $hash, $width, $height, $source, $name, $kind, $difficulty, $logic, $title, $author, $metadata, $definition)
            ON CONFLICT(id) DO NOTHING
            """;
        if (!PuzzleId.TryParse(puzzle.Id, out var id) || !PlacementBoard.Games.Contains(id.Game))
            throw new ArgumentException("Invalid placement puzzle ID.", nameof(puzzle));
        var metadata = puzzle.Metadata;
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$game", PlacementBoard.Slug(id.Game));
        command.Parameters.AddWithValue("$hash", metadata.ContentHash);
        command.Parameters.AddWithValue("$width", puzzle.Width);
        command.Parameters.AddWithValue("$height", puzzle.Height);
        command.Parameters.AddWithValue("$source", metadata.Source);
        command.Parameters.AddWithValue("$name", metadata.SourceName);
        command.Parameters.AddWithValue("$kind", metadata.SourceKind);
        command.Parameters.AddWithValue("$difficulty", metadata.Difficulty);
        command.Parameters.AddWithValue("$logic", metadata.Solvability);
        command.Parameters.AddWithValue("$title", puzzle.Title);
        command.Parameters.AddWithValue("$author", (object?)puzzle.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, Json));
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize<GamePuzzleDefinition>(puzzle, Json));
        return command.ExecuteNonQuery() > 0;
    }

    internal PlacementDefinition? FindPlacement(PuzzleId id, bool unstartedOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition FROM placement_puzzles
            WHERE id = $id AND ($unstarted = 0 OR NOT EXISTS (SELECT 1 FROM placement_progress WHERE puzzle_id = $id))
            """;
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        return command.ExecuteScalar() is string value ? ReadPlacement(value) : null;
    }

    private static PlacementDefinition ReadPlacement(string value) =>
        (PlacementDefinition)JsonSerializer.Deserialize<GamePuzzleDefinition>(value, Json)!;

    internal IReadOnlyList<GamePuzzleSummary> ListPlacementCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.width, p.height, p.title, p.author, p.metadata, s.completed
            FROM placement_puzzles p LEFT JOIN placement_progress s ON s.puzzle_id = p.id
            WHERE p.game = $game AND ($before IS NULL OR p.id < $before)
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($square = 0 OR p.width = p.height)
                AND ($width IS NULL OR p.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR p.height IN (SELECT value FROM json_each($height)))
                AND ($difficulty IS NULL OR p.difficulty IN (SELECT value FROM json_each($difficulty)))
                AND ($logic IS NULL OR p.solvability IN (SELECT value FROM json_each($logic)))
                AND ($source IS NULL OR p.source IN (SELECT value FROM json_each($source)))
            ORDER BY p.id DESC LIMIT $limit
            """;
        var game = PlacementBoard.Slug(filter.Game);
        command.Parameters.AddWithValue("$game", game);
        command.Parameters.AddWithValue("$before", (object?)before?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$square", filter.SquareOnly);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$difficulty", FilterValues(filter.PlacementDifficulties));
        command.Parameters.AddWithValue("$logic", FilterValues(filter.PlacementSolvabilities));
        command.Parameters.AddWithValue("$source", FilterValues(filter.Sources));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<GamePuzzleSummary>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), game, reader.GetInt32(1), reader.GetInt32(2),
                GamePuzzleSummary.PlayStatus(reader.IsDBNull(6) ? null : reader.GetBoolean(6)),
                JsonSerializer.Deserialize<PlacementMetadata>(reader.GetString(5), Json),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    internal IReadOnlyDictionary<string, object> ListPlacementMetadata(IReadOnlyList<PuzzleId> ids)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.metadata FROM json_each($ids) AS requested
            JOIN placement_puzzles p ON p.id = requested.value
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value), Json));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0), JsonSerializer.Deserialize<PlacementMetadata>(reader.GetString(1), Json)!);
        return result;
    }

    internal PlacementStock[] CountUnstartedPlacement(PuzzleGame game)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.width, p.difficulty, p.solvability, count(*) FROM placement_puzzles p
            LEFT JOIN placement_progress s ON s.puzzle_id = p.id
            WHERE p.game = $game AND p.source = $source AND p.width = p.height AND s.puzzle_id IS NULL
            GROUP BY p.width, p.difficulty, p.solvability
            """;
        command.Parameters.AddWithValue("$game", PlacementBoard.Slug(game));
        command.Parameters.AddWithValue("$source", PlacementGenerator.Source(game));
        using var reader = command.ExecuteReader();
        var counts = new List<PlacementStock>();
        while (reader.Read())
            counts.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return counts.ToArray();
    }

    internal FileStream? TryLockPlacement(PuzzleGame game, string operation)
    {
        try
        {
            return File.Open(Path.GetFullPath(databasePath) + $".{PlacementBoard.Slug(game)}-{operation}.lock",
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }

    internal (string Cursor, long CheckedAt) GetPlacementRefresh(PuzzleGame game)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor, checked_at FROM placement_collection_refresh WHERE game = $game";
        command.Parameters.AddWithValue("$game", PlacementBoard.Slug(game));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : ("", 0);
    }

    internal void SavePlacementRefresh(PuzzleGame game, string cursor, long checkedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO placement_collection_refresh VALUES ($game, $cursor, $time)
            ON CONFLICT(game) DO UPDATE SET cursor = excluded.cursor, checked_at = excluded.checked_at
            """;
        command.Parameters.AddWithValue("$game", PlacementBoard.Slug(game));
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$time", checkedAt);
        command.ExecuteNonQuery();
    }

    private static void RestorePlacementProgress(SqliteConnection connection, SqliteTransaction transaction, PuzzleGame? game,
        List<GameSavedBoard> boards)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.definition, s.marks, s.completed, s.backed_up_at
            FROM placement_progress s JOIN placement_puzzles p ON p.id = s.puzzle_id
            WHERE ($game IS NULL OR p.game = $game) ORDER BY p.id
            """;
        command.Parameters.AddWithValue("$game", game is null ? DBNull.Value : PlacementBoard.Slug(game.Value));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            boards.Add(ToSavedBoard(ReadPlacement(reader.GetString(0)), reader.GetString(1), reader.GetBoolean(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
    }
}

internal sealed class PlacementPuzzleProvider(PuzzleStore store, PuzzleGame game) : IPuzzleProvider
{
    public PuzzleGame Game => game;

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var puzzle = store.FindPlacement(id);
        return ValueTask.FromResult<GamePuzzleDefinition?>(includeSolution || puzzle is null ? puzzle : puzzle with { Solution = null });
    }

    public ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken) =>
        GetDefinitionAsync(id, true, cancellationToken);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListPlacementCatalog(filter, before, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindPlacement(id, unstartedOnly: true) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) => store.ListPlacementMetadata(ids);
}
