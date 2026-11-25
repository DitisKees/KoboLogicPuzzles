using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task V2DefinitionsUseTypedIdsAndOptionalSolutionsForBothGames()
    {
        using var database = new TestDatabase();
        database.Store.CachePuzzle(TestData.Puzzle(12) with { Solution = "#.#.##", Metadata = new PuzzleMetadata(4, 5, 2) });
        var hashi = TestData.VerifyHashi(TestData.HashiPair());
        Assert.True(database.Store.SaveHashiPuzzle(hashi));
        var hashiId = PuzzleId.FromHashi(hashi.Puzzle.ContentHash);

        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        await using var app = CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        var nonogram = await http.GetFromJsonAsync<JsonElement>("/api/v2/puzzles/nonogram-webpbn-12");
        Assert.Equal("nonogram", nonogram.GetProperty("game").GetString());
        Assert.Equal("nonogram-webpbn-12", nonogram.GetProperty("id").GetString());
        Assert.Equal(1, nonogram.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, nonogram.GetProperty("rowClues").GetArrayLength());
        Assert.Equal(4, nonogram.GetProperty("metadata").GetProperty("difficulty").GetDecimal());
        Assert.False(nonogram.TryGetProperty("solution", out _));
        var nonogramWithSolution = await http.GetFromJsonAsync<JsonElement>("/api/v2/puzzles/nonogram-webpbn-12?includeSolution=true");
        Assert.Equal("#.#.##", nonogramWithSolution.GetProperty("solution").GetString());

        var puzzle = await http.GetFromJsonAsync<JsonElement>($"/api/v2/puzzles/{hashiId}");
        Assert.Equal("hashi", puzzle.GetProperty("game").GetString());
        Assert.Equal(hashiId.Value, puzzle.GetProperty("id").GetString());
        Assert.Equal(1, puzzle.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(20, puzzle.GetProperty("width").GetInt32());
        Assert.Equal(2, puzzle.GetProperty("islands").GetArrayLength());
        var edge = Assert.Single(puzzle.GetProperty("edges").EnumerateArray());
        Assert.Equal(0, edge.GetProperty("first").GetInt32());
        Assert.Equal(1, edge.GetProperty("second").GetInt32());
        Assert.Equal("basic_logic", puzzle.GetProperty("metadata").GetProperty("solvability").GetString());
        Assert.False(puzzle.TryGetProperty("solution", out _));
        var hashiWithSolution = await http.GetFromJsonAsync<JsonElement>($"/api/v2/puzzles/{hashiId}?includeSolution=true");
        Assert.Equal(1, Assert.Single(hashiWithSolution.GetProperty("solution").EnumerateArray()).GetInt32());

        using var invalid = await http.GetAsync("/api/v2/puzzles/12");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await http.GetAsync($"/api/v2/puzzles/hashi-local-{new string('b', 64)}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task DownloadsAndBacksUpPartialBoardThenRestoresAfterRestart()
    {
        using var database = new TestDatabase();
        var metadata = new PuzzleMetadata(3.75m, 4.25m, 3);
        database.SeedCatalog([TestData.Summary(12), TestData.Summary(13) with { Metadata = metadata }]);
        using var source = TestData.Source(uri =>
        {
            Assert.Equal("/export.cgi", uri.AbsolutePath);
            Assert.Contains("id=13", uri.Query);
            return TestData.Ok(TestData.Export(13));
        });

        await using (var app = CreateApp(database, source))
        {
            await app.StartAsync();
            using var http = app.GetTestClient();
            var firstPage = (await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=nonogram&limit=1"))!;
            var summary = Assert.Single(firstPage.Puzzles);
            Assert.Equal("nonogram-webpbn-13", summary.Id);
            Assert.Equal(metadata, Assert.IsType<JsonElement>(summary.Metadata).Deserialize<PuzzleMetadata>(JsonSerializerOptions.Web));
            var nextPage = (await http.GetFromJsonAsync<GameCatalogPage>(
                $"/api/v2/puzzles?game=nonogram&beforeId={firstPage.NextBeforeId}"))!;
            var unrated = Assert.Single(nextPage.Puzzles);
            Assert.Equal("nonogram-webpbn-12", unrated.Id);
            Assert.Null(unrated.Metadata);

            using var queue = await http.PostAsJsonAsync("/api/v2/offline-queue", new { Game = "nonogram", TargetCount = 1 });
            queue.EnsureSuccessStatusCode();
            var response = await queue.Content.ReadFromJsonAsync<GameOfflineQueueResponse>();
            Assert.Equal(metadata, Assert.IsType<NonogramPuzzleDefinition>(Assert.Single(response!.Puzzles)).Metadata);
            var ratings = await http.GetFromJsonAsync<Dictionary<string, PuzzleMetadata>>(
                "/api/v2/puzzles/metadata?id=nonogram-webpbn-13&id=nonogram-webpbn-99");
            Assert.Equal(new KeyValuePair<string, PuzzleMetadata>("nonogram-webpbn-13", metadata), Assert.Single(ratings!));

            using var backup = await http.PostAsJsonAsync("/api/v2/progress", Upload(13, "#x....", false));
            backup.EnsureSuccessStatusCode();
            var receipt = await backup.Content.ReadFromJsonAsync<ProgressReceipt>();
            Assert.Equal(1, receipt!.SavedCount);
            Assert.NotNull(receipt.BackedUpAt);
        }

        database.SeedCatalog([]);
        using var unavailableSource = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        await using var restarted = CreateApp(database, unavailableSource);
        await restarted.StartAsync();
        using var freshClient = restarted.GetTestClient();
        var restored = Assert.Single((await freshClient.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore"))!);

        Assert.Equal("nonogram-webpbn-13", restored.Puzzle.Id);
        Assert.Equal(metadata, Assert.IsType<NonogramPuzzleDefinition>(restored.Puzzle).Metadata);
        Assert.Equal("#x....", restored.Progress.Marks.GetString());
        Assert.False(restored.Progress.Completed);
        Assert.Equal("nonogram-webpbn-13",
            (await freshClient.GetFromJsonAsync<GamePuzzleDefinition>("/api/v2/puzzles/nonogram-webpbn-13"))?.Id);
    }

    [Fact]
    public async Task BacksUpAnOfflineBoardMissingFromTheServerCacheAndCatalog()
    {
        using var database = new TestDatabase();
        using var source = TestData.Source(uri =>
        {
            Assert.Equal("/export.cgi", uri.AbsolutePath);
            Assert.Contains("id=40883", uri.Query);
            return TestData.Ok(TestData.Export(40883).Replace("</author>",
                "</author><note>published,definitely unique,definitely requires moderate lookahead</note>"));
        });
        await using var app = CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v2/puzzles/nonogram-webpbn-40883")).StatusCode);
        using var backup = await http.PostAsJsonAsync("/api/v2/progress", Upload(40883, "#x....", false));
        backup.EnsureSuccessStatusCode();
        var boards = await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore");
        var restored = Assert.Single(boards!);
        Assert.Equal("nonogram-webpbn-40883", restored.Puzzle.Id);
        Assert.Equal("#x....", restored.Progress.Marks.GetString());
        Assert.Empty(database.Store.ListCatalog(null, 10));
        var ratings = await http.GetFromJsonAsync<Dictionary<string, JsonElement>>(
            "/api/v2/puzzles/metadata?id=nonogram-webpbn-40883");
        var classification = ratings!["nonogram-webpbn-40883"];
        Assert.Equal(3, classification.GetProperty("solvability").GetInt32());
        Assert.False(classification.TryGetProperty("difficulty", out _));
        Assert.False(classification.TryGetProperty("quality", out _));
    }

    [Fact]
    public async Task InvalidRequestsAndFailedDownloadsLeaveSavedBoardsIntact()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(13)]);
        database.Store.CachePuzzle(TestData.Puzzle(12));
        database.Store.SaveProgress([new ProgressUpdate(12, "#x....", false)], DateTimeOffset.UtcNow);
        using var source = TestData.Source(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await using var app = CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v2/puzzles/nonogram-webpbn-99")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v2/puzzles?game=nonogram&limit=0")).StatusCode);

        using var invalidQueue = await http.PostAsJsonAsync("/api/v2/offline-queue", new { Game = "nonogram", TargetCount = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidQueue.StatusCode);

        using var invalidBackup = await http.PostAsJsonAsync("/api/v2/progress", Upload(12, "#x....", true));
        Assert.Equal(HttpStatusCode.BadRequest, invalidBackup.StatusCode);

        using var failedDownload = await http.PostAsJsonAsync("/api/v2/offline-queue", new { Game = "nonogram", TargetCount = 1 });
        Assert.Equal(HttpStatusCode.BadGateway, failedDownload.StatusCode);

        using var emptyBackup = await http.PostAsJsonAsync("/api/v2/progress", new GameProgressUpload(1, []));
        emptyBackup.EnsureSuccessStatusCode();
        var receipt = await emptyBackup.Content.ReadFromJsonAsync<ProgressReceipt>();
        Assert.Null(receipt!.BackedUpAt);
        Assert.Equal("#x....", Assert.Single(database.Store.RestoreProgress()).Progress.Marks);
    }

    private static GameProgressUpload Upload(int id, string marks, bool completed) =>
        new(1, [new GameProgressUpdate($"nonogram-webpbn-{id}", JsonSerializer.SerializeToElement(marks), completed)]);

    internal static WebApplication CreateApp(TestDatabase database, WebPbnClient source)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Storage:DatabasePath"] = database.Path;
        builder.Configuration["WebPbn:MinArea"] = "1";
        builder.Configuration["CatalogRefresh:Enabled"] = "false";
        Server.AddServices(builder);
        builder.Services.AddSingleton(source);
        var app = builder.Build();
        app.Services.GetRequiredService<PuzzleStore>().Initialize();
        Server.MapEndpoints(app);
        return app;
    }
}
