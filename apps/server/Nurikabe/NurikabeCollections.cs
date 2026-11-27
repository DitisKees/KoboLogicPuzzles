using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace KoboLogicPuzzles.Server.Nurikabe;

public sealed class NurikabeCollectionOptions
{
    public bool RefreshEnabled { get; init; }
}

internal sealed partial class NurikabeCollections(PuzzleStore store, NurikabeImporter importer, HttpClient http,
    TimeProvider time, ILogger<NurikabeCollections> logger)
{
    private static readonly Uri Site = new("https://www.janko.at/Raetsel/Nurikabe/");
    // Verified list from Janko's Palmer Mebane author page. Check each puzzle's permission again when downloading.
    private static readonly int[] PalmerMebaneIds = [690, 689, 756, 755, 687, 686, 685, 684, .. Enumerable.Range(661, 10)];
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    public async Task<int> RefreshAsync(bool force, CancellationToken token)
    {
        using var refresh = store.TryLockNurikabe("janko");
        if (refresh is null)
            return 0;
        var (cursor, checkedAt) = store.GetNurikabeRefresh();
        if (!force && cursor == 0 && time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(checkedAt) < Interval)
            return 0;

        var imported = 0;
        for (var index = cursor; index < PalmerMebaneIds.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var id = PalmerMebaneIds[index];
            var url = new Uri(Site, id.ToString("D4", CultureInfo.InvariantCulture) + ".a.htm");
            try
            {
                var html = await http.GetStringAsync(url, token);
                if (ParseJanko(html, url) is { } entry && importer.Store(entry, token))
                    imported++;
            }
            catch (Exception exception) when (exception is InvalidDataException or TimeoutException ||
                exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                logger.LogWarning("Skipped Janko Nurikabe {PuzzleId}: {Reason}", id, exception.Message);
            }

            store.SaveNurikabeRefresh(index + 1, checkedAt);
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, token);
        }
        store.SaveNurikabeRefresh(0, time.GetUtcNow().ToUnixTimeSeconds());
        return imported;
    }

    internal static NurikabeImportEntry? ParseJanko(string html, Uri url)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var footer = doc.QuerySelectorAll("footer td");
        var author = Field("Autor:")?.TextContent.Trim();
        var license = Field("Lizenz:");
        if (string.IsNullOrWhiteSpace(author) || license is null)
            return null;

        var terms = string.Join(' ', license.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var explicitPermission = author == "Palmer Mebane" &&
            terms.Trim('"', '.') == "You may redistribute these puzzles as long as credit for their construction is provided to me";
        if (!explicitPermission)
            return null;

        var script = doc.QuerySelector("script[type='application/x-janko']")?.TextContent ??
            throw new InvalidDataException("The structured Janko puzzle is missing.");
        var sections = SectionHeader().Split(script);
        if (sections.Length < 5 || sections[1] != "begin" || Array.IndexOf(sections, "problem") is not (>= 3 and var problem))
            throw new InvalidDataException("The Janko clue grid is missing.");

        var fields = NurikabeImporter.Lines(sections[2]);
        string? Value(string key) => fields.FirstOrDefault(line => line.StartsWith(key + " ", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();
        if (Value("puzzle") != "nurikabe" ||
            !int.TryParse(Value("cols") ?? Value("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(Value("rows") ?? Value("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            throw new InvalidDataException("Invalid dimensions or game in the Janko puzzle.");

        var clues = NurikabeImporter.ReadGrid(width, height, NurikabeImporter.Lines(sections[problem + 1]), NurikabeImporter.ParseClue);
        var answerIndex = Array.IndexOf(sections, "solution");
        var solution = answerIndex < 0 ? null : NurikabeImporter.DecodeAnswer(width, height, NurikabeImporter.Lines(sections[answerIndex + 1]));
        var sourceId = Path.GetFileName(url.AbsolutePath).Split('.')[0];
        return new("janko", "Janko", sourceId, url.AbsoluteUri, terms.Trim('"'), author,
            doc.QuerySelector("h1")?.TextContent.Trim(), width, height, clues, solution, OriginalRating: Value("difficulty"));

        IElement? Field(string label) => footer.FirstOrDefault(cell => cell.TextContent.Trim() == label)?.NextElementSibling;
    }

    [GeneratedRegex(@"(?m)^\[([a-z]+)\][^\S\n]*$")]
    private static partial Regex SectionHeader();
}

internal sealed class NurikabeCollectionsWorker(NurikabeCollections collections, NurikabeCollectionOptions options,
    TimeProvider time, ILogger<NurikabeCollectionsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.RefreshEnabled)
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var added = await collections.RefreshAsync(false, stoppingToken);
                if (added > 0)
                    logger.LogInformation("Imported {Count} verified Nurikabe puzzles from Janko.", added);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Nurikabe collection refresh will retry in one hour.");
            }
            await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken);
        }
    }
}
