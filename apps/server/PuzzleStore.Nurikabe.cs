using System.Globalization;
using System.Text.Json;
using KoboLogicPuzzles.Server.Nurikabe;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore
{
    internal FileStream? TryLockNurikabe(string operation)
    {
        try
        {
            return File.Open(Path.GetFullPath(databasePath) + $".nurikabe-{operation}.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }

    internal bool SaveNurikabe(NurikabeDefinition puzzle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO nurikabe_puzzles
                (id, width, height, source, source_name, source_kind, difficulty, solvability, title, author, metadata, definition)
            VALUES ($id, $width, $height, $source, $sourceName, $sourceKind, $difficulty, $solvability, $title, $author, $metadata, $definition)
            ON CONFLICT(id) DO NOTHING
            """;
        var metadata = puzzle.Metadata;
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$width", puzzle.Width);
        command.Parameters.AddWithValue("$height", puzzle.Height);
        command.Parameters.AddWithValue("$source", metadata.Source);
        command.Parameters.AddWithValue("$sourceName", metadata.SourceName);
        command.Parameters.AddWithValue("$sourceKind", metadata.SourceKind);
        command.Parameters.AddWithValue("$difficulty", metadata.Difficulty);
        command.Parameters.AddWithValue("$solvability", metadata.Solvability);
        command.Parameters.AddWithValue("$title", puzzle.Title);
        command.Parameters.AddWithValue("$author", (object?)puzzle.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, Json));
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(puzzle, Json));
        return command.ExecuteNonQuery() != 0;
    }

    internal NurikabeDefinition? FindNurikabe(string id, bool unstartedOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition FROM nurikabe_puzzles
            WHERE id = $id AND ($unstarted = 0 OR NOT EXISTS (SELECT 1 FROM nurikabe_progress WHERE puzzle_id = $id))
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        return command.ExecuteScalar() is string value ? JsonSerializer.Deserialize<NurikabeDefinition>(value, Json) : null;
    }

    internal IReadOnlyList<GamePuzzleSummary> ListNurikabeCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.width, p.height, p.title, p.author, p.metadata, s.completed
            FROM nurikabe_puzzles p LEFT JOIN nurikabe_progress s ON s.puzzle_id = p.id
            WHERE ($before IS NULL OR p.id < $before)
                AND ($unstarted = 0 OR s.puzzle_id IS NULL)
                AND ($width IS NULL OR p.width IN (SELECT value FROM json_each($width)))
                AND ($height IS NULL OR p.height IN (SELECT value FROM json_each($height)))
                AND ($difficulty IS NULL OR p.difficulty IN (SELECT value FROM json_each($difficulty)))
                AND ($solvability IS NULL OR p.solvability IN (SELECT value FROM json_each($solvability)))
                AND ($source IS NULL OR p.source IN (SELECT value FROM json_each($source)))
            ORDER BY p.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", (object?)before?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$unstarted", unstartedOnly);
        command.Parameters.AddWithValue("$width", FilterValues(filter.Widths));
        command.Parameters.AddWithValue("$height", FilterValues(filter.Heights));
        command.Parameters.AddWithValue("$difficulty", FilterValues(filter.NurikabeDifficulties));
        command.Parameters.AddWithValue("$solvability", FilterValues(filter.NurikabeSolvabilities));
        command.Parameters.AddWithValue("$source", FilterValues(filter.Sources));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<GamePuzzleSummary>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), "nurikabe", reader.GetInt32(1), reader.GetInt32(2),
                GamePuzzleSummary.PlayStatus(reader.IsDBNull(6) ? null : reader.GetBoolean(6)),
                JsonSerializer.Deserialize<NurikabeMetadata>(reader.GetString(5), Json),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    internal Dictionary<string, PuzzleSource> ListNurikabeSources()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source, min(source_name), source_kind
            FROM nurikabe_puzzles GROUP BY source, source_kind ORDER BY source
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, PuzzleSource>(StringComparer.Ordinal);
        while (reader.Read())
            result.TryAdd(reader.GetString(0), new(reader.GetString(1), reader.GetString(2)));
        return result;
    }

    internal IReadOnlyDictionary<string, object> ListNurikabeMetadata(IReadOnlyList<PuzzleId> ids)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.metadata FROM json_each($ids) AS requested
            JOIN nurikabe_puzzles p ON p.id = requested.value
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value), Json));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0), JsonSerializer.Deserialize<NurikabeMetadata>(reader.GetString(1), Json)!);
        return result;
    }

    internal Dictionary<NurikabeBucket, int> CountUnstartedNurikabe()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.width, p.difficulty, p.solvability, count(*)
            FROM nurikabe_puzzles p LEFT JOIN nurikabe_progress s ON s.puzzle_id = p.id
            WHERE p.source = $source AND p.width = p.height AND s.puzzle_id IS NULL
            GROUP BY p.width, p.difficulty, p.solvability
            """;
        command.Parameters.AddWithValue("$source", NurikabeGenerator.Source);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<NurikabeBucket, int>();
        while (reader.Read())
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)), reader.GetInt32(3));
        return result;
    }

    internal (int NextIndex, long CheckedAt) GetNurikabeRefresh()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT next_index, checked_at FROM nurikabe_collection_refresh WHERE resource = 'janko'";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt64(1)) : (0, 0);
    }

    internal void SaveNurikabeRefresh(int nextIndex, long checkedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO nurikabe_collection_refresh VALUES ('janko', $index, $time)
            ON CONFLICT(resource) DO UPDATE SET checked_at = excluded.checked_at, next_index = excluded.next_index
            """;
        command.Parameters.AddWithValue("$index", nextIndex);
        command.Parameters.AddWithValue("$time", checkedAt);
        command.ExecuteNonQuery();
    }

    private static void RestoreNurikabeProgress(SqliteConnection connection, SqliteTransaction transaction, List<GameSavedBoard> boards)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.definition, s.marks, s.completed, s.backed_up_at
            FROM nurikabe_progress s JOIN nurikabe_puzzles p ON p.id = s.puzzle_id ORDER BY p.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            boards.Add(ToSavedBoard(JsonSerializer.Deserialize<NurikabeDefinition>(reader.GetString(0), Json)!,
                reader.GetString(1), reader.GetBoolean(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
    }
}

internal sealed class NurikabePuzzleProvider(PuzzleStore store) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.Nurikabe;

    public ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var puzzle = store.FindNurikabe(id.Value);
        return ValueTask.FromResult<GamePuzzleDefinition?>(includeSolution || puzzle is null ? puzzle : puzzle with { Solution = null });
    }

    public ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken) =>
        GetDefinitionAsync(id, includeSolution: true, cancellationToken);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListNurikabeCatalog(filter, before, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.FindNurikabe(id.Value, unstartedOnly: true) is { } puzzle && filter.Matches(puzzle);

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) => store.ListNurikabeMetadata(ids);
}
