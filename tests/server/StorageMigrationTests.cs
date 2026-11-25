using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class StorageMigrationTests
{
    private static readonly DateTimeOffset SavedAt = new(2026, 11, 24, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentStartupUpgradesInPlaceAndBacksUpTheCommittedLegacyRows()
    {
        using var database = new TestDatabase(initialize: false);
        SeedLegacy(database);
        var rows = SnapshotLegacyRows(database.Path);
        var backup = database.Path + ".before-hashi";

        await Task.WhenAll(Task.Run(database.Store.Initialize), Task.Run(new PuzzleStore(database.Path).Initialize));
        database.Store.Initialize();

        Assert.Equal(1L, Scalar(database.Path, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(backup, "PRAGMA user_version"));
        Assert.Equal("ok", Scalar(backup, "PRAGMA integrity_check"));
        Assert.Equal(rows, SnapshotLegacyRows(database.Path));
        Assert.Equal(rows, SnapshotLegacyRows(backup));
        Assert.Equal(2L, Scalar(database.Path, """
            SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('hashi_puzzles', 'hashi_progress')
            """));
        Assert.Equal(new CatalogRefreshState(2, SavedAt.AddDays(1)), database.Store.GetCatalogRefreshState());

        var boards = database.Store.RestoreProgress();
        Assert.Equal(new SavedProgress(12, "#x....", false, SavedAt), boards[0].Progress);
        Assert.Equal(new SavedProgress(21, "#.#.##", true, SavedAt), boards[1].Progress);
        Assert.Equal("#.#.##", boards[1].Puzzle.Solution);
        Assert.Equal(new PuzzleMetadata(4, 5, 3), boards[1].Puzzle.Metadata);
    }

    [Fact]
    public void FailedMigrationRollsBackAndRetrySnapshotsTheCurrentRows()
    {
        using var database = new TestDatabase(initialize: false);
        SeedLegacy(database);
        Execute(database.Path, "CREATE TABLE hashi_progress (unexpected INTEGER)");
        var rows = SnapshotLegacyRows(database.Path);

        Assert.Throws<SqliteException>(database.Store.Initialize);
        Assert.Equal(0L, Scalar(database.Path, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(database.Path, "SELECT count(*) FROM sqlite_schema WHERE name = 'hashi_puzzles'"));
        Assert.Equal(rows, SnapshotLegacyRows(database.Path));

        Execute(database.Path, "DROP TABLE hashi_progress");
        database.Store.SaveProgress([new ProgressUpdate(12, "#xx...", false)], SavedAt.AddMinutes(1));
        var latest = SnapshotLegacyRows(database.Path);
        database.Store.Initialize();

        Assert.Equal(1L, Scalar(database.Path, "PRAGMA user_version"));
        Assert.Equal(latest, SnapshotLegacyRows(database.Path));
        Assert.Equal(latest, SnapshotLegacyRows(database.Path + ".before-hashi"));
    }

    [Fact]
    public void FreshDatabaseCreatesBothSchemasWithoutALegacyBackup()
    {
        using var database = new TestDatabase();
        Assert.Equal(1L, Scalar(database.Path, "PRAGMA user_version"));
        Assert.False(File.Exists(database.Path + ".before-hashi"));
        Assert.Empty(database.Store.RestoreProgress());
        Assert.Empty(database.Store.RestoreHashiProgress());
    }

    [Fact]
    public void NewerDatabaseVersionFailsBeforeCreatingTables()
    {
        using var database = new TestDatabase(initialize: false);
        Execute(database.Path, "PRAGMA user_version = 99");
        Assert.Throws<InvalidOperationException>(database.Store.Initialize);
        Assert.Equal(99L, Scalar(database.Path, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(database.Path, "SELECT count(*) FROM sqlite_schema"));
    }

    private static void SeedLegacy(TestDatabase database)
    {
        // Schema from the deployed Nonogram application before the additive Hashi migration.
        Execute(database.Path, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE catalog (id INTEGER PRIMARY KEY, title TEXT NOT NULL, author TEXT NOT NULL,
                width INTEGER NOT NULL, height INTEGER NOT NULL);
            CREATE TABLE puzzle_metadata (id INTEGER PRIMARY KEY, metadata TEXT NOT NULL);
            CREATE TABLE puzzles (id INTEGER PRIMARY KEY, definition TEXT NOT NULL);
            CREATE TABLE progress (puzzle_id INTEGER PRIMARY KEY REFERENCES puzzles(id), marks TEXT NOT NULL,
                completed INTEGER NOT NULL, backed_up_at TEXT NOT NULL);
            CREATE TABLE catalog_refresh (id INTEGER PRIMARY KEY CHECK (id = 1), revision INTEGER NOT NULL, completed_at TEXT);
            INSERT INTO catalog_refresh (id, revision) VALUES (1, 0);
            """);
        database.SeedCatalog([TestData.Summary(21) with { Metadata = new PuzzleMetadata(4, 5, 3) }], SavedAt);
        database.Store.CachePuzzle(TestData.Puzzle(21) with { Solution = "#.#.##" });
        database.SeedCatalog([TestData.Summary(12), TestData.Summary(13)], SavedAt.AddDays(1));
        database.Store.CachePuzzle(TestData.Puzzle(12));
        database.Store.SaveProgress([new ProgressUpdate(12, "#x....", false), new ProgressUpdate(21, "#.#.##", true)], SavedAt);
    }

    private static string SnapshotLegacyRows(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM catalog ORDER BY id;
            SELECT * FROM puzzle_metadata ORDER BY id;
            SELECT * FROM puzzles ORDER BY id;
            SELECT * FROM progress ORDER BY puzzle_id;
            SELECT * FROM catalog_refresh ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var tables = new List<List<object[]>>();
        do
        {
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
            tables.Add(rows);
        } while (reader.NextResult());
        return JsonSerializer.Serialize(tables);
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, ForeignKeys = true, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
