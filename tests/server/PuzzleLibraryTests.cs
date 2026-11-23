using System.Net;
using KoboNonogram.Server;
using Microsoft.AspNetCore.WebUtilities;

namespace KoboNonogram.Server.Tests;

public sealed class PuzzleLibraryTests
{
    [Fact]
    public async Task RefreshPaginatesDeduplicatesAndFiltersByDimensions()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(99)]);
        var seenPages = new List<int>();
        using var source = TestData.Source(uri =>
        {
            var page = int.Parse(QueryHelpers.ParseQuery(uri.Query)["pageno"].ToString());
            seenPages.Add(page);
            return TestData.Ok(page == 0
                ? TestData.SearchPage(true, TestData.Summary(12), TestData.Summary(14, 99, 70))
                : TestData.SearchPage(false, TestData.Summary(12), TestData.Summary(15, 80, 80)));
        });
        var library = new PuzzleLibrary(database.Store, source, TestData.Options, TimeProvider.System);

        Assert.Equal(2, await library.RefreshAsync(CancellationToken.None));
        Assert.Equal(new[] { 0, 1 }, seenPages);
        Assert.Equal(new[] { 15, 12 }, database.Store.ListCatalog(null, 10).Select(puzzle => puzzle.Id));
    }

    [Theory]
    [InlineData("<h1>Temporarily unavailable</h1>")]
    [InlineData(null)]
    public async Task FailedRefreshKeepsPreviousCatalog(string? lastPage)
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(99)]);
        var previous = database.Store.GetCatalogRefreshState();
        var firstPage = TestData.SearchPage(true, TestData.Summary(12));
        using var source = TestData.Source(uri => TestData.Ok(uri.Query.Contains("pageno=0") ? firstPage : lastPage ?? firstPage));
        var library = new PuzzleLibrary(database.Store, source, TestData.Options, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidDataException>(() => library.RefreshAsync(CancellationToken.None));

        Assert.Equal(99, Assert.Single(database.Store.ListCatalog(null, 10)).Id);
        Assert.Equal(previous, database.Store.GetCatalogRefreshState());
    }

    [Fact]
    public async Task ExplicitEmptyRefreshKeepsDownloadedBoardsAvailable()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(12)]);
        database.Store.CachePuzzle(TestData.Puzzle(12));
        new ProgressBackup(database.Store, TimeProvider.System).Save([new ProgressUpdate(12, "#x....", false)]);
        using var source = TestData.Source(uri =>
        {
            Assert.Equal("/find.cgi", uri.AbsolutePath);
            return TestData.Ok(TestData.SearchPage(false));
        });
        var library = new PuzzleLibrary(database.Store, source, TestData.Options, TimeProvider.System);

        Assert.Equal(0, await library.RefreshAsync(CancellationToken.None));
        Assert.Empty(database.Store.ListCatalog(null, 10));
        Assert.Equal(12, (await library.GetPuzzleAsync(12, CancellationToken.None))?.Id);
        Assert.Equal("#x....", Assert.Single(database.Store.RestoreProgress()).Progress.Marks);
    }

    [Fact]
    public async Task AddsASolutionToACachedPuzzleWithoutReplacingItsProgress()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12));
        new ProgressBackup(database.Store, TimeProvider.System).Save([new ProgressUpdate(12, "#x....", false)]);
        var exports = 0;
        using var source = TestData.Source(uri =>
        {
            exports++;
            Assert.Equal("on", QueryHelpers.ParseQuery(uri.Query)["xml_soln"].ToString());
            return TestData.Ok(TestData.Export(12).Replace("</puzzle>",
                "<solution type=\"goal\"><image>|X.X||.XX|</image></solution></puzzle>"));
        });
        var library = new PuzzleLibrary(database.Store, source, TestData.Options, TimeProvider.System);

        Assert.Null((await library.GetPuzzleAsync(12, CancellationToken.None))?.Solution);
        Assert.Equal(0, exports);
        Assert.Equal("#.#.##", (await library.GetPuzzleAsync(12, CancellationToken.None, includeSolution: true))?.Solution);
        Assert.Equal("#.#.##", (await library.GetPuzzleAsync(12, CancellationToken.None, includeSolution: true))?.Solution);
        Assert.Equal(1, exports);
        var restored = Assert.Single(database.Store.RestoreProgress());
        Assert.Equal("#x....", restored.Progress.Marks);
        Assert.Equal("Puzzle 12", restored.Puzzle.Title);
        Assert.Equal("#.#.##", restored.Puzzle.Solution);
    }

    [Fact]
    public async Task TopsUpQueueSkippingDownloadedPlayedAndUnavailablePuzzles()
    {
        using var database = new TestDatabase();
        database.SeedCatalog(Enumerable.Range(1, 7).Select(id => TestData.Summary(id)));
        database.Store.CachePuzzle(TestData.Puzzle(7));
        database.Store.CachePuzzle(TestData.Puzzle(6));
        new ProgressBackup(database.Store, TimeProvider.System).Save([
            new ProgressUpdate(7, "#x....", false), new ProgressUpdate(6, "#x#x##", true),
        ]);
        var exports = new List<int>();
        using var source = TestData.Source(uri =>
        {
            var id = int.Parse(QueryHelpers.ParseQuery(uri.Query)["id"].ToString());
            exports.Add(id);
            return id switch
            {
                4 => new HttpResponseMessage(HttpStatusCode.Gone),
                3 => TestData.Ok(TestData.Export(id).Replace("000000", "FF0000")),
                _ => TestData.Ok(TestData.Export(id)),
            };
        });
        var library = new PuzzleLibrary(database.Store, source, TestData.Options, TimeProvider.System);

        var added = await library.TopUpAsync(3, [5, 5], CancellationToken.None);

        Assert.Equal(new[] { 2, 1 }, added.Select(puzzle => puzzle.Id));
        Assert.Equal(new[] { 4, 3, 2, 1 }, exports);
        Assert.Equal(2, (await library.GetPuzzleAsync(2, CancellationToken.None))?.Id);
        Assert.Empty(await library.TopUpAsync(3, [5, 2, 1], CancellationToken.None));
        Assert.Equal(4, exports.Count);
    }
}
