using System.Net;
using KoboNonogram.Server;
using Microsoft.Data.Sqlite;

namespace KoboNonogram.Server.Tests;

internal static class TestData
{
    public static PuzzleDefinition Puzzle(int id) => new(id, $"Puzzle {id}", "Tess", 3, 2, [[1, 1], [2]], [[1], [1], [2]]);

    public static PuzzleSummary Summary(int id, int width = 3, int height = 2) => new(id, $"Puzzle {id}", "Tess", width, height);

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
        var rows = puzzles.Select(puzzle => $"""
            <tr><td><img src="img/w.gif"></td>
            <td><a href="play.cgi?id={puzzle.Id}&amp;sid=">#{puzzle.Id}:
            {WebUtility.HtmlEncode(puzzle.Title)}</a></td><td></td><td></td><td></td><td>1</td>
            <td>{puzzle.Width}x{puzzle.Height}</td><td>2</td><td>{WebUtility.HtmlEncode(puzzle.Author)}</td></tr>
            """);
        var contents = puzzles.Length == 0 ? "<tr><td colspan=\"9\"><em>None Found</em></td></tr>" : string.Join('\n', rows);
        var next = hasMore ? "<a href=\"find.cgi?pageno=1\">NEXT PAGE OF RESULTS</a>" : "";
        return $$"""
            <html><body><table><tr><td>Navigation</td></tr></table>{{next}}
            <table><tr><td colspan="2"><small>puzzle</small></td><td>quality</td>
            <td colspan="2">difficulty</td><td>comments</td><td>size</td><td>colors</td><td>author</td></tr>
            {{contents}}</table></body></html>
            """;
    }

    public static WebPbnClient Source(Func<Uri, HttpResponseMessage> respond, WebPbnOptions? options = null) =>
        new(new HttpClient(new ScriptedHandler(respond)) { BaseAddress = new Uri("https://webpbn.com/") },
            options ?? Options);

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    public static WebPbnOptions Options { get; } = new() { MinArea = 1, RequestDelayMilliseconds = 0 };

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
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("kobononogram-test-");

    public string Path => System.IO.Path.Combine(directory.FullName, "test.db");
    public PuzzleStore Store { get; }

    public TestDatabase()
    {
        Store = new PuzzleStore(Path);
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
