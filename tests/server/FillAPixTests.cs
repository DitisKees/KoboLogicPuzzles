using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.GrandGames;
using KoboLogicPuzzles.Server.Hashi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class FillAPixTests
{
    [Fact]
    public async Task CombinedCatalogUsesSourceIdsAndCachesPlayableDefinitionsWithoutNonogramSizeLimits()
    {
        using var database = new TestDatabase();
        const string nonogram = "nonogram-webpbn-12";
        database.Store.CachePuzzle(TestData.Puzzle(12));
        var hashiPuzzle = TestData.VerifyHashi(TestData.HashiPair());
        Assert.True(database.Store.SaveHashiPuzzle(hashiPuzzle));
        var hashi = PuzzleId.FromHashi(hashiPuzzle.Puzzle.ContentHash).Value;
        using var webPbn = TestData.Source(_ => throw new InvalidOperationException("Unexpected WebPBN request."));
        var upstream = new Sources();
        await using var app = CreateApp(database, webPbn, upstream);
        await app.StartAsync();
        using var http = app.GetTestClient();

        const string catalogPath = "/api/v2/puzzles?game=fill-a-pix";
        var catalog = await WaitForCatalogAsync(http, catalogPath, 4);
        Assert.Equal(["fillapixnet", "grandgames", "janko"], catalog.Puzzles.Select(puzzle =>
            ((JsonElement)puzzle.Metadata!).GetProperty("source").GetString()).Distinct().Order());
        const string large = "fill-a-pix-fillapixnet-1";
        const string grandGames = "fill-a-pix-grandgames-1";
        const string janko = "fill-a-pix-janko-1";
        Assert.Contains(catalog.Puzzles, puzzle => puzzle.Id == large && puzzle is { Width: 118, Height: 100, Title: "Treetops" });
        Assert.Contains(catalog.Puzzles, puzzle => puzzle.Id == "fill-a-pix-grandgames-2");
        Assert.Contains(catalog.Puzzles, puzzle => puzzle.Id == janko && puzzle is { Width: 2, Height: 2, Author: "Tess" });

        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities");
        Assert.Contains("fill-a-pix", capabilities.GetProperty("games").EnumerateArray().Select(game => game.GetString()));
        var filtered = await http.GetFromJsonAsync<GameCatalogPage>(catalogPath + "&widthFilter=110-120&height=100");
        Assert.Equal(large, Assert.Single(filtered!.Puzzles).Id);
        var firstPage = await http.GetFromJsonAsync<GameCatalogPage>(catalogPath + "&limit=2");
        var secondPage = await http.GetFromJsonAsync<GameCatalogPage>(catalogPath + $"&limit=2&beforeId={firstPage!.NextBeforeId}");
        Assert.Equal(catalog.Puzzles.Select(puzzle => puzzle.Id), firstPage.Puzzles.Concat(secondPage!.Puzzles).Select(puzzle => puzzle.Id));

        var definition = Assert.IsType<FillAPixPuzzleDefinition>(await http.GetFromJsonAsync<GamePuzzleDefinition>($"/api/v2/puzzles/{grandGames}"));
        Assert.Equal(new int?[] { 2, 3, 1, null, 3, 1 }, definition.Clues);
        Assert.Null(definition.Solution);
        Assert.Equal(2, upstream.GrandGamesBoardRequests);
        definition = Assert.IsType<FillAPixPuzzleDefinition>(await http.GetFromJsonAsync<GamePuzzleDefinition>($"/api/v2/puzzles/{grandGames}?includeSolution=true"));
        Assert.Equal("#..#.#", definition.Solution);
        Assert.Equal(2, upstream.GrandGamesBoardRequests);

        var queueReply = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "fill-a-pix", TargetCount = 3, WidthFilter = "2,118", CachedUnstartedPuzzleIds = new[] { janko },
        });
        queueReply.EnsureSuccessStatusCode();
        var queue = (await queueReply.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal(large, Assert.Single(queue.Puzzles).Id);
        Assert.Equal(1, queue.Remaining);

        using var invalid = await http.PostAsJsonAsync("/api/v2/progress", new
        {
            SchemaVersion = 1, Boards = new[] { new { PuzzleId = grandGames, Marks = "......", Completed = true } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var valid = await http.PostAsJsonAsync("/api/v2/progress", new
        {
            SchemaVersion = 1, Boards = new object[]
            {
                new { PuzzleId = nonogram, Marks = "#x....", Completed = false },
                new { PuzzleId = hashi, Marks = new[] { new HashiEdgeMark(0, 1, false) }, Completed = true },
                new { PuzzleId = grandGames, Marks = "#xx#x#", Completed = true },
            },
        });
        valid.EnsureSuccessStatusCode();
        var all = (await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore"))!;
        Assert.Equal(new[] { nonogram, hashi, grandGames }.Order(), all.Select(board => board.Puzzle.Id).Order());
        Assert.Single(all.Select(board => board.Progress.BackedUpAt).Distinct());
        Assert.Equal("#xx#x#", Assert.Single(all, board => board.Puzzle.Id == grandGames).Progress.Marks.GetString());
        var restored = await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore?game=fill-a-pix");
        Assert.Equal("#xx#x#", Assert.Single(restored!).Progress.Marks.GetString());
        Assert.Equal("solved", Assert.Single((await http.GetFromJsonAsync<GameCatalogPage>(catalogPath + "&width=3"))!.Puzzles).Status);
    }

    [Fact]
    public async Task FailedSourcesLeaveOtherImportsAndGrandGamesResumesAfterRestart()
    {
        using var database = new TestDatabase();
        using var webPbn = TestData.Source(_ => throw new InvalidOperationException("Unexpected WebPBN request."));
        const string path = "/api/v2/puzzles?game=fill-a-pix";
        var limited = new Sources(jankoUnavailable: true, rateLimitSecondPage: true);
        var clock = new FakeTimeProvider();
        await using (var app = CreateApp(database, webPbn, limited, clock))
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            await limited.RateLimited.WaitAsync(TimeSpan.FromSeconds(10));
            var partial = await WaitForCatalogAsync(client, path, 2);
            Assert.Contains(partial.Puzzles, puzzle => puzzle.Id == "fill-a-pix-grandgames-1");
            Assert.Contains(partial.Puzzles, puzzle => puzzle.Id == "fill-a-pix-fillapixnet-1");
            await app.StopAsync();
        }

        clock.Advance(GrandGamesRequests.MissingRetryAfter);
        var available = new Sources(jankoUnavailable: true);
        await using var appAfterRestart = CreateApp(database, webPbn, available, clock);
        await appAfterRestart.StartAsync();
        using var http = appAfterRestart.GetTestClient();
        var page = await WaitForCatalogAsync(http, path, 3);
        Assert.Equal(2, available.FirstGrandGamesPage);
        Assert.Contains(page.Puzzles, puzzle => puzzle.Id == "fill-a-pix-grandgames-1");
        Assert.Contains(page.Puzzles, puzzle => puzzle.Id == "fill-a-pix-grandgames-2");
        Assert.DoesNotContain(page.Puzzles, puzzle => puzzle.Id == "fill-a-pix-janko-1");
    }

    private static WebApplication CreateApp(TestDatabase database, WebPbnClient webPbn, Sources upstream, TimeProvider? clock = null) =>
        ApiTests.CreateApp(database, webPbn, builder => builder.Configuration["FillAPix:RefreshEnabled"] = "true",
            services =>
            {
                services.AddHttpClient("fill-a-pix").ConfigurePrimaryHttpMessageHandler(() => upstream);
                services.AddSingleton(provider => new GrandGamesRequests(provider.GetRequiredService<PuzzleStore>(),
                    clock ?? TimeProvider.System, TimeSpan.Zero));
            });

    private static async Task<GameCatalogPage> WaitForCatalogAsync(HttpClient http, string path, int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var page = (await http.GetFromJsonAsync<GameCatalogPage>(path, timeout.Token))!;
            if (page.Puzzles.Count == expectedCount)
                return page;
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class Sources(bool jankoUnavailable = false, bool rateLimitSecondPage = false) : HttpMessageHandler
    {
        private readonly TaskCompletionSource rateLimited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int grandGamesBoardRequests;
        private int firstGrandGamesPage;
        public int GrandGamesBoardRequests => Volatile.Read(ref grandGamesBoardRequests);
        public int FirstGrandGamesPage => Volatile.Read(ref firstGrandGamesPage);
        public Task RateLimited => rateLimited.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "www.janko.at")
            {
                if (jankoUnavailable)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return uri.AbsolutePath.EndsWith("index.htm", StringComparison.Ordinal) ? TestData.Ok("""
                    <div id="index-1"><a href="001.a.htm">01</a></div>
                    """) : TestData.Ok("""
                    <h1>Mosaik, Nr. 1</h1>
                    <footer><table><tr><td>Lizenz:</td><td><a href="/license">CC BY-NC-SA 3.0</a></td></tr></table></footer>
                    <script type="application/x-janko">
                    [begin]
                    puzzle mosaic
                    author Tess
                    size 2
                    [problem]
                    1 -
                    - -
                    [solution]
                    x -
                    - -
                    [end]
                    </script>
                    """);
            }
            if (uri.Host == "fill-a-pix.net")
                return uri.AbsolutePath == "/collection/index.html" ? TestData.Ok("""
                    <div id="puzzle-grid"><article><header>Treetops</header><a class=pzlsolve href=./0001/index.html>Play</a></article></div>
                    """) : TestData.Ok(JsonSerializer.Serialize(new
                    {
                        width = 118, height = 100, data = Enumerable.Repeat(new int[118], 100),
                    }));
            if (uri.Host == "en.grandgames.net")
            {
                var id = uri.Query.EndsWith("page=2", StringComparison.Ordinal) ? 2 : 1;
                Interlocked.CompareExchange(ref firstGrandGamesPage, id, 0);
                if (id == 2 && rateLimitSecondPage)
                {
                    rateLimited.SetResult();
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                }
                var next = id == 1 ? "<nav class=pagination><a rel=next href='/mosaic?page=2'>Next</a></nav>" : "";
                var version = GrandGamesBoardRequests == 0 ? "6223" : "6224";
                return TestData.Ok($$"""
                    <script>SiteVersion = {{version}};</script>
                    <div class=ag-catalog>{{next}}
                    <article class=gg-card>
                      <h2 class=gg-card__title><a href="/mosaic/id{{id}}">Fill-a-Pix #{{id}}</a></h2>
                      <span class=gg-card__badge>{{id + 2}}x2</span>
                    </article></div>
                    """);
            }
            Assert.Equal("https://api.grandgames.net/agmove/start", uri.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(1, body.GetProperty("id").GetInt32());
            var count = Interlocked.Increment(ref grandGamesBoardRequests);
            Assert.Equal(count == 1 ? "6223" : "6224", Assert.Single(request.Headers.GetValues("x-version")));
            return count == 1 ? new HttpResponseMessage(HttpStatusCode.PreconditionFailed) : TestData.Ok("""
                {"level":{"id":1,"game":"mosaic","sx":3,"sy":2,"p":"31|2N3311"}}
                """);
        }
    }
}
