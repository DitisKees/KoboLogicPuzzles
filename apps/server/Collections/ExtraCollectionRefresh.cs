using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using KoboLogicPuzzles.Server.Hashi;

namespace KoboLogicPuzzles.Server;

public sealed class ExtraCollectionOptions
{
    public bool BundledEnabled { get; init; } = true;
    public bool JankoEnabled { get; init; }
    public bool AllowNonCommercial { get; init; }
}

internal sealed partial class ExtraCollectionRefresh(PuzzleStore store, HttpClient http, TimeProvider time,
    ILogger<ExtraCollectionRefresh> logger)
{
    private static readonly Uri JankoRoot = new("https://www.janko.at/Raetsel/");
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(3);
    private static readonly BundledPack[] Packs =
    [
        new("world-at-play", "The World at Play", PuzzleGame.Nonogram, "world-at-play-cc0",
            "https://theworldatplay.com/en/nonogram", "https://creativecommons.org/publicdomain/zero/1.0/", "collection"),
        Sen("nonogram", "4c26ce7ae5f4471818f2b13bb9281dbf77e47330", PuzzleGame.Nonogram, "collection"),
        Sen("hashiwokakero", "e9166695b59cd0972dc973b56b0571713e7f6bb3", PuzzleGame.Hashi, "generated"),
    ];

    private static BundledPack Sen(string repo, string revision, PuzzleGame game, string kind)
    {
        var release = $"https://github.com/sen-ltd/{repo}/blob/{revision}/";
        return new("sen-ltd", "Sen Ltd", game, $"sen-ltd-{repo}", release + "src/puzzles.ts", release + "LICENSE", kind);
    }

    internal async Task<(int Nonograms, int Hashi)> ImportBundledAsync(string? source, PuzzleGame? game, CancellationToken token)
    {
        int nonograms = 0, hashi = 0;
        foreach (var pack in Packs.Where(pack => (source is null || pack.Source == source) && (game is null || pack.Game == game)))
        {
            var added = await ImportPackAsync(pack, token);
            if (pack.Game == PuzzleGame.Nonogram) nonograms += added;
            else hashi += added;
        }
        return (nonograms, hashi);
    }

    private async Task<int> ImportPackAsync(BundledPack pack, CancellationToken token)
    {
        var resource = $"{pack.Name}-v1";
        using var refresh = store.TryLockExtraCollection(resource);
        if (refresh is null || store.ReadExtraCollectionRefresh(resource).CompletedAt != 0)
            return 0;

        await using var stream = typeof(ExtraCollectionRefresh).Assembly.GetManifestResourceStream(pack.Name + ".json")
            ?? throw new InvalidOperationException($"Missing bundled puzzle pack {pack.Name}.");
        var boards = await JsonSerializer.DeserializeAsync<BundledPuzzle[]>(stream, JsonSerializerOptions.Web, token)
            ?? throw new InvalidDataException($"Missing entries in puzzle pack {pack.Name}.");
        var added = 0;
        foreach (var board in boards)
        {
            token.ThrowIfCancellationRequested();
            var attribution = new PublishedPuzzle(pack.Source, pack.SourceName, board.Id, pack.Url, pack.License,
                board.Title, pack.SourceName, pack.Kind);
            var width = board.Rows[0].Length;
            var height = board.Rows.Length;
            if (board.Rows.Any(row => row.Length != width))
                throw new InvalidDataException($"Puzzle {board.Id} has uneven rows.");
            if (pack.Game == PuzzleGame.Nonogram)
            {
                var definition = CollectionNonogram.Create(width, height, string.Concat(board.Rows), attribution, token);
                if (store.SaveCollectionNonogram(definition)) added++;
            }
            else if (Save(pack.Game, new(width, height, ToGrid(board.Rows), board.Answer is null ? null : ToGrid(board.Answer),
                attribution), token))
                added++;
        }
        store.SaveExtraCollectionRefresh(resource, 0, time.GetUtcNow().ToUnixTimeSeconds());
        return added;

        static string[] ToGrid(string[] rows) => string.Concat(rows).Select(cell => cell.ToString()).ToArray();
    }

    internal async Task<(int Added, bool More)> RefreshJankoAsync(PuzzleGame game, bool allowNonCommercial, bool force,
        CancellationToken token)
    {
        var (resource, directory, index) = game switch
        {
            PuzzleGame.Hashi => ("janko-hashi", "Hashi/", "index.htm"),
            PuzzleGame.Nonogram => ("janko-nonogram", "Nonogramme/", "index-1.htm"),
            _ => throw new ArgumentException("Choose nonogram or hashi.", nameof(game)),
        };
        if (allowNonCommercial)
            resource += "-noncommercial";
        using var refresh = store.TryLockExtraCollection(resource);
        if (refresh is null)
            return (0, false);
        var (cursor, completedAt) = store.ReadExtraCollectionRefresh(resource);
        if (!force && cursor == 0 && time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(completedAt) < Interval)
            return (0, false);

        var baseUrl = new Uri(JankoRoot, directory);
        var indexHtml = await http.GetStringAsync(new Uri(baseUrl, index), token);
        var links = new HtmlParser().ParseDocument(indexHtml).QuerySelectorAll("a[href]")
            .Select(link => link.GetAttribute("href")!).Where(link => JankoPuzzleFile().IsMatch(link))
            .Select(link => (Number: int.Parse(link.AsSpan(0, link.IndexOf('.')), CultureInfo.InvariantCulture), Link: link))
            .DistinctBy(link => link.Number).OrderBy(link => link.Number).ToArray();
        if (links.Length == 0)
            throw new InvalidDataException("No puzzle pages found in the Janko index.");

        var batch = links.Where(link => link.Number > cursor).Take(41).ToArray();
        var added = 0;
        foreach (var (number, link) in batch.Take(40))
        {
            token.ThrowIfCancellationRequested();
            var url = new Uri(baseUrl, link);
            try
            {
                var html = await http.GetStringAsync(url, token);
                if (ParseJanko(html, url, game, allowNonCommercial) is { } board && Save(game, board, token))
                    added++;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException ||
                exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                logger.LogDebug("Skipped Janko {Game} {PuzzleId}: {Reason}", game, number, exception.Message);
            }
            store.SaveExtraCollectionRefresh(resource, number, completedAt);
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, token);
        }

        var more = batch.Length > 40;
        if (!more)
            store.SaveExtraCollectionRefresh(resource, 0, time.GetUtcNow().ToUnixTimeSeconds());
        return (added, more);
    }

    private bool Save(PuzzleGame game, CollectionBoard board, CancellationToken token)
    {
        if (game == PuzzleGame.Nonogram)
        {
            var solution = string.Concat(board.Grid.Select(cell => cell switch
            {
                "x" or "X" => '#',
                "-" or "." => '.',
                _ => throw new InvalidDataException("Expected a black-and-white Nonogram."),
            }));
            var definition = CollectionNonogram.Create(board.Width, board.Height, solution, board.Attribution, token);
            token.ThrowIfCancellationRequested();
            return store.SaveCollectionNonogram(definition);
        }

        var islands = new List<HashiIsland>();
        for (var cell = 0; cell < board.Grid.Length; cell++)
        {
            var clue = board.Grid[cell];
            if (clue is "-" or ".")
                continue;
            if (clue is not [>= '1' and <= '8'])
                throw new InvalidDataException("Unexpected Hashi clue.");
            islands.Add(new(cell % board.Width, cell / board.Width, clue[0] - '0'));
        }
        var puzzle = HashiPuzzle.Create(board.Width, board.Height, islands);
        if (store.FindHashiPuzzle(puzzle.ContentHash) is not null)
            return false;
        var (validation, verified) = VerifiedHashiPuzzle.VerifyCollection(puzzle, board.Attribution, ProofTimeout,
            time.GetUtcNow(), token);
        if (verified is null)
            throw new InvalidDataException($"Hashi uniqueness verification returned {validation.Status}.");
        if (board.Answer is { } answer && !MatchesHashiGrid(puzzle, verified.Solution, answer))
            throw new InvalidDataException("The publisher's Hashi answer differs from the verified bridges.");
        token.ThrowIfCancellationRequested();
        return store.SaveHashiPuzzle(verified);
    }

    internal static bool MatchesHashiGrid(HashiPuzzle puzzle, IReadOnlyList<byte> solution, string[] answer)
    {
        var expected = Enumerable.Repeat("-", puzzle.Width * puzzle.Height).ToArray();
        for (var edge = 0; edge < puzzle.Edges.Count; edge++)
        {
            var count = solution[edge];
            if (count == 0)
                continue;
            var (first, second) = puzzle.Edges[edge];
            var (x, y, _) = puzzle.Islands[first];
            var (endX, endY, _) = puzzle.Islands[second];
            var step = y == endY ? 1 : puzzle.Width;
            var symbol = y == endY ? count == 1 ? "1" : "2" : count == 1 ? "a" : "b";
            for (var cell = y * puzzle.Width + x + step; cell < endY * puzzle.Width + endX; cell += step)
                expected[cell] = symbol;
        }
        return expected.SequenceEqual(answer);
    }

    internal static CollectionBoard? ParseJanko(string html, Uri url, PuzzleGame game, bool allowNonCommercial)
    {
        var document = new HtmlParser().ParseDocument(html);
        var footer = document.QuerySelectorAll("footer td");
        var author = footer.FirstOrDefault(cell => cell.TextContent.Trim() == "Autor:")?.NextElementSibling?.TextContent.Trim();
        var licenseCell = footer.FirstOrDefault(cell => cell.TextContent.TrimStart().StartsWith("Lizenz:", StringComparison.Ordinal));
        var licenseLink = licenseCell?.NextElementSibling?.QuerySelector("a[rel='license'][href]") ??
            licenseCell?.QuerySelector("a[rel='license'][href]");
        if (string.IsNullOrWhiteSpace(author) || licenseLink?.GetAttribute("href") is not { } href ||
            !Uri.TryCreate(url, href, out var terms) || !PermittedLicense(terms, allowNonCommercial))
            return null;

        var script = document.QuerySelector("script[type='application/x-janko']")?.TextContent
            ?? throw new InvalidDataException("Missing structured puzzle data.");
        var parts = SectionHeader().Split(script);
        if (parts.Length < 3 || parts[1] != "begin")
            throw new InvalidDataException("Missing puzzle header.");
        var fields = Lines(parts[2]);
        string? Value(string key) => fields.FirstOrDefault(line => line.StartsWith(key + " ", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();
        if (Value("puzzle") != (game == PuzzleGame.Hashi ? "hashi" : "nonogram") ||
            !int.TryParse(Value("cols") ?? Value("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(Value("rows") ?? Value("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            width is < 1 or > 100 || height is < 1 or > 100)
            throw new InvalidDataException("Unexpected puzzle game or dimensions.");

        var id = Path.GetFileName(url.AbsolutePath).Split('.')[0];
        var title = document.QuerySelector("h1")?.TextContent.Trim() ?? $"Janko {id}";
        var attribution = new PublishedPuzzle("janko", "Janko", id, url.AbsoluteUri, terms.AbsoluteUri, title, author);
        var grid = ReadGrid(game == PuzzleGame.Hashi ? "problem" : "solution")
            ?? throw new InvalidDataException("Missing puzzle grid.");
        return new(width, height, grid, game == PuzzleGame.Hashi ? ReadGrid("solution") : null, attribution);

        string[]? ReadGrid(string name)
        {
            var section = Array.IndexOf(parts, name);
            if (section < 0)
                return null;
            var rows = Lines(parts[section + 1]).Select(row => row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
            if (rows.Length != height || rows.Any(row => row.Length != width))
                throw new InvalidDataException("Puzzle grid does not match its dimensions.");
            return rows.SelectMany(row => row).ToArray();
        }
    }

    private static bool PermittedLicense(Uri uri, bool allowNonCommercial)
    {
        if (uri.Scheme is not ("https" or "http"))
            return false;
        if (uri.Host == JankoRoot.Host && uri.AbsolutePath == "/Raetsel/Creative-Commons.htm")
            return allowNonCommercial;
        if (uri.Host != "creativecommons.org")
            return false;
        return uri.AbsolutePath.StartsWith("/licenses/by/", StringComparison.Ordinal) ||
            uri.AbsolutePath.StartsWith("/licenses/by-sa/", StringComparison.Ordinal) ||
            uri.AbsolutePath.StartsWith("/publicdomain/zero/", StringComparison.Ordinal) ||
            allowNonCommercial && uri.AbsolutePath.StartsWith("/licenses/by-nc-sa/", StringComparison.Ordinal);
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal sealed record CollectionBoard(int Width, int Height, string[] Grid, string[]? Answer, PublishedPuzzle Attribution);
    private sealed record BundledPack(string Source, string SourceName, PuzzleGame Game, string Name, string Url, string License, string Kind);
    private sealed record BundledPuzzle(string Id, string Title, string[] Rows, string[]? Answer = null);

    [GeneratedRegex(@"^[0-9]{3,4}\.a\.htm$")]
    private static partial Regex JankoPuzzleFile();

    [GeneratedRegex(@"(?m)^\[([a-z]+)\][^\S\n]*$")]
    private static partial Regex SectionHeader();
}

internal sealed class ExtraCollectionWorker(ExtraCollectionRefresh refresh, ExtraCollectionOptions options,
    TimeProvider time, ILogger<ExtraCollectionWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        options.BundledEnabled ? ImportBundledAsync(stoppingToken) : Task.CompletedTask,
        options.JankoEnabled ? RunJankoAsync(PuzzleGame.Nonogram, stoppingToken) : Task.CompletedTask,
        options.JankoEnabled ? RunJankoAsync(PuzzleGame.Hashi, stoppingToken) : Task.CompletedTask);

    private async Task ImportBundledAsync(CancellationToken token)
    {
        var (nonograms, hashi) = await refresh.ImportBundledAsync(null, null, token);
        if (nonograms + hashi > 0)
            logger.LogInformation("Imported {Nonograms} Nonograms and {Hashi} Hashi boards from bundled packs.", nonograms, hashi);
    }

    private async Task RunJankoAsync(PuzzleGame game, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var more = false;
            try
            {
                var result = await refresh.RefreshJankoAsync(game, options.AllowNonCommercial, false, token);
                more = result.More;
                if (result.Added > 0)
                    logger.LogInformation("Imported {Count} Janko {Game} boards.", result.Added, game);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Janko {Game} refresh will retry in an hour.", game);
            }
            await Task.Delay(more ? TimeSpan.FromMinutes(3) : TimeSpan.FromHours(1), time, token);
        }
    }
}
