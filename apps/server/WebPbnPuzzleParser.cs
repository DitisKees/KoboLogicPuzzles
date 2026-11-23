using System.Xml;
using System.Xml.Linq;

namespace KoboNonogram.Server;

public static class WebPbnPuzzleParser
{
    public static PuzzleDefinition? Parse(string xml, int expectedId)
    {
        XDocument document;
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 2 * 1024 * 1024,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"WebPBN export for {expectedId} was not valid XML.", exception);
        }

        var puzzle = document.Root?.Name == "puzzleset" ? document.Root.Element("puzzle") : null;
        var idText = puzzle?.Element("id")?.Value.Trim().Split(' ', 2)[0];
        if (puzzle is null || idText is not { Length: > 1 } || idText[0] != '#' ||
            !int.TryParse(idText.AsSpan(1), out var id) || id != expectedId)
            throw new InvalidDataException($"WebPBN export did not identify itself as puzzle {expectedId}.");

        var colors = puzzle.Elements("color").ToArray();
        var white = colors.FirstOrDefault(color => color.Value.Trim().ToLowerInvariant() is "fff" or "ffffff");
        var black = colors.FirstOrDefault(color => color.Value.Trim() is "000" or "000000");
        var blackName = (string?)black?.Attribute("name");
        if ((string?)puzzle.Attribute("type") != "grid" || colors.Length != 2 || white is null ||
            string.IsNullOrWhiteSpace(blackName) || string.IsNullOrWhiteSpace((string?)white.Attribute("name")))
            return null;

        var defaultColor = (string?)puzzle.Attribute("defaultcolor") ?? "black";
        var rows = ReadClues(puzzle, "rows", defaultColor, blackName);
        var columns = ReadClues(puzzle, "columns", defaultColor, blackName);
        if (rows is null || columns is null)
            return null;

        if (!LinesFit(rows, columns.Length) || !LinesFit(columns, rows.Length) ||
            rows.Sum(line => line.Sum()) != columns.Sum(line => line.Sum()))
            throw new InvalidDataException($"WebPBN puzzle {id} has inconsistent row and column clues.");

        var title = puzzle.Element("title")?.Value.Trim();
        var author = puzzle.Element("author")?.Value.Trim() ?? "";
        var definition = new PuzzleDefinition(id, string.IsNullOrEmpty(title) ? $"#{id}" : title, author,
            columns.Length, rows.Length, rows, columns);
        var solution = ReadSolution(puzzle, definition, black!, white);
        return definition with { Solution = solution };
    }

    private static string? ReadSolution(XElement puzzle, PuzzleDefinition definition, XElement black, XElement white)
    {
        var image = puzzle.Elements("solution")
            .FirstOrDefault(solution => ((string?)solution.Attribute("type") ?? "goal") == "goal")
            ?.Element("image");
        var ink = (string?)black.Attribute("char") ?? ((string?)black.Attribute("name") == "black" ? "X" : null);
        var paper = (string?)white.Attribute("char") ?? ((string?)white.Attribute("name") == "white" ? "." : null);
        if (image is null || ink is not { Length: 1 } || paper is not { Length: 1 } || ink == paper)
            return null;

        var pixels = image.Value.Where(character => !char.IsWhiteSpace(character)).ToArray();
        var rowLength = definition.Width + 2;
        if (pixels.Length != rowLength * definition.Height)
            return null;

        var marks = new char[definition.Width * definition.Height];
        for (var row = 0; row < definition.Height; row++)
        {
            var start = row * rowLength;
            if (pixels[start] != '|' || pixels[start + rowLength - 1] != '|')
                return null;
            for (var column = 0; column < definition.Width; column++)
            {
                var pixel = pixels[start + column + 1];
                if (pixel != ink[0] && pixel != paper[0])
                    return null;
                marks[row * definition.Width + column] = pixel == ink[0] ? '#' : '.';
            }
        }

        var result = new string(marks);
        return definition.MatchesClues(result) ? result : null;
    }

    private static int[][]? ReadClues(XElement puzzle, string direction, string defaultColor, string black)
    {
        var lists = puzzle.Elements("clues").Where(clues => (string?)clues.Attribute("type") == direction).ToArray();
        var lines = lists.Length == 1 ? lists[0].Elements("line").ToArray() : [];
        if (lines.Length == 0)
            throw new InvalidDataException($"WebPBN export is missing its {direction} clues.");
        if (lines.Length > 100)
            return null;

        var result = new int[lines.Length][];
        for (var index = 0; index < lines.Length; index++)
        {
            var counts = new List<int>();
            foreach (var clue in lines[index].Elements())
            {
                if (clue.Name != "count" || !int.TryParse(clue.Value, out var count) || count is < 0 or > 100)
                    throw new InvalidDataException("WebPBN export contains an invalid clue.");
                if (count == 0 || ((string?)clue.Attribute("color") ?? defaultColor) != black)
                    return null;
                counts.Add(count);
            }
            result[index] = counts.ToArray();
        }

        return result;
    }

    private static bool LinesFit(int[][] lines, int length) =>
        lines.All(line => line.Sum() + Math.Max(0, line.Length - 1) <= length);
}
