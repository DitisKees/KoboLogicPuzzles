using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using KoboLogicPuzzles.Server.GrandGames;

namespace KoboLogicPuzzles.Server.LinkAPix;

internal sealed record LinkImportEntry(int Width, int Height, LinkClue[] Clues, LinkAttribution Attribution,
    int[][]? Paths = null, string? Picture = null);
internal sealed record LinkRefreshState(string Cursor, long CheckedAt, int Rejected, int Inconclusive);
internal enum LinkImportResult { Added, Duplicate, Invalid, Ambiguous, Inconclusive }

internal sealed partial class LinkAPixCollections(PuzzleStore store, IConfiguration config, IHttpClientFactory clients,
    TimeProvider time, ILogger<LinkAPixCollections> logger)
{
    private static readonly Uri Janko = new("https://www.janko.at/Raetsel/Ketten/");
    private static readonly Uri JankoLicense = new(Janko, "../Creative-Commons.htm");
    private static readonly TimeSpan ProofBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);
    internal bool AllowNonCommercial => config.GetValue("LinkAPixCollections:AllowNonCommercial", false);
    internal bool JankoEnabled => AllowNonCommercial && config.GetValue("LinkAPixCollections:JankoEnabled", false);

    internal object Status()
    {
        return RefreshSources.Append(GrandGamesCollections.Source).ToDictionary(source => source, source =>
        {
            var state = store.GetLinkRefresh(source);
            return new
            {
                Enabled = source == GrandGamesCollections.Source ? config.GetValue("GrandGamesCollections:Enabled", false) : Enabled(source),
                state.Cursor,
                state.Rejected,
                state.Inconclusive,
                CheckedAt = state.CheckedAt == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(state.CheckedAt),
            };
        });
    }

    internal bool HasPendingRefresh(string source) => store.GetLinkRefresh(source).Cursor.Length > 0;

    internal LinkImportResult Import(LinkImportEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var source = entry.Attribution;
            if (source is null || !LinkSources.Valid(source.Source) || string.IsNullOrWhiteSpace(source.Author) ||
                string.IsNullOrWhiteSpace(source.Title) || string.IsNullOrWhiteSpace(source.License) ||
                string.IsNullOrWhiteSpace(source.SourceId) || !WebUrl(source.SourceUrl) ||
                source.LicenseUrl is not null && !WebUrl(source.LicenseUrl) || entry.Clues is null)
                return LinkImportResult.Invalid;
            // The first release accepts monochrome boards. Never reinterpret a color clue as black.
            if (entry.Clues.Any(c => c?.Color is { } color &&
                !color.Equals("black", StringComparison.OrdinalIgnoreCase) && color != "#000000"))
                return LinkImportResult.Invalid;
            var board = new LinkAPixBoard(entry.Width, entry.Height, entry.Clues);
            var suppliedPicture = entry.Paths is null ? null : board.Picture(entry.Paths);
            if (entry.Paths is not null && suppliedPicture is null ||
                entry.Picture is not null && (entry.Picture.Length != board.Area || entry.Picture.Any(c => c is not ('#' or '.')) ||
                    suppliedPicture is not null && entry.Picture != suppliedPicture))
                return LinkImportResult.Invalid;

            var hash = board.ContentHash();
            var id = PuzzleId.FromLinkAPix(hash).Value;
            if (store.FindLinkAPix(id) is { } existing)
            {
                if (entry.Paths is not null && !SamePaths(LinkAPixBoard.CanonicalPaths(entry.Paths), existing.Solution!) ||
                    entry.Picture is not null && entry.Picture != existing.Picture)
                    return LinkImportResult.Invalid;
                if (!existing.Metadata.Attributions.Any(a => a.Source == source.Source && a.SourceId == source.SourceId))
                    store.SaveLinkAPix(existing with { Metadata = existing.Metadata with { Attributions = [source] } });
                return LinkImportResult.Duplicate;
            }

            var proof = new LinkAPixSolver(board, ProofBudget, token).CountSolutions();
            if (proof.Count > 1)
                return LinkImportResult.Ambiguous;
            if (proof.Count != 1 || proof.Paths is null || board.Picture(proof.Paths) is not { } picture ||
                entry.Paths is not null && !SamePaths(LinkAPixBoard.CanonicalPaths(entry.Paths), proof.Paths) ||
                entry.Picture is not null && entry.Picture != picture)
                return LinkImportResult.Invalid;

            token.ThrowIfCancellationRequested();
            var metadata = new LinkMetadata(hash, proof.Difficulty, proof.Solvability, [source]);
            return store.SaveLinkAPix(new(id, board.Width, board.Height, board.Clues, $"Link-a-Pix {id[^6..]}",
                source.Author, metadata, proof.Paths, picture)) ? LinkImportResult.Added : LinkImportResult.Duplicate;
        }
        catch (TimeoutException)
        {
            return LinkImportResult.Inconclusive;
        }
        catch (InvalidDataException)
        {
            return LinkImportResult.Invalid;
        }
    }

    private static bool SamePaths(int[][] first, int[][] second) =>
        first.Length == second.Length && first.Zip(second, (a, b) => a.SequenceEqual(b)).All(equal => equal);

    private static bool WebUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && uri.UserInfo == "";

    internal async Task<int> RefreshJanko(bool force, CancellationToken token)
    {
        using var lease = store.TryLockLinkRefresh("janko");
        if (lease is null)
            return 0;
        var state = store.GetLinkRefresh("janko");
        if (!force && state.Cursor == "" && time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(state.CheckedAt) < RefreshInterval)
            return 0;
        if (state.Cursor == "")
            state = state with { Rejected = 0, Inconclusive = 0 };

        using var http = clients.CreateClient("link-a-pix");
        var index = new HtmlParser().ParseDocument(await http.GetStringAsync(new Uri(Janko, "index.htm"), token));
        var links = index.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")!)
            .Where(path => JankoLink().IsMatch(path)).Distinct().OrderDescending(StringComparer.Ordinal).ToArray();
        if (links.Length == 0)
            throw new InvalidDataException("The Janko Ketten index contained no numbered pages.");

        var added = 0;
        foreach (var link in links.Where(path => state.Cursor.Length == 0 || string.CompareOrdinal(path, state.Cursor) < 0))
        {
            var url = new Uri(Janko, link);
            try
            {
                var html = await http.GetStringAsync(url, token);
                if (ParseJanko(html, url) is { } entry)
                {
                    var result = Import(entry, token);
                    switch (result)
                    {
                        case LinkImportResult.Added:
                            added++;
                            break;
                        case LinkImportResult.Inconclusive:
                            state = state with { Inconclusive = state.Inconclusive + 1 };
                            break;
                        case LinkImportResult.Ambiguous or LinkImportResult.Invalid:
                            state = state with { Rejected = state.Rejected + 1 };
                            break;
                    }
                    if (result is not (LinkImportResult.Added or LinkImportResult.Duplicate))
                        logger.LogWarning("Janko Link-a-Pix {Id}: {Result}", entry.Attribution.SourceId, result);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException ||
                exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                state = state with { Rejected = state.Rejected + 1 };
                logger.LogWarning("Skipped Janko Link-a-Pix {Link}: {Reason}", link, exception.Message);
            }
            token.ThrowIfCancellationRequested();
            state = state with { Cursor = link };
            store.SaveLinkRefresh("janko", state);
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, token);
        }
        store.SaveLinkRefresh("janko", state with { Cursor = "", CheckedAt = time.GetUtcNow().ToUnixTimeSeconds() });
        return added;
    }

    internal static LinkImportEntry? ParseJanko(string html, Uri url)
    {
        var document = new HtmlParser().ParseDocument(html);
        var author = document.QuerySelectorAll("footer td").FirstOrDefault(td => td.TextContent.Trim() == "Autor:")
            ?.NextElementSibling?.TextContent.Trim();
        var license = document.QuerySelectorAll("footer a[rel=license][href]").Any(a =>
            Uri.TryCreate(url, a.GetAttribute("href"), out var link) && link == JankoLicense);
        if (author != "Otto Janko" || !license)
            return null;
        var data = document.QuerySelector("script[type='application/x-janko']")?.TextContent ??
            throw new InvalidDataException("Missing structured Ketten definition.");
        return ParseKetten(data, new("janko", Path.GetFileName(url.AbsolutePath).Split('.')[0], url.AbsoluteUri, author,
            document.QuerySelector("h1")?.TextContent.Trim() ?? "Ketten", "CC BY-NC-SA 3.0", JankoLicense.AbsoluteUri));
    }

    internal static LinkImportEntry ParseKetten(string data, LinkAttribution source)
    {
        var sections = SectionHeading().Split(data);
        if (sections.Length < 5 || sections[1] != "begin" || !sections.Contains("end"))
            throw new InvalidDataException("Missing Ketten header or terminator.");
        string? Field(string key) => sections[2].Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(key + " ", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();
        if (Field("puzzle") != "ketten" || Field("author") != source.Author)
            throw new InvalidDataException("Expected monochrome Ketten data and matching author attribution.");
        var width = Number(Field("cols") ?? Field("size") ?? "");
        var height = Number(Field("rows") ?? Field("size") ?? "");
        if (width is < 1 or > LinkAPixBoard.MaxDimension || height is < 1 or > LinkAPixBoard.MaxDimension)
            throw new InvalidDataException("Unsupported Ketten dimensions.");
        var problem = Grid("problem") ?? throw new InvalidDataException("Missing Ketten clues.");
        var clues = problem.Select((value, i) => (value, i)).Where(item => item.value is not ("." or "-"))
            .Select(item => new LinkClue(item.i, Number(item.value))).ToArray();
        var board = new LinkAPixBoard(width, height, clues);
        int[][]? paths = null;
        if (Grid("solution") is { } solution)
        {
            var marks = new char[solution.Length];
            for (var i = 0; i < solution.Length; i++)
            {
                var cell = solution[i];
                var mask = 0;
                if (cell is not ("." or "-") && !(cell == "1" && board.ByCell[i]?.Length == 1))
                    foreach (var direction in cell)
                    {
                        var side = "nesw".IndexOf(direction);
                        if (side < 0 || (mask & (1 << side)) != 0)
                            throw new InvalidDataException("Unknown Ketten direction.");
                        mask |= 1 << side;
                    }
                marks[i] = LinkAPixBoard.DirectionMark(mask);
            }
            if (!board.TryReadMarks(new(marks), true, out paths))
                throw new InvalidDataException("The published Ketten answer violates the puzzle rules.");
        }
        return new(width, height, clues, source with
        {
            Title = Field("title") ?? source.Title,
            OriginalRating = Field("difficulty") ?? source.OriginalRating,
        }, paths);

        string[]? Grid(string name)
        {
            var index = Array.IndexOf(sections, name);
            if (index < 0)
                return null;
            var grid = sections[index + 1].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(row => row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
            if (grid.Length != height || grid.Any(row => row.Length != width))
                throw new InvalidDataException("Invalid Ketten grid dimensions.");
            return grid.SelectMany(row => row).ToArray();
        }
    }

    private static int Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException("Expected a Ketten number.");

    [GeneratedRegex(@"\A[0-9]{3}\.a\.htm\z")]
    private static partial Regex JankoLink();

    [GeneratedRegex(@"(?m)^\[([a-z]+)\][^\S\n]*$")]
    private static partial Regex SectionHeading();
}

internal sealed class LinkAPixRefreshWorker(LinkAPixCollections collections, TimeProvider time,
    ILogger<LinkAPixRefreshWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(LinkAPixCollections.RefreshSources.Where(collections.Enabled).Select(source => RunAsync(source, stoppingToken)));

    private async Task RunAsync(string source, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(1);
            try
            {
                var added = await collections.Refresh(source, false, stoppingToken);
                if (added > 0)
                    logger.LogInformation("Imported {Count} verified Link-a-Pix boards from {Source}.", added, source);
                if (collections.HasPendingRefresh(source))
                    delay = TimeSpan.FromMinutes(2);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Link-a-Pix {Source} refresh will retry in one hour.", source);
            }
            await Task.Delay(delay, time, stoppingToken);
        }
    }
}

