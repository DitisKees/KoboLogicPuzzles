using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class StorageInitializationTests
{
    [Fact]
    public async Task ConcurrentStartupCreatesCurrentSchema()
    {
        using var database = new TestDatabase(initialize: false);

        await Task.WhenAll(Task.Run(database.Store.Initialize), Task.Run(new PuzzleStore(database.Path).Initialize));

        Assert.Equal(3L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Empty(database.Store.RestoreProgress());
        Assert.Empty(database.Store.RestoreHashiProgress());
    }

    [Fact]
    public void SchemaOneIsUpgradedWithoutLosingExistingPuzzles()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        Execute(database.Path, """
            DROP TABLE fill_a_pix_progress;
            DROP TABLE fill_a_pix_puzzles;
            DROP TABLE fill_a_pix_refresh;
            DROP TABLE slitherlink_progress;
            DROP TABLE slitherlink_puzzles;
            PRAGMA user_version = 1;
            """);

        database.Store.Initialize();

        Assert.Equal(3L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal(12, database.Store.FindPuzzle(12)!.Id);
        Assert.Empty(database.Store.RestoreGameProgress(PuzzleGame.FillAPix));
    }

    [Fact]
    public void SchemaTwoMigrationKeepsFillAPixData()
    {
        using var database = new TestDatabase();
        Execute(database.Path, """
            UPDATE fill_a_pix_refresh SET revision = 7 WHERE source = 'grandgames';
            DROP TABLE slitherlink_progress;
            DROP TABLE slitherlink_puzzles;
            PRAGMA user_version = 2;
            """);
        database.Store.Initialize();

        Assert.Equal(3L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal(7L, Execute(database.Path, "SELECT revision FROM fill_a_pix_refresh WHERE source = 'grandgames'"));
        Assert.Empty(database.Store.RestoreGameProgress(PuzzleGame.Slitherlink));
    }

    [Fact]
    public void UnversionedDatabaseWithDataIsRejected()
    {
        using var database = new TestDatabase(initialize: false);
        Execute(database.Path, "CREATE TABLE entries (value INTEGER); INSERT INTO entries VALUES (42);");

        Assert.Throws<InvalidOperationException>(database.Store.Initialize);

        Assert.Equal(0L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal(42L, Execute(database.Path, "SELECT value FROM entries"));
        Assert.Equal(1L, Execute(database.Path, "SELECT count(*) FROM sqlite_schema"));
    }

    [Fact]
    public void NewerDatabaseVersionFailsBeforeCreatingTables()
    {
        using var database = new TestDatabase(initialize: false);
        Execute(database.Path, "PRAGMA user_version = 99");

        Assert.Throws<InvalidOperationException>(database.Store.Initialize);

        Assert.Equal(99L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal(0L, Execute(database.Path, "SELECT count(*) FROM sqlite_schema"));
    }

    private static object? Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
