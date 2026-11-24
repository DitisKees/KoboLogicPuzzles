using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboNonogram.Server.Hashi;
using Microsoft.AspNetCore.TestHost;

namespace KoboNonogram.Server.Tests;

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
        var easy = AddHashi(database, row: 0);
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HashiFixtures.json")));
        var fixture = fixtures.RootElement.EnumerateArray().Single(row => row.GetProperty("size").GetInt32() == 20 &&
            row.GetProperty("seed").GetString() == "00000065");
        var candidate = HashiCandidate.ParseBridges(20, fixture.GetProperty("description").GetString()!,
            fixture.GetProperty("solution").GetString()!);
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(candidate, "00000065")));
        var preferredId = PuzzleId.FromHashi(candidate.Puzzle.ContentHash).Value;
        using var source = UnavailableSource();
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        using var preferred = await http.PostAsJsonAsync("/api/v2/offline-queue", new { Game = "hashi", TargetCount = 2 });
        preferred.EnsureSuccessStatusCode();
        var queue = (await preferred.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal(new[] { preferredId, easy.Value }, queue.Puzzles.Select(puzzle => puzzle.Id));
        using var matching = await http.PostAsJsonAsync("/api/v2/offline-queue",
            new { Game = "hashi", TargetCount = 1, Difficulty = "easy" });
        matching.EnsureSuccessStatusCode();
        var filtered = (await matching.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal(easy.Value, Assert.Single(filtered.Puzzles).Id);
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
            using var incompatible = await http.PostAsJsonAsync("/api/v2/progress", new ProgressUpload([new ProgressUpdate(12, "#.#.##", true)]));
            Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
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
        Assert.Single((await restoredClient.GetFromJsonAsync<SavedBoard[]>("/api/v1/restore"))!);
    }

    private static PuzzleId AddHashi(TestDatabase database, int row, int size = 20)
    {
        var verified = TestData.VerifyHashi(TestData.HashiPair(row, size: size));
        Assert.True(database.Store.SaveHashiPuzzle(verified));
        return PuzzleId.FromHashi(verified.Puzzle.ContentHash);
    }

    private static WebPbnClient UnavailableSource() => TestData.Source(_ => throw new InvalidOperationException("Unexpected WebPBN request."));
}
