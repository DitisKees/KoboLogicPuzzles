using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.Nurikabe;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class NurikabeTests
{
    [Theory]
    [InlineData(3, 1, new[] { 1, 0, 0 }, "x#x")]
    [InlineData(3, 1, new[] { 1, 1, 0 }, "xx#")]
    [InlineData(3, 1, new[] { 2, 0, 0 }, "x##")]
    [InlineData(3, 1, new[] { 0, 1, 0 }, "#x#")]
    [InlineData(3, 2, new[] { 2, 0, 0, 0, 0, 0 }, "x##x##")]
    public void RulesRejectInvalidIslandsAndSeas(int width, int height, int[] clues, string marks)
    {
        Assert.False(new NurikabeBoard(width, height, clues).IsSolved(marks));
    }

    [Fact]
    public void ExactCountingMatchesExhaustiveEnumerationOfAllClueGridsOnTwoByThree()
    {
        const int size = 6;
        for (var encoded = 1; encoded < 46656; encoded++)
        {
            var digits = encoded;
            var clues = new int[size];
            for (var cell = 0; cell < size; cell++, digits /= size)
                clues[cell] = digits % size;
            if (clues.Sum() >= size)
                continue;

            var board = new NurikabeBoard(2, 3, clues);
            var answers = Enumerable.Range(0, 1 << size)
                .Select(mask => new string(Enumerable.Range(0, size)
                    .Select(cell => (mask & 1 << cell) == 0 ? 'x' : '#').ToArray()))
                .Where(board.IsSolved).Take(2).ToArray();
            var proof = Prove(board);
            Assert.Equal(answers.Length, proof.Count);
            if (answers.Length == 1)
                Assert.Equal(answers[0], proof.Solution);
        }
    }

    [Theory]
    [InlineData(25, 2)]
    [InlineData(50, 2)]
    public void GeneratedCandidatesAreReproducibleAndValid(int size, int seed)
    {
        var first = NurikabeGenerator.Candidate(size, seed, default);
        var second = NurikabeGenerator.Candidate(size, seed, default);
        Assert.Equal(first.Board.Id(), second.Board.Id());
        Assert.Equal(first.Solution, second.Solution);
        Assert.True(first.Board.IsSolved(first.Solution));
        var proof = Prove(first.Board);
        Assert.Equal(1, proof.Count);
        Assert.Equal(first.Solution, proof.Solution);
        Assert.Equal("bounded_lookahead", proof.Solvability);
    }

    [Fact]
    public void CancellationAndTimeoutDoNotProduceAProof()
    {
        var board = new NurikabeBoard(2, 2, [1, 0, 0, 0]);
        Assert.Throws<TimeoutException>(() => new NurikabeSolver(board, TimeSpan.Zero, default).CountSolutions());
        using var token = new CancellationTokenSource();
        token.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new NurikabeSolver(board, TimeSpan.FromSeconds(1), token.Token).CountSolutions());
    }

    [Fact]
    public void CollectionDataRequiresMatchingRightsAndValidSolutions()
    {
        const string html = """
            <h1>Test Island</h1><footer><table>
            <tr><td>Autor:</td><td>Palmer Mebane</td></tr>
            <tr><td>Lizenz:</td><td>"You may redistribute these puzzles as long as credit for
            their construction is provided to me"</td></tr>
            </table></footer><script type="application/x-janko">
            [begin]
            puzzle nurikabe
            size 2
            [problem]
            1 -
            - -
            [solution]
            - x
            x x
            [end]
            </script>
            """;
        var url = new Uri("https://www.janko.at/Raetsel/Nurikabe/0690.a.htm");
        var entry = NurikabeCollections.ParseJanko(html, url);
        Assert.NotNull(entry);
        Assert.Equal("0690", entry.SourceId);
        Assert.Equal("Palmer Mebane", entry.Author);
        Assert.Equal("x###", entry.Solution);
        Assert.Equal([1, 0, 0, 0], entry.Clues!);
        Assert.Null(NurikabeCollections.ParseJanko(html.Replace("Palmer Mebane", "Another Author"), url));

        var direct = NurikabeImporter.DecodePuzzLink("https://puzz.link/p?nurikabe/2/2/i1");
        Assert.Equal(new NurikabeBoard(2, 2, entry.Clues!).Id(), direct.Id());
        Assert.Throws<InvalidDataException>(() => NurikabeImporter.DecodePuzzLink("https://puzz.link/p?nurikabe/2/2/.i"));
        using var database = new TestDatabase();
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        using var app = ApiTests.CreateApp(database, source);
        var importer = app.Services.GetRequiredService<NurikabeImporter>();
        Assert.Throws<InvalidDataException>(() => importer.Store(entry with { Solution = "xx##" }, default));
        Assert.Throws<InvalidDataException>(() => importer.Store(entry with { Solution = null, Clues = [2, 0, 0, 0] }, default));
        Assert.True(importer.Store(entry, default));
        Assert.False(importer.Store(entry with { Clues = direct.Clues, Solution = null }, default));
    }

    [Fact]
    public async Task CatalogOfflineQueueProgressAndRestoreIncludeProvenance()
    {
        using var database = new TestDatabase();
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream request."));
        await using var app = ApiTests.CreateApp(database, source);
        var importer = app.Services.GetRequiredService<NurikabeImporter>();
        Assert.True(importer.Store(new("test-library", "Test Library", "1", "https://example.test/nurikabe/1",
            "Permission granted", "Puzzle Author", "Tiny Island", 2, 2, [1, 0, 0, 0]), default));
        var id = new NurikabeBoard(2, 2, [1, 0, 0, 0]).Id();

        await app.StartAsync();
        using var http = app.GetTestClient();
        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities");
        Assert.Contains("nurikabe", capabilities.GetProperty("games").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("collection", capabilities.GetProperty("puzzleSources").GetProperty("nurikabe")
            .GetProperty("test-library").GetProperty("kind").GetString());

        var page = (await http.GetFromJsonAsync<GameCatalogPage>(
            "/api/v2/puzzles?game=nurikabe&source=test-library&difficulty=easy&solvability=basic_logic&width=2"))!;
        Assert.Equal(id, Assert.Single(page.Puzzles).Id);
        var definition = await http.GetFromJsonAsync<JsonElement>($"/api/v2/puzzles/{id}");
        Assert.False(definition.TryGetProperty("solution", out _));
        Assert.Equal("Puzzle Author", definition.GetProperty("author").GetString());

        using var queued = await http.PostAsJsonAsync("/api/v2/offline-queue", new { Game = "nurikabe", TargetCount = 1 });
        queued.EnsureSuccessStatusCode();
        var queue = (await queued.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
        Assert.Equal("x###", Assert.IsType<NurikabeDefinition>(Assert.Single(queue.Puzzles)).Solution);
        Assert.Equal(0, queue.Remaining);

        using var bad = await SaveAsync(".###", true);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var incomplete = await SaveAsync("x#..", false);
        incomplete.EnsureSuccessStatusCode();
        using var complete = await SaveAsync("x###", true);
        complete.EnsureSuccessStatusCode();
        var restore = Assert.Single((await http.GetFromJsonAsync<GameSavedBoard[]>("/api/v2/restore?game=nurikabe"))!);
        Assert.True(restore.Progress.Completed);
        Assert.Equal("x###", restore.Progress.Marks.GetString());
        Assert.True(Assert.IsType<NurikabeDefinition>(restore.Puzzle).Metadata.Unique);

        var dashboard = await http.GetFromJsonAsync<JsonElement>("/api/v2/dashboard");
        var row = Assert.Single(dashboard.GetProperty("catalog").EnumerateArray(),
            entry => entry.GetProperty("game").GetString() == "nurikabe");
        Assert.Equal("collection", row.GetProperty("sourceKind").GetString());
        Assert.Equal(0, row.GetProperty("unstarted").GetInt32());

        Task<HttpResponseMessage> SaveAsync(string marks, bool completed) =>
            http.PostAsJsonAsync("/api/v2/progress", new { SchemaVersion = 1, Boards = new[] { new { PuzzleId = id, Marks = marks, Completed = completed } } });
    }

    private static NurikabeProof Prove(NurikabeBoard board) =>
        new NurikabeSolver(board, TimeSpan.FromSeconds(5), default).CountSolutions();
}
