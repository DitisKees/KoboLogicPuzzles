using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace KoboLogicPuzzles.Server.Slitherlink;

internal sealed partial class SlitherlinkCollectionRefresh
{
    internal const string Janko = "janko";
    internal const string GrandGames = "grandgames";
    private static readonly Uri JankoIndex = new("https://www.janko.at/Raetsel/Slitherlink/index-1.htm");
    private static readonly Uri GrandGamesSite = new("https://en.grandgames.net/");
    private static readonly TimeSpan PuzzlePageDelay = TimeSpan.FromMilliseconds(500);

    private sealed record GrandGamesResponse(GrandGamesLevel? Level);
    private sealed record GrandGamesLevel(int Id, string? Game, int Sx, int Sy, string? P);

    public async Task RefreshJankoAsync(CancellationToken token)
    {
        var index = await ReadHtmlAsync(JankoIndex, PuzzlePageDelay, token);
        var boards = index.QuerySelectorAll("a[href]").Select(link => link.GetAttribute("href")!)
            .Where(link => JankoPuzzleLink().IsMatch(link)).Distinct().ToArray();
        if (boards.Length == 0)
            throw new InvalidDataException("The Janko index has no Slitherlink puzzles.");
        foreach (var path in boards)
        {
            var url = new Uri(JankoIndex, path);
            var resource = $"{Janko}/{Path.GetFileName(url.AbsolutePath)}";
            if (store.GetSlitherlinkCollectionRefreshState(resource) is not null)
                continue;
            try
            {
                var document = await ReadHtmlAsync(url, PuzzlePageDelay, token);
                await ImportJankoAsync(document, url, token);
            }
            catch (InvalidDataException exception)
            {
                logger.LogWarning("Skipping Janko puzzle at {Url}: {Reason}", url, exception.Message);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                logger.LogWarning("Janko puzzle at {Url} is unavailable.", url);
            }
            MarkChecked(resource);
        }
        MarkChecked(Janko);
    }

    private Task ImportJankoAsync(IDocument document, Uri url, CancellationToken token)
    {
        var data = document.QuerySelector("script[type='application/x-janko']")?.TextContent ??
            throw new InvalidDataException("The puzzle data is absent.");
        var sections = JankoSection().Split(data);
        if (sections.Length < 5 || sections[1] != "begin" || Array.IndexOf(sections, "problem") is not (>= 3 and var problem))
            throw new InvalidDataException("The clue grid is absent.");
        var fields = sections[2].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var clues = sections[problem + 1].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(row => !row.StartsWith("unit ", StringComparison.Ordinal))
            .Select(row => row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(value => value switch
            {
                "-" => (int?)null,
                [>= '0' and <= '4'] => value[0] - '0',
                _ => throw new InvalidDataException("Unexpected Janko clue."),
            }).ToArray()).ToArray();
        var license = document.QuerySelectorAll("footer td").FirstOrDefault(cell => cell.TextContent.Trim() == "Lizenz:")
            ?.NextElementSibling?.TextContent.Trim();
        var author = fields.FirstOrDefault(field => field.StartsWith("author ", StringComparison.Ordinal))?[7..].Trim();
        return importer.ImportSquareAsync(clues, new("square", Janko, "Janko", 0, SourceUrl: url.AbsoluteUri, License: license),
            document.QuerySelector("h1")?.TextContent.Trim(), author, token);
    }

    public async Task RefreshGrandGamesAsync(CancellationToken token)
    {
        const string cursor = GrandGames + "/page";
        var previous = store.GetSlitherlinkCollectionRefreshState(GrandGames);
        var since = int.Parse(previous?.Version ?? "0", CultureInfo.InvariantCulture);
        var checkpoint = store.GetSlitherlinkCollectionRefreshState(cursor)?.Version?.Split('/');
        var page = checkpoint is [var savedPage, _] ? int.Parse(savedPage, CultureInfo.InvariantCulture) : 1;
        var newest = checkpoint is [_, var savedNewest] ? int.Parse(savedNewest, CultureInfo.InvariantCulture) : since;
        while (true)
        {
            var url = new Uri(GrandGamesSite, $"chinawall?sort=id&status=all&page={page}");
            var catalog = await ReadHtmlAsync(url, TimeSpan.FromSeconds(2.5), token);
            var siteVersion = catalog.Scripts.Select(script => GrandGamesVersion().Match(script.TextContent)).FirstOrDefault(match => match.Success);
            var entries = catalog.QuerySelectorAll(".ag-catalog article.gg-card .gg-card__title a")
                .Select(link => (Link: link, Match: GrandGamesLink().Match(link.GetAttribute("href") ?? "")))
                .Where(entry => entry.Match.Success)
                .Select(entry => (Id: int.Parse(entry.Match.Groups[1].Value, CultureInfo.InvariantCulture), Title: entry.Link.TextContent.Trim()))
                .ToArray();
            if (siteVersion is null || entries.Length == 0)
                throw new InvalidDataException("GrandGames returned an unrecognized Wall of China catalog.");
            newest = Math.Max(newest, entries.Max(entry => entry.Id));
            foreach (var entry in entries.Where(entry => entry.Id > since))
            {
                var resource = $"{GrandGames}/{entry.Id}";
                if (store.GetSlitherlinkCollectionRefreshState(resource) is not null)
                    continue;
                try
                {
                    await ImportGrandGamesAsync(entry.Id, entry.Title, siteVersion.Groups[1].Value, token);
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException)
                {
                    logger.LogWarning("Skipping GrandGames Slitherlink puzzle {Id}: {Reason}", entry.Id, exception.Message);
                }
                MarkChecked(resource);
            }
            if (entries.Any(entry => entry.Id <= since) || catalog.QuerySelector(".ag-catalog .pagination a[rel='next']") is null)
                break;
            MarkChecked(cursor, $"{++page}/{newest}");
        }
        MarkChecked(cursor);
        MarkChecked(GrandGames, newest.ToString(CultureInfo.InvariantCulture));
    }

