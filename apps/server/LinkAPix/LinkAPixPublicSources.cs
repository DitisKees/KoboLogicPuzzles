using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace KoboLogicPuzzles.Server.LinkAPix;

internal sealed partial class LinkAPixCollections
{
    private static readonly Uri PlaySudoku = new("https://playsudoku.me/");
    private static readonly Uri GrayCell = new("https://www.graycell.ru/");
    private static readonly string[] PlayCategories = ["lap_xlarge", "lap_large"];
    internal static readonly string[] RefreshSources = ["janko", "playsudoku", "graycell"];
    private const string PersonalUse = "Personal noncommercial use (assumed; no explicit digital reuse grant)";

    internal bool Enabled(string source) => source switch
    {
        "janko" => JankoEnabled,
        "playsudoku" => AllowNonCommercial && config.GetValue("LinkAPixCollections:PlaySudokuEnabled", false),
        "graycell" => AllowNonCommercial && config.GetValue("LinkAPixCollections:GrayCellEnabled", false),
        _ => false,
    };

    internal Task<int> Refresh(string source, bool force, CancellationToken token) =>
        source == "janko" ? RefreshJanko(force, token) : RefreshPublic(source, force, token);

    private async Task<int> RefreshPublic(string source, bool force, CancellationToken token)
    {
        using var lease = store.TryLockLinkRefresh(source);
        if (lease is null)
            return 0;
        var state = store.GetLinkRefresh(source);
        if (!force && state.Cursor == "" && time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(state.CheckedAt) < RefreshInterval)
            return 0;
        if (state.Cursor == "")
            state = state with { Rejected = 0, Inconclusive = 0 };
        using var http = clients.CreateClient("link-a-pix");
        var added = 0;
        var checkedItems = 0;
        // Every item and every page boundary carry a cursor, even when upstream
        // data is unsupported. A transient failure leaves the last cursor intact.
        var work = source == "graycell" ? GrayEntries(http, state.Cursor, token) : PlayEntries(http, state.Cursor, token);
        await foreach (var (cursor, fetch) in work)
        {
            token.ThrowIfCancellationRequested();
            if (fetch is not null)
            {
                LinkImportResult result;
                try
                {
                    var entry = await fetch();
                    result = entry is null ? LinkImportResult.Invalid : Import(entry, token);
                }
                catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException ||
                    exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
                {
                    logger.LogWarning("Skipped Link-a-Pix {Source} {Cursor}: {Reason}", source, cursor, exception.Message);
                    result = LinkImportResult.Invalid;
                }
                if (result == LinkImportResult.Added) added++;
                if (result == LinkImportResult.Inconclusive)
                    state = state with { Inconclusive = state.Inconclusive + 1 };
                if (result is LinkImportResult.Ambiguous or LinkImportResult.Invalid)
                    state = state with { Rejected = state.Rejected + 1 };
                if (result is not (LinkImportResult.Added or LinkImportResult.Duplicate))
                    logger.LogDebug("Link-a-Pix {Source} {Cursor}: {Result}", source, cursor, result);
            }

            token.ThrowIfCancellationRequested();
            state = state with { Cursor = cursor };
            store.SaveLinkRefresh(source, state);
            await Task.Delay(TimeSpan.FromMilliseconds(700), time, token);
            if (source == "playsudoku" && fetch is not null && ++checkedItems == 32)
                return added;
        }

        store.SaveLinkRefresh(source, state with { Cursor = "", CheckedAt = time.GetUtcNow().ToUnixTimeSeconds() });
        return added;
    }

