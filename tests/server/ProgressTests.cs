using System.Net;
using KoboNonogram.Server;
using Microsoft.Data.Sqlite;

namespace KoboNonogram.Server.Tests;

public sealed class ProgressTests
{
    [Fact]
    public async Task ProgressSurvivesStoreReopeningAndUpsertsByPuzzleId()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        var timestamp = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        var backup = CreateBackup(database.Store, source, new FixedClock(timestamp));

        Assert.Equal(new ProgressReceipt(1, timestamp),
            await backup.SaveAsync([new ProgressUpdate(12, "#x....", false)], CancellationToken.None));
        await backup.SaveAsync([new ProgressUpdate(12, "#x#x##", true)], CancellationToken.None);
        Assert.Equal(new ProgressReceipt(0, null), await backup.SaveAsync([], CancellationToken.None));

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
    public async Task RejectsInvalidMarksAndFalseCompletion(string marks, bool completed)
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        var backup = CreateBackup(database.Store, source, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            backup.SaveAsync([new ProgressUpdate(12, marks, completed)], CancellationToken.None));
        Assert.Empty(database.Store.RestoreProgress());
    }

    [Fact]
    public async Task RejectsWholeBatchWhenOneBoardIsUnknownOrRepeated()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        using var source = TestData.Source(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var backup = CreateBackup(database.Store, source, TimeProvider.System);
        var valid = new ProgressUpdate(12, "#x....", false);

        await Assert.ThrowsAsync<ArgumentException>(() => backup.SaveAsync([valid, valid], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            backup.SaveAsync([valid, new ProgressUpdate(99, "#x....", false)], CancellationToken.None));
        Assert.Empty(database.Store.RestoreProgress());
    }

    private static ProgressBackup CreateBackup(PuzzleStore store, WebPbnClient source, TimeProvider time) =>
        new(store, new PuzzleLibrary(store, source, TestData.Options, time), time);

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
