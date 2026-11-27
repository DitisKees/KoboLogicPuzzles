using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace KoboLogicPuzzles.Server.Galaxies;

internal sealed record GalaxiesEntry(string Id, string Title, int Width, int Height, JsonElement[][] Centers,
    string Version, string? Url = null, string? Solution = null);
internal sealed record GalaxiesAttribution(string Source, string Name, string Author, string Url, string License, string LicenseUrl,
    bool Picture = true, string Kind = "collection");

internal sealed partial class GalaxiesCollections(PuzzleStore store, IHttpClientFactory clients, TimeProvider time,
    IConfiguration configuration, ILogger<GalaxiesCollections> logger) : BackgroundService
{
    private static readonly Uri Janko = new("https://www.janko.at/Raetsel/Galaxien/");
    private static readonly Uri JankoLicense = new(Janko, "../Creative-Commons.htm");
    private static readonly GalaxiesAttribution Font = new("spiral-galaxies-font", "Spiral Galaxies Font",
        "Walker Anderson, Erik Demaine and Martin Demaine", "https://erikdemaine.org/fonts/spiralgalaxies/", "MIT",
        "https://github.com/edemaine/font-spiralgalaxies/blob/27dec3ab6d964e7d9ec66f4a5cb6dbbe7eba79c6/LICENSE");
    private static readonly GalaxiesAttribution ArtistsBlock = new("artists-block", "Artist’s Block", "Grant Fikes",
        "https://mathgrant.blogspot.com/search/label/Artist%27s%20Block", "CC BY-NC 3.0 US", "https://creativecommons.org/licenses/by-nc/3.0/us/");
    private static readonly GalaxiesAttribution Originals = new("galaxies-pictures", "Kobo Logic Puzzles Pictures",
        "Kobo Logic Puzzles", "https://github.com/DitisKees/KoboLogicPuzzles", "MIT", "https://github.com/DitisKees/KoboLogicPuzzles/blob/main/LICENSE",
        Kind: "generated");
    private static readonly GalaxiesAttribution Tatham = new("tatham-galaxies", "Simon Tatham’s Galaxies",
        "Kobo Logic Puzzles", "https://www.chiark.greenend.org.uk/~sgtatham/puzzles/doc/galaxies.html", "MIT",
        "https://www.chiark.greenend.org.uk/~sgtatham/puzzles/doc/licence.html", Kind: "generated");
    private static readonly GalaxiesAttribution Wikimedia = new("wikimedia-tentaisho", "Wikimedia Commons",
        "LightbulbMEOW", "https://commons.wikimedia.org/wiki/File:10x10_Tentaisho_unsolved.png", "CC BY-SA 4.0",
        "https://creativecommons.org/licenses/by-sa/4.0/");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("GalaxiesCollections:RefreshEnabled", configuration.GetValue("CatalogRefresh:Enabled", true)))
            return;
        var nonCommercial = configuration.GetValue("GalaxiesCollections:AllowNonCommercial",
            configuration.GetValue("ExtraCollections:AllowNonCommercial", false));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSource(Originals.Source, () => ImportBundled(Originals, stoppingToken), stoppingToken);
            await RunSource(Tatham.Source, () => ImportBundled(Tatham, stoppingToken), stoppingToken);
            await RunSource(Font.Source, () => ImportBundled(Font, stoppingToken), stoppingToken);
            await RunSource(Wikimedia.Source, () => ImportBundled(Wikimedia, stoppingToken), stoppingToken);
            if (nonCommercial)
            {
                await RunSource(ArtistsBlock.Source, () => ImportBundled(ArtistsBlock, stoppingToken), stoppingToken);
                await RunSource("janko", () => RefreshJankoAsync(stoppingToken), stoppingToken);
            }

            await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken);
        }
    }

    private async Task RunSource(string source, Func<Task<int>> refresh, CancellationToken token)
    {
        try
        {
            var count = await refresh();
            if (count > 0)
                logger.LogInformation("Imported {Count} verified Galaxies puzzles from {Source}.", count, source);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Galaxies source {Source} will retry in one hour.", source);
        }
    }

    private async Task<int> ImportBundled(GalaxiesAttribution source, CancellationToken token)
    {
        await using var data = typeof(GalaxiesCollections).Assembly.GetManifestResourceStream(source.Source + ".json")
            ?? throw new InvalidOperationException($"Missing embedded Galaxies collection {source.Source}.");
        var version = Convert.ToHexStringLower(await SHA256.HashDataAsync(data, token));
        if (store.GetGalaxiesRefresh(source.Source).Cursor == version)
            return 0;
        data.Position = 0;
        var entries = await JsonSerializer.DeserializeAsync<GalaxiesEntry[]>(data, JsonSerializerOptions.Web, token)
            ?? throw new InvalidDataException("Missing Galaxies collection entries.");
        var added = 0;
        foreach (var entry in entries.OrderByDescending(entry => entry.Width * entry.Height))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var centers = entry.Centers.Select(ParseCenter);
                if (VerifyAndStore(new(entry.Width, entry.Height, centers), source, entry.Id, entry.Title,
                    entry.Version, entry.Url, entry.Solution, token))
                    added++;
            }
            catch (Exception exception) when (exception is InvalidDataException or TimeoutException)
            {
                logger.LogWarning("Rejected {Source} Galaxies {Id}: {Reason}", source.Source, entry.Id, exception.Message);
            }
        }
        store.SaveGalaxiesRefresh(source.Source, version, time.GetUtcNow().ToUnixTimeSeconds());
        return added;
    }

    private static GalaxyCenter ParseCenter(JsonElement[] point)
    {
        if (point is [var x, var y, var black] && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var col) &&
            y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var row) && black.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return new(col, row, black.GetBoolean());
        throw new InvalidDataException("Invalid Galaxies center.");
    }

    internal bool VerifyAndStore(GalaxiesBoard board, GalaxiesAttribution source, string sourceId, string title,
        string version, string? url, string? suppliedSolution, CancellationToken token)
    {
        if (source.Picture && (!board.Centers.Any(center => center.Black) || !board.Centers.Any(center => !center.Black)))
            throw new InvalidDataException("A picture board requires both black and white centers.");
        if (suppliedSolution is not null && !board.IsSolved(suppliedSolution))
            throw new InvalidDataException("The supplied partition violates the Galaxies rules.");
        var hash = board.ContentHash();
        var id = PuzzleId.FromGalaxies(hash).Value;
        if (store.FindGalaxies(id) is { } existing)
        {
            if (suppliedSolution is not null && suppliedSolution.Replace('.', 'x') != existing.Solution)
                throw new InvalidDataException("The supplied partition disagrees with the stored proof.");
            return false;
        }
        var proof = new GalaxiesSolver(board, TimeSpan.FromSeconds(10), token).CountSolutions();
        if (proof.Count != 1)
            throw new InvalidDataException(proof.Count == 0 ? "No solution." : "Multiple solutions.");
        if (suppliedSolution is not null && suppliedSolution.Replace('.', 'x') != proof.Solution)
            throw new InvalidDataException("The supplied partition disagrees with the uniqueness proof.");

        token.ThrowIfCancellationRequested();
        var metadata = new GalaxiesMetadata(source.Source, source.Name, source.Kind, sourceId, url ?? source.Url,
            source.License, source.LicenseUrl, version, hash, proof.Difficulty, proof.Solvability, source.Picture);
        return store.SaveGalaxies(new(id, board.Width, board.Height, board.Centers, title, source.Author, metadata, proof.Solution));
    }

    internal async Task<int> RefreshJankoAsync(CancellationToken token)
    {
        using var refresh = store.TryLockGalaxiesRefresh();
        if (refresh is null)
            return 0;
        var (cursor, checkedAt) = store.GetGalaxiesRefresh("janko");
        if (cursor == "" && time.GetUtcNow().ToUnixTimeSeconds() - checkedAt < 7 * 24 * 60 * 60)
            return 0;
        using var http = clients.CreateClient("galaxies");
        var index = new HtmlParser().ParseDocument(await http.GetStringAsync(new Uri(Janko, "index.htm"), token));
        var links = index.QuerySelectorAll("a[href]").Select(link => link.GetAttribute("href")!)
            .Where(href => JankoPuzzleLink().IsMatch(href)).Distinct().OrderBy(href => href, StringComparer.Ordinal).ToArray();
        if (links.Length == 0)
            throw new InvalidDataException("The Janko Galaxies catalog contains no numbered pages.");
        var added = 0;
        foreach (var link in links.Where(link => string.CompareOrdinal(link, cursor) > 0))
        {
            token.ThrowIfCancellationRequested();
            var url = new Uri(Janko, link);
            try
            {
                var html = await http.GetStringAsync(url, token);
                if (ParseJanko(html, url) is { } entry &&
                    VerifyAndStore(entry.Board, entry.Source, link.Split('.')[0], entry.Title,
                        entry.Version, url.AbsoluteUri, entry.Solution, token))
                    added++;
            }
            catch (Exception exception) when (exception is InvalidDataException or TimeoutException ||
                exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                logger.LogWarning("Skipped Janko Galaxies {Id}: {Reason}", link, exception.Message);
            }
            store.SaveGalaxiesRefresh("janko", link);
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, token);
        }
        store.SaveGalaxiesRefresh("janko", "", time.GetUtcNow().ToUnixTimeSeconds());
        return added;
    }

    internal static (GalaxiesBoard Board, GalaxiesAttribution Source, string Title, string Version, string? Solution)?
        ParseJanko(string html, Uri url)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var author = doc.QuerySelectorAll("footer td").FirstOrDefault(cell => cell.TextContent.Trim() == "Autor:")
            ?.NextElementSibling?.TextContent.Trim();
        var licensed = doc.QuerySelectorAll("footer a[rel=license][href]").Any(link =>
            Uri.TryCreate(url, link.GetAttribute("href"), out var license) && license == JankoLicense);
        if (author != "Otto Janko" || !licensed)
            return null;

        var data = doc.QuerySelector("script[type='application/x-janko']")?.TextContent
            ?? throw new InvalidDataException("Missing structured Galaxies clues.");
        var sections = JankoSection().Split(data);
        if (sections.Length < 5 || sections[1] != "begin" || Array.IndexOf(sections, "problem") is not (>= 3 and var problem))
            throw new InvalidDataException("Missing Janko Galaxies grid.");
        var header = Lines(sections[2]);
        string? Field(string key) => header.FirstOrDefault(line => line.StartsWith(key + " ", StringComparison.Ordinal))
            ?[(key.Length + 1)..].Trim();
        if (Field("puzzle") != "galaxies" ||
            !int.TryParse(Field("cols") ?? Field("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(Field("rows") ?? Field("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            throw new InvalidDataException("Invalid Janko Galaxies dimensions.");
        var clues = ReadGrid(sections[problem + 1], width, height);
        var centers = new List<GalaxyCenter>();
        for (var i = 0; i < clues.Length; i++)
        {
            if (clues[i] == "-")
                continue;
            if (clues[i] is not ("0" or "1" or "2" or "3"))
                throw new InvalidDataException("Unknown Janko center encoding.");
            var offset = clues[i][0] - '0';
            centers.Add(new(2 * (i % width) + 1 + (offset & 1), 2 * (i / width) + 1 + (offset >> 1), false));
        }
        var board = new GalaxiesBoard(width, height, centers);
        string? solution = null;
        if (Array.IndexOf(sections, "solution") is >= 3 and var answer)
        {
            var labels = ReadGrid(sections[answer + 1], width, height);
            if (labels.Any(label => !int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var region) || region < 1))
                throw new InvalidDataException("Invalid Janko region labels.");
            solution = new(board.Edges.Select(edge => labels[edge.A] == labels[edge.B] ? 'x' : '#').ToArray());
        }
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        return (board, new("janko", "Janko", author, url.AbsoluteUri, "CC BY-NC-SA 3.0", JankoLicense.AbsoluteUri, Picture: false),
            doc.QuerySelector("h1")?.TextContent.Trim() ?? "Galaxien", version, solution);
    }

    private static string[] ReadGrid(string body, int width, int height)
    {
        var rows = Lines(body).Select(row => row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (width is < 1 or > GalaxiesBoard.MaxDimension || height is < 1 or > GalaxiesBoard.MaxDimension ||
            rows.Length != height || rows.Any(row => row.Length != width))
            throw new InvalidDataException("Invalid Janko grid size.");
        return rows.SelectMany(row => row).ToArray();
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^\d{3,6}\.a\.htm$")]
    private static partial Regex JankoPuzzleLink();
    [GeneratedRegex(@"(?m)^\[([a-z]+)\][^\S\n]*$")]
    private static partial Regex JankoSection();
}
