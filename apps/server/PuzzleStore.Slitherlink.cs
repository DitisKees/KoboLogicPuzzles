using System.Globalization;
using System.Text.Json;
using KoboLogicPuzzles.Server.Slitherlink;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

internal sealed record SlitherlinkCollectionRefreshState(DateTimeOffset CompletedAt, string? Version);

public sealed partial class PuzzleStore
{
    internal SlitherlinkCollectionRefreshState? GetSlitherlinkCollectionRefreshState(string resource)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_at, version FROM slitherlink_collection_refresh WHERE resource = $resource";
        command.Parameters.AddWithValue("$resource", resource);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    internal void SaveSlitherlinkCollectionRefreshState(string resource, DateTimeOffset completedAt, string? version)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO slitherlink_collection_refresh (resource, completed_at, version)
            VALUES ($resource, $completedAt, $version)
            ON CONFLICT(resource) DO UPDATE SET completed_at = excluded.completed_at, version = excluded.version
            """;
        command.Parameters.AddWithValue("$resource", resource);
        command.Parameters.AddWithValue("$completedAt", completedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal bool SaveSlitherlink(SlitherlinkDefinition puzzle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO slitherlink_puzzles (id, width, height, tiling, source, source_name, difficulty, title, author, metadata, definition)
            VALUES ($id, $width, $height, $tiling, $source, $sourceName, $difficulty, $title, $author, $metadata, $definition)
            ON CONFLICT(id) DO NOTHING
            """;
        var metadata = puzzle.Metadata;
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$width", puzzle.Width);
        command.Parameters.AddWithValue("$height", puzzle.Height);
        command.Parameters.AddWithValue("$tiling", metadata.Tiling);
        command.Parameters.AddWithValue("$source", metadata.Source);
        command.Parameters.AddWithValue("$sourceName", metadata.SourceName);
        command.Parameters.AddWithValue("$difficulty", (object?)metadata.Difficulty ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", puzzle.Title);
        command.Parameters.AddWithValue("$author", (object?)puzzle.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, Json));
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(puzzle, Json));
        return command.ExecuteNonQuery() != 0;
    }

    internal SlitherlinkDefinition? FindSlitherlink(string id, bool unstartedOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition FROM slitherlink_puzzles
            WHERE id = $id AND ($unstarted = 0 OR NOT EXISTS (SELECT 1 FROM slitherlink_progress WHERE puzzle_id = $id))
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        return command.ExecuteScalar() is string value ? JsonSerializer.Deserialize<SlitherlinkDefinition>(value, Json) : null;
    }

    internal IReadOnlyList<GamePuzzleSummary> ListSlitherlinkCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.width, p.height, p.title, p.author, p.metadata, s.completed
            FROM slitherlink_puzzles p LEFT JOIN slitherlink_progress s ON s.puzzle_id = p.id
            WHERE ($before IS NULL OR p.id < $before)
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($width IS NULL OR p.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR p.height IN (SELECT value FROM json_each($height)))
                AND ($difficulty IS NULL OR p.difficulty IN (SELECT value FROM json_each($difficulty)))
                AND ($tiling IS NULL OR p.tiling IN (SELECT value FROM json_each($tiling)))
                AND ($source IS NULL OR p.source IN (SELECT value FROM json_each($source)))
            ORDER BY p.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$difficulty", SlitherlinkFilter(filter.SlitherlinkDifficulties));
        command.Parameters.AddWithValue("$tiling", SlitherlinkFilter(filter.Tilings));
        command.Parameters.AddWithValue("$source", SlitherlinkFilter(filter.Sources));
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var result = new List<GamePuzzleSummary>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), "slitherlink", reader.GetInt32(1), reader.GetInt32(2),
                GamePuzzleSummary.PlayStatus(reader.IsDBNull(6) ? null : reader.GetBoolean(6)),
                JsonSerializer.Deserialize<SlitherlinkMetadata>(reader.GetString(5), Json),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    private static object SlitherlinkFilter(string[]? values) =>
        values is null ? DBNull.Value : JsonSerializer.Serialize(values, Json);

    internal Dictionary<string, string> ListSlitherlinkSources()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source, min(source_name) FROM slitherlink_puzzles GROUP BY source ORDER BY source
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    internal IReadOnlyDictionary<string, object> ListSlitherlinkMetadata(IReadOnlyList<PuzzleId> ids)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.metadata FROM json_each($ids) AS requested
            JOIN slitherlink_puzzles p ON p.id = requested.value
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value), Json));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0), JsonSerializer.Deserialize<SlitherlinkMetadata>(reader.GetString(1), Json)!);
        return result;
    }

    internal Dictionary<SlitherlinkBucket, int> CountUnstartedSlitherlink()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.width, p.tiling, p.difficulty, count(*)
            FROM slitherlink_puzzles p LEFT JOIN slitherlink_progress s ON s.puzzle_id = p.id
            WHERE p.source = $source AND p.width = p.height AND p.difficulty IS NOT NULL AND s.puzzle_id IS NULL
            GROUP BY p.width, p.tiling, p.difficulty
            """;
        command.Parameters.AddWithValue("$source", LoopyGenerator.Source);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<SlitherlinkBucket, int>();
        while (reader.Read())
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)), reader.GetInt32(3));
        return result;
    }

    internal FileStream? TryLockSlitherlinkGeneration()
    {
        try
        {
            return File.Open(Path.GetFullPath(databasePath) + ".slitherlink-generation.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }

    private static void RestoreSlitherlinkProgress(SqliteConnection connection, SqliteTransaction transaction, List<GameSavedBoard> boards)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.definition, s.marks, s.completed, s.backed_up_at
            FROM slitherlink_progress s JOIN slitherlink_puzzles p ON p.id = s.puzzle_id ORDER BY p.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            boards.Add(ToSavedBoard(JsonSerializer.Deserialize<SlitherlinkDefinition>(reader.GetString(0), Json)!,
                reader.GetString(1), reader.GetBoolean(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
    }
}

internal sealed class SlitherlinkPuzzleProvider(PuzzleStore store) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.Slitherlink;

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var puzzle = store.FindSlitherlink(id.Value);
        return ValueTask.FromResult<GamePuzzleDefinition?>(includeSolution || puzzle is null ? puzzle : puzzle with { Solution = null });
    }

    public ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken) =>
        GetDefinitionAsync(id, includeSolution: true, cancellationToken);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListSlitherlinkCatalog(filter, before, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindSlitherlink(id.Value, unstartedOnly: true) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) => store.ListSlitherlinkMetadata(ids);
}