    private static async IAsyncEnumerable<(string Cursor, Func<Task<LinkImportEntry?>>? Fetch)> PlayEntries(HttpClient http,
        string cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var fields = cursor.Split('/');
        var category = fields.Length == 3 ? Number(fields[0]) : 0;
        var page = fields.Length == 3 ? Number(fields[1]) : 1;
        var before = fields.Length == 3 ? Number(fields[2]) : int.MaxValue;
        for (; category < PlayCategories.Length; category++, page = 1, before = int.MaxValue)
        {
            var root = $"lap/{PlayCategories[category]}";
            for (; page <= 250; page++, before = int.MaxValue)
            {
                token.ThrowIfCancellationRequested();
                var url = new Uri(PlaySudoku, page == 1 ? root : $"{root}/page/{page}");
                var html = await http.GetStringAsync(url, token);
                var doc = new HtmlParser().ParseDocument(html);
                var links = doc.QuerySelectorAll("article a[href]").Select(a => a.GetAttribute("href")!)
                    .Select(link => PlayId().Match(link)).Where(match => match.Success)
                    .Select(match => Number(match.Groups[1].Value)).Distinct().OrderDescending().ToArray();
                if (links.Length == 0)
                    throw new InvalidDataException("The PlaySudoku archive page contains no puzzle links.");
                foreach (var id in links.Where(id => id < before))
                {
                    var detail = new Uri(PlaySudoku, $"lap/l_{id}.html");
                    yield return ($"{category}/{page}/{id}", async () =>
                        ParsePlaySudoku(await http.GetStringAsync(detail, token), detail, id.ToString(CultureInfo.InvariantCulture)));
                }

                var next = new Uri(PlaySudoku, $"{root}/page/{page + 1}");
                if (page == 250 || !doc.QuerySelectorAll("a[href]").Any(a =>
                    Uri.TryCreate(url, a.GetAttribute("href"), out var link) && link.AbsoluteUri.TrimEnd('/') == next.AbsoluteUri))
                    break;
                yield return ($"{category}/{page + 1}/{int.MaxValue}", null);
            }
        }
    }

    internal static LinkImportEntry? ParsePlaySudoku(string html, Uri url, string id)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var data = doc.QuerySelector("script[type='application/x-janko']")?.TextContent ??
            throw new InvalidDataException("Missing the structured PlaySudoku clues.");
        var author = KettenAuthor().Match(data).Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidDataException("Missing the PlaySudoku author.");
        var title = doc.QuerySelector("h1.entry-title")?.TextContent.Trim() ?? $"PlaySudoku #{id}";
        var entry = ParseKetten(data, new("playsudoku", id, url.AbsoluteUri, author, title, PersonalUse));
        return entry.Width >= 30 && entry.Height >= 30 ? entry : null;
    }

    private static async IAsyncEnumerable<(string Cursor, Func<Task<LinkImportEntry?>>? Fetch)> GrayEntries(HttpClient http,
        string cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        // The printable links are relative to the site root, as in GrayCell's browser UI.
        var last = cursor.Length == 0 ? int.MaxValue : Number(cursor);
        var entries = new List<(int Id, string Title, int Width, int Height)>();
        for (var page = 1; page <= 3; page++)
        {
            var url = new Uri(GrayCell, $"works/filip/{page}.html");
            var doc = new HtmlParser().ParseDocument(await http.GetStringAsync(url, token));
            foreach (var link in doc.QuerySelectorAll("a[href]"))
            {
                var match = GrayId().Match(link.GetAttribute("href")!);
                if (!match.Success) continue;
                var container = link.ParentElement;
                while (container is not null && !container.ClassList.Contains("workfield"))
                    container = container.ParentElement;
                var dimensions = GraySize().Match(container?.QuerySelector(".workdescbar")?.TextContent ?? "");
                if (!dimensions.Success) continue;
                var title = container?.QuerySelector("img[alt]")?.GetAttribute("alt")?.Trim() ?? $"GrayCell #{match.Groups[1].Value}";
                entries.Add((Number(match.Groups[1].Value), title, Number(dimensions.Groups[1].Value), Number(dimensions.Groups[2].Value)));
            }
        }
        if (entries.Count == 0)
            throw new InvalidDataException("The GrayCell catalog contains no printable puzzle ZIPs.");

        foreach (var entry in entries.DistinctBy(entry => entry.Id).OrderByDescending(entry => entry.Id).Where(entry => entry.Id < last))
        {
            var id = entry.Id.ToString(CultureInfo.InvariantCulture);
            var url = new Uri(GrayCell, $"download/zip/Filip{id}.zip");
            yield return (id, async () => LinkAPixGrayCell.Decode(await http.GetByteArrayAsync(url, token), entry.Width, entry.Height,
                new("graycell", id, url.AbsoluteUri, "Алексей Пискунов", entry.Title, PersonalUse,
                    new Uri(GrayCell, "FAQ.html").AbsoluteUri)));
        }
    }

    [GeneratedRegex(@"(?:\A|/lap/)l_([0-9]{1,8})\.html\z")]
    private static partial Regex PlayId();

    [GeneratedRegex(@"(?m)^\s*author\s+([^\r\n]+)$")]
    private static partial Regex KettenAuthor();

    [GeneratedRegex(@"\Adownload/zip/Filip([0-9]{1,3})\.zip\z")]
    private static partial Regex GrayId();

    [GeneratedRegex(@"(\d{1,3})\s*[xх×]\s*(\d{1,3})")]
    private static partial Regex GraySize();
}
