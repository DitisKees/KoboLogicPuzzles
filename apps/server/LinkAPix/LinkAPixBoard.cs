using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace KoboLogicPuzzles.Server.LinkAPix;

public sealed record LinkClue(int Cell, int Length,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null);
public sealed record LinkAttribution(string Source, string SourceId, string SourceUrl, string Author,
    string Title, string License, string? LicenseUrl = null, string? OriginalRating = null)
{
    public string SourceName => LinkSources.Name(Source);
}

public sealed record LinkMetadata(string ContentHash, string Difficulty, string Solvability, LinkAttribution[] Attributions)
{
    public bool Unique => true;
    public string Source => Attributions[0].Source;
    public string SourceName => Attributions[0].SourceName;
    public string SourceKind => "collection";
}

public sealed record LinkAPixDefinition(string Id, int Width, int Height, LinkClue[] Clues, string Title, string Author,
    LinkMetadata Metadata, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[][]? Solution,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Picture)
    : GamePuzzleDefinition(Id, Width, Height)
{
    internal LinkAPixDefinition WithoutAnswer() => this with { Solution = null, Picture = null };
}

internal static class LinkSources
{
    internal const string Game = "link-a-pix";

    internal static string Name(string source) => source switch
    {
        "grandgames" => "GrandGames",
        "playsudoku" => "PlaySudoku",
        "janko" => "Janko",
        "graycell" => "GrayCell",
        "conceptis" => "Conceptis",
        "local" => "Local collection",
        _ => throw new InvalidDataException("Unknown Link-a-Pix source."),
    };

    internal static bool Valid(string source) => source is "grandgames" or "playsudoku" or "janko" or "graycell" or "conceptis" or "local";
}

internal sealed class LinkAPixBoard
{
    internal const int MaxDimension = 200;
    private const string Hex = "0123456789abcdef";

