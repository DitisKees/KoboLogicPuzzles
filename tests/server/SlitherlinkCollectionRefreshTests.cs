using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using KoboLogicPuzzles.Server.Slitherlink;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class SlitherlinkCollectionRefreshTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task PuzzleKitRetrySkipsShardsAlreadyImportedAndPreservesOriginalSource()
    {
        using var database = new TestDatabase();
        var firstRequests = 0;
        var secondRequests = 0;
        using var http = new HttpClient(new CollectionHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/contents/assets/data/Slitherlink", StringComparison.Ordinal))
                return TestData.Ok($$"""
                    [
                        {"name":"Slitherlink_dataset_000.json","type":"file","sha":"{{First}}"},
                        {"name":"Slitherlink_dataset_001.json","type":"file","sha":"{{Second}}"}
                    ]
                    """);
            Assert.Contains(request.Headers.Accept, type => type.MediaType == "application/vnd.github.raw+json");
            if (path.EndsWith($"/git/blobs/{First}", StringComparison.Ordinal))
            {
                firstRequests++;
                return TestData.Ok("""
                    {"name":"Slitherlink","data":{"one":{"problem":"1 1\n4","source":"https://www.janko.at/example"}}}
                    """);
            }
            Assert.EndsWith($"/git/blobs/{Second}", path);
            return ++secondRequests == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : TestData.Ok("""
                {"name":"Slitherlink","data":{"two":{"problem":"1 2\n4 -"}}}
                """);
        }));
        var refresh = CreateRefresh(database.Store, http, new FakeTimeProvider(Start));

        await Assert.ThrowsAsync<HttpRequestException>(() => refresh.RefreshPuzzleKitAsync(default));
        Assert.Equal("Janko", database.Store.ListSlitherlinkSources()["janko"]);
        Assert.Null(database.Store.GetSlitherlinkCollectionRefreshState(SlitherlinkCollectionRefresh.PuzzleKit));

        await refresh.RefreshPuzzleKitAsync(default);
        Assert.Equal(1, firstRequests);
        Assert.Equal(2, secondRequests);
        Assert.Equal("PuzzleKit", database.Store.ListSlitherlinkSources()["puzzlekit"]);
        Assert.Equal(Start, database.Store.GetSlitherlinkCollectionRefreshState(SlitherlinkCollectionRefresh.PuzzleKit)!.CompletedAt);
    }

    [Fact]
    public async Task MindgamesUsesSavedETagWhenCheckingForUpdates()
    {
        using var database = new TestDatabase();
        var clock = new FakeTimeProvider(Start);
        var requests = 0;
        using var http = new HttpClient(new CollectionHandler(request =>
        {
            Assert.Equal("https://ulhaque.co.uk/common/exe/slitherlink20k.zip", request.RequestUri!.AbsoluteUri);
            if (++requests == 1)
            {
                Assert.Empty(request.Headers.IfNoneMatch);
                return MindgamesArchive();
            }
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        }));

        await CreateRefresh(database.Store, http, clock).RefreshMindgamesAsync(default);
        var reopened = new PuzzleStore(database.Path);
        reopened.Initialize();
        clock.Advance(TimeSpan.FromDays(7));
        await CreateRefresh(reopened, http, clock).RefreshMindgamesAsync(default);

        Assert.Equal("Mindgames", reopened.ListSlitherlinkSources()["mindgames"]);
        Assert.Single(reopened.ListSlitherlinkCatalog(PuzzleFilter.Parse("slitherlink", null, null, null, null)!,
            null, 10, true));
        Assert.Equal(clock.GetUtcNow(), reopened.GetSlitherlinkCollectionRefreshState(SlitherlinkCollectionRefresh.Mindgames)!.CompletedAt);
    }

    [Fact]
    public async Task ServerStartsCollectionRefreshWithGenerationDisabled()
    {
        using var database = new TestDatabase();
        using var nonograms = TestData.Source(_ => throw new InvalidOperationException("Unexpected WebPBN request."));
        await using var app = ApiTests.CreateApp(database, nonograms, builder =>
        {
            builder.Configuration["SlitherlinkCollections:RefreshEnabled"] = "true";
            builder.Configuration["SlitherlinkGeneration:Enabled"] = "false";
        }, services => services.AddHttpClient<SlitherlinkCollectionRefresh>().ConfigurePrimaryHttpMessageHandler(() =>
            new CollectionHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "ulhaque.co.uk")
                    return MindgamesArchive();
                if (uri.Host == "www.janko.at")
                    return TestData.Ok(uri.AbsolutePath.EndsWith("index-1.htm", StringComparison.Ordinal)
                        ? """<a href="0001.a.htm">1</a>"""
                        : """
                        <h1>Slitherlink, Nr. 1</h1>
                        <script type="application/x-janko">
                        [begin]
                        puzzle slitherlink
                        author Example Author
                        [problem]
                        4
                        unit 30
                        [end]
                        </script>
                        """);
                if (uri.Host == "en.grandgames.net")
                    return TestData.Ok("""
                        <script>const SiteVersion = 42;</script>
                        <div class="ag-catalog"><article class="gg-card"><div class="gg-card__title">
                        <a href="/chinawall/id1">Wall of China</a></div></article></div>
                        """);
                if (uri.Host == "api.grandgames.net")
                    return TestData.Ok("""{"level":{"id":1,"game":"chinawall","sx":3,"sy":2,"p":"[[\"\",\"\",\"\"],[\"\",4,\"\"]]"}}""");
                if (uri.Host == "krazydad.com")
                    return TestData.Ok("""
                        <select id="kind"><option value="altair_sm_d2">Altair Small</option></select>
                        <script src="https://files.krazydad.com/play/js/tiling_altair.min.js"></script>
                        <script>
                        const prevPuz = '?kind=altair_sm_d2&volumeNumber=1&bookNumber=1&puzzleNumber=1';
                        const pRec = {"puzzle_id":"KD_VSlitherlink_altair_sm_d2_V1-B1-P1","puzzle_data":{"puzz":"3"}}; const displayName = "Altair";
                        </script>
                        """);
                if (uri.Host == "files.krazydad.com")
                    return TestData.Ok("""
                        mesh={dots:[{id:0,x:1.25,y:0},{id:1,x:2.5,y:2},{id:2,x:0,y:2}],cells:[{id:0,dots:[0,1,2]}]};
                        """);
                Assert.Equal("api.github.com", uri.Host);
                if (uri.AbsolutePath.EndsWith($"/git/blobs/{First}", StringComparison.Ordinal))
                    return TestData.Ok("""
                        {"name":"Slitherlink","data":{"one":{"problem":"1 2\n4 -"}}}
                        """);
                return TestData.Ok($$"""[{"name":"Slitherlink_dataset.json","type":"file","sha":"{{First}}"}]""");
            })));
        await app.StartAsync();

        string[] sources = ["grandgames", "janko", "krazydad", "mindgames", "puzzlekit"];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (sources.Any(source => database.Store.GetSlitherlinkCollectionRefreshState(source) is null))
            await Task.Delay(20, deadline.Token);
        Assert.Equal(sources, database.Store.ListSlitherlinkSources().Keys);

        SlitherlinkDefinition ReadPuzzle(string source, string? tiling = null)
        {
            var filter = PuzzleFilter.Parse("slitherlink", null, null, null, null, source: source, tiling: tiling)!;
            var puzzle = Assert.Single(database.Store.ListSlitherlinkCatalog(filter, null, 10, false));
            return database.Store.FindSlitherlink(puzzle.Id)!;
        }
        Assert.Equal("Example Author", ReadPuzzle("janko").Author);
        var wall = ReadPuzzle("grandgames");
        var clue = Assert.Single(wall.Faces, face => face.Clue == 4);
        Assert.Equal((15, 15), (clue.X, clue.Y));
        Assert.Equal("###", ReadPuzzle("krazydad", "altair").Solution);
    }

    private static SlitherlinkCollectionRefresh CreateRefresh(PuzzleStore store, HttpClient http, TimeProvider time)
    {
        var options = new SlitherlinkOptions();
        var importer = new SlitherlinkImporter(store, new LoopyGenerator(options), options, NullLogger<SlitherlinkImporter>.Instance);
        return new(store, importer, http, time, NullLogger<SlitherlinkCollectionRefresh>.Instance);
    }

    private static HttpResponseMessage MindgamesArchive()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("puz-01x01.txt").Open()))
            writer.WriteLine("0101HA4X");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        return response;
    }

    private sealed class CollectionHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

}
