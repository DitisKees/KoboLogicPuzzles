using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal bool SaveCollectionNonogram(NonogramPuzzleDefinition puzzle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO nonogram_collection_puzzles (id, source, width, height, definition)
            VALUES ($id, $source, $width, $height, $definition) ON CONFLICT(id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$source", puzzle.Metadata!.Source!);
        command.Parameters.AddWithValue("$width", puzzle.Width);
        command.Parameters.AddWithValue("$height", puzzle.Height);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(puzzle, Json));
        return command.ExecuteNonQuery() == 1;
    }

    internal NonogramPuzzleDefinition? FindCollectionNonogram(string id, bool unstartedOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition FROM nonogram_collection_puzzles WHERE id = $id
                AND ($unstarted = 0 OR NOT EXISTS (SELECT 1 FROM nonogram_collection_progress WHERE puzzle_id = $id))
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        return command.ExecuteScalar() is string value ? JsonSerializer.Deserialize<NonogramPuzzleDefinition>(value, Json) : null;
    }

    internal IReadOnlyList<GamePuzzleSummary> ListCollectionNonograms(PuzzleFilter filter, string? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.width, p.height, json_extract(p.definition, '$.title'),
                json_extract(p.definition, '$.author'), json_extract(p.definition, '$.metadata'), s.completed
            FROM nonogram_collection_puzzles p LEFT JOIN nonogram_collection_progress s ON s.puzzle_id = p.id
            WHERE ($before IS NULL OR p.id < $before)
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($width IS NULL OR p.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR p.height IN (SELECT value FROM json_each($height)))
                AND ($source IS NULL OR p.source IN (SELECT value FROM json_each($source)))
                AND ($difficulty IS NULL OR 4 * json_extract(p.definition, '$.metadata.difficulty') IN (SELECT value FROM json_each($difficulty)))
                AND ($solvability IS NULL OR json_extract(p.definition, '$.metadata.solvability') IN (SELECT value FROM json_each($solvability)))
            ORDER BY p.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$source", FilterValues(filter.Sources));
        command.Parameters.AddWithValue("$difficulty", FilterValues(filter.Difficulties));
        command.Parameters.AddWithValue("$solvability", FilterValues(filter.Solvabilities));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<GamePuzzleSummary>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), "nonogram", reader.GetInt32(1), reader.GetInt32(2),
                GamePuzzleSummary.PlayStatus(reader.IsDBNull(6) ? null : reader.GetBoolean(6)),
                ReadMetadata(reader, 5), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    internal void AddCollectionNonogramMetadata(IReadOnlyList<PuzzleId> ids, Dictionary<string, object> metadata)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, json_extract(p.definition, '$.metadata') FROM json_each($ids) AS requested
            JOIN nonogram_collection_puzzles p ON p.id = requested.value
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Where(id => id.WebPbnId is null).Select(id => id.Value), Json));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            metadata.Add(reader.GetString(0), ReadMetadata(reader, 1)!);
    }

    private static void RestoreCollectionNonograms(SqliteConnection connection, SqliteTransaction transaction, List<GameSavedBoard> boards)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.definition, s.marks, s.completed, s.backed_up_at FROM nonogram_collection_progress s
            JOIN nonogram_collection_puzzles p ON p.id = s.puzzle_id ORDER BY p.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            boards.Add(ToSavedBoard(JsonSerializer.Deserialize<NonogramPuzzleDefinition>(reader.GetString(0), Json)!,
                reader.GetString(1), reader.GetBoolean(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
    }

    internal (int Cursor, long CompletedAt) ReadExtraCollectionRefresh(string resource)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor, completed_at FROM extra_collection_refresh WHERE resource = $resource";
        command.Parameters.AddWithValue("$resource", resource);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt64(1)) : (0, 0);
    }

    internal void SaveExtraCollectionRefresh(string resource, int cursor, long completedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO extra_collection_refresh VALUES ($resource, $cursor, $completedAt)
            ON CONFLICT(resource) DO UPDATE SET cursor = excluded.cursor, completed_at = excluded.completed_at
            """;
        command.Parameters.AddWithValue("$resource", resource);
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$completedAt", completedAt);
        command.ExecuteNonQuery();
    }

    internal FileStream? TryLockExtraCollection(string resource)
    {
        try
        {
            return File.Open(Path.GetFullPath(databasePath) + "." + resource + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }
}