    private async Task ImportGrandGamesAsync(int id, string title, string version, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.grandgames.net/agmove/start")
        {
            Content = JsonContent.Create(new { id }),
        };
        request.Headers.Add("Origin", GrandGamesSite.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("x-client", "web-php");
        request.Headers.Add("x-lang", "en");
        request.Headers.Add("x-version", version);
        await Task.Delay(PuzzlePageDelay, time, token);
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return;
        response.EnsureSuccessStatusCode();
        var level = (await response.Content.ReadFromJsonAsync<GrandGamesResponse>(cancellationToken: token))?.Level;
        if (level is null || level.Id != id || level.Game != "chinawall" || level.Sx is < 1 or > 100 || level.Sy is < 1 or > 100 ||
            string.IsNullOrWhiteSpace(level.P))
            throw new InvalidDataException($"GrandGames returned an invalid clue grid for puzzle {id}.");
        var clues = ReadGrandGamesClues(level.P, level.Sx, level.Sy);
        await importer.ImportSquareAsync(clues, new("square", GrandGames, "GrandGames", 0,
            SourceUrl: new Uri(GrandGamesSite, $"chinawall/id{id}").AbsoluteUri), title, null, token);
    }

    private static int?[][] ReadGrandGamesClues(string puzzle, int width, int height)
    {
        if (puzzle.AsSpan().TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            var rows = JsonSerializer.Deserialize<JsonElement[][]>(puzzle);
            if (rows is null || rows.Length != height || rows.Any(row => row is null || row.Length != width))
                throw new InvalidDataException("Unexpected dimensions in the GrandGames clue grid.");
            return rows.Select(row => row.Select(value => value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var clue) && clue is >= 0 and <= 4 => (int?)clue,
                JsonValueKind.String when value.GetString() is "" or "." or " " => null,
                JsonValueKind.String when value.GetString() is [>= '0' and <= '4'] text => text[0] - '0',
                _ => throw new InvalidDataException("Unexpected GrandGames clue."),
            }).ToArray()).ToArray();
        }
        if (puzzle.Length != width * height)
            throw new InvalidDataException("Unexpected dimensions in the GrandGames clue string.");

        // Wall of China flattens rows in display order.
        var clues = new int?[height][];
        for (var y = 0; y < height; y++)
        {
            clues[y] = new int?[width];
            for (var x = 0; x < width; x++)
                clues[y][x] = puzzle[y * width + x] switch
                {
                    '.' or ' ' => null,
                    >= '0' and <= '4' and var clue => clue - '0',
                    _ => throw new InvalidDataException("Unexpected GrandGames clue."),
                };
        }
        return clues;
    }

    private async Task<IDocument> ReadHtmlAsync(Uri url, TimeSpan delay, CancellationToken token)
    {
        await Task.Delay(delay, time, token);
        using var response = await http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        return new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(token));
    }

    private void MarkChecked(string resource, string? version = null) =>
        store.SaveSlitherlinkCollectionRefreshState(resource, time.GetUtcNow(), version);

    [GeneratedRegex(@"\A(?:/Raetsel/Slitherlink/)?\d+\.a\.htm\z")]
    private static partial Regex JankoPuzzleLink();

    [GeneratedRegex(@"(?m)^\s*\[([a-z]+)\][ \t]*\r?$")]
    private static partial Regex JankoSection();

    [GeneratedRegex(@"\bSiteVersion\s*=\s*(\d+)\s*;")]
    private static partial Regex GrandGamesVersion();

    [GeneratedRegex(@"\A/chinawall/id(\d+)\z")]
    private static partial Regex GrandGamesLink();
}
