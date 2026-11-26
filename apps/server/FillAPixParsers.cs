using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace KoboLogicPuzzles.Server;

internal static partial class FillAPixParsers
{
    public static IReadOnlyDictionary<int, string> JankoCatalog(IDocument document) =>
        ReadIndex(document.QuerySelectorAll("#index-1 a[href]"), JankoLink(), _ => "");

    public static IReadOnlyDictionary<int, string> FillAPixNetCatalog(IDocument document) =>
        ReadIndex(document.QuerySelectorAll("#puzzle-grid a.pzlsolve[href]"), FillAPixNetLink(),
            link => link.Closest("article")?.QuerySelector("header")?.TextContent.Trim() ?? "");

    private static IReadOnlyDictionary<int, string> ReadIndex(IEnumerable<IElement> links, Regex pattern, Func<IElement, string> title)
    {
        var entries = new Dictionary<int, string>();
        foreach (var link in links)
        {
            var match = pattern.Match(link.GetAttribute("href")!);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var id) || id < 1)
                throw new InvalidDataException("A Fill-a-Pix index contained an unrecognized link.");
            entries.TryAdd(id, title(link));
        }
        if (entries.Count == 0)
            throw new InvalidDataException("The Fill-a-Pix index did not contain any puzzles.");
        return entries;
    }

    public static FillAPixPuzzleDefinition Janko(PuzzleId id, Uri url, IDocument document)
    {
        var source = document.QuerySelector("script[type='application/x-janko']")?.TextContent ??
            throw new InvalidDataException($"{id} is missing its puzzle data.");
        var sections = JankoSection().Split(source.Trim());
        if (sections.Length < 5 || sections[1] != "begin")
            throw new InvalidDataException($"{id} has malformed puzzle data.");
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < sections.Length; i += 2)
            parts.TryAdd(sections[i], sections[i + 1].Trim());
        if (!parts.TryGetValue("problem", out var problem))
            throw new InvalidDataException($"{id} is missing its clue grid.");

        var fields = parts["begin"].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? Field(string name) => fields.FirstOrDefault(line => line.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..].Trim();
        var rows = Rows(problem);
        var width = rows[0].Length;
        if (rows.Any(row => row.Length != width) || Field("puzzle") is not ("mosaic" or "mosaik"))
            throw new InvalidDataException($"{id} has an invalid Janko grid.");

        int?[] clues = rows.SelectMany(row => row.Select(value => value switch
        {
            "-" => (int?)null,
            [>= '0' and <= '9'] => value[0] - '0',
            _ => throw new InvalidDataException($"{id} has an invalid Janko clue."),
        })).ToArray();
        var solution = parts.TryGetValue("solution", out var answer) ? new string(Rows(answer).SelectMany(row =>
            row.Select(value => value switch { "x" => '#', "-" => '.', _ => '?' })).ToArray()) : null;
        var licenseCell = document.QuerySelectorAll("footer td").FirstOrDefault(
            cell => cell.TextContent.Trim() == "Lizenz:")?.NextElementSibling;
        var license = (licenseCell?.QuerySelector("a") ?? licenseCell)?.TextContent.Trim();
        var entry = new FillAPixCatalogEntry(id, width, rows.Length,
            document.QuerySelector("h1")?.TextContent.Trim() ?? $"Mosaik #{id.FillAPixNumber}", Field("author"),
            new(id.FillAPixSource!.Value.Slug(), url.AbsoluteUri, string.IsNullOrWhiteSpace(license) ? null : license));
        return entry.Define(clues, solution);
    }

    private static string[][] Rows(string data)
    {
        var rows = data.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0)
            throw new InvalidDataException("The Janko grid is empty.");
        return rows.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
    }

    public static FillAPixPuzzleDefinition FillAPixNet(PuzzleId id, Uri url, string title, string json)
    {
        var board = ParseJson<FillAPixNetBoard>(json);
        if (board.Data is null || board.Data.Length != board.Height || board.Data.Any(row => row is null || row.Length != board.Width))
            throw new InvalidDataException($"{id} has an invalid JSON grid.");
        var entry = new FillAPixCatalogEntry(id, board.Width, board.Height,
            string.IsNullOrEmpty(title) ? $"Fill-a-Pix #{id.FillAPixNumber}" : title, "Matthias Rüster",
            new(id.FillAPixSource!.Value.Slug(), url.AbsoluteUri));
        return entry.Define(board.Data.SelectMany(row => row.Select(value => value == -1 ? (int?)null : value)).ToArray(), null);
    }

    public static IReadOnlyList<FillAPixCatalogEntry> GrandGamesCatalog(IDocument document, Uri baseUri)
    {
        var entries = new List<FillAPixCatalogEntry>();
        foreach (var card in document.QuerySelectorAll(".ag-catalog article.gg-card"))
        {
            var link = card.QuerySelector(".gg-card__title a");
            var number = GrandGamesLink().Match(link?.GetAttribute("href") ?? "");
            var size = Dimensions().Match(card.QuerySelector(".gg-card__badge")?.TextContent.Trim() ?? "");
            if (!number.Success || !size.Success || !int.TryParse(number.Groups[1].Value, out var id) || id < 1 ||
                !int.TryParse(size.Groups[1].Value, out var width) || !int.TryParse(size.Groups[2].Value, out var height) ||
                width < 1 || height < 1 || (long)width * height > int.MaxValue)
                throw new InvalidDataException("GrandGames returned an unrecognized Mosaic catalog entry.");

            var ratingText = card.QuerySelector("[title='Rating'] b")?.TextContent.Trim();
            var complexityText = card.QuerySelector("[title='Complexity'] b")?.TextContent.Trim().TrimEnd('%');
            decimal? quality = decimal.TryParse(ratingText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rating) &&
                rating is > 0 and <= 5 ? rating : null;
            int? complexity = int.TryParse(complexityText, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) &&
                percent is >= 0 and <= 100 ? percent : null;
            entries.Add(new(PuzzleId.FromFillAPix(FillAPixSource.GrandGames, id), width, height, link!.TextContent.Trim(), null,
                new(FillAPixSource.GrandGames.Slug(), new Uri(baseUri, $"/mosaic/id{id}").AbsoluteUri, Quality: quality, Complexity: complexity)));
        }
        if (entries.Count == 0)
            throw new InvalidDataException("GrandGames returned an empty or unrecognized Mosaic catalog page.");
        return entries;
    }

    public static FillAPixPuzzleDefinition GrandGames(FillAPixCatalogEntry entry, string json)
    {
        var level = ParseJson<GrandGamesResponse>(json).Level;
        if (level is null || level.Id != entry.Id.FillAPixNumber || level.Game != "mosaic" ||
            level.Sx != entry.Width || level.Sy != entry.Height || level.P is null)
            throw new InvalidDataException($"GrandGames returned a different puzzle for {entry.Id}.");
        var parts = level.P.Split('|');
        var area = checked(entry.Width * entry.Height);
        if (parts.Length != 2 || parts[1].Length != area)
            throw new InvalidDataException($"{entry.Id} has an invalid GrandGames clue grid.");

        var clues = new int?[area];
        var solution = new char[area];
        // GrandGames stores both grids column-major. The hex bitmap is aligned at its least significant bit.
        var paddingBits = (long)parts[0].Length * 4 - area;
        if (parts[0].Length == 0 || !parts[0].All(Uri.IsHexDigit))
            throw new InvalidDataException($"{entry.Id} has an invalid GrandGames solution.");
        for (var y = 0; y < entry.Height; y++)
        {
            for (var x = 0; x < entry.Width; x++)
            {
                var target = y * entry.Width + x;
                var source = x * entry.Height + y;
                clues[target] = parts[1][source] switch
                {
                    'N' => null,
                    >= '0' and <= '9' and var clue => clue - '0',
                    _ => throw new InvalidDataException($"{entry.Id} has an invalid GrandGames clue."),
                };
                var bitIndex = source + paddingBits;
                var (digit, bit) = Math.DivRem(bitIndex, 4L);
                solution[target] = bitIndex >= 0 && (Uri.FromHex(parts[0][(int)digit]) & (8 >> (int)bit)) != 0 ? '#' : '.';
            }
        }
        return entry.Define(clues, new string(solution));
    }

    private static T ParseJson<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? throw new InvalidDataException("Missing puzzle data.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The upstream puzzle response was not valid JSON.", exception);
        }
    }

    private sealed record FillAPixNetBoard(int Width, int Height, int[][]? Data);
    private sealed record GrandGamesResponse(GrandGamesLevel? Level);
    private sealed record GrandGamesLevel(int Id, string Game, int Sx, int Sy, string? P);

    [GeneratedRegex(@"^(\d+)\.a\.htm$")]
    private static partial Regex JankoLink();
    [GeneratedRegex(@"^\./(\d+)/index\.html$")]
    private static partial Regex FillAPixNetLink();
    [GeneratedRegex(@"^/mosaic/id(\d+)$")]
    private static partial Regex GrandGamesLink();
    [GeneratedRegex(@"^(\d+)\s*[x×]\s*(\d+)$")]
    private static partial Regex Dimensions();
    [GeneratedRegex(@"^\s*\[([a-z]+)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex JankoSection();
}
