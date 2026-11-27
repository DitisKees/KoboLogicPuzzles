using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.Hashi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class ExtraCollectionTests
{
    [Fact]
    public async Task BundledNonogramsJoinLegacyCatalogAndRestoreWithStableIds()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(12)]);
        database.Store.CachePuzzle(TestData.Puzzle(12));
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        await using var app = ApiTests.CreateApp(database, source);
        var importer = app.Services.GetRequiredService<ExtraCollectionRefresh>();
        Assert.Equal((32, 0), await importer.ImportBundledAsync("world-at-play", null, default));
        Assert.Equal((0, 0), await importer.ImportBundledAsync("world-at-play", null, default));
        await app.StartAsync();
        using var http = app.GetTestClient();

        var legacy = await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=nonogram");
        Assert.Equal("nonogram-webpbn-12", Assert.Single(legacy!.Puzzles).Id);
        var all = await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=nonogram&includeCollections=true&limit=32");
        Assert.Equal(32, all!.Puzzles.Count);
        Assert.Equal(all.Puzzles[^1].Id, all.NextBeforeId);
        var next = await http.GetFromJsonAsync<GameCatalogPage>($"/api/v2/puzzles?game=nonogram&includeCollections=true&beforeId={all.NextBeforeId}");
        Assert.Equal("nonogram-webpbn-12", Assert.Single(next!.Puzzles).Id);

        using var reply = await http.PostAsJsonAsync("/api/v2/offline-queue", new { game = "nonogram", source = "world-at-play", targetCount = 1 });
        reply.EnsureSuccessStatusCode();
        var queue = (await reply.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        var board = Assert.IsType<NonogramPuzzleDefinition>(Assert.Single(queue.Puzzles));
        Assert.Equal("The World at Play", board.Author);
        Assert.StartsWith("nonogram-collection-", board.Id);
        Assert.Equal("collection", board.Metadata!.SourceKind);
        Assert.Equal("https://creativecommons.org/publicdomain/zero/1.0/", board.Metadata.License);
        var details = await http.GetFromJsonAsync<JsonElement>($"/api/v2/puzzles/{board.Id}");
        Assert.False(details.TryGetProperty("solution", out _));
        var metadata = await http.GetFromJsonAsync<Dictionary<string, PuzzleMetadata>>($"/api/v2/puzzles/metadata?id={board.Id}");
        Assert.Equal("world-at-play", metadata![board.Id].Source);

        using var invalid = await Upload(new string('.', 25));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var saved = await Upload(board.Solution!);
        saved.EnsureSuccessStatusCode();
        Assert.Empty((await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore?game=nonogram"))!);
        var restored = Assert.Single((await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore?game=nonogram&includeCollections=true"))!);
        Assert.Equal(board.Id, restored.Puzzle.Id);

        Task<HttpResponseMessage> Upload(string marks) => http.PostAsJsonAsync("/api/v2/progress",
            new { schemaVersion = 1, boards = new[] { new { puzzleId = board.Id, marks, completed = true } } });
    }

    [Fact]
    public async Task AuthoredHashiKeepsRectangularGeometryAttributionAndBackup()
    {
        using var database = new TestDatabase();
        var board = HashiPuzzle.Create(5, 3, [new(0, 0, 1), new(4, 0, 1)]);
        var original = new PublishedPuzzle("janko", "Janko", "005", "https://www.janko.at/Raetsel/Hashi/005.a.htm",
            "https://www.janko.at/Raetsel/Creative-Commons.htm", "Hashi No. 5", "Puzzle Author");
        var (validation, verified) = VerifiedHashiPuzzle.VerifyCollection(board, original, TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow, default);
        Assert.Equal(HashiValidationStatus.Unique, validation.Status);
        Assert.True(database.Store.SaveHashiPuzzle(verified!));
        Assert.Empty(database.Store.ReadHashiStock(0, 0));
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities");
        Assert.True(capabilities.GetProperty("extraCollections").GetBoolean());
        Assert.Equal("collection", capabilities.GetProperty("puzzleSources").GetProperty("hashi").GetProperty("janko").GetProperty("kind").GetString());
        Assert.Empty((await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=hashi"))!.Puzzles);
        var summary = Assert.Single((await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=hashi&source=janko&width=5"))!.Puzzles);
        Assert.Equal("Puzzle Author", summary.Author);
        Assert.Equal(3, summary.Height);
        using var queue = await http.PostAsJsonAsync("/api/v2/offline-queue", new { game = "hashi", targetCount = 1, includeCollections = true });
        queue.EnsureSuccessStatusCode();
        var definition = Assert.IsType<HashiPuzzleDefinition>(Assert.Single((await queue.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!.Puzzles));
        Assert.Equal("collection", definition.Metadata.SourceKind);
        Assert.Equal("005", definition.Metadata.SourceId);
        using var upload = await http.PostAsJsonAsync("/api/v2/progress", new
        {
            schemaVersion = 1,
            boards = new[] { new { puzzleId = summary.Id, marks = new[] { new HashiEdgeMark(0, 1, false) }, completed = true } },
        });
        upload.EnsureSuccessStatusCode();
        Assert.Empty((await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore?game=hashi"))!);
        var restored = Assert.Single((await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore?game=hashi&includeCollections=true"))!);
        Assert.True(restored.Progress.Completed);
        Assert.Equal("Janko", Assert.IsType<HashiPuzzleDefinition>(restored.Puzzle).Metadata.SourceName);
    }

    [Fact]
    public async Task SenPacksPreserveDifferentOriginsForAuthoredAndGeneratedBoards()
    {
        using var database = new TestDatabase();
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        await using var app = ApiTests.CreateApp(database, source);
        var importer = app.Services.GetRequiredService<ExtraCollectionRefresh>();
        Assert.Equal((36, 6), await importer.ImportBundledAsync(null, null, default));
        Assert.Equal((0, 0), await importer.ImportBundledAsync(null, null, default));
        var catalog = database.Store.ReadDashboardCatalog();
        var nonograms = Assert.Single(catalog, row => row.Game == "nonogram" && row.Source == "sen-ltd");
        Assert.Equal("collection", nonograms.SourceKind);
        Assert.Equal(4, nonograms.Total);
        Assert.Equal("generated", Assert.Single(catalog, row => row.Game == "hashi" && row.Source == "sen-ltd").SourceKind);
        Assert.Empty(database.Store.ReadHashiStock(0, 0));
    }

    [Fact]
    public void JankoRequiresAPermittedPerPuzzleLicense()
    {
        const string page = """
            <h1>Hashi example</h1>
            <footer><table><tr><td>Autor:</td><td>A. Author</td></tr>
              <tr><td>Lizenz:</td><td><a rel="license" href="../Creative-Commons.htm">CC BY-NC-SA</a></td></tr>
            </table></footer>
            <script type="application/x-janko">
            [begin]
            puzzle hashi
            cols 3
            rows 1
            [problem]
            1 - 1
            [solution]
            - 1 -
            [end]
            </script>
            """;
        var url = new Uri("https://www.janko.at/Raetsel/Hashi/001.a.htm");
        Assert.Null(ExtraCollectionRefresh.ParseJanko(page, url, PuzzleGame.Hashi, false));
        Assert.Null(ExtraCollectionRefresh.ParseJanko(page.Replace("rel=\"license\"", ""), url, PuzzleGame.Hashi, true));
        var parsed = ExtraCollectionRefresh.ParseJanko(page, url, PuzzleGame.Hashi, true);
        Assert.NotNull(parsed);
        Assert.Equal("A. Author", parsed.Attribution.Author);
        Assert.Equal(["1", "-", "1"], parsed.Grid);
        var board = HashiPuzzle.Create(3, 1, [new(0, 0, 1), new(2, 0, 1)]);
        Assert.True(ExtraCollectionRefresh.MatchesHashiGrid(board, [1], parsed.Answer!));
        Assert.False(ExtraCollectionRefresh.MatchesHashiGrid(board, [1], ["-", "2", "-"]));
    }

    [Fact]
    public void NonogramProofRejectsAmbiguousLayouts()
    {
        var source = new PublishedPuzzle("test", "Test", "1", "https://example.test/puzzle/1", "CC0", "Test", "Test");
        Assert.Throws<InvalidDataException>(() => CollectionNonogram.Create(2, 2, "#..#", source, default));
        var unique = CollectionNonogram.Create(3, 2, "###..#", source, default);
        Assert.Equal("###..#", unique.Solution);
        Assert.Equal(2, unique.Metadata!.Solvability);
    }
}
