using System.Net;
using System.Threading.Channels;
using KoboNonogram.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace KoboNonogram.Server.Tests;

public sealed class CatalogRefreshTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunsOnFirstStartupAndRemembersItsNextRunAfterRestart()
    {
        using var database = new TestDatabase();
        var clock = new TestClock(Start);
        var requests = 0;
        using var source = TestData.Source(_ => TestData.Ok(TestData.SearchPage(false, TestData.Summary(++requests))));

        using (var worker = CreateWorker(database.Store, source, clock))
        {
            await worker.StartAsync(CancellationToken.None);
            Assert.Equal(TimeSpan.FromDays(7), await clock.NextDelayAsync());
            Assert.Equal(Start, database.Store.GetCatalogRefreshState().CompletedAt);
            clock.Advance(TimeSpan.FromDays(1));
            await worker.StopAsync(CancellationToken.None);
        }

        var reopened = new PuzzleStore(database.Path);
        reopened.Initialize();
        using var restarted = CreateWorker(reopened, source, clock);
        await restarted.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromDays(6), await clock.NextDelayAsync());
        Assert.Equal(1, requests);

        clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(TimeSpan.FromDays(7), await clock.NextDelayAsync());
        Assert.Equal(new CatalogRefreshState(2, Start.AddDays(7)), reopened.GetCatalogRefreshState());
        Assert.Equal(2, Assert.Single(reopened.ListCatalog(null, 10)).Id);
        await restarted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefreshesOverdueCatalogOnStartupAndRetriesFailureWithoutReplacingCatalog()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(99)], Start.AddDays(-8));
        var previous = database.Store.GetCatalogRefreshState();
        var clock = new TestClock(Start);
        var requests = 0;
        using var source = TestData.Source(_ => ++requests == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : TestData.Ok(TestData.SearchPage(false, TestData.Summary(12))));
        using var worker = CreateWorker(database.Store, source, clock);
        await worker.StartAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(1), await clock.NextDelayAsync());
        Assert.Equal(previous, database.Store.GetCatalogRefreshState());
        Assert.Equal(99, Assert.Single(database.Store.ListCatalog(null, 10)).Id);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromDays(7), await clock.NextDelayAsync());
        Assert.Equal(new CatalogRefreshState(2, Start.AddHours(1)), database.Store.GetCatalogRefreshState());
        Assert.Equal(12, Assert.Single(database.Store.ListCatalog(null, 10)).Id);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void OverlappingRefreshCannotReplaceAnAlreadyPublishedCatalog()
    {
        using var database = new TestDatabase();
        var initial = database.Store.GetCatalogRefreshState();

        Assert.True(database.Store.TryReplaceCatalog([TestData.Summary(12)], initial.Revision, Start));
        Assert.False(database.Store.TryReplaceCatalog([TestData.Summary(99)], initial.Revision, Start.AddMinutes(1)));

        Assert.Equal(new CatalogRefreshState(1, Start), database.Store.GetCatalogRefreshState());
        Assert.Equal(12, Assert.Single(database.Store.ListCatalog(null, 10)).Id);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void ExistingDatabaseKeepsCatalogAndProgressAndSchedulesRefresh(bool keepRefreshTable, long revision)
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(12)], Start);
        database.Store.CachePuzzle(TestData.Puzzle(12));
        database.Store.SaveProgress([new ProgressUpdate(12, "#x....", false)], DateTimeOffset.UtcNow);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path, ForeignKeys = true,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = keepRefreshTable ? "DROP TABLE puzzle_metadata" : "DROP TABLE catalog_refresh";
            command.ExecuteNonQuery();
        }

        database.Store.Initialize();

        Assert.Equal(new CatalogRefreshState(revision, null), database.Store.GetCatalogRefreshState());
        Assert.Equal(12, Assert.Single(database.Store.ListCatalog(null, 10)).Id);
        Assert.Equal("#x....", Assert.Single(database.Store.RestoreProgress()).Progress.Marks);
    }

    private static CatalogRefreshWorker CreateWorker(PuzzleStore store, WebPbnClient source, TimeProvider time) =>
        new(store, new PuzzleLibrary(store, source, TestData.Options, time), time, NullLogger<CatalogRefreshWorker>.Instance);

    private sealed class TestClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly Channel<TimeSpan> delays = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            delays.Writer.TryWrite(dueTime);
            return timer;
        }

        public Task<TimeSpan> NextDelayAsync() =>
            delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
