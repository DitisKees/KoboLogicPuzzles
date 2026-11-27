using System.Globalization;
using System.Text.Json;
using KoboLogicPuzzles.Server.Galaxies;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal FileStream? TryLockGalaxiesRefresh()
    {
        try
        {
            return File.Open(Path.GetFullPath(databasePath) + ".galaxies-refresh.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }

    internal bool SaveGalaxies(GalaxiesDefinition puzzle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO galaxies_puzzles
                (id, content_hash, width, height, source, source_name, source_kind, picture, difficulty, solvability, title, author, metadata, definition)
            VALUES ($id, $hash, $width, $height, $source, $sourceName, $sourceKind, $picture, $difficulty, $solvability, $title, $author, $metadata, $definition)
            ON CONFLICT(id) DO NOTHING
            """;
        var metadata = puzzle.Metadata;
        command.Parameters.AddWithValue("$hash", metadata.ContentHash);
        command.Parameters.AddWithValue("$picture", metadata.Picture);
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$width", puzzle.Width);
        command.Parameters.AddWithValue("$height", puzzle.Height);
        command.Parameters.AddWithValue("$source", metadata.Source);
        command.Parameters.AddWithValue("$sourceName", metadata.SourceName);
        command.Parameters.AddWithValue("$sourceKind", metadata.SourceKind);
        command.Parameters.AddWithValue("$difficulty", metadata.Difficulty);
        command.Parameters.AddWithValue("$solvability", metadata.Solvability);
        command.Parameters.AddWithValue("$title", puzzle.Title);
        command.Parameters.AddWithValue("$author", puzzle.Author);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, Json));
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(puzzle, Json));
        return command.ExecuteNonQuery() != 0;
    }

    internal GalaxiesDefinition? FindGalaxies(string id, bool unstartedOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition FROM galaxies_puzzles
            WHERE id = $id AND ($unstarted = 0 OR NOT EXISTS (SELECT 1 FROM galaxies_progress WHERE puzzle_id = $id))
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        return command.ExecuteScalar() is string value ? JsonSerializer.Deserialize<GalaxiesDefinition>(value, Json) : null;
    }

    internal IReadOnlyList<GamePuzzleSummary> ListGalaxiesCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.width, p.height, p.title, p.author, p.metadata, s.completed
            FROM galaxies_puzzles p LEFT JOIN galaxies_progress s ON s.puzzle_id = p.id
            WHERE ($before IS NULL OR (p.catalog_rank, p.id) < (coalesce(
                (SELECT catalog_rank FROM galaxies_puzzles WHERE id = $before), 3), $before))
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($square = 0 OR p.width = p.height)
                AND ($width IS NULL OR p.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR p.height IN (SELECT value FROM json_each($height)))
                AND ($difficulty IS NULL OR p.difficulty IN (SELECT value FROM json_each($difficulty)))
                AND ($solvability IS NULL OR p.solvability IN (SELECT value FROM json_each($solvability)))
                AND ($source IS NULL OR p.source IN (SELECT value FROM json_each($source)))
            ORDER BY p.catalog_rank DESC, p.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$square", filter.SquareOnly);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$difficulty", FilterValues(filter.GalaxiesDifficulties));
        command.Parameters.AddWithValue("$solvability", FilterValues(filter.GalaxiesSolvabilities));
        command.Parameters.AddWithValue("$source", FilterValues(filter.Sources));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<GamePuzzleSummary>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), "galaxies", reader.GetInt32(1), reader.GetInt32(2),
                GamePuzzleSummary.PlayStatus(reader.IsDBNull(6) ? null : reader.GetBoolean(6)),
                JsonSerializer.Deserialize<GalaxiesMetadata>(reader.GetString(5), Json),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    internal IReadOnlyDictionary<string, object> ListGalaxiesMetadata(IReadOnlyList<PuzzleId> ids)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.metadata FROM json_each($ids) AS requested
            JOIN galaxies_puzzles p ON p.id = requested.value
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value), Json));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0), JsonSerializer.Deserialize<GalaxiesMetadata>(reader.GetString(1), Json)!);
        return result;
    }

    internal (string Cursor, long CompletedAt) GetGalaxiesRefresh(string resource)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor, completed_at FROM galaxies_collection_refresh WHERE resource = $resource";
        command.Parameters.AddWithValue("$resource", resource);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : ("", 0);
    }

    internal void SaveGalaxiesRefresh(string resource, string cursor, long completedAt = 0)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO galaxies_collection_refresh VALUES ($resource, $cursor, $time)
            ON CONFLICT(resource) DO UPDATE SET cursor = excluded.cursor, completed_at = excluded.completed_at
            """;
        command.Parameters.AddWithValue("$resource", resource);
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$time", completedAt);
        command.ExecuteNonQuery();
    }

    private static void RestoreGalaxiesProgress(SqliteConnection connection, SqliteTransaction transaction, List<GameSavedBoard> boards)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.definition, s.marks, s.completed, s.backed_up_at
            FROM galaxies_progress s JOIN galaxies_puzzles p ON p.id = s.puzzle_id ORDER BY p.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            boards.Add(ToSavedBoard(JsonSerializer.Deserialize<GalaxiesDefinition>(reader.GetString(0), Json)!,
                reader.GetString(1), reader.GetBoolean(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
    }
}

internal sealed class GalaxiesPuzzleProvider(PuzzleStore store) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.Galaxies;

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var puzzle = store.FindGalaxies(id.Value);
        return ValueTask.FromResult<GamePuzzleDefinition?>(includeSolution || puzzle is null ? puzzle : puzzle with { Solution = null });
    }

    public ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken) =>
        GetDefinitionAsync(id, includeSolution: true, cancellationToken);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListGalaxiesCatalog(filter, before, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindGalaxies(id.Value, unstartedOnly: true) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) => store.ListGalaxiesMetadata(ids);
}
