using System.Globalization;
using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;

namespace KoboNonogram.Server;

public sealed class WebPbnClient(HttpClient http, WebPbnOptions options) : IDisposable
{
    private readonly SemaphoreSlim requestGate = new(1);

    public async Task<WebPbnPage> SearchAsync(int page, CancellationToken cancellationToken, int? solvability = null)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["search"] = "1", ["status"] = "0", ["minsize"] = Format(options.MinArea),
            ["maxsize"] = Format(options.MaxArea), ["mincolor"] = "2", ["maxcolor"] = "2",
            ["uniq"] = "1", ["blots"] = "2", ["guess"] = Format(solvability ?? options.Solvability),
            ["minqual"] = Format((int)(options.MinQuality * 4)), ["maxqual"] = Format((int)(options.MaxQuality * 4)),
            ["mindiff"] = Format((int)(options.MinDifficulty * 4)), ["maxdiff"] = Format((int)(options.MaxDifficulty * 4)),
            ["order"] = "0", ["perpage"] = "100", ["pageno"] = Format(page),
        };
        if (options.IncludeUnratedQuality)
            parameters["unqual"] = "1";
        if (options.IncludeUnratedDifficulty)
            parameters["undiff"] = "1";

        var html = await ReadAsync(QueryHelpers.AddQueryString("find.cgi", parameters), false, cancellationToken);
        return WebPbnCatalogParser.Parse(html!);
    }

    public async Task<PuzzleDefinition?> FetchAsync(int id, CancellationToken cancellationToken)
    {
        var xml = await ReadAsync($"export.cgi?go=1&id={Format(id)}&fmt=xml&xml_clue=on&xml_soln=on",
            true, cancellationToken);
        if (xml is null)
            return null;

        var missingMessage = $"Puzzle {Format(id)} does not exist";
        if (xml.Contains(missingMessage, StringComparison.Ordinal) &&
            new HtmlParser().ParseDocument(xml).Body?.TextContent.Trim() == missingMessage)
            return null;
        return WebPbnPuzzleParser.Parse(xml, id);
    }

    private async Task<string?> ReadAsync(string path, bool allowMissing, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Delay(options.RequestDelayMilliseconds, cancellationToken);
            using var response = await http.GetAsync(path, cancellationToken);
            if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        finally
        {
            requestGate.Release();
        }
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        http.Dispose();
        requestGate.Dispose();
    }
}