internal static class LinkAPixCli
{
    internal static async Task<int> RunAsync(string command, WebApplication app, CancellationToken token)
    {
        var collections = app.Services.GetRequiredService<LinkAPixCollections>();
        if (command == "link-a-pix-status")
        {
            Console.WriteLine(JsonSerializer.Serialize(collections.Status(), JsonSerializerOptions.Web));
            return 0;
        }
        if (command == "link-a-pix-refresh")
        {
            if (!app.Configuration.GetValue("allow-non-commercial", collections.AllowNonCommercial))
                throw new InvalidDataException("Link-a-Pix online refresh requires --allow-non-commercial true.");
            var source = app.Configuration["source"] ?? "janko";
            if (!LinkAPixCollections.RefreshSources.Contains(source))
                throw new InvalidDataException("Use --source janko|playsudoku|graycell.");
            var added = await collections.Refresh(source, true, token);
            Console.WriteLine($"Imported {added} verified {LinkSources.Name(source)} Link-a-Pix boards." +
                (collections.HasPendingRefresh(source) ? " A later refresh will continue from the saved cursor." : ""));
            return 0;
        }

        var path = app.Configuration["file"] ?? throw new InvalidDataException("Supply --file with an authorized Link-a-Pix pack.");
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("The Link-a-Pix pack exceeds 32 MiB.");
            var results = new Dictionary<LinkImportResult, int>();
            await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable<LinkImportEntry>(file, JsonSerializerOptions.Web, token))
            {
                var result = entry is null ? LinkImportResult.Invalid : collections.Import(entry, token);
                results[result] = results.GetValueOrDefault(result) + 1;
            }
            Console.WriteLine(string.Join(", ", results.Select(row => $"{row.Key}: {row.Value}")));
            return results.Keys.All(key => key is LinkImportResult.Added or LinkImportResult.Duplicate) ? 0 : 1;
        }
        catch (Exception exception) when (exception is JsonException or IOException && exception is not InvalidDataException)
        {
            throw new InvalidDataException("The authorized Link-a-Pix pack could not be read.", exception);
        }
    }
}
