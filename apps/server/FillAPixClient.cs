using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AngleSharp.Dom;

namespace KoboLogicPuzzles.Server;

internal sealed partial class FillAPixClient(HttpClient http) : IDisposable
{
    private static readonly Uri Janko = new("https://www.janko.at/Raetsel/Mosaik/");
    private static readonly Uri FillAPixNet = new("https://fill-a-pix.net/");
    private static readonly Uri GrandGames = new("https://en.grandgames.net/");
    private readonly Dictionary<FillAPixSource, SemaphoreSlim> gates = Enum.GetValues<FillAPixSource>()
        .ToDictionary(source => source, _ => new SemaphoreSlim(1));
    private volatile string? grandGamesVersion;

    public async Task<IReadOnlyDictionary<int, string>> ReadIndexAsync(FillAPixSource source, CancellationToken token) =>
        source switch
        {
            FillAPixSource.Janko => FillAPixParsers.JankoCatalog(await ReadHtmlAsync(source, new Uri(Janko, "index.htm"), token)),
            FillAPixSource.FillAPixNet => FillAPixParsers.FillAPixNetCatalog(
                await ReadHtmlAsync(source, new Uri(FillAPixNet, "collection/index.html"), token)),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    public async Task<FillAPixPuzzleDefinition> FetchIndexedAsync(PuzzleId id, string title, CancellationToken token)
    {
        var source = id.FillAPixSource!.Value;
        var number = id.FillAPixNumber!.Value;
        if (source == FillAPixSource.Janko)
        {
            var url = new Uri(Janko, number.ToString("D3", CultureInfo.InvariantCulture) + ".a.htm");
            return FillAPixParsers.Janko(id, url, await ReadHtmlAsync(source, url, token));
        }
        var padded = number.ToString("D4", CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(FillAPixNet, $"puzzles/puzzle_{padded}.json"));
        using var response = await SendAsync(source, request, token);
        response.EnsureSuccessStatusCode();
        return FillAPixParsers.FillAPixNet(id, new Uri(FillAPixNet, $"collection/{padded}/index.html"), title,
            await response.Content.ReadAsStringAsync(token));
    }

    public async Task<(IReadOnlyList<FillAPixCatalogEntry> Entries, bool HasMore)> ReadGrandGamesCatalogPageAsync(int page, CancellationToken token)
    {
        var document = await ReadGrandGamesPageAsync(page, token);
        return (FillAPixParsers.GrandGamesCatalog(document, GrandGames),
            document.QuerySelector(".ag-catalog .pagination a[rel='next']") is not null);
    }

    public async Task<FillAPixPuzzleDefinition?> FetchGrandGamesAsync(FillAPixCatalogEntry entry, CancellationToken token)
    {
        var version = grandGamesVersion ?? await RefreshGrandGamesVersionAsync(token);
        using var initial = await StartGrandGamesAsync(entry.Id.FillAPixNumber!.Value, version, token);
        if (initial.StatusCode != HttpStatusCode.PreconditionFailed)
            return await ReadGrandGamesPuzzleAsync(entry, initial, token);

        version = await RefreshGrandGamesVersionAsync(token);
        using var retry = await StartGrandGamesAsync(entry.Id.FillAPixNumber.Value, version, token);
        return await ReadGrandGamesPuzzleAsync(entry, retry, token);
    }

    private async Task<HttpResponseMessage> StartGrandGamesAsync(int id, string version, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.grandgames.net/agmove/start")
        {
            Content = JsonContent.Create(new { id }),
        };
        request.Headers.Add("Origin", GrandGames.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("x-client", "web-php");
        request.Headers.Add("x-lang", "en");
        request.Headers.Add("x-version", version);
        return await SendAsync(FillAPixSource.GrandGames, request, token);
    }

    private static async Task<FillAPixPuzzleDefinition?> ReadGrandGamesPuzzleAsync(FillAPixCatalogEntry entry,
        HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;
        response.EnsureSuccessStatusCode();
        return FillAPixParsers.GrandGames(entry, await response.Content.ReadAsStringAsync(token));
    }

    private async Task<string> RefreshGrandGamesVersionAsync(CancellationToken token)
    {
        await ReadGrandGamesPageAsync(1, token);
        return grandGamesVersion!;
    }

    private async Task<IDocument> ReadGrandGamesPageAsync(int page, CancellationToken token)
    {
        var path = FormattableString.Invariant($"mosaic?sort=id_asc&status=all&page={page}");
        var document = await ReadHtmlAsync(FillAPixSource.GrandGames, new Uri(GrandGames, path), token);
        var version = document.Scripts.Select(script => SiteVersion().Match(script.TextContent)).FirstOrDefault(match => match.Success);
        if (version is null)
            throw new InvalidDataException("GrandGames did not publish its player version.");
        grandGamesVersion = version.Groups[1].Value;
        return document;
    }

    private async Task<IDocument> ReadHtmlAsync(FillAPixSource source, Uri url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(source, request, token);
        response.EnsureSuccessStatusCode();
        return new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(token));
    }

    private async Task<HttpResponseMessage> SendAsync(FillAPixSource source, HttpRequestMessage request, CancellationToken token)
    {
        var gate = gates[source];
        await gate.WaitAsync(token);
        try
        {
            var delay = source == FillAPixSource.GrandGames && request.Method == HttpMethod.Get
                ? TimeSpan.FromSeconds(2.5) : TimeSpan.FromMilliseconds(250);
            await Task.Delay(delay, token);
            return await http.SendAsync(request, token);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        http.Dispose();
        foreach (var gate in gates.Values)
            gate.Dispose();
    }

    [GeneratedRegex(@"\bSiteVersion\s*=\s*(\d+)\s*;")]
    private static partial Regex SiteVersion();
}
