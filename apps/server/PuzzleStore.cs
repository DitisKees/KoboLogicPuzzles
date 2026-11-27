using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server;

public sealed partial class PuzzleStore(string databasePath)
{
    private const int SchemaVersion = 8;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        ForeignKeys = true,
    }.ToString();

    internal bool HasCurrentSchema()
    {
        var probeSettings = new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        };
        using var connection = new SqliteConnection(probeSettings.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return command.ExecuteScalar() is long version && version == SchemaVersion;
    }

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
        if (version == SchemaVersion)
        {
            transaction.Commit();
            return;
        }
        if (version == 0)
        {
            command.CommandText = "SELECT 1 FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' LIMIT 1";
            if (command.ExecuteScalar() is not null)
                throw new InvalidOperationException("The database must use a versioned schema.");

            command.CommandText = """
            CREATE TABLE catalog (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                author TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL
            );
            CREATE TABLE puzzle_metadata (
                id INTEGER PRIMARY KEY,
                metadata TEXT NOT NULL
            );
            CREATE TABLE puzzles (
                id INTEGER PRIMARY KEY,
                definition TEXT NOT NULL
            );
            CREATE TABLE progress (
                puzzle_id INTEGER PRIMARY KEY REFERENCES puzzles(id),
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL,
                backed_up_at TEXT NOT NULL
            );
            CREATE TABLE catalog_refresh (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                revision INTEGER NOT NULL,
                completed_at TEXT
            );
            INSERT INTO catalog_refresh (id, revision) VALUES (1, 0);
            CREATE TABLE hashi_puzzles (
                content_hash TEXT NOT NULL PRIMARY KEY CHECK (length(content_hash) = 64),
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                islands TEXT NOT NULL,
                solution BLOB NOT NULL,
                metadata TEXT NOT NULL
            );
            CREATE TABLE hashi_progress (
                content_hash TEXT NOT NULL PRIMARY KEY REFERENCES hashi_puzzles(content_hash) ON DELETE RESTRICT,
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                backed_up_at TEXT NOT NULL
            );
            """;
            command.ExecuteNonQuery();
        }

        if (version < 2)
        {
            command.CommandText = """
            CREATE TABLE fill_a_pix_puzzles (
                id TEXT NOT NULL PRIMARY KEY,
                source TEXT NOT NULL,
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                title TEXT NOT NULL,
                author TEXT,
                metadata TEXT NOT NULL,
                definition TEXT,
                seen_revision INTEGER NOT NULL,
                active INTEGER NOT NULL CHECK (active IN (0, 1))
            );
            CREATE INDEX fill_a_pix_puzzles_source ON fill_a_pix_puzzles (source, active);
            CREATE TABLE fill_a_pix_refresh (
                source TEXT NOT NULL PRIMARY KEY,
                revision INTEGER NOT NULL,
                completed_at TEXT,
                next_page INTEGER NOT NULL DEFAULT 1
            );
            INSERT INTO fill_a_pix_refresh (source, revision) VALUES ('grandgames', 0);
            CREATE TABLE fill_a_pix_progress (
                puzzle_id TEXT NOT NULL PRIMARY KEY REFERENCES fill_a_pix_puzzles(id),
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                backed_up_at TEXT NOT NULL
            );
            """;
            command.ExecuteNonQuery();
        }

        if (version < 3)
        {
            command.CommandText = """
            CREATE TABLE slitherlink_puzzles (
                id TEXT NOT NULL PRIMARY KEY,
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                tiling TEXT NOT NULL,
                source TEXT NOT NULL,
                source_name TEXT NOT NULL,
                difficulty TEXT,
                title TEXT NOT NULL,
                author TEXT,
                metadata TEXT NOT NULL,
                definition TEXT NOT NULL
            );
            CREATE INDEX slitherlink_inventory ON slitherlink_puzzles (source, width, height, tiling, difficulty);
            CREATE TABLE slitherlink_progress (
                puzzle_id TEXT NOT NULL PRIMARY KEY REFERENCES slitherlink_puzzles(id),
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                backed_up_at TEXT NOT NULL
            );
            """;
            command.ExecuteNonQuery();
        }

        if (version < 4)
        {
            command.CommandText = """
                CREATE TABLE slitherlink_collection_refresh (
                    resource TEXT NOT NULL PRIMARY KEY,
                    completed_at INTEGER NOT NULL,
                    version TEXT
                );
                """;
            command.ExecuteNonQuery();
        }

        if (version < 5)
        {
            command.CommandText = """
                CREATE TABLE nurikabe_puzzles (
                    id TEXT NOT NULL PRIMARY KEY,
                    width INTEGER NOT NULL CHECK (width BETWEEN 1 AND 50),
                    height INTEGER NOT NULL CHECK (height BETWEEN 1 AND 50),
                    source TEXT NOT NULL,
                    source_name TEXT NOT NULL,
                    source_kind TEXT NOT NULL CHECK (source_kind IN ('generated', 'collection')),
                    difficulty TEXT NOT NULL,
                    solvability TEXT NOT NULL,
                    title TEXT NOT NULL,
                    author TEXT,
                    metadata TEXT NOT NULL,
                    definition TEXT NOT NULL
                );
                CREATE INDEX nurikabe_inventory ON nurikabe_puzzles (source, width, height, difficulty, solvability);
                CREATE TABLE nurikabe_progress (
                    puzzle_id TEXT NOT NULL PRIMARY KEY REFERENCES nurikabe_puzzles(id),
                    marks TEXT NOT NULL,
                    completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                    backed_up_at TEXT NOT NULL
                );
                CREATE TABLE nurikabe_collection_refresh (
                    resource TEXT NOT NULL PRIMARY KEY,
                    checked_at INTEGER NOT NULL,
                    next_index INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        if (version < 6)
        {
            command.CommandText = """
                CREATE TABLE nonogram_collection_puzzles (
                    id TEXT NOT NULL PRIMARY KEY,
                    source TEXT NOT NULL,
                    width INTEGER NOT NULL CHECK (width BETWEEN 1 AND 100),
                    height INTEGER NOT NULL CHECK (height BETWEEN 1 AND 100),
                    definition TEXT NOT NULL
                );
                CREATE INDEX nonogram_collection_source ON nonogram_collection_puzzles (source, width, height);
                CREATE TABLE nonogram_collection_progress (
                    puzzle_id TEXT NOT NULL PRIMARY KEY REFERENCES nonogram_collection_puzzles(id),
                    marks TEXT NOT NULL,
                    completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                    backed_up_at TEXT NOT NULL
                );
                CREATE TABLE extra_collection_refresh (
                    resource TEXT NOT NULL PRIMARY KEY,
                    cursor INTEGER NOT NULL,
                    completed_at INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        if (version < 7)
        {
            command.CommandText = """
                CREATE TABLE galaxies_puzzles (
                    id TEXT NOT NULL PRIMARY KEY,
                    content_hash TEXT NOT NULL UNIQUE,
                    width INTEGER NOT NULL,
                    height INTEGER NOT NULL,
                    source TEXT NOT NULL,
                    source_name TEXT NOT NULL,
                    source_kind TEXT NOT NULL,
                    picture INTEGER NOT NULL CHECK (picture IN (0, 1)),
                    catalog_rank INTEGER GENERATED ALWAYS AS (CASE
                        WHEN picture = 0 THEN 0
                        WHEN width >= 30 AND height >= 30 THEN 3
                        WHEN width * height >= 400 THEN 2
                        ELSE 1 END) VIRTUAL,
                    difficulty TEXT NOT NULL,
                    solvability TEXT NOT NULL,
                    title TEXT NOT NULL,
                    author TEXT NOT NULL,
                    metadata TEXT NOT NULL,
                    definition TEXT NOT NULL
                );
                CREATE INDEX galaxies_catalog_order ON galaxies_puzzles (catalog_rank DESC, id DESC);
                CREATE INDEX galaxies_source ON galaxies_puzzles (source, width, height, difficulty, solvability);
                CREATE TABLE galaxies_progress (
                    puzzle_id TEXT NOT NULL PRIMARY KEY REFERENCES galaxies_puzzles(id),
                    marks TEXT NOT NULL,
                    completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                    backed_up_at TEXT NOT NULL
                );
                CREATE TABLE galaxies_collection_refresh (
                    resource TEXT NOT NULL PRIMARY KEY,
                    cursor TEXT NOT NULL,
                    completed_at INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        command.CommandText = $"""
            CREATE TABLE placement_puzzles (
                id TEXT NOT NULL PRIMARY KEY,
                game TEXT NOT NULL CHECK (game IN ('tents', 'battleships')),
                content_hash TEXT NOT NULL UNIQUE,
                width INTEGER NOT NULL CHECK (width BETWEEN 1 AND 50),
                height INTEGER NOT NULL CHECK (height BETWEEN 1 AND 50),
                source TEXT NOT NULL,
                source_name TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                difficulty TEXT NOT NULL,
                solvability TEXT NOT NULL,
                title TEXT NOT NULL,
                author TEXT,
                metadata TEXT NOT NULL,
                definition TEXT NOT NULL
            );
            CREATE INDEX placement_catalog ON placement_puzzles (game, id DESC);
            CREATE INDEX placement_stock ON placement_puzzles (game, source, width, height, difficulty);
            CREATE TABLE placement_progress (
                puzzle_id TEXT NOT NULL PRIMARY KEY REFERENCES placement_puzzles(id),
                marks TEXT NOT NULL,
                completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
                backed_up_at TEXT NOT NULL
            );
            CREATE TABLE placement_collection_refresh (
                game TEXT NOT NULL PRIMARY KEY,
                cursor TEXT NOT NULL,
                checked_at INTEGER NOT NULL
            );
            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
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
