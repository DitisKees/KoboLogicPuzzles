using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.Slitherlink;
using Microsoft.AspNetCore.TestHost;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class DashboardTests
{
    [Fact]
    public async Task SnapshotReportsCatalogCountsAndGeneratorsWithoutPuzzleDefinitions()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(12), TestData.Summary(13)]);
        database.Store.CachePuzzle(TestData.Puzzle(12));
        database.Store.SaveProgress([new ProgressUpdate(12, "#.....", false)], DateTimeOffset.UtcNow);
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(TestData.HashiPair())));
        var puzzle = NativeLoopBoard.Square([[4]]).ToDefinition(1, 1, new("square", "example", "Example collection", 0))
            with
        { Solution = "####" };
        Assert.True(database.Store.SaveSlitherlink(puzzle));

        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();
        using var response = await http.GetAsync("/api/v2/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var snapshot = await response.Content.ReadFromJsonAsync<JsonElement>();
        var catalog = snapshot.GetProperty("catalog").EnumerateArray().ToArray();
        var nonogram = Assert.Single(catalog, row => row.GetProperty("game").GetString() == "nonogram");
        Assert.Equal(2, nonogram.GetProperty("total").GetInt32());
        Assert.Equal(1, nonogram.GetProperty("unstarted").GetInt32());
        var hashi = Assert.Single(catalog, row => row.GetProperty("game").GetString() == "hashi");
        Assert.Equal(1, hashi.GetProperty("unstarted").GetInt32());
        Assert.Equal("generated", hashi.GetProperty("sourceKind").GetString());
        var slitherlink = Assert.Single(catalog, row => row.GetProperty("game").GetString() == "slitherlink");
        Assert.Equal("Example collection", slitherlink.GetProperty("sourceName").GetString());
        Assert.Equal("collection", slitherlink.GetProperty("sourceKind").GetString());
        Assert.Equal(1, slitherlink.GetProperty("total").GetInt32());
        Assert.False(slitherlink.TryGetProperty("solution", out _));
        Assert.False(slitherlink.TryGetProperty("marks", out _));

        Assert.True(snapshot.GetProperty("hashi").GetProperty("targets").GetArrayLength() > 0);
        Assert.Equal(18, snapshot.GetProperty("nurikabe").GetProperty("stock").GetArrayLength());
        Assert.Equal(216, snapshot.GetProperty("slitherlink").GetProperty("stock").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("hashi").GetProperty("activity").GetProperty("current").ValueKind);
        Assert.True(snapshot.GetProperty("sampledAt").TryGetDateTimeOffset(out _));
    }
}
