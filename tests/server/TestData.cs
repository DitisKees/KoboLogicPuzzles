using System.Globalization;
using System.Net;
using System.Text.Json;
using KoboLogicPuzzles.Server;
using KoboLogicPuzzles.Server.Hashi;
using Microsoft.Data.Sqlite;

namespace KoboLogicPuzzles.Server.Tests;

internal static class TestData
{
    public static PuzzleDefinition Puzzle(int id) => new(id, $"Puzzle {id}", "Tess", 3, 2, [[1, 1], [2]], [[1], [1], [2]]);

    public static PuzzleSummary Summary(int id, int width = 3, int height = 2) => new(id, $"Puzzle {id}", "Tess", width, height);

    public static HashiCandidate HashiPair(int row = 0, int count = 1, int size = 20, int column = 0)
    {
        var description = Enumerable.Repeat('a', size * size).ToArray();
        description[row * size + column] = description[row * size + column + 4] = (char)('0' + count);
        return HashiCandidate.ParseBridges(size, new string(description), $"S;L{column},{row},{column + 4},{row},{count}");
    }

    public static HashiCandidate HashiFixture(int size, string seed, int upstreamDifficulty = 2)
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HashiFixtures.json")));
        var fixture = fixtures.RootElement.EnumerateArray().Single(row => row.GetProperty("size").GetInt32() == size &&
            row.GetProperty("seed").GetString() == seed && row.GetProperty("upstreamDifficulty").GetInt32() == upstreamDifficulty);
        return HashiCandidate.ParseBridges(size, fixture.GetProperty("description").GetString()!,
            fixture.GetProperty("solution").GetString()!);
    }

    public static VerifiedHashiPuzzle VerifyHashi(HashiCandidate candidate, string seed = "00000001")
    {
        var (validation, puzzle) = VerifiedHashiPuzzle.Verify(candidate, new HashiGenerationParameters(seed, 10, 10, 2),
            TimeSpan.FromMinutes(3), new DateTimeOffset(2026, 11, 24, 11, 0, 0, TimeSpan.Zero));
        Assert.Equal(HashiValidationStatus.Unique, validation.Status);
        return Assert.IsType<VerifiedHashiPuzzle>(puzzle);
    }

    public static string Export(int id) => $$"""
        <?xml version="1.0"?>
        <!DOCTYPE pbn SYSTEM "https://webpbn.com/pbn-0.3.dtd">
        <puzzleset><source>webpbn.com</source>
        <puzzle type="grid" defaultcolor="ink">
        <id>#{{id}} (v.1)</id><title>Bricks &amp; Steps</title><author>Tess</author>
        <color name="paper" char=".">FFFFFF</color><color name="ink" char="X">000000</color>
        <clues type="rows"><line><count>1</count><count>1</count></line><line><count>2</count></line></clues>
        <clues type="columns"><line><count>1</count></line><line><count>1</count></line><line><count>2</count></line></clues>
        </puzzle></puzzleset>
        """;

    public static string SearchPage(bool hasMore, params PuzzleSummary[] puzzles)
    {
        static string Star(decimal? rating) => rating is { } value
            ? $"<img src=\"img/star{(value * 4).ToString("0", CultureInfo.InvariantCulture)}.gif\">"
            : "";
        var rows = puzzles.Select(puzzle => $"""
            <tr><td><img src="img/w.gif"></td>
            <td><a href="play.cgi?id={puzzle.Id}&amp;sid=">#{puzzle.Id}:
            {WebUtility.HtmlEncode(puzzle.Title)}</a></td>
            <td>{Star(puzzle.Metadata?.Quality)}</td><td>{Star(puzzle.Metadata?.Difficulty)}</td><td></td><td>1</td>
            <td>{puzzle.Width}x{puzzle.Height}</td><td>2</td><td>{WebUtility.HtmlEncode(puzzle.Author)}</td></tr>
            """);
        var contents = puzzles.Length == 0 ? "<tr><td colspan=\"9\"><em>None Found</em></td></tr>" : string.Join('\n', rows);
        var next = hasMore ? """
            <tr><td colspan="10" align="center"><a href="find.cgi?search=1&amp;pageno=1">NEXT PAGE OF RESULTS</a></td></tr>
            """ : "";
        return $$"""
            <html><body><table><tr><td>Navigation</td></tr></table>
            <table><tr><td colspan="2"><small>puzzle</small></td><td>quality</td>
            <td colspan="2">difficulty</td><td>comments</td><td>size</td><td>colors</td><td>author</td></tr>
            {{contents}}{{next}}</table></body></html>
            """;
    }

    public static WebPbnClient Source(Func<Uri, HttpResponseMessage> respond, WebPbnOptions? options = null) =>
        new(new HttpClient(new ScriptedHandler(respond)) { BaseAddress = new Uri("https://webpbn.com/") },
            options ?? Options);

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    public static WebPbnOptions Options { get; } = new() { MinArea = 1, Solvability = 2, RequestDelayMilliseconds = 0 };

    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}

internal sealed class TestDatabase : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("kobologicpuzzles-test-");

    public string Path => System.IO.Path.Combine(directory.FullName, "test.db");
    public PuzzleStore Store { get; }

    public TestDatabase(bool initialize = true)
    {
        Store = new PuzzleStore(Path);
        if (initialize)
            Store.Initialize();
    }

    public void SeedCatalog(IEnumerable<PuzzleSummary> puzzles, DateTimeOffset? completedAt = null) =>
        Assert.True(Store.TryReplaceCatalog(puzzles, Store.GetCatalogRefreshState().Revision,
            completedAt ?? DateTimeOffset.UtcNow));

    public void Dispose()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, ForeignKeys = true }.ToString());
        SqliteConnection.ClearPool(connection);
        directory.Delete(recursive: true);
    }
}
