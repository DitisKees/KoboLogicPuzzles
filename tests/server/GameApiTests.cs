using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.Hashi;
using Microsoft.AspNetCore.TestHost;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class GameApiTests
{
    [Fact]
    public async Task CatalogPagesAndMetadataRespectGameSpecificFiltersAndProgress()
    {
        using var database = new TestDatabase();
        var nonogramMetadata = new PuzzleMetadata(3.75m, 4.5m, 2);
        database.SeedCatalog([TestData.Summary(12) with { Metadata = nonogramMetadata }, TestData.Summary(13, 9, 9)]);
        var hashi = AddHashi(database, row: 0);
        var solved = AddHashi(database, row: 1);
        _ = AddHashi(database, row: 0, size: 25);
        database.Store.SaveHashiProgress([new HashiProgressUpdate(solved.ContentHash!, [new HashiEdgeMark(0, 1, false)], true)],
            DateTimeOffset.UtcNow);
        using var source = UnavailableSource();
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities");
        Assert.Equal(new[] { "nonogram", "hashi" }, capabilities.GetProperty("games").EnumerateArray().Select(value => value.GetString()));
        Assert.True(capabilities.GetProperty("advancedFilters").GetBoolean());
        var query = "/api/v2/puzzles?game=hashi&width=20&height=20&difficulty=easy&solvability=basic_logic&limit=1";
        var first = (await http.GetFromJsonAsync<GameCatalogPage>(query))!;
        var firstPuzzle = Assert.Single(first.Puzzles);
        Assert.Equal(firstPuzzle.Id, first.NextBeforeId);
        var second = (await http.GetFromJsonAsync<GameCatalogPage>($"{query}&beforeId={first.NextBeforeId}"))!;
        Assert.Null(second.NextBeforeId);
        var byId = new[] { firstPuzzle, Assert.Single(second.Puzzles) }.ToDictionary(puzzle => puzzle.Id);
        Assert.Equal("unstarted", byId[hashi.Value].Status);
        Assert.Equal("solved", byId[solved.Value].Status);
        var nonogram = (await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=nonogram&width=3&difficulty=3.75&solvability=2"))!;
        Assert.Equal("nonogram-webpbn-12", Assert.Single(nonogram.Puzzles).Id);
        var ratings = await http.GetFromJsonAsync<Dictionary<string, JsonElement>>(
            $"/api/v2/puzzles/metadata?id=nonogram-webpbn-12&id={hashi}&id={hashi}&id=nonogram-webpbn-999");
        Assert.Equal(2, ratings!.Count);
        Assert.Equal(3.75m, ratings["nonogram-webpbn-12"].GetProperty("difficulty").GetDecimal());
        Assert.Equal("easy", ratings[hashi.Value].GetProperty("difficulty").GetString());
        using var invalid = await http.GetAsync($"/api/v2/puzzles?game=nonogram&beforeId={hashi}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task NonogramRangesAndChoicesApplyToCatalogPagesAndQueueCounts()
    {
        using var database = new TestDatabase();
        var entries = new[]
        {
            TestData.Summary(18, 7, 2) with { Metadata = new PuzzleMetadata(4.75m, null, 3) },
            TestData.Summary(17, 3, 2),
            TestData.Summary(16, 7, 2) with { Metadata = new PuzzleMetadata(4.75m, null, 4) },
            TestData.Summary(15, 5, 2) with { Metadata = new PuzzleMetadata(3.75m, null, 2) },
            TestData.Summary(14, 4, 2) with { Metadata = new PuzzleMetadata(4m, null, 4) },
            TestData.Summary(13, 3, 2) with { Metadata = new PuzzleMetadata(3.5m, null, 2) },
            TestData.Summary(12, 3, 3) with { Metadata = new PuzzleMetadata(3.75m, null, 2) },
        };
        database.SeedCatalog(entries);
        foreach (var entry in entries)
            database.Store.CachePuzzle(new PuzzleDefinition(entry.Id, entry.Title, entry.Author, entry.Width, entry.Height,
                Enumerable.Repeat(Array.Empty<int>(), entry.Height).ToArray(), Enumerable.Repeat(Array.Empty<int>(), entry.Width).ToArray()));
        using var source = UnavailableSource();
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        var path = "/api/v2/puzzles?game=nonogram&widthFilter=3-4,7&heightFilter=2&difficultyFilter=3.5-4,4.75&solvabilityFilter=2,4&limit=2";
        var first = (await http.GetFromJsonAsync<GameCatalogPage>(path))!;
        Assert.Equal(["nonogram-webpbn-16", "nonogram-webpbn-14"], first.Puzzles.Select(puzzle => puzzle.Id));
        Assert.Equal("nonogram-webpbn-14", first.NextBeforeId);
        var second = (await http.GetFromJsonAsync<GameCatalogPage>($"{path}&beforeId={first.NextBeforeId}"))!;
        Assert.Equal("nonogram-webpbn-13", Assert.Single(second.Puzzles).Id);
        Assert.Null(second.NextBeforeId);

        using var reply = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "nonogram", TargetCount = 4, WidthFilter = "3-4,7", Height = 2, DifficultyFilter = "3.5-4,4.75",
            SolvabilityFilter = "2,4", CachedUnstartedPuzzleIds = new[] { "nonogram-webpbn-13", "nonogram-webpbn-15", "nonogram-webpbn-17" },
        });
        reply.EnsureSuccessStatusCode();
        var queue = (await reply.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal(["nonogram-webpbn-16", "nonogram-webpbn-14"], queue.Puzzles.Select(puzzle => puzzle.Id));
        Assert.Equal(1, queue.Remaining);
    }

    [Fact]
    public async Task HashiSizeRangesAndRatingChoicesUseTheSameRulesForBrowsingAndDownloads()
    {
        using var database = new TestDatabase();
        var cached = AddHashi(database, row: 0, size: 25);
        var lastInRange = AddHashi(database, row: 0, size: 30);
        var separateSize = AddHashi(database, row: 0, size: 50);
        var outside = AddHashi(database, row: 0, size: 35);
        var medium = TestData.HashiFixture(20, "00000066", upstreamDifficulty: 1);
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(medium)));
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(TestData.HashiFixture(20, "00000065"))));
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(TestData.HashiFixture(20, "00000066"))));
        var mediumId = PuzzleId.FromHashi(medium.Puzzle.ContentHash).Value;
        using var source = UnavailableSource();
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        var page = (await http.GetFromJsonAsync<GameCatalogPage>(
            "/api/v2/puzzles?game=hashi&sizeFilter=20-30,50&difficultyFilter=easy,medium,expert&solvabilityFilter=basic_logic,advanced_logic"))!;
        Assert.Equal(new[] { cached.Value, lastInRange.Value, separateSize.Value, mediumId }.OrderDescending(), page.Puzzles.Select(puzzle => puzzle.Id));
        using var reply = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "hashi", TargetCount = 5, SizeFilter = "20-30,50", DifficultyFilter = "easy,medium,expert",
            SolvabilityFilter = "basic_logic,advanced_logic", CachedUnstartedPuzzleIds = new[] { cached.Value, outside.Value },
        });
        reply.EnsureSuccessStatusCode();
        var queue = (await reply.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal(page.Puzzles.Where(puzzle => puzzle.Id != cached.Value).Select(puzzle => puzzle.Id), queue.Puzzles.Select(puzzle => puzzle.Id));
        Assert.Equal(1, queue.Remaining);
    }

    [Theory]
    [InlineData("nonogram", "widthFilter", "35-20")]
    [InlineData("nonogram", "heightFilter", "20,")]
    [InlineData("nonogram", "difficultyFilter", "3.2-4.25")]
    [InlineData("nonogram", "solvabilityFilter", "2-4")]
    [InlineData("nonogram", "sizeFilter", "20-30")]
    [InlineData("hashi", "sizeFilter", "20-31")]
    [InlineData("hashi", "sizeFilter", "")]
    [InlineData("hashi", "difficultyFilter", "easy,3.75")]
    [InlineData("hashi", "solvabilityFilter", "basic_logic,all")]
    public async Task BothEndpointsRejectInvalidFilterExpressions(string game, string key, string value)
    {
        using var database = new TestDatabase();
        using var source = UnavailableSource();
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();
        using var catalog = await http.GetAsync($"/api/v2/puzzles?game={game}&{key}={Uri.EscapeDataString(value)}");
        using var queue = await http.PostAsJsonAsync("/api/v2/offline-queue", new Dictionary<string, object>
        {
            ["game"] = game, ["targetCount"] = 2, [key] = value,
        });
        Assert.Equal(HttpStatusCode.BadRequest, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, queue.StatusCode);
    }

    [Fact]
    public async Task QueueCountsOnlyMatchingUnstartedCacheAndReturnsStoredSolutionsAndShortfalls()
    {
        using var database = new TestDatabase();
        var cached = AddHashi(database, row: 0);
        var started = AddHashi(database, row: 1);
        var locallyPlayed = AddHashi(database, row: 2);
        var available = AddHashi(database, row: 3);
        var otherSize = AddHashi(database, row: 0, size: 25);
        database.Store.SaveHashiProgress([new HashiProgressUpdate(started.ContentHash!, [], false)], DateTimeOffset.UtcNow);
        database.SeedCatalog([TestData.Summary(12), TestData.Summary(13)]);
        database.Store.CachePuzzle(TestData.Puzzle(12) with { Solution = "#.#.##" });
        database.Store.CachePuzzle(TestData.Puzzle(13));
        using var source = UnavailableSource();
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        using var reply = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "hashi", TargetCount = 3, Width = 20, Difficulty = "easy", Solvability = "basic_logic",
            CachedUnstartedPuzzleIds = new[] { cached.Value, cached.Value, started.Value, otherSize.Value },
            ExcludedPuzzleIds = new[] { locallyPlayed.Value },
        });
        reply.EnsureSuccessStatusCode();
        var queue = (await reply.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        var puzzle = Assert.IsType<HashiPuzzleDefinition>(Assert.Single(queue.Puzzles));
        Assert.Equal(available.Value, puzzle.Id);
        Assert.Equal((byte)1, Assert.Single(puzzle.Solution!));
        Assert.Equal(1, queue.Remaining);

        using var nonogram = await http.PostAsJsonAsync("/api/v2/offline-queue", new { Game = "nonogram", TargetCount = 2 });
        nonogram.EnsureSuccessStatusCode();
        var other = (await nonogram.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Null(Assert.IsType<NonogramPuzzleDefinition>(other.Puzzles[0]).Solution);
        Assert.Equal("#.#.##", Assert.IsType<NonogramPuzzleDefinition>(other.Puzzles[1]).Solution);
        Assert.Equal(0, other.Remaining);
        using var invalid = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "nonogram", TargetCount = 1, CachedUnstartedPuzzleIds = new[] { cached.Value },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task HashiQueuePrefersWorkerRatingsUnlessTheClientSetsItsOwnFilter()
    {
        using var database = new TestDatabase();
        var medium = TestData.HashiFixture(20, "00000066", upstreamDifficulty: 1);
        var expert = TestData.HashiFixture(20, "00000066");
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(medium, "00000066")));
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(expert, "00000066")));
        var mediumId = PuzzleId.FromHashi(medium.Puzzle.ContentHash).Value;
        var expertId = PuzzleId.FromHashi(expert.Puzzle.ContentHash).Value;
        using var source = UnavailableSource();
        await using var app = ApiTests.CreateApp(database, source, builder =>
        {
            builder.Configuration["HashiGeneration:Solvabilities:0"] = "BasicLogic";
            builder.Configuration["HashiGeneration:Solvabilities:1"] = "AdvancedLogic";
        });
        await app.StartAsync();
        using var http = app.GetTestClient();

        using var preferred = await http.PostAsJsonAsync("/api/v2/offline-queue", new { Game = "hashi", TargetCount = 1 });
        preferred.EnsureSuccessStatusCode();
        var queue = (await preferred.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal(mediumId, Assert.Single(queue.Puzzles).Id);
        using var matching = await http.PostAsJsonAsync("/api/v2/offline-queue",
            new { Game = "hashi", TargetCount = 1, Solvability = "bounded_lookahead" });
        matching.EnsureSuccessStatusCode();
        var filtered = (await matching.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal(expertId, Assert.Single(filtered.Puzzles).Id);
    }

    [Fact]
    public async Task MixedProgressCommitsTogetherAndRestoresAfterReopening()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12) with { Solution = "#.#.##" });
        var hashi = AddHashi(database, row: 0);
        using var source = UnavailableSource();

        await using (var app = ApiTests.CreateApp(database, source))
        {
            await app.StartAsync();
            using var http = app.GetTestClient();
            using var saved = await http.PostAsJsonAsync("/api/v2/progress", new
            {
                SchemaVersion = 1, Boards = new object[]
                {
                    new { PuzzleId = "nonogram-webpbn-12", Marks = "#x....", Completed = false },
                    new { PuzzleId = hashi.Value, Marks = new[] { new HashiEdgeMark(0, 1, false) }, Completed = true },
                },
            });
            saved.EnsureSuccessStatusCode();
            Assert.Equal(2, (await saved.Content.ReadFromJsonAsync<ProgressReceipt>())!.SavedCount);

            using var invalid = await http.PostAsJsonAsync("/api/v2/progress", new
            {
                SchemaVersion = 1, Boards = new object[]
                {
                    new { PuzzleId = "nonogram-webpbn-12", Marks = "#.#.##", Completed = true },
                    new { PuzzleId = hashi.Value, Marks = new[] { new HashiEdgeMark(0, 2, false) }, Completed = true },
                },
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var untypedPuzzleId = new { SchemaVersion = 1, Boards = new[] { new ProgressUpdate(12, "#.#.##", true) } };
            using var untyped = await http.PostAsJsonAsync("/api/v2/progress", untypedPuzzleId);
            Assert.Equal(HttpStatusCode.BadRequest, untyped.StatusCode);
        }

        await using var restarted = ApiTests.CreateApp(database, source);
        await restarted.StartAsync();
        using var restoredClient = restarted.GetTestClient();
        var restored = (await restoredClient.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore"))!;
        Assert.Equal(2, restored.Length);
        Assert.All(restored, board => Assert.Equal(board.Puzzle.Id, board.Progress.PuzzleId));
        Assert.Equal(restored[0].Progress.BackedUpAt, restored[1].Progress.BackedUpAt);
        Assert.Equal("#x....", restored[0].Progress.Marks.GetString());
        Assert.False(restored[0].Progress.Completed);
        Assert.Equal("#.#.##", Assert.IsType<NonogramPuzzleDefinition>(restored[0].Puzzle).Solution);
        Assert.True(restored[1].Progress.Completed);
        Assert.Equal(1, Assert.Single(restored[1].Progress.Marks.EnumerateArray()).GetProperty("count").GetInt32());
        Assert.IsType<HashiPuzzleDefinition>(Assert.Single((await restoredClient.GetFromJsonAsync<GameSavedBoard[]>(
            "/api/v2/restore?game=hashi"))!).Puzzle);
    }

    private static PuzzleId AddHashi(TestDatabase database, int row, int size = 20)
    {
        var verified = TestData.VerifyHashi(TestData.HashiPair(row, size: size));
        Assert.True(database.Store.SaveHashiPuzzle(verified));
        return PuzzleId.FromHashi(verified.Puzzle.ContentHash);
    }

    private static WebPbnClient UnavailableSource() => TestData.Source(_ => throw new InvalidOperationException("Unexpected WebPBN request."));
}
