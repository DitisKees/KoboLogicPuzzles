using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KoboNonogram.Server;

public sealed partial class PuzzleStore(string databasePath)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        ForeignKeys = true,
    }.ToString();

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL";
        command.ExecuteNonQuery();

        using var transaction = connection.BeginTransaction(deferred: false);
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = (long)command.ExecuteScalar()!;
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Database schema {version} is newer than the supported schema {SchemaVersion}.");
        if (version == 0)
        {
            command.CommandText = """
                SELECT 1 FROM sqlite_schema
                WHERE type = 'table' AND name IN ('catalog', 'puzzles', 'progress') LIMIT 1
                """;
            if (command.ExecuteScalar() is not null)
                BackupBeforeHashi();
        }

        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'puzzle_metadata'";
        var needsMetadata = command.ExecuteScalar() is null;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS catalog (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                author TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS puzzle_metadata (
                id INTEGER PRIMARY KEY,
                metadata TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS puzzles (
                id INTEGER PRIMARY KEY,
                definition TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS progress (
                puzzle_id INTEGER PRIMARY KEY REFERENCES puzzles(id),
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL,
                backed_up_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS catalog_refresh (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                revision INTEGER NOT NULL,
                completed_at TEXT
            );
            INSERT OR IGNORE INTO catalog_refresh (id, revision) VALUES (1, 0);
            """;
        command.ExecuteNonQuery();
        if (needsMetadata)
        {
            command.CommandText = "UPDATE catalog_refresh SET completed_at = NULL";
            command.ExecuteNonQuery();
        }
        if (version == 0)
            InitializeHashiSchema(command);
        transaction.Commit();
    }

    private void BackupBeforeHashi()
    {
        var backupPath = Path.GetFullPath(databasePath) + ".before-hashi";
        var temporaryPath = backupPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // The migration connection holds the writer lock. A separate connection reads the committed snapshot.
            using (var source = Open())
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Pooling = false,
            }.ToString()))
            {
                backup.Open();
                source.BackupDatabase(backup);
            }
            File.Move(temporaryPath, backupPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal FileStream? TryLockHashiGeneration()
    {
        try
        {
            return File.Open(Path.GetFullPath(databasePath) + ".hashi-generation.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }

    public CatalogRefreshState GetCatalogRefreshState()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, completed_at FROM catalog_refresh WHERE id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("The catalog refresh state is missing.");

        return new CatalogRefreshState(reader.GetInt64(0), reader.IsDBNull(1) ? null :
            DateTimeOffset.ParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture));
    }

    public bool TryReplaceCatalog(IEnumerable<PuzzleSummary> puzzles, long expectedRevision, DateTimeOffset completedAt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE catalog_refresh SET revision = revision + 1, completed_at = $completedAt
            WHERE id = 1 AND revision = $expectedRevision
            """;
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        if (command.ExecuteNonQuery() != 1)
            return false;

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM catalog";
        command.ExecuteNonQuery();

        command.CommandText = """
            INSERT INTO catalog (id, title, author, width, height) VALUES ($id, $title, $author, $width, $height)
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var author = command.Parameters.Add("$author", SqliteType.Text);
        var width = command.Parameters.Add("$width", SqliteType.Integer);
        var height = command.Parameters.Add("$height", SqliteType.Integer);
        using var saveMetadata = connection.CreateCommand();
        saveMetadata.Transaction = transaction;
        saveMetadata.CommandText = """
            INSERT INTO puzzle_metadata (id, metadata) VALUES ($id, $metadata)
            ON CONFLICT(id) DO UPDATE SET metadata = excluded.metadata
            """;
        var metadataId = saveMetadata.Parameters.Add("$id", SqliteType.Integer);
        var metadata = saveMetadata.Parameters.Add("$metadata", SqliteType.Text);
        foreach (var puzzle in puzzles)
        {
            id.Value = puzzle.Id;
            title.Value = puzzle.Title;
            author.Value = puzzle.Author;
            width.Value = puzzle.Width;
            height.Value = puzzle.Height;
            command.ExecuteNonQuery();
            if (puzzle.Metadata is not null)
            {
                metadataId.Value = puzzle.Id;
                metadata.Value = JsonSerializer.Serialize(puzzle.Metadata, Json);
                saveMetadata.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return true;
    }

    public IReadOnlyList<PuzzleSummary> ListCatalog(int? beforeId, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, author, width, height, metadata FROM catalog LEFT JOIN puzzle_metadata USING (id)
            WHERE $before IS NULL OR id < $before ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$before", beforeId.HasValue ? beforeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var puzzles = new List<PuzzleSummary>();
        while (reader.Read())
        {
            puzzles.Add(new PuzzleSummary(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), ReadMetadata(reader, 5)));
        }

        return puzzles;
    }

    public IReadOnlyDictionary<int, PuzzleMetadata> ListMetadata(int[] ids)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT requested.value, metadata, json_extract(definition, '$.metadata')
            FROM json_each($ids) AS requested
            LEFT JOIN puzzle_metadata ON puzzle_metadata.id = requested.value
            LEFT JOIN puzzles ON puzzles.id = requested.value
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids, Json));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<int, PuzzleMetadata>();
        while (reader.Read())
        {
            var metadata = MergeMetadata(ReadMetadata(reader, 1), ReadMetadata(reader, 2));
            if (metadata is not null)
                result[reader.GetInt32(0)] = metadata;
        }
        return result;
    }

    public bool IsInCatalog(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM catalog WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<int> ListUnstartedIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM catalog WHERE NOT EXISTS (SELECT 1 FROM progress WHERE puzzle_id = catalog.id)
            ORDER BY id DESC
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));

        return ids;
    }

    public PuzzleDefinition? FindPuzzle(int id)
    {
        using var connection = Open();
        return FindPuzzle(connection, id);
    }

    public PuzzleDefinition CachePuzzle(PuzzleDefinition puzzle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO puzzles (id, definition) VALUES ($id, $definition) ON CONFLICT(id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$id", puzzle.Id);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(puzzle, Json));
        command.ExecuteNonQuery();
        return FindPuzzle(connection, puzzle.Id)!;
    }

    public PuzzleDefinition CacheSolution(int id, string solution)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE puzzles SET definition = json_set(definition, '$.solution', $solution)
            WHERE id = $id AND json_extract(definition, '$.solution') IS NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$solution", solution);
        command.ExecuteNonQuery();
        return FindPuzzle(connection, id)!;
    }

    public void SaveProgress(IEnumerable<ProgressUpdate> boards, DateTimeOffset backedUpAt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        WriteNonogramProgress(connection, transaction, boards, backedUpAt);
        transaction.Commit();
    }

    private static void WriteNonogramProgress(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<ProgressUpdate> boards, DateTimeOffset backedUpAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO progress (puzzle_id, marks, completed, backed_up_at)
            VALUES ($id, $marks, $completed, $backedUpAt)
            ON CONFLICT(puzzle_id) DO UPDATE SET marks = excluded.marks,
                completed = excluded.completed, backed_up_at = excluded.backed_up_at
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var marks = command.Parameters.Add("$marks", SqliteType.Text);
        var completed = command.Parameters.Add("$completed", SqliteType.Integer);
        command.Parameters.AddWithValue("$backedUpAt", backedUpAt.ToString("O", CultureInfo.InvariantCulture));
        foreach (var board in boards)
        {
            id.Value = board.PuzzleId;
            marks.Value = board.Marks;
            completed.Value = board.Completed;
            command.ExecuteNonQuery();
        }

    }

    public IReadOnlyList<SavedBoard> RestoreProgress()
    {
        using var connection = Open();
        return ReadNonogramProgress(connection, null);
    }

    private static IReadOnlyList<SavedBoard> ReadNonogramProgress(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT definition, puzzle_id, marks, completed, backed_up_at, metadata
            FROM progress JOIN puzzles ON puzzles.id = progress.puzzle_id
            LEFT JOIN puzzle_metadata USING (id) ORDER BY puzzle_id
            """;
        using var reader = command.ExecuteReader();
        var boards = new List<SavedBoard>();
        while (reader.Read())
        {
            boards.Add(new SavedBoard(ReadDefinition(reader, 0, 5), new SavedProgress(
                reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3),
                DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture))));
        }

        return boards;
    }

    private static PuzzleDefinition? FindPuzzle(SqliteConnection connection, int id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition, metadata FROM puzzles LEFT JOIN puzzle_metadata USING (id) WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDefinition(reader, 0, 1) : null;
    }

    private static PuzzleDefinition ReadDefinition(SqliteDataReader reader, int definitionColumn, int metadataColumn)
    {
        var puzzle = JsonSerializer.Deserialize<PuzzleDefinition>(reader.GetString(definitionColumn), Json) ??
            throw new InvalidOperationException("Missing cached definition.");
        return puzzle with { Metadata = MergeMetadata(ReadMetadata(reader, metadataColumn), puzzle.Metadata) };
    }

    private static PuzzleMetadata? ReadMetadata(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : JsonSerializer.Deserialize<PuzzleMetadata>(reader.GetString(column), Json);

    private static PuzzleMetadata? MergeMetadata(PuzzleMetadata? catalog, PuzzleMetadata? cached) =>
        catalog is null ? cached : catalog with { Solvability = catalog.Solvability ?? cached?.Solvability };

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
