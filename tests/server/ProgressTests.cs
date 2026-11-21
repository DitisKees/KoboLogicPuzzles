using KoboNonogram.Server;
using Microsoft.Data.Sqlite;

namespace KoboNonogram.Server.Tests;

public sealed class ProgressTests
{
    [Fact]
    public void ProgressSurvivesStoreReopeningAndUpsertsByPuzzleId()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        var timestamp = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        var backup = new ProgressBackup(database.Store, new FixedClock(timestamp));

        Assert.Equal(new ProgressReceipt(1, timestamp), backup.Save([new ProgressUpdate(12, "#x....", false)]));
        backup.Save([new ProgressUpdate(12, "#x#x##", true)]);
        Assert.Equal(new ProgressReceipt(0, null), backup.Save([]));

        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path, ForeignKeys = true,
        }.ToString()));
        var reopened = new PuzzleStore(database.Path);
        reopened.Initialize();

        var restored = Assert.Single(reopened.RestoreProgress());
        Assert.Equal(12, restored.Puzzle.Id);
        Assert.Equal(new[] { 1, 1 }, restored.Puzzle.RowClues[0]);
        Assert.Equal(new SavedProgress(12, "#x#x##", true, timestamp), restored.Progress);
    }

    [Theory]
    [InlineData("#x", false)]
    [InlineData("#x...X", false)]
    [InlineData("#x###x", true)]
    [InlineData("xxxxxx", true)]
    public void RejectsInvalidMarksAndFalseCompletion(string marks, bool completed)
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        var backup = new ProgressBackup(database.Store, TimeProvider.System);

        Assert.Throws<ArgumentException>(() => backup.Save([new ProgressUpdate(12, marks, completed)]));
        Assert.Empty(database.Store.RestoreProgress());
    }

    [Fact]
    public void RejectsWholeBatchWhenOneBoardIsUnknownOrRepeated()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        var backup = new ProgressBackup(database.Store, TimeProvider.System);
        var valid = new ProgressUpdate(12, "#x....", false);

        Assert.Throws<ArgumentException>(() => backup.Save([valid, valid]));
        Assert.Throws<ArgumentException>(() => backup.Save([valid, new ProgressUpdate(99, "#x....", false)]));
        Assert.Empty(database.Store.RestoreProgress());
    }

    [Fact]
    public void DatabaseRollsBackBatchOnWriteFailureAndRetainsOriginalDefinition()
    {
        using var database = new TestDatabase();
        var original = TestData.Puzzle(12);
        database.Store.CachePuzzle(original);
        Assert.Equal(original.Title, database.Store.CachePuzzle(original with { Title = "New title" }).Title);

        Assert.Throws<SqliteException>(() => database.Store.SaveProgress([
            new ProgressUpdate(12, "#x....", false), new ProgressUpdate(99, "#x....", false),
        ], DateTimeOffset.UtcNow));
        Assert.Empty(database.Store.RestoreProgress());
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
