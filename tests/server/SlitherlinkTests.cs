using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.Slitherlink;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class SlitherlinkTests
{
    [Fact]
    public async Task ImportsVerifyUniquenessAndServeSourceFilteredCatalogQueueAndRestore()
    {
        using var database = new TestDatabase();
        using var upstream = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        await using var app = ApiTests.CreateApp(database, upstream);
        var importer = app.Services.GetRequiredService<SlitherlinkImporter>();
        var path = Path.Combine(Path.GetDirectoryName(database.Path)!, "collection.json");
        await File.WriteAllTextAsync(path, """
            { "puzzles": [
                { "source": "test-library", "sourceName": "Test Library", "author": "Sample", "difficulty": "hard",
                  "title": "A checked board", "clues": [[4, null]] },
                { "source": "test-library", "clues": [[null, null]] },
                { "source": "test-library", "clues": [[4, 0]] }
            ] }
            """);
        Assert.Equal(new SlitherlinkImportResult(1, 0, 2), await importer.ImportAsync(path, null, null, default));
        Assert.Equal(new SlitherlinkImportResult(0, 1, 2), await importer.ImportAsync(path, null, null, default));
        var kit = Path.Combine(Path.GetDirectoryName(database.Path)!, "kit.json");
        await File.WriteAllTextAsync(kit, """
            { "name": "Slitherlink", "data": { "one": { "problem": "1 2\n4 -", "source": "https://www.janko.at/example" } } }
            """);
        Assert.Equal(new SlitherlinkImportResult(1, 0, 0), await importer.ImportAsync(kit, null, null, default));

        await app.StartAsync();
        using var http = app.GetTestClient();
        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities");
        Assert.Contains("slitherlink", capabilities.GetProperty("games").EnumerateArray().Select(game => game.GetString()));
        Assert.Equal("Janko", capabilities.GetProperty("slitherlinkSources").GetProperty("janko").GetString());
        Assert.Equal("Test Library", capabilities.GetProperty("slitherlinkSources").GetProperty("test-library").GetString());

        const string catalog = "/api/v2/puzzles?game=slitherlink";
        var page = await http.GetFromJsonAsync<GameCatalogPage>(catalog +
            "&source=test-library&tilingFilter=square,triangular&difficulty=hard&width=2&height=1");
        var entry = Assert.Single(page!.Puzzles);
        Assert.Equal("Sample", entry.Author);
        Assert.Equal("A checked board", entry.Title);
        Assert.True(PuzzleId.TryParse(entry.Id, out var id) && id.Game == PuzzleGame.Slitherlink);
        var first = await http.GetFromJsonAsync<GameCatalogPage>(catalog + "&limit=1&sourceFilter=janko,test-library");
        Assert.NotNull(first!.NextBeforeId);
        var next = await http.GetFromJsonAsync<GameCatalogPage>(catalog + $"&limit=1&beforeId={first.NextBeforeId}");
        Assert.NotEqual(first.Puzzles[0].Id, Assert.Single(next!.Puzzles).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(catalog + "&source=../invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v2/puzzles?game=hashi&source=loopy")).StatusCode);

        var publicDefinition = Assert.IsType<SlitherlinkDefinition>(
            await http.GetFromJsonAsync<GamePuzzleDefinition>($"/api/v2/puzzles/{entry.Id}"));
        Assert.Null(publicDefinition.Solution);
        var response = await http.PostAsJsonAsync("/api/v2/offline-queue", new
        {
            Game = "slitherlink",
            TargetCount = 2,
            SourceFilter = "test-library",
            Tiling = "square",
            CachedUnstartedPuzzleIds = new[] { first.Puzzles.Concat(next.Puzzles).Single(puzzle => puzzle.Id != entry.Id).Id },
        });
        response.EnsureSuccessStatusCode();
        var queue = (await response.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        var playable = Assert.IsType<SlitherlinkDefinition>(Assert.Single(queue.Puzzles));
        Assert.Equal(entry.Id, playable.Id);
        Assert.Equal(1, queue.Remaining);
        Assert.True(playable.IsComplete(playable.Solution!));

        var rejected = await http.PostAsJsonAsync("/api/v2/progress", new
        {
            SchemaVersion = 1,
            Boards = new[] { new { PuzzleId = playable.Id, Marks = ".......", Completed = true } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var accepted = await http.PostAsJsonAsync("/api/v2/progress", new
        {
            SchemaVersion = 1,
            Boards = new[] { new { PuzzleId = playable.Id, Marks = playable.Solution, Completed = true } },
        });
        accepted.EnsureSuccessStatusCode();
        var restored = Assert.Single((await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore"))!);
        Assert.Equal(playable.Id, restored.Puzzle.Id);
        Assert.Equal(playable.Solution, restored.Progress.Marks.GetString());
        Assert.True(restored.Progress.Completed);
        Assert.Equal("solved", Assert.Single((await http.GetFromJsonAsync<GameCatalogPage>(catalog +
            "&source=test-library"))!.Puzzles).Status);
    }

    [Fact]
    public async Task ReadsMindgamesSquarePuzzleTextFromTheZipWithoutTrustingItsSolution()
    {
        using var database = new TestDatabase();
        var options = new SlitherlinkOptions();
        var importer = new SlitherlinkImporter(database.Store, new LoopyGenerator(options), options,
            NullLogger<SlitherlinkImporter>.Instance);
        var path = Path.Combine(Path.GetDirectoryName(database.Path)!, "mindgames.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("puz-01x01.txt").Open());
            await writer.WriteLineAsync("0101HA4X"); // The supplied solution is intentionally incorrect.
        }
        Assert.Equal(new SlitherlinkImportResult(1, 0, 0), await importer.ImportAsync(path, null, null, default));
        var filter = PuzzleFilter.Parse("slitherlink", null, null, "hard", null, source: "mindgames")!;
        var puzzle = Assert.Single(database.Store.ListSlitherlinkCatalog(filter, null, 10, false));
        Assert.Equal("####", database.Store.FindSlitherlink(puzzle.Id)!.Solution);
    }

    [Fact]
    public void DefaultStockCoversEverySizeTilingAndDifficultyCombination()
    {
        var options = new SlitherlinkOptions();
        var buckets = options.Buckets().Distinct().ToArray();
        Assert.Equal(3 * 18 * 4, buckets.Length);
        Assert.Equal([8, 12, 15], buckets.Select(bucket => bucket.Size).Distinct().Order());
        Assert.Equal(18, buckets.Select(bucket => bucket.Tiling).Distinct().Count());
        Assert.Equal(4, buckets.Select(bucket => bucket.Difficulty).Distinct().Count());
        Assert.All(buckets, bucket => Assert.True(SlitherlinkOptions.ValidDimensions(bucket.Size, bucket.Size, bucket.Tiling)));
        Assert.InRange(options.TimeoutSeconds, 1, 3590);
        Assert.True(options.TargetPerCombination > 1);
    }
}
