using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.Galaxies;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class GalaxiesTests
{
    private static readonly GalaxyCenter[] PictureCenters = [new(1, 2, true), new(4, 2, false)];
    private static readonly GalaxyCenter[] AmbiguousCenters = [new(3, 1, true), new(3, 3, false), new(3, 5, true)];
    private static readonly GalaxiesAttribution TestSource = new("fixture", "Fixture", "Puzzle author",
        "https://example.test/galaxies", "CC0", "https://creativecommons.org/publicdomain/zero/1.0/");

    [Fact]
    public void PartitionsMustBeCompleteConnectedSymmetricAndRespectCenterCores()
    {
        var board = new GalaxiesBoard(3, 2, PictureCenters);
        Assert.Equal("#..#..", board.Picture("xxx#x#x"));
        Assert.Equal("#..#..", board.Picture("...#.#."));
        Assert.False(board.IsSolved(".......")); // Two centers in one region.
        Assert.False(board.IsSolved("#######")); // Isolated cells and split centers.
        Assert.False(board.IsSolved("xxx##xx")); // A wall inside an otherwise connected region.
        Assert.False(board.IsSolved("xxx#x##")); // A center touches multiple regions.
        Assert.False(board.IsSolved("x#x#x#x")); // A wall splits the core of the white galaxy.
        Assert.Throws<InvalidDataException>(() => new GalaxiesBoard(2, 2, [new(2, 2, true), new(3, 3, false)]));
        Assert.Throws<InvalidDataException>(() => new GalaxiesBoard(2, 2, [new(0, 1, true)]));

        var asymmetric = new GalaxiesBoard(3, 1, [new(1, 1, false)]);
        Assert.False(asymmetric.IsSolved("xx")); // The full region is symmetric about a different point.
        Assert.Equal(0, Prove(asymmetric).Count);
        Assert.Equal(1, Prove(board).Count);
        Assert.Equal(2, Prove(new GalaxiesBoard(3, 3, AmbiguousCenters)).Count);
        Assert.Equal(board.ContentHash(), new GalaxiesBoard(3, 2, PictureCenters.Reverse()).ContentHash());
        Assert.NotEqual(board.ContentHash(), new GalaxiesBoard(3, 2, [new(1, 2, false), new(4, 2, true)]).ContentHash());
        Assert.Throws<TimeoutException>(() => new GalaxiesSolver(board, TimeSpan.Zero, default).CountSolutions());
        Assert.Throws<OperationCanceledException>(() =>
            new GalaxiesSolver(board, TimeSpan.FromSeconds(1), new CancellationToken(true)).CountSolutions());
    }

    [Fact]
    public void ExactCountingAgreesWithExhaustiveWallsOnTwoByThree()
    {
        var positions = (from y in Enumerable.Range(1, 5)
                         from x in Enumerable.Range(1, 3)
                         select new GalaxyCenter(x, y, false)).ToArray();
        var layouts = Enumerable.Range(0, 1 << 7).Select(mask =>
            new string(Enumerable.Range(0, 7).Select(edge => (mask & 1 << edge) == 0 ? 'x' : '#').ToArray())).ToArray();
        for (var mask = 1; mask < 1 << positions.Length; mask++)
        {
            var centers = positions.Where((_, i) => (mask & 1 << i) != 0).ToArray();
            GalaxiesBoard board;
            try { board = new(2, 3, centers); }
            catch (InvalidDataException) { continue; }
            var solutions = layouts.Where(board.IsSolved).Take(2).ToArray();
            var proof = Prove(board);
            Assert.Equal(solutions.Length, proof.Count);
            if (proof.Count == 1)
                Assert.Equal(solutions[0], proof.Solution);
        }
    }

    private const string JankoExample = """
            <h1>Galaxien 123</h1><footer><table><tr><td>Autor:</td><td>Otto Janko</td></tr>
            <tr><td>Lizenz:</td><td><a href="../Creative-Commons.htm" rel="license">CC BY-NC-SA</a></td></tr>
            </table></footer><script type="application/x-janko">
            [begin]
            puzzle galaxies
            cols 3
            rows 2
            [problem]
            2 3 -
            - - -
            [solution]
            5 7 7
            5 7 7
            [end]
            </script>
            """;

    [Fact]
    public void JankoImportUsesThePublisherCenterOffsetsAndChecksPermission()
    {
        const string html = JankoExample;
        var url = new Uri("https://www.janko.at/Raetsel/Galaxien/123.a.htm");
        var entry = GalaxiesCollections.ParseJanko(html, url);
        Assert.NotNull(entry);
        Assert.Equal(new GalaxyCenter[] { new(1, 2, false), new(4, 2, false) }, entry.Value.Board.Centers);
        Assert.Equal("xxx#x#x", entry.Value.Solution);
        Assert.False(entry.Value.Source.Picture);
        Assert.Null(GalaxiesCollections.ParseJanko(html.Replace("rel=\"license\"", ""), url));
        Assert.Null(GalaxiesCollections.ParseJanko(html.Replace("Otto Janko", "Unlicensed Author"), url));
    }

    [Fact]
    public async Task JankoRefreshResumesAfterTheLastCompletedPage()
    {
        using var database = new TestDatabase();
        using var webPbn = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        using var handler = new JankoHandler();
        await using var app = ApiTests.CreateApp(database, webPbn,
            configureServices: services => services.AddSingleton<IHttpClientFactory>(new LocalClientFactory(handler)));
        var service = app.Services.GetServices<IHostedService>().OfType<GalaxiesCollections>().Single();
        var store = app.Services.GetRequiredService<PuzzleStore>();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshJankoAsync(default));
        Assert.Equal(("001.a.htm", 0L), store.GetGalaxiesRefresh("janko"));

        handler.FailSecondPage = false;
        Assert.Equal(1, await service.RefreshJankoAsync(default));
        Assert.Equal(1, handler.FirstPageRequests);
        Assert.Empty(store.GetGalaxiesRefresh("janko").Cursor);
        Assert.Equal(0, await service.RefreshJankoAsync(default));
        Assert.Equal(1, handler.FirstPageRequests);
    }

    [Fact]
    public async Task StartupImportCatalogQueueAndRestoreHandleLargePictureBoards()
    {
        using var database = new TestDatabase();
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        await using var app = ApiTests.CreateApp(database, source,
            builder => builder.Configuration["GalaxiesCollections:RefreshEnabled"] = "true");
        var collection = app.Services.GetServices<IHostedService>().OfType<GalaxiesCollections>().Single();
        Assert.Throws<InvalidDataException>(() => collection.VerifyAndStore(new(3, 3, AmbiguousCenters), TestSource,
            "ambiguous", "Ambiguous", "1", null, "######xxxxxx", default));
        Assert.Throws<InvalidDataException>(() => collection.VerifyAndStore(new(3, 2, PictureCenters), TestSource,
            "bad", "Bad solution", "1", null, "#######", default));

        await app.StartAsync();
        using var http = app.GetTestClient();
        var filter = PuzzleFilter.Parse("galaxies", null, null, null, null, source: "galaxies-pictures")!;
        var store = app.Services.GetRequiredService<PuzzleStore>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (store.GetGalaxiesRefresh("wikimedia-tentaisho").Cursor.Length == 0)
            await Task.Delay(20, deadline.Token);

        var sources = (await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities")).GetProperty("puzzleSources")
            .GetProperty("galaxies");
        Assert.Equal("generated", sources.GetProperty("galaxies-pictures").GetProperty("kind").GetString());
        Assert.Equal("collection", sources.GetProperty("spiral-galaxies-font").GetProperty("kind").GetString());
        Assert.True(sources.TryGetProperty("wikimedia-tentaisho", out _));
        Assert.Equal(3, store.ListGalaxiesCatalog(filter with { Sources = ["tatham-galaxies"] }, null, 100, true).Count);
        Assert.False(sources.TryGetProperty("artists-block", out _));
        Assert.Equal(36, store.ListGalaxiesCatalog(filter with { Sources = ["spiral-galaxies-font"] }, null, 100, true).Count);

        var page = (await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=galaxies&limit=1"))!;
        var summary = Assert.Single(page.Puzzles);
        Assert.True(summary.Width >= 30 && summary.Height >= 30);
        var second = (await http.GetFromJsonAsync<GameCatalogPage>($"/api/v2/puzzles?game=galaxies&limit=1&beforeId={page.NextBeforeId}"))!;
        Assert.NotEqual(summary.Id, Assert.Single(second.Puzzles).Id);

        var page80 = (await http.GetFromJsonAsync<GameCatalogPage>(
            "/api/v2/puzzles?game=galaxies&widthFilter=80-100&source=galaxies-pictures"))!;
        var id = Assert.Single(page80.Puzzles).Id;
        var definition = await http.GetFromJsonAsync<JsonElement>($"/api/v2/puzzles/{id}");
        Assert.False(definition.TryGetProperty("solution", out _));
        using var response = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "galaxies", TargetCount = 1, Width = 80, Source = "galaxies-pictures",
        });
        response.EnsureSuccessStatusCode();
        var puzzle = Assert.IsType<GalaxiesDefinition>(Assert.Single((await response.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!.Puzzles));
        Assert.Equal(id, puzzle.Id);
        Assert.True(puzzle.Metadata.Picture);
        Assert.Equal(id["galaxies-".Length..], puzzle.Metadata.ContentHash);
        var picture = new GalaxiesBoard(puzzle.Width, puzzle.Height, puzzle.Centers).Picture(puzzle.Solution!)!;
        Assert.Contains('#', picture);
        Assert.Contains('.', picture);

        var partial = new string('.', puzzle.Solution!.Length);
        using var invalid = await Save(partial, true);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var saved = await Save(partial, false);
        saved.EnsureSuccessStatusCode();
        var restored = Assert.Single((await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore?game=galaxies"))!);
        Assert.False(restored.Progress.Completed);
        Assert.Equal(partial, restored.Progress.Marks.GetString());
        Assert.Equal(puzzle.Solution, Assert.IsType<GalaxiesDefinition>(restored.Puzzle).Solution);
        using var solved = await Save(puzzle.Solution, true);
        solved.EnsureSuccessStatusCode();
        Assert.True(Assert.Single(new PuzzleStore(database.Path).RestoreGameProgress(PuzzleGame.Galaxies)).Progress.Completed);
        using var keptOut = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "galaxies", TargetCount = 1, Width = 80, CachedUnstartedPuzzleIds = new[] { id },
        });
        Assert.Equal(1, (await keptOut.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!.Remaining);
        var dashboard = await http.GetFromJsonAsync<JsonElement>("/api/v2/dashboard");
        Assert.Contains(dashboard.GetProperty("catalog").EnumerateArray(), row =>
            row.GetProperty("game").GetString() == "galaxies" && row.GetProperty("source").GetString() == "galaxies-pictures");

        Task<HttpResponseMessage> Save(string marks, bool completed) => http.PostAsJsonAsync("/api/v2/progress",
            new { SchemaVersion = 1, Boards = new[] { new { PuzzleId = id, Marks = marks, Completed = completed } } });
    }

    private sealed class LocalClientFactory(JankoHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class JankoHandler : HttpMessageHandler
    {
        internal int FirstPageRequests { get; private set; }
        internal bool FailSecondPage { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = request.RequestUri!.Segments[^1];
            if (page == "index.htm")
                return Task.FromResult(TestData.Ok("""<a href="001.a.htm">1</a><a href="002.a.htm">2</a>"""));
            if (page == "001.a.htm")
            {
                FirstPageRequests++;
                return Task.FromResult(TestData.Ok(JankoExample.Replace("Otto Janko", "Unlicensed")));
            }
            Assert.Equal("002.a.htm", page);
            return Task.FromResult(FailSecondPage ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : TestData.Ok(JankoExample));
        }
    }

    private static GalaxiesProof Prove(GalaxiesBoard board) => new GalaxiesSolver(board, TimeSpan.FromSeconds(5), default).CountSolutions();
}
