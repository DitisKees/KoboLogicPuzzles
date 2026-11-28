using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoboLogicPuzzles.Server.LinkAPix;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class LinkAPixTests
{
    [Fact]
    public void InclusivePathsCanDetourAndLeaveWhiteCells()
    {
        var board = new LinkAPixBoard(3, 2, [new(0, 4), new(1, 4), new(5, 1)]);
        int[][] path = [[0, 3, 4, 1]];
        Assert.Equal("##.###", board.Picture(path));
        Assert.Equal("44.39.", board.EncodeMarks(path));
        Assert.Equal(1, Prove(board).Count);
        Assert.Equal(path[0], Prove(board).Paths![0]);
        Assert.Null(board.Picture([[0, 1]]));
        Assert.Null(board.Picture([[0, 4, 3, 1]]));
        Assert.Null(board.Picture([[0, 3, 0, 1]]));
        Assert.Null(board.Picture([[0, 3, 4, 1], [1, 4, 3, 0]]));
        Assert.True(board.TryReadMarks("4..1..", false, out var unfinished));
        Assert.Equal(new[] { 0, 3 }, Assert.Single(unfinished));
        Assert.False(board.TryReadMarks("4..1..", true, out _));
        Assert.False(board.TryReadMarks("4..x..", false, out _));
        Assert.False(board.TryReadMarks("4..1.x", false, out _));
        Assert.Equal(path[0], Assert.Single(LinkAPixBoard.CanonicalPaths([[1, 4, 3, 0]])));
    }

    [Fact]
    public void CountsDistinctPathsIncludingSamePictureAndAlternativePairings()
    {
        var routes = new LinkAPixBoard(3, 3, [new(0, 9), new(8, 9)]);
        Assert.Equal("#########", routes.Picture([[0, 1, 2, 5, 4, 3, 6, 7, 8]]));
        Assert.Equal("#########", routes.Picture([[0, 3, 6, 7, 4, 1, 2, 5, 8]]));
        Assert.Equal(2, Prove(routes).Count);
        var pairings = new LinkAPixBoard(3, 3, [new(0, 3), new(2, 3), new(6, 3), new(8, 3)]);
        Assert.Equal(2, Prove(pairings).Count);

        var colors = new LinkAPixBoard(3, 2, [new(0, 2, "red"), new(1, 2, "blue"), new(3, 2, "red"), new(4, 2, "blue")]);
        Assert.Null(colors.Picture([[0, 1], [3, 4]]));
        Assert.Equal(1, Prove(colors).Count);
        var monochrome = new LinkAPixBoard(3, 2, [new(0, 2), new(1, 2), new(3, 2), new(4, 2)]);
        Assert.NotEqual(monochrome.ContentHash(), colors.ContentHash());
        Assert.Equal(2, Prove(monochrome).Count);
    }

    [Fact]
    public void ForeignCluesAndDisconnectedCyclesAreRejected()
    {
        var board = new LinkAPixBoard(3, 3, [new(0, 5), new(8, 5), new(1, 1)]);
        Assert.Null(board.Picture([[0, 1, 2, 5, 8]]));
        Assert.False(board.TryReadMarks("26c.39...", false, out _));
        Assert.Throws<TimeoutException>(() => new LinkAPixSolver(board, TimeSpan.Zero, default).CountSolutions());
        Assert.Throws<OperationCanceledException>(() =>
            new LinkAPixSolver(board, TimeSpan.FromSeconds(1), new CancellationToken(true)).CountSolutions());
        var longClue = new LinkAPixBoard(90, 20, [new(0, 513), new(1, 513)]);
        Assert.Throws<TimeoutException>(() => Prove(longClue));
    }

    [Fact]
    public void ParsesAuthorizedKettenAndGrayCellVectors()
    {
        var url = new Uri("https://www.janko.at/Raetsel/Ketten/041.a.htm");
        var entry = Assert.IsType<LinkImportEntry>(LinkAPixCollections.ParseJanko(JankoHtml, url));
        Assert.Equal("041", entry.Attribution.SourceId);
        Assert.Equal("CC BY-NC-SA 3.0", entry.Attribution.License);
        Assert.Equal(new[] { 0, 3, 4, 1 }, Assert.Single(entry.Paths!));
        Assert.Null(LinkAPixCollections.ParseJanko(JankoHtml.Replace("rel='license'", ""), url));
        Assert.Throws<InvalidDataException>(() => LinkAPixCollections.ParseJanko(JankoHtml.Replace("ne nw -", "n nw -"), url));

        var gray = LinkAPixGrayCell.Decode(GrayFixture(), 3, 2, Attribution("graycell"));
        Assert.Equal(entry.Clues, gray.Clues);
        Assert.Equal(new[] { 0, 3, 4, 1 }, Assert.Single(gray.Paths!));
        Assert.Throws<InvalidDataException>(() => LinkAPixGrayCell.Decode(GrayFixture(), 2, 2, Attribution("graycell")));
    }

    [Fact]
    public async Task ExistingCollectionPackDeduplicatesAndRoutesThroughOfflineQueueAndRestore()
    {
        using var database = new TestDatabase();
        using var source = TestData.Source(_ => throw new InvalidOperationException("Unexpected upstream call."));
        await using var app = ApiTests.CreateApp(database, source);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var importer = app.Services.GetRequiredService<LinkAPixCollections>();
        var entry = new LinkImportEntry(3, 2, [new(0, 4), new(1, 4), new(5, 1)], Attribution("janko"),
            [[1, 4, 3, 0]], "##.###");
        Assert.Equal(LinkImportResult.Added, importer.Import(entry, default));
        foreach (var name in new[] { "grandgames", "playsudoku", "graycell", "conceptis" })
            Assert.Equal(LinkImportResult.Duplicate, importer.Import(entry with { Attribution = Attribution(name) }, default));
        Assert.Equal(LinkImportResult.Invalid, importer.Import(entry with { Picture = "######" }, default));
        Assert.Equal(LinkImportResult.Ambiguous, importer.Import(new(3, 3, [new(0, 9), new(8, 9)], Attribution("local"),
            [[0, 1, 2, 5, 4, 3, 6, 7, 8]], "#########"), default));

        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v2/capabilities");
        Assert.True(capabilities.GetProperty("puzzleSources").GetProperty("link-a-pix").TryGetProperty("conceptis", out _));
        var listed = await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=link-a-pix&source=graycell");
        var summary = Assert.Single(listed!.Puzzles);
        Assert.StartsWith("Link-a-Pix ", summary.Title);
        Assert.Equal(5, ((JsonElement)summary.Metadata!).GetProperty("attributions").GetArrayLength());
        using var queue = await http.PostAsJsonAsync("/api/v2/offline-queue", new { game = "link-a-pix", targetCount = 1, source = "playsudoku" });
        queue.EnsureSuccessStatusCode();
        var downloaded = Assert.IsType<LinkAPixDefinition>(Assert.Single((await queue.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!.Puzzles));
        Assert.Null(downloaded.Solution);
        Assert.Null(downloaded.Picture);
        Assert.Equal("##.###", (await http.GetFromJsonAsync<JsonElement>($"/api/v2/puzzles/{summary.Id}?includeSolution=true")).GetProperty("picture").GetString());
        using var invalid = await Upload("4..1..", true);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var valid = await Upload("4..1..", false);
        valid.EnsureSuccessStatusCode();
        var backups = await http.GetFromJsonAsync<JsonElement>("/api/v2/restore?game=link-a-pix");
        var backup = Assert.Single(backups.EnumerateArray());
        Assert.Equal("4..1..", backup.GetProperty("progress").GetProperty("marks").GetString());
        Assert.False(backup.GetProperty("puzzle").TryGetProperty("solution", out _));
        var dashboard = await http.GetFromJsonAsync<JsonElement>("/api/v2/dashboard");
        Assert.Equal(1, dashboard.GetProperty("linkAPix").GetProperty("total").GetInt32());
        Assert.Equal(0, dashboard.GetProperty("linkAPix").GetProperty("unstarted").GetInt32());

        Task<HttpResponseMessage> Upload(string marks, bool completed) => http.PostAsJsonAsync("/api/v2/progress",
            new { schemaVersion = 1, boards = new[] { new { puzzleId = summary.Id, marks, completed } } });
    }

    private static LinkAttribution Attribution(string source) =>
        new(source, "41", "https://example.test/41", "Fixture author", "Picture title", "CC0");
    private static LinkProof Prove(LinkAPixBoard board) => new LinkAPixSolver(board, TimeSpan.FromSeconds(2), default).CountSolutions();

    private static byte[] GrayFixture()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var grid = new BinaryWriter(zip.CreateEntry("puzzle.emf").Open()))
            {
                Header(grid);
                for (var y = 0; y < 2; y++)
                    for (var x = 0; x < 3; x++)
                        Record(grid, 43, x * 20, y * 20, (x + 1) * 20, (y + 1) * 20);
                Label(grid, 3, 3, '4');
                Label(grid, 23, 3, '4');
                Label(grid, 43, 23, '1');
                Record(grid, 14, 0, 0, 20);
            }
            using (var answer = new BinaryWriter(zip.CreateEntry("ответы.emf").Open()))
            {
                Header(answer);
                Record(answer, 27, 10, 10);
                Record(answer, 54, 10, 30);
                Record(answer, 54, 30, 30);
                Record(answer, 54, 30, 10);
                Record(answer, 14, 0, 0, 20);
            }
        }
        return stream.ToArray();

        static void Header(BinaryWriter writer)
        {
            var fields = new int[20];
            fields[8] = 0x464d4520;
            Record(writer, 1, fields);
        }

        static void Label(BinaryWriter writer, int x, int y, char value)
        {
            var fields = new int[18];
            fields[7] = x;
            fields[8] = y;
            fields[9] = 1;
            fields[10] = 76;
            fields[17] = value;
            Record(writer, 84, fields);
        }

        static void Record(BinaryWriter writer, int kind, params int[] words)
        {
            writer.Write(kind);
            writer.Write(8 + 4 * words.Length);
            foreach (var word in words) writer.Write(word);
        }
    }

    private const string JankoHtml = """
        <h1>Fixture</h1><footer><table><tr><td>Autor:</td><td>Otto Janko</td></tr>
        <tr><td><a rel='license' href='../Creative-Commons.htm'>CC</a></td></tr></table></footer>
        <script type='application/x-janko'>
        [begin]
        puzzle ketten
        author Otto Janko
        cols 3
        rows 2
        [problem]
        4 4 -
        - - 1
        [solution]
        s s -
        ne nw -
        [end]
        </script>
        """;
}
