using KoboLogicPuzzles.Server.Galaxies;
using Microsoft.Data.Sqlite;
using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class StorageInitializationTests
{
    [Fact]
    public async Task ConcurrentStartupCreatesCurrentSchema()
    {
        using var database = new TestDatabase(initialize: false);

        await Task.WhenAll(Task.Run(database.Store.Initialize), Task.Run(new PuzzleStore(database.Path).Initialize));

        Assert.Equal(8L, Execute(database.Path, "PRAGMA user_version"));
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
            DROP TABLE slitherlink_collection_refresh;
            DROP TABLE nurikabe_progress;
            DROP TABLE nurikabe_puzzles;
            DROP TABLE nurikabe_collection_refresh;
            DROP TABLE nonogram_collection_progress;
            DROP TABLE nonogram_collection_puzzles;
            DROP TABLE extra_collection_refresh;
            """ + RemoveRecentSchemas + "PRAGMA user_version = 1;");

        database.Store.Initialize();

        Assert.Equal(8L, Execute(database.Path, "PRAGMA user_version"));
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
            DROP TABLE slitherlink_collection_refresh;
            DROP TABLE nurikabe_progress;
            DROP TABLE nurikabe_puzzles;
            DROP TABLE nurikabe_collection_refresh;
            DROP TABLE nonogram_collection_progress;
            DROP TABLE nonogram_collection_puzzles;
            DROP TABLE extra_collection_refresh;
            """ + RemoveRecentSchemas + "PRAGMA user_version = 2;");
        database.Store.Initialize();

        Assert.Equal(8L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal(7L, Execute(database.Path, "SELECT revision FROM fill_a_pix_refresh WHERE source = 'grandgames'"));
        Assert.Empty(database.Store.RestoreGameProgress(PuzzleGame.Slitherlink));
    }

    [Fact]
    public void SchemaThreeMigrationKeepsSlitherlinkPuzzles()
    {
        using var database = new TestDatabase();
        var puzzle = NativeLoopBoard.Square([[4]]).ToDefinition(1, 1, new("square", "example", "Example", 0))
            with { Solution = "####" };
        Assert.True(database.Store.SaveSlitherlink(puzzle));
        Execute(database.Path, """
            DROP TABLE slitherlink_collection_refresh;
            DROP TABLE nurikabe_progress;
            DROP TABLE nurikabe_puzzles;
            DROP TABLE nurikabe_collection_refresh;
            DROP TABLE nonogram_collection_progress;
            DROP TABLE nonogram_collection_puzzles;
            DROP TABLE extra_collection_refresh;
            """ + RemoveRecentSchemas + "PRAGMA user_version = 3;");

        database.Store.Initialize();

        Assert.Equal(8L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal("####", database.Store.FindSlitherlink(puzzle.Id)!.Solution);
        Assert.Null(database.Store.GetSlitherlinkCollectionRefreshState(SlitherlinkCollectionRefresh.PuzzleKit));
    }

    [Fact]
    public void SchemaSixMigrationKeepsCollectionData()
    {
        using var database = new TestDatabase();
        Execute(database.Path, """
            INSERT INTO extra_collection_refresh VALUES ('fixture', 17, 42);
            """ + RemoveRecentSchemas + "PRAGMA user_version = 6;");

        database.Store.Initialize();

        Assert.Equal(8L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal(17L, Execute(database.Path, "SELECT cursor FROM extra_collection_refresh WHERE resource = 'fixture'"));
        Assert.Empty(database.Store.RestoreGameProgress(PuzzleGame.Galaxies));
    }

    [Fact]
    public void SchemaSevenMigrationKeepsGalaxiesAndAddsPlacementStorage()
    {
        using var database = new TestDatabase();
        var board = new GalaxiesBoard(2, 1, [new(2, 1, false)]);
        var hash = board.ContentHash();
        var id = PuzzleId.FromGalaxies(hash).Value;
        var metadata = new GalaxiesMetadata("fixture", "Fixture", "collection", "1", "https://example.test/",
            "CC0", "https://creativecommons.org/publicdomain/zero/1.0/", "1", hash, "easy", "basic_logic", false);
        Assert.True(database.Store.SaveGalaxies(new(id, 2, 1, board.Centers, "Fixture", "Author", metadata, "x")));
        Execute(database.Path, """
            DROP TABLE placement_progress;
            DROP TABLE placement_puzzles;
            DROP TABLE placement_collection_refresh;
            PRAGMA user_version = 7;
            """);

        database.Store.Initialize();

        Assert.Equal(8L, Execute(database.Path, "PRAGMA user_version"));
        Assert.Equal("x", database.Store.FindGalaxies(id)!.Solution);
        Assert.Empty(database.Store.RestoreGameProgress(PuzzleGame.Tents));
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

    private const string RemoveRecentSchemas = """
        DROP TABLE galaxies_progress;
        DROP TABLE galaxies_puzzles;
        DROP TABLE galaxies_collection_refresh;
        DROP TABLE placement_progress;
        DROP TABLE placement_puzzles;
        DROP TABLE placement_collection_refresh;
        """;

    private static object? Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
