using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.Placement;
using Microsoft.AspNetCore.TestHost;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class PlacementTests
{
    [Fact]
    public void TentsCountLayoutsInsteadOfTreeAssignments()
    {
        var board = TentsCycle();
        var proof = Solve(board);
        Assert.Equal(1, proof.Count);
        Assert.Equal("#.#.x.#.#", proof.Solution);
        Assert.False(board.IsSolved("x.#.x.#.#"));
        Assert.False(board.ValidMarks("##x......"));
    }

    [Fact]
    public void TentsRejectsHallViolationAndDiagonalContact()
    {
        var givens = new string('.', 35).ToCharArray();
        givens[1] = givens[7] = givens[33] = 't';
        var marks = new string('.', 35).ToCharArray();
        marks[8] = marks[32] = marks[34] = '#';
        var (rows, columns) = PlacementBoard.Totals(7, 5, new(marks));
        var unmatched = new PlacementBoard(PuzzleGame.Tents, 7, 5, rows, columns, new(givens), []);
        Assert.False(unmatched.IsSolved(new(marks)));
        Assert.Equal(0, Solve(unmatched).Count);

        var touching = new PlacementBoard(PuzzleGame.Tents, 3, 3, [1, 1, 0], [1, 1, 0], ".t.t.....", []);
        Assert.False(touching.IsSolved("#...#...."));
        Assert.Equal(0, Solve(touching).Count);
    }

    [Fact]
    public void BattleshipsChecksFleetSegmentsAndEveryContactRule()
    {
        var board = Battleships();
        Assert.True(board.IsSolved("##xxxxxx#xxx"));
        Assert.False(board.IsSolved("#xxxxxxx#xxx"));
        Assert.False(board.IsSolved("####xxxxxxxx"));
        Assert.False(board.ValidMarks("x#xxxxxx#xxx"));
        Assert.Equal(1, Solve(board).Count);

        var bent = new PlacementBoard(PuzzleGame.Battleships, 3, 2, [2, 1], [2, 1, 0], "......", [0, 0, 1]);
        Assert.Equal(0, Solve(bent).Count);
        var diagonal = new PlacementBoard(PuzzleGame.Battleships, 2, 2, [1, 1], [1, 1], "....", [2]);
        Assert.Equal(0, Solve(diagonal).Count);
        var orthogonal = new PlacementBoard(PuzzleGame.Battleships, 2, 1, [2], [1, 1], "..", [2]);
        Assert.Equal(0, Solve(orthogonal).Count);
        var wrongTip = new PlacementBoard(PuzzleGame.Battleships, 2, 1, [2], [1, 1], "e.", [0, 1]);
        Assert.Equal(0, Solve(wrongTip).Count);
        var submarine = new PlacementBoard(PuzzleGame.Battleships, 1, 1, [1], [1], ".", [1]);
        Assert.Equal(1, Solve(submarine).Count);
        var ambiguous = new PlacementBoard(PuzzleGame.Battleships, 3, 3, [1, 0, 1], [1, 0, 1], ".........", [2]);
        Assert.Equal(2, Solve(ambiguous).Count);
        var ambiguousTents = new PlacementBoard(PuzzleGame.Tents, 3, 3, [1, 0, 1], [1, 0, 1], ".t.....t.", []);
        Assert.Equal(2, Solve(ambiguousTents).Count);
    }

    [Fact]
    public void ExactSearchAgreesWithExhaustiveSmallLayouts()
    {
        var random = new Random(173);
        foreach (var game in PlacementBoard.Games)
            for (var attempt = 0; attempt < 100; attempt++)
            {
                const int width = 4, height = 3, area = width * height;
                var order = Enumerable.Range(0, area).ToArray();
                random.Shuffle(order);
                var givens = new string('.', area).ToCharArray();
                var generator = new string('.', area).ToCharArray();
                var count = random.Next(1, 5);
                for (var j = 0; j < count; j++)
                {
                    generator[order[j]] = '#';
                    if (game == PuzzleGame.Tents)
                        givens[order[j + count]] = 't';
                }
                if (game == PuzzleGame.Battleships)
                    givens[order[0]] = attempt % 2 == 0 ? 'o' : 'x';
                var (rows, columns) = PlacementBoard.Totals(width, height, new(generator));
                int[] fleet = game == PuzzleGame.Tents ? [] : attempt % 2 == 0 || count == 1 ? [count] : [count - 2, 1];
                var board = new PlacementBoard(game, width, height, rows, columns, new(givens), fleet);
                var marks = new char[area];
                var expected = 0;
                for (var mask = 0; mask < 1 << area && expected < 2; mask++)
                {
                    for (var i = 0; i < area; i++)
                        marks[i] = (mask & (1 << i)) != 0 ? '#' : PlacementBoard.FixedMark(givens[i]) == 'x' ? 'x' : '.';
                    if (board.IsSolved(new(marks)))
                        expected++;
                }
                Assert.Equal(expected, Solve(board).Count);
            }
    }

    [Fact]
    public void NativeDecoderReadsColumnTotalsBeforeRowsAndRequiresSentinel()
    {
        var (board, solution) = PlacementGenerator.DecodeTents(3, 2, "ad,0,0,1,1,0", "S;T2,0");
        Assert.Equal(".t....", board.Givens);
        Assert.Equal("x.#xxx", solution);
        Assert.True(board.IsSolved(solution));
        Assert.Throws<InvalidDataException>(() => PlacementGenerator.DecodeTents(3, 2, "a,0,0,1,1,0", "S;T2,0"));
        Assert.Throws<InvalidDataException>(() => PlacementGenerator.DecodeTents(3, 2, "ad,0,0,1,1,0", "S;T2,0;T2,0"));
    }

    [Fact]
    public void GeneratedBattleshipsIsReproducibleAndIndependentlyVerifiable()
    {
        var first = PlacementGenerator.Battleships(20, 42, "hard", TimeSpan.FromSeconds(8), default);
        var second = PlacementGenerator.Battleships(20, 42, "hard", TimeSpan.FromSeconds(8), default);
        Assert.Equal(first.Board.Hash(), second.Board.Hash());
        Assert.Equal(first.Proof.Solution, second.Proof.Solution);
        Assert.Equal(1, Solve(first.Board).Count);
    }

    [Fact]
    public void ProofHonorsBudgetAndCancellation()
    {
        Assert.Throws<TimeoutException>(() => new PlacementSolver(TentsCycle(), TimeSpan.Zero, default).CountSolutions());
        Assert.Throws<OperationCanceledException>(() => new PlacementSolver(TentsCycle(), TimeSpan.FromSeconds(1),
            new CancellationToken(true)).CountSolutions());
    }

    [Fact]
    public async Task AuthorizedImportCatalogQueueBackupAndRestoreUseTypedDefinitions()
    {
        using var database = new TestDatabase();
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var collections = app.Services.GetRequiredService<PlacementCollections>();
        foreach (var (game, html, path, partial) in new[]
        {
            (PuzzleGame.Tents, JankoTents, "Zeltlager", "#........"),
            (PuzzleGame.Battleships, JankoShips, "Battleships", "#.x......"),
        })
        {
            var url = new Uri($"https://www.janko.at/Raetsel/{path}/901.a.htm");
            Assert.Null(PlacementCollections.ParseJanko(game, html.Replace("rel='license'", ""), url));
            Assert.Null(PlacementCollections.ParseJanko(game, html.Replace("Otto Janko", "Other author"), url));
            var entry = Assert.IsType<PlacementImport>(PlacementCollections.ParseJanko(game, html, url));
            Assert.True(collections.VerifyAndStore(entry, default));
            Assert.False(collections.VerifyAndStore(entry, default));
            Assert.Throws<InvalidDataException>(() => collections.VerifyAndStore(entry with { Solution = partial }, default));

            var slug = PlacementBoard.Slug(game);
            var list = await http.GetFromJsonAsync<GameCatalogPage>($"/api/v2/puzzles?game={slug}&source=janko&limit=1");
            var summary = Assert.Single(list!.Puzzles);
            Assert.Equal("Otto Janko", summary.Author);
            var summaryMetadata = Assert.IsType<JsonElement>(summary.Metadata);
            Assert.Equal("901", summaryMetadata.GetProperty("sourceId").GetString());
            Assert.Equal(url.AbsoluteUri, summaryMetadata.GetProperty("sourceUrl").GetString());
            Assert.True(summaryMetadata.GetProperty("unique").GetBoolean());
            var definition = await http.GetFromJsonAsync<JsonElement>($"/api/v2/puzzles/{summary.Id}");
            Assert.False(definition.TryGetProperty("solution", out _));

            using var reply = await http.PostAsJsonAsync("/api/v2/offline-queue", new { game = slug, targetCount = 1, source = "janko" });
            reply.EnsureSuccessStatusCode();
            var queued = Assert.IsAssignableFrom<PlacementDefinition>(Assert.Single((await reply.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!.Puzzles));
            Assert.Equal(entry.Solution, queued.Solution);
            Assert.True(queued.ToBoard().ValidMarks(partial));
            using var cachedReply = await http.PostAsJsonAsync("/api/v2/offline-queue",
                new { game = slug, targetCount = 1, source = "janko", cachedUnstartedPuzzleIds = new[] { queued.Id } });
            var cached = (await cachedReply.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!;
            Assert.Empty(cached.Puzzles);
            Assert.Equal(0, cached.Remaining);
            using var rejected = await Upload(partial, true);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var saved = await Upload(partial, false);
            saved.EnsureSuccessStatusCode();
            var restore = await http.GetFromJsonAsync<JsonElement>($"/api/v2/restore?game={slug}");
            var restored = Assert.Single(restore.EnumerateArray()).GetProperty("progress");
            Assert.Equal(partial, restored.GetProperty("marks").GetString());
            Assert.False(restored.GetProperty("completed").GetBoolean());
            using var solved = await Upload(queued.Solution!, true);
            solved.EnsureSuccessStatusCode();
            using var next = await http.PostAsJsonAsync("/api/v2/offline-queue", new { game = slug, targetCount = 1 });
            Assert.Empty((await next.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!.Puzzles);

            Task<HttpResponseMessage> Upload(string marks, bool completed) => http.PostAsJsonAsync("/api/v2/progress",
                new { schemaVersion = 1, boards = new[] { new { puzzleId = summary.Id, marks, completed } } });
        }
        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities");
        Assert.Equal(new[] { "tents", "battleships" }, capabilities.GetProperty("games").EnumerateArray().Select(v => v.GetString()).Where(v => v is "tents" or "battleships"));
        Assert.Equal(2, (await http.GetFromJsonAsync<JsonElement>("/api/v2/restore")).GetArrayLength());
        var dashboard = await http.GetFromJsonAsync<JsonElement>("/api/v2/dashboard");
        Assert.Equal(JsonValueKind.Object, dashboard.GetProperty("tents").GetProperty("activity").ValueKind);
        Assert.Contains(dashboard.GetProperty("catalog").EnumerateArray(), row => row.GetProperty("game").GetString() == "battleships");
    }

    private static PlacementBoard TentsCycle() => new(PuzzleGame.Tents, 3, 3, [2, 0, 2], [2, 0, 2], ".t.t.t.t.", []);
    private static PlacementBoard Battleships() => new(PuzzleGame.Battleships, 4, 3, [2, 0, 1], [2, 1, 0, 0], "w.x.........", [1, 1]);
    private static PlacementProof Solve(PlacementBoard board) => new PlacementSolver(board, TimeSpan.FromSeconds(3), default).CountSolutions();

    private const string Header = """
        <h1>Placement test</h1><footer><table><tr><td>Autor:</td><td>Otto Janko</td></tr>
        <tr><td><a rel='license' href='../Creative-Commons.htm'>CC</a></td></tr></table></footer>
        <script type='application/x-janko'>
        [begin]
        author Otto Janko
        """;

    private const string JankoTents = Header + "\n" + """
        puzzle tents
        size 3
        [solution]
        o x o
        x - x
        o x o
        [end]
        </script>
        """;

    private const string JankoShips = Header + "\n" + """
        puzzle battleships
        rows 3
        cols 3
        ships 0 1
        [rlabels]
        2 0 0
        [clabels]
        1 1 0
        [problem]
        w - x
        - - -
        - - -
        [solution]
        w e -
        - - -
        - - -
        [end]
        </script>
        """;
}
