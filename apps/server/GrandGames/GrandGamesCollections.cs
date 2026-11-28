using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using KoboLogicPuzzles.Server.Galaxies;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.LinkAPix;
using KoboLogicPuzzles.Server.Nurikabe;
using KoboLogicPuzzles.Server.Placement;

namespace KoboLogicPuzzles.Server.GrandGames;

internal sealed record GrandGamesFeed(PuzzleGame Game, string Catalog, string Filter = "");
internal sealed record GrandGamesEntry(int Id, string Url, string Title, string Author, string? Rating);
internal sealed record GrandGamesCatalog(string Version, GrandGamesEntry[] Entries, bool HasMore);
internal sealed record GrandGamesResult(int Added, int Present, int Skipped, int Inconclusive, bool More);
internal sealed partial class GrandGamesCollections(PuzzleStore store, IHttpClientFactory clients, TimeProvider time,
    GrandGamesRequests requests, NurikabeImporter nurikabe, GalaxiesCollections galaxies, PlacementCollections placement, LinkAPixCollections linkAPix,
    ILogger<GrandGamesCollections> logger)
{
    private static readonly Uri Site = new("https://en.grandgames.net/");
    internal const string Source = "grandgames";
    private const string Name = "GrandGames";
    private const string Permission = "Personal noncommercial offline play";
    private const string Terms = "https://en.grandgames.net/termsofuse";
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private static readonly TimeSpan ProofBudget = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int BatchSize = 8;
    private const int PageStride = 1000; // Leaves space for the index within each catalog page.

    // Fill-a-Pix (Mosaic) and Slitherlink (Wall of China) already have dedicated GrandGames refreshes.
    internal static IReadOnlyList<GrandGamesFeed> Feeds { get; } =
    [
        new(PuzzleGame.Nonogram, "nonograms", "&iscolor=2"),
        new(PuzzleGame.Hashi, "bridges"),
        new(PuzzleGame.Nurikabe, "nurikabe"),
        new(PuzzleGame.Galaxies, "galaxies"),
        new(PuzzleGame.Tents, "tents"),
        new(PuzzleGame.Battleships, "ships"),
        new(PuzzleGame.LinkAPix, "filippinskie", "&color=0&csize=0"),
    ];

    internal async Task<GrandGamesResult> Refresh(GrandGamesFeed feed, bool force, CancellationToken token)
    {
        var resource = $"grandgames-{feed.Catalog}";
        var isLink = feed.Game == PuzzleGame.LinkAPix;
        using var refresh = isLink ? store.TryLockLinkRefresh(Source) : store.TryLockExtraCollection(resource);
        if (refresh is null)
            return new(0, 0, 0, 0, true);
        var (cursor, completedAt) = store.ReadExtraCollectionRefresh(resource);
        var linkState = isLink ? store.GetLinkRefresh(Source) : null;
        if (linkState is not null)
        {
            cursor = int.TryParse(linkState.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            completedAt = linkState.CheckedAt;
            if (cursor == 0)
                linkState = linkState with { Inconclusive = 0, Rejected = 0 };
        }
        if (!force && cursor == 0 && time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(completedAt) < Interval)
            return new(0, 0, 0, 0, false);

        var page = Math.Max(1, cursor / PageStride);
        var offset = cursor % PageStride;
        using var http = clients.CreateClient("grandgames");
        var url = new Uri(Site, FormattableString.Invariant($"{feed.Catalog}?status=all&page={page}{feed.Filter}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var listing = await requests.SendAsync(http, request, token);
        listing.EnsureSuccessStatusCode();
        var catalog = ReadCatalog(await listing.Content.ReadAsStringAsync(token), feed);
        int added = 0, present = 0, skipped = 0, inconclusive = 0;
        foreach (var entry in catalog.Entries.Skip(offset).Take(BatchSize))
        {
            try
            {
                using var start = new HttpRequestMessage(HttpMethod.Post, "https://api.grandgames.net/agmove/start")
                {
                    Content = JsonContent.Create(new { id = entry.Id }),
                };
                start.Headers.Add("Origin", Site.GetLeftPart(UriPartial.Authority));
                start.Headers.Add("x-client", "web-php");
                start.Headers.Add("x-lang", "en");
                start.Headers.Add("x-version", catalog.Version);

                using var response = await requests.SendAsync(http, start, token);
                response.EnsureSuccessStatusCode();
                var data = JsonSerializer.Deserialize<StartResponse>(await response.Content.ReadAsStringAsync(token), Json);
                if (data is not { Mode: "normal", Level: { } level } || level.Id != entry.Id || level.Game != feed.Catalog)
                    throw new InvalidDataException("Unexpected GrandGames guest puzzle response.");
                if (Import(feed.Game, level, entry, token))
                    added++;
                else
                    present++;
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or TimeoutException ||
                exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                if (exception is TimeoutException)
                    inconclusive++;
                else
                    skipped++;
                if (linkState is not null)
                    linkState = exception is TimeoutException ? linkState with { Inconclusive = linkState.Inconclusive + 1 } :
                        linkState with { Rejected = linkState.Rejected + 1 };
                logger.LogDebug("GrandGames {Game} puzzle {Id} skipped. {Reason}", feed.Game, entry.Id, exception.Message);
            }
            Save(checked(page * PageStride + ++offset));
        }
        var more = offset < catalog.Entries.Length || catalog.HasMore;
        if (offset >= catalog.Entries.Length)
            Save(catalog.HasMore ? checked((page + 1) * PageStride) : 0);
        return new(added, present, skipped, inconclusive, more);

        void Save(int next)
        {
            token.ThrowIfCancellationRequested();
            var checkedAt = next == 0 ? time.GetUtcNow().ToUnixTimeSeconds() : completedAt;
            if (linkState is not null)
                store.SaveLinkRefresh(Source, linkState with
                {
                    Cursor = next == 0 ? "" : next.ToString(CultureInfo.InvariantCulture), CheckedAt = checkedAt,
                });
            else
                store.SaveExtraCollectionRefresh(resource, next, checkedAt);
        }
    }

    internal static GrandGamesCatalog ReadCatalog(string html, GrandGamesFeed feed)
    {
        var document = new HtmlParser().ParseDocument(html);
        var version = document.Scripts.Select(script => SiteVersion().Match(script.TextContent))
            .FirstOrDefault(match => match.Success)?.Groups[1].Value ??
            throw new InvalidDataException("GrandGames did not publish its guest player version.");
        var prefix = "/" + feed.Catalog + "/id";
        var entries = new Dictionary<int, GrandGamesEntry>();
        foreach (var card in document.QuerySelectorAll("#cards-list .gg-card[data-ag-item]"))
        {
            var link = card.QuerySelector(".gg-card__title a[href]");
            if (!int.TryParse(card.GetAttribute("data-ag-item"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id <= 0 || link is null || !Uri.TryCreate(Site, link.GetAttribute("href"), out var url) ||
                url.Scheme != Site.Scheme || url.Authority != Site.Authority || url.UserInfo.Length != 0)
                continue;
            var expected = prefix + id.ToString(CultureInfo.InvariantCulture);
            if (url.AbsolutePath != expected && !url.AbsolutePath.StartsWith(expected + "_", StringComparison.Ordinal))
                continue;

            var author = card.QuerySelector(".gg-card__meta [title='Author']")?.TextContent.Trim();
            var rating = card.QuerySelector(".gg-card__stats [title='Rating'] b")?.TextContent.Trim();
            entries.TryAdd(id, new(id, url.AbsoluteUri, link.TextContent.Trim(), string.IsNullOrEmpty(author) ? Name : author, rating));
        }
        if (entries.Count is 0 or >= PageStride)
            throw new InvalidDataException("GrandGames returned an unrecognized catalog page.");
        return new(version, entries.Values.ToArray(), document.QuerySelector(".ag-catalog .pagination a[rel='next']") is not null);
    }

    internal bool Import(PuzzleGame game, GrandGamesLevel level, GrandGamesEntry entry, CancellationToken token)
    {
        var id = entry.Id.ToString(CultureInfo.InvariantCulture);
        var title = string.IsNullOrWhiteSpace(entry.Title) ? $"{game} {level.Sx}×{level.Sy}" : entry.Title;
        var attribution = new PublishedPuzzle(Source, Name, id, entry.Url, Permission, title, entry.Author);
        switch (game)
        {
            case PuzzleGame.Nonogram:
                var bitmap = GrandGamesFormats.Nonogram(level);
                return store.SaveCollectionNonogram(CollectionNonogram.Create(level.Sx, level.Sy, bitmap, attribution, token));
            case PuzzleGame.Hashi:
                var hashi = GrandGamesFormats.Hashi(level);
                if (store.FindHashiPuzzle(hashi.ContentHash) is not null)
                    return false;
                var (validation, verified) = VerifiedHashiPuzzle.VerifyCollection(hashi, attribution, ProofBudget, time.GetUtcNow(), token);
                if (verified is null)
                    throw new InvalidDataException($"Bridges uniqueness verification returned {validation.Status}.");
                return store.SaveHashiPuzzle(verified);
            case PuzzleGame.Nurikabe:
                var (clues, sea) = GrandGamesFormats.Nurikabe(level);
                return nurikabe.Store(new(Source, Name, id, entry.Url, Permission, entry.Author, title, level.Sx, level.Sy, clues,
                    sea, OriginalRating: entry.Rating), token, ProofBudget);
            case PuzzleGame.Galaxies:
                var (galaxyBoard, walls) = GrandGamesFormats.Galaxies(level);
                return galaxies.VerifyAndStore(galaxyBoard, new(Source, Name, entry.Author, entry.Url, Permission, Terms, Picture: false),
                    id, title, "grandgames-v1", null, walls, token);
            case PuzzleGame.Tents:
            case PuzzleGame.Battleships:
                var placementBoard = GrandGamesFormats.Placement(level, game);
                return placement.VerifyAndStore(new(placementBoard, entry.Author, title, id, entry.Url, entry.Rating,
                    null, Source, Name, Permission, Terms), token);
            case PuzzleGame.LinkAPix:
                var link = GrandGamesFormats.LinkAPix(level, new(Source, id, entry.Url, entry.Author, title, Permission, Terms, entry.Rating));
                return linkAPix.Import(link, token) switch
                {
                    LinkImportResult.Added => true,
                    LinkImportResult.Duplicate => false,
                    LinkImportResult.Inconclusive => throw new TimeoutException("The Filipino crossword reached the proof limit."),
                    var result => throw new InvalidDataException($"Filipino crossword verification returned {result}."),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(game));
        }
    }

    private sealed record StartResponse(string? Mode, GrandGamesLevel? Level);

    [GeneratedRegex(@"\bSiteVersion\s*=\s*([0-9]+)\s*;")]
    private static partial Regex SiteVersion();
}

internal sealed class GrandGamesWorker(GrandGamesCollections collections, IConfiguration config, TimeProvider time,
    ILogger<GrandGamesWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("GrandGamesCollections:Enabled", false))
            return;

        var next = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var more = false;
            var delay = TimeSpan.Zero;
            for (var i = 0; i < GrandGamesCollections.Feeds.Count; i++)
            {
                var feed = GrandGamesCollections.Feeds[next];
                next = (next + 1) % GrandGamesCollections.Feeds.Count;
                try
                {
                    var result = await collections.Refresh(feed, false, stoppingToken);
                    more |= result.More;
                    if (result.Added + result.Present + result.Skipped + result.Inconclusive > 0)
                        logger.LogInformation("GrandGames {Game} added {Added}, already had {Present}, skipped {Skipped}, and reached the proof limit on {Inconclusive}.",
                            feed.Game, result.Added, result.Present, result.Skipped, result.Inconclusive);
                }
                catch (GrandGamesRateLimited exception)
                {
                    delay = exception.CheckAgainIn;
                    logger.LogInformation("GrandGames paused requests for {Delay}.", exception.Delay);
                    break;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "GrandGames {Game} refresh will retry.", feed.Game);
                }
            }
            if (delay == TimeSpan.Zero)
                delay = more ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(1);
            await Task.Delay(delay, time, stoppingToken);
        }
    }
}
