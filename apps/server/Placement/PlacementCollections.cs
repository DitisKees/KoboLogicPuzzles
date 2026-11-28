using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace KoboLogicPuzzles.Server.Placement;

internal sealed record PlacementImport(PlacementBoard Board, string Author, string? Title, string SourceId,
    string SourceUrl, string? Rating, string? Solution, string Source = "janko", string SourceName = "Janko",
    string License = "CC BY-NC-SA 3.0", string? LicenseUrl = null);

internal sealed partial class PlacementCollections(PuzzleStore store, IConfiguration config, IHttpClientFactory clients,
    TimeProvider time, ILogger<PlacementCollections> logger)
{
    private static readonly Uri LicensePage = new("https://www.janko.at/Raetsel/Creative-Commons.htm");
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    internal bool Enabled(PuzzleGame game) => config.GetValue($"{game}Collections:JankoEnabled", false) &&
        config.GetValue("PlacementCollections:AllowNonCommercial", false);

    internal async Task<(int Added, bool More)> RefreshAsync(PuzzleGame game, bool force, CancellationToken token)
    {
        using var refresh = store.TryLockPlacement(game, "janko");
        if (refresh is null)
            return (0, true);
        var (cursor, checkedAt) = store.GetPlacementRefresh(game);
        if (!force && cursor == "" && time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(checkedAt) < Interval)
            return (0, false);

        var directory = game == PuzzleGame.Tents ? "Zeltlager" : "Battleships";
        var site = new Uri($"https://www.janko.at/Raetsel/{directory}/");
        using var http = clients.CreateClient("placement-collections");
        var parser = new HtmlParser();
        var html = await http.GetStringAsync(new Uri(site, "index.htm"), token);
        var links = parser.ParseDocument(html).QuerySelectorAll("a[href]")
            .Select(link => link.GetAttribute("href")!).Where(link => PuzzleLink().IsMatch(link))
            .Distinct().OrderDescending(StringComparer.Ordinal).ToArray();
        if (links.Length == 0)
            throw new InvalidDataException("No numbered Janko placement puzzles were found.");

        var batch = links.Where(link => cursor.Length == 0 || string.CompareOrdinal(link, cursor) < 0).Take(41).ToArray();
        var added = 0;
        foreach (var link in batch.Take(40))
        {
            token.ThrowIfCancellationRequested();
            var url = new Uri(site, link);
            try
            {
                if (ParseJanko(game, await http.GetStringAsync(url, token), url) is { } entry && VerifyAndStore(entry, token))
                    added++;
            }
            catch (Exception exception) when (exception is InvalidDataException or TimeoutException ||
                exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                logger.LogWarning("Skipped Janko {Game} {Id}: {Reason}", game, link, exception.Message);
            }
            store.SavePlacementRefresh(game, link, checkedAt);
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, token);
        }
        if (batch.Length <= 40)
            store.SavePlacementRefresh(game, "", time.GetUtcNow().ToUnixTimeSeconds());
        return (added, batch.Length > 40);
    }

    internal bool VerifyAndStore(PlacementImport entry, CancellationToken token)
    {
        var board = entry.Board;
        if (entry.Solution is { } supplied && !board.IsSolved(supplied))
            throw new InvalidDataException("The supplied solution violates the puzzle rules.");

        var hash = board.Hash();
        if (store.FindPlacement(PuzzleId.FromPlacement(board.Game, hash)) is { } existing)
        {
            if (entry.Solution is not null && entry.Solution != existing.Solution)
                throw new InvalidDataException("The supplied answer disagrees with the stored solution.");
            return false;
        }

        var proof = new PlacementSolver(board, TimeSpan.FromSeconds(8), token).CountSolutions();
        if (proof.Count != 1 || entry.Solution is not null && proof.Solution != entry.Solution)
            throw new InvalidDataException($"The board failed the uniqueness proof ({proof.Count} solutions found).");

        token.ThrowIfCancellationRequested();
        var metadata = new PlacementMetadata(entry.Source, entry.SourceName, "collection", entry.SourceId, entry.SourceUrl,
            entry.License, null, null, proof.Difficulty!, proof.Solvability!, hash, entry.Rating, entry.LicenseUrl);
        return store.SavePlacement(board.ToDefinition(metadata, entry.Title, entry.Author, proof.Solution!));
    }

    internal static PlacementImport? ParseJanko(PuzzleGame game, string html, Uri url)
    {
        var document = new HtmlParser().ParseDocument(html);
        var author = document.QuerySelectorAll("footer td").FirstOrDefault(cell => cell.TextContent.Trim() == "Autor:")
            ?.NextElementSibling?.TextContent.Trim();
        var licensed = document.QuerySelectorAll("footer a[rel=license][href]").Any(link =>
            Uri.TryCreate(url, link.GetAttribute("href"), out var license) && license == LicensePage);
        if (author != "Otto Janko" || !licensed)
            return null;

        var data = document.QuerySelector("script[type='application/x-janko']")?.TextContent ??
            throw new InvalidDataException("Missing the structured puzzle data.");
        var sections = SectionHeader().Split(data);
        if (sections.Length < 5 || sections[1] != "begin" || !sections.Contains("end"))
            throw new InvalidDataException("Missing puzzle header or terminator.");
        string? Field(string name) => sections[2].Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..].Trim();
        if (Field("puzzle") != PlacementBoard.Slug(game) || Field("author") != author)
            throw new InvalidDataException("Mismatched Janko game or author.");
        int Dimension(string field) => PlacementGenerator.ParseNumber(Field(field) ?? Field("size") ?? "");
        var width = Dimension("cols");
        var height = Dimension("rows");
        if (width is < 1 or > PlacementBoard.MaxDimension || height is < 1 or > PlacementBoard.MaxDimension)
            throw new InvalidDataException("Janko dimensions exceed supported bounds.");

        var problem = ReadGrid("problem");
        var answer = ReadGrid("solution");
        var tents = game == PuzzleGame.Tents;
        string givens;
        string? solution;
        if (tents)
        {
            givens = problem is not null ? Map(problem, c => c switch { 'x' => 't', 'o' => '#', '#' => 'x', '.' or '-' => '.', _ => Invalid(c) }) :
                answer is not null ? Map(answer, c => c switch { 'x' => 't', 'o' or '.' or '-' => '.', _ => Invalid(c) }) :
                throw new InvalidDataException("Missing the tree grid.");
            solution = answer is null ? null : Map(answer, c => c switch { 'x' => '.', 'o' => '#', '.' or '-' or '#' => 'x', _ => Invalid(c) });
            if (solution is not null && Enumerable.Range(0, givens.Length).Any(i => (givens[i] == 't') != (answer![i] == 'x')))
                throw new InvalidDataException("The Janko tree grid and answer disagree.");
        }
        else
        {
            givens = problem is null ? new string('.', width * height) :
                Map(problem, c => c switch { '.' or '-' => '.', '?' => '#', 'x' or 'o' or 'n' or 's' or 'e' or 'w' or 'm' => c, _ => Invalid(c) });
            solution = answer is null ? null : Map(answer, c => c switch
            {
                '.' or '-' or 'x' => 'x', 'o' or 'n' or 's' or 'e' or 'w' or 'm' => '#', _ => Invalid(c)
            });
        }

        var rows = Numbers(Section("rlabels"));
        var columns = Numbers(Section("clabels"));
        if (rows.Length == 0 && columns.Length == 0 && solution is not null && (problem is null || !tents))
            (rows, columns) = PlacementBoard.Totals(width, height, solution);
        var board = new PlacementBoard(game, width, height, rows, columns, givens, tents ? [] : Numbers(Field("ships")));
        if (!tents && answer is not null && !new PlacementBoard(game, width, height, rows, columns,
            new string(answer.Select(c => c is '.' or '-' ? 'x' : c).ToArray()), board.Fleet).IsSolved(solution!))
            throw new InvalidDataException("The published Battleships solution has invalid segments.");

        var id = Path.GetFileName(url.AbsolutePath).Split('.')[0];
        return new(board, author, document.QuerySelector("h1")?.TextContent.Trim(), id, url.AbsoluteUri,
            Field("difficulty"), solution, LicenseUrl: LicensePage.AbsoluteUri);

        string? Section(string name) => Array.IndexOf(sections, name) is >= 3 and var i && i + 1 < sections.Length ? sections[i + 1] : null;
        string? ReadGrid(string name)
        {
            if (Section(name) is not { } section)
                return null;
            var grid = section.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Words).ToArray();
            if (grid.Length != height || grid.Any(row => row.Length != width || row.Any(cell => cell.Length != 1)))
                throw new InvalidDataException("Incorrect structured Janko grid dimensions.");
            return string.Concat(grid.SelectMany(row => row));
        }
    }

    private static char Invalid(char value) => throw new InvalidDataException($"Unknown Janko grid symbol '{value}'.");
    private static string Map(string grid, Func<char, char> convert) => new(grid.Select(convert).ToArray());
    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static int[] Numbers(string? text) => text is null ? [] : Words(text).Select(PlacementGenerator.ParseNumber).ToArray();

    [GeneratedRegex(@"\A[0-9]{3,5}\.a\.htm\z")]
    private static partial Regex PuzzleLink();

    [GeneratedRegex(@"(?m)^\[([a-z]+)\][^\S\n]*$")]
    private static partial Regex SectionHeader();
}

internal sealed class PlacementCollectionsWorker(PlacementCollections collections, TimeProvider time,
    ILogger<PlacementCollectionsWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(PlacementBoard.Games.Where(collections.Enabled).Select(game => RunAsync(game, stoppingToken)));

    private async Task RunAsync(PuzzleGame game, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(1);
            try
            {
                var (added, more) = await collections.RefreshAsync(game, false, token);
                if (added > 0)
                    logger.LogInformation("Imported {Count} verified Janko {Game} boards.", added, game);
                if (more)
                    delay = TimeSpan.FromMinutes(2);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Janko {Game} refresh will retry in an hour.", game);
            }
            await Task.Delay(delay, time, token);
        }
    }
}
