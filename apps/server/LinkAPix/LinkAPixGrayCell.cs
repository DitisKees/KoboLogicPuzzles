using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace KoboLogicPuzzles.Server.LinkAPix;

// GrayCell publishes ordinary EMF records for grid squares, clue text and
// solution lines. Read just those vector records without invoking a renderer.
internal static class LinkAPixGrayCell
{
    private const int MaxEmfBytes = 1024 * 1024;
    private readonly record struct Point(int X, int Y);

    internal static LinkImportEntry Decode(byte[] archive, int width, int height, LinkAttribution source)
    {
        if (width is < 1 or > LinkAPixBoard.MaxDimension || height is < 1 or > LinkAPixBoard.MaxDimension)
            throw new InvalidDataException("Unsupported GrayCell dimensions.");
        using var stream = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count != 2 || zip.Entries.Any(entry => entry.Length is < 88 or > MaxEmfBytes ||
            !entry.Name.EndsWith(".emf", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Expected a pair of bounded GrayCell EMF documents.");

        // Some archives use DOS-encoded Cyrillic filenames. The answer has
        // more line records than the clue sheet, regardless of the filename.
        var documents = zip.Entries.Select(Read)
            .Select(data => (Data: data, Lines: Records(data).Count(record => record.Kind == 54)))
            .OrderBy(document => document.Lines).ToArray();
        if (documents[0].Lines == documents[1].Lines)
            throw new InvalidDataException("The GrayCell vector answer could not be identified.");
        var puzzleBytes = documents[0].Data;
        var answerBytes = documents[1].Data;

        var tiles = new Dictionary<(int Width, int Height), HashSet<Point>>();
        var labels = new List<(Point Point, int Value)>();
        foreach (var record in Records(puzzleBytes))
        {
            if (record.Kind == 43)
            {
                var p = At(puzzleBytes, record.Offset + 8, record.End);
                var end = At(puzzleBytes, record.Offset + 16, record.End);
                var size = (end.X - p.X, end.Y - p.Y);
                if (!tiles.TryGetValue(size, out var cells))
                    tiles.Add(size, cells = []);
                cells.Add(p);
            }
            else if (record.Kind == 84)
            {
                var p = At(puzzleBytes, record.Offset + 36, record.End);
                var count = Word(puzzleBytes, record.Offset + 44, record.End);
                var offset = Word(puzzleBytes, record.Offset + 48, record.End);
                if (count is < 1 or > 5 || offset < 76 || offset > record.End - record.Offset - count * 2)
                    continue;
                var label = Encoding.Unicode.GetString(puzzleBytes, record.Offset + offset, count * 2);
                if (int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    labels.Add((p, value));
            }
        }

        var grid = tiles.FirstOrDefault(item => item.Key.Width is > 1 and <= 4096 && item.Key.Height is > 1 and <= 4096 &&
            item.Value.Count == width * height);
        if (grid.Value is null)
            throw new InvalidDataException("The GrayCell vector grid does not match the published dimensions.");
        var origin = new Point(grid.Value.Min(p => p.X), grid.Value.Min(p => p.Y));
        var (dx, dy) = grid.Key;
        if (Math.Abs((long)origin.X) > 1_000_000 || Math.Abs((long)origin.Y) > 1_000_000 ||
            Enumerable.Range(0, width * height).Any(i => !grid.Value.Contains(new(origin.X + i % width * dx, origin.Y + i / width * dy))))
            throw new InvalidDataException("The GrayCell grid has irregular tiles.");
        var clues = labels.Select(label => (Cell: Cell(label.Point), label.Value))
            .Where(label => label.Cell >= 0).Select(label => new LinkClue(label.Cell, label.Value)).ToArray();
        var board = new LinkAPixBoard(width, height, clues);
        var masks = new int[board.Area];
        Point? last = null;
        foreach (var record in Records(answerBytes))
        {
            if (record.Kind is not (27 or 54))
                continue;
            var p = At(answerBytes, record.Offset + 8, record.End);
            if (record.Kind == 54 && last is { } from)
            {
                var a = Cell(from);
                var b = Cell(p);
                if (a >= 0 && b >= 0 && IsCenter(from) && IsCenter(p))
                {
                    var direction = Array.IndexOf(board.Neighbors[a], b);
                    if (direction < 0)
                        throw new InvalidDataException("The GrayCell answer contains an unsupported stroke.");
                    masks[a] |= 1 << direction;
                    masks[b] |= 1 << ((direction + 2) % 4);
                }
            }
            last = p;
        }

        var marks = new string(masks.Select(LinkAPixBoard.DirectionMark).ToArray());
        if (!board.TryReadMarks(marks, true, out var paths))
            throw new InvalidDataException("The GrayCell vector answer does not connect the clues.");
        return new(width, height, clues, source, paths);

        int Cell(Point point)
        {
            var x = (long)point.X - origin.X;
            var y = (long)point.Y - origin.Y;
            return x < 0 || y < 0 || x >= width * dx || y >= height * dy ? -1 : (int)(y / dy * width + x / dx);
        }

        bool IsCenter(Point p) => ((long)p.X - origin.X) % dx == dx / 2 && ((long)p.Y - origin.Y) % dy == dy / 2;
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        var data = new byte[(int)entry.Length];
        input.ReadExactly(data);
        if (input.ReadByte() != -1)
            throw new InvalidDataException("An EMF exceeded its declared size.");
        return data;
    }

    private static IEnumerable<(int Kind, int Offset, int End)> Records(byte[] data)
    {
        if (Word(data, 0, data.Length) != 1 || Word(data, 40, data.Length) != 0x464d4520)
            throw new InvalidDataException("Invalid EMF header.");
        for (var offset = 0; offset < data.Length;)
        {
            var kind = Word(data, offset, data.Length);
            var size = Word(data, offset + 4, data.Length);
            if (size < 8 || size > data.Length - offset || size % 4 != 0)
                throw new InvalidDataException("Invalid EMF record size.");
            yield return (kind, offset, offset + size);
            if (kind == 14 && offset + size == data.Length)
                yield break;
            offset += size;
        }
        throw new InvalidDataException("The EMF is missing its terminator.");
    }

    private static Point At(byte[] data, int offset, int limit) => new(Word(data, offset, limit), Word(data, offset + 4, limit));
    private static int Word(byte[] data, int offset, int limit) => offset >= 0 && offset <= limit - 4
        ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4))
        : throw new InvalidDataException("Truncated EMF record.");
}