    internal LinkAPixBoard(int width, int height, LinkClue[] clues)
    {
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || clues.Length == 0 ||
            clues.Length > width * height || clues.Any(c => c is null || c.Cell < 0 || c.Cell >= width * height ||
                c.Length < 1 || c.Length > width * height ||
                c.Color is { } color && (color.Length is < 1 or > 32 || !color.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '#' or '-'))))
            throw new InvalidDataException("Invalid Link-a-Pix dimensions or clues.");

        Width = width;
        Height = height;
        Clues = clues.OrderBy(c => c.Cell).Select(c => c with
        {
            Color = c.Color?.ToLowerInvariant() switch { "black" or "#000000" => null, var color => color },
        }).ToArray();
        ByCell = new LinkClue?[Area];
        foreach (var clue in Clues)
        {
            if (ByCell[clue.Cell] is not null)
                throw new InvalidDataException("Two clues occupy the same cell.");
            ByCell[clue.Cell] = clue;
        }

        if (Clues.Where(c => c.Length > 1).GroupBy(c => (c.Length, c.Color)).Any(group => group.Count() % 2 != 0) ||
            Clues.Sum(c => (long)c.Length + (c.Length == 1 ? 1 : 0)) > 2L * Area)
            throw new InvalidDataException("The clue classes cannot form disjoint pairs.");

        Neighbors = new int[Area][];
        for (var i = 0; i < Area; i++)
            Neighbors[i] = [i >= Width ? i - Width : -1, i % Width + 1 < Width ? i + 1 : -1,
                i + Width < Area ? i + Width : -1, i % Width > 0 ? i - 1 : -1];
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int Area => Width * Height;
    internal LinkClue[] Clues { get; }
    internal LinkClue?[] ByCell { get; }
    internal int[][] Neighbors { get; }
    internal static bool SameClass(LinkClue a, LinkClue b) => a.Length == b.Length && a.Color == b.Color;

    internal string ContentHash()
    {
        // Number classes are independent of palette names, but every distinct class remains distinct.
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        var canonical = new StringBuilder(FormattableString.Invariant($"link-a-pix|1|{Width}|{Height}"));
        foreach (var clue in Clues)
        {
            var color = clue.Color ?? "black";
            if (!classes.TryGetValue(color, out var colorClass))
                classes.Add(color, colorClass = classes.Count);
            canonical.Append(FormattableString.Invariant($"|{clue.Cell},{clue.Length},{colorClass}"));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(canonical.ToString())));
    }

    internal static int[][] CanonicalPaths(IEnumerable<int[]> paths) =>
        paths.Select(path => path[0] < path[^1] ? path : path.Reverse().ToArray()).OrderBy(path => path[0]).ToArray();

    internal string? Picture(int[][] paths)
    {
        var picture = new string('.', Area).ToCharArray();
        foreach (var clue in Clues)
            if (clue.Length == 1)
                picture[clue.Cell] = '#';

        foreach (var path in paths)
        {
            if (path is null || path.Length < 2 || path.Length > Area || path.Any(i => i < 0 || i >= Area) ||
                ByCell[path[0]] is not { } start || ByCell[path[^1]] is not { } end ||
                path.Length != start.Length || !SameClass(start, end))
                return null;
            for (var i = 0; i < path.Length; i++)
            {
                var cell = path[i];
                if (picture[cell] != '.' || (i > 0 && !Neighbors[path[i - 1]].Contains(cell)) ||
                    (i > 0 && i < path.Length - 1 && ByCell[cell] is not null))
                    return null;
                picture[cell] = '#';
            }
        }
        return Clues.All(c => picture[c.Cell] == '#') ? new(picture) : null;
    }

    internal bool TryReadMarks(string marks, bool completed, out int[][] paths)
    {
        paths = [];
        if (marks.Length != Area)
            return false;

        var mask = new int[Area];
        for (var i = 0; i < Area; i++)
        {
            var c = marks[i];
            if (c == '.')
                continue;
            if (c == 'x')
            {
                if (ByCell[i] is not null)
                    return false;
                continue;
            }
            var value = Hex.IndexOf(c);
            var degree = value > 0 ? System.Numerics.BitOperations.PopCount((uint)value) : 0;
            if (degree is < 1 or > 2 || ByCell[i] is { } clue && (clue.Length == 1 || degree != 1))
                return false;
            mask[i] = value;
        }

        for (var i = 0; i < Area; i++)
            for (var d = 0; d < 4; d++)
                if ((mask[i] & (1 << d)) != 0 &&
                    (Neighbors[i][d] is not (>= 0 and var next) || (mask[next] & (1 << ((d + 2) % 4))) == 0))
                    return false;

        var visited = new bool[Area];
        var result = new List<int[]>();
        foreach (var clue in Clues.Where(c => c.Length > 1))
        {
            if (visited[clue.Cell])
                continue;
            if (mask[clue.Cell] == 0)
            {
                if (completed)
                    return false;
                continue;
            }
            var path = new List<int>();
            var prior = -1;
            var cell = clue.Cell;
            while (cell >= 0)
            {
                if (visited[cell] || path.Count == clue.Length)
                    return false;
                visited[cell] = true;
                path.Add(cell);
                var next = -1;
                for (var d = 0; d < 4; d++)
                    if ((mask[cell] & (1 << d)) != 0 && Neighbors[cell][d] != prior)
                        next = Neighbors[cell][d];
                prior = cell;
                cell = next;
            }
            if (ByCell[prior] is { } end ? !SameClass(clue, end) || path.Count != clue.Length :
                completed || path.Count >= clue.Length)
                return false;
            result.Add(path.ToArray());
        }

        if (Enumerable.Range(0, Area).Any(i => mask[i] != 0 && !visited[i]))
            return false;
        paths = result.ToArray();
        return true;
    }

    internal string EncodeMarks(IEnumerable<int[]> paths)
    {
        var mask = new int[Area];
        foreach (var path in paths)
            for (var offset = 1; offset < path.Length; offset++)
            {
                var a = path[offset - 1];
                var b = path[offset];
                var direction = Array.IndexOf(Neighbors[a], b);
                mask[a] |= 1 << direction;
                mask[b] |= 1 << ((direction + 2) % 4);
            }
        return new(mask.Select(n => n == 0 ? '.' : Hex[n]).ToArray());
    }

    internal static char DirectionMark(int mask) => mask == 0 ? '.' : Hex[mask];
}
