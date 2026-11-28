using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using KoboLogicPuzzles.Server.Galaxies;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.LinkAPix;
using KoboLogicPuzzles.Server.Placement;

namespace KoboLogicPuzzles.Server.GrandGames;

internal sealed record GrandGamesLevel(int Id, string Game, int Sx, int Sy, string P, JsonElement P1 = default,
    string? P2 = null);

internal static class GrandGamesFormats
{
    internal static string Nonogram(GrandGamesLevel level)
    {
        CheckSize(level, 100);
        // The picross player permutes three Base64 characters before decoding raw DEFLATE.
        var encoded = string.Create(level.P.Length, level.P, (output, input) =>
        {
            for (var i = 0; i < input.Length; i++)
                output[i] = input[i] switch { '2' => '1', 'a' => '2', '1' => 'a', var symbol => symbol };
        });
        using var data = JsonDocument.Parse(Inflate(encoded, zlib: false));
        var root = data.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("move", out var bitmap) ||
            !root.TryGetProperty("colors", out var palette) || palette.ValueKind != JsonValueKind.Object ||
            palette.EnumerateObject().Count() != 1 || !palette.TryGetProperty("1", out var ink) ||
            ink.ValueKind != JsonValueKind.String || ink.GetString() != "#000000")
            throw new InvalidDataException("Expected a black-and-white GrandGames Nonogram.");

        CheckGrid(bitmap, level.Sx, level.Sy);
        var picture = new char[level.Sx * level.Sy];
        for (var x = 0; x < level.Sx; x++)
            for (var y = 0; y < level.Sy; y++)
                picture[y * level.Sx + x] = ReadInt(bitmap[x][y]) switch
                {
                    1 => '#', 9 => '.', _ => throw new InvalidDataException("Unexpected Nonogram pixel."),
                };
        return new(picture);
    }

    internal static HashiPuzzle Hashi(GrandGamesLevel level)
    {
        CheckSize(level, 50);
        if (level.P.Length != level.Sx * level.Sy)
            throw new InvalidDataException("Unexpected Bridges grid length.");
        var islands = new List<HashiIsland>();
        for (var i = 0; i < level.P.Length; i++)
        {
            var clue = level.P[i];
            if (clue is ' ' or '.')
                continue;
            if (clue is < '1' or > '8')
                throw new InvalidDataException("Unexpected Bridges clue.");
            islands.Add(new(i % level.Sx, i / level.Sx, clue - '0'));
        }
        if (islands.Count < 2)
            throw new InvalidDataException("Bridges requires at least two islands.");
        return HashiPuzzle.Create(level.Sx, level.Sy, islands);
    }

    internal static (int[] Clues, string? Solution) Nurikabe(GrandGamesLevel level)
    {
        CheckSize(level, 50);
        using var data = JsonDocument.Parse(level.P);
        var solution = level.P2?.Replace('.', 'x');
        return (ReadRows(data.RootElement, level.Sx, level.Sy, value => value.ValueKind == JsonValueKind.String &&
            value.GetString() == "." ? 0 : ReadInt(value)), solution);
    }

    internal static (GalaxiesBoard Board, string? Solution) Galaxies(GrandGamesLevel level)
    {
        CheckSize(level, GalaxiesBoard.MaxDimension);
        var stride = 2 * level.Sx - 1;
        if (level.P.Length != stride * (2 * level.Sy - 1))
            throw new InvalidDataException("Unexpected Galaxies center grid length.");
        var centers = new List<GalaxyCenter>();
        for (var i = 0; i < level.P.Length; i++)
            if (level.P[i] == '1')
                centers.Add(new(i % stride + 1, i / stride + 1, false));
            else if (level.P[i] != '0')
                throw new InvalidDataException("Unexpected Galaxies center symbol.");

        var board = new GalaxiesBoard(level.Sx, level.Sy, centers);
        if (string.IsNullOrEmpty(level.P2))
            return (board, null);
        using var answer = JsonDocument.Parse(level.P2);
        var owners = ReadRows(answer.RootElement, level.Sx, level.Sy, ReadInt);
        return (board, board.ToEdges(owners));
    }

    internal static PlacementBoard Placement(GrandGamesLevel level, PuzzleGame game)
    {
        CheckSize(level, PlacementBoard.MaxDimension);
        var stride = level.Sx + 1;
        // The corner shared by the row and column totals has no clue and may be omitted.
        var fullLength = stride * (level.Sy + 1);
        if (level.P.Length != fullLength && level.P.Length != fullLength - 1)
            throw new InvalidDataException("Unexpected placement grid length.");
        var tents = game == PuzzleGame.Tents;
        var rows = new int[level.Sy];
        var columns = new int[level.Sx];
        var givens = new char[level.Sx * level.Sy];
        for (var y = 0; y < level.Sy; y++)
        {
            for (var x = 0; x < level.Sx; x++)
            {
                var symbol = level.P[y * stride + x];
                givens[y * level.Sx + x] = tents ? symbol switch
                {
                    '.' or 't' => symbol, 'a' => '#', 'b' => 'x',
                    _ => throw new InvalidDataException("Unexpected Tents clue."),
                } : symbol switch
                {
                    '.' => '.', 'a' => 'x', 'b' => 'o', 'c' => 'm',
                    'd' => 'n', 'e' => 'e', 'f' => 's', 'g' => 'w',
                    _ => throw new InvalidDataException("Unexpected Battleships clue."),
                };
            }
            rows[y] = ReadTotal(level.P[y * stride + level.Sx]);
        }
        for (var x = 0; x < level.Sx; x++)
            columns[x] = ReadTotal(level.P[level.Sy * stride + x]);

        return new(game, level.Sx, level.Sy, rows, columns, new(givens), tents ? [] : [4, 3, 2, 1]);
    }

    internal static LinkImportEntry LinkAPix(GrandGamesLevel level, LinkAttribution attribution)
    {
        CheckSize(level, LinkAPixBoard.MaxDimension);
        if (level.P.StartsWith("F2:", StringComparison.Ordinal) || level.P.StartsWith("F3:", StringComparison.Ordinal))
        {
            var modern = level.P.StartsWith("F3:", StringComparison.Ordinal);
            using var data = modern ? JsonDocument.Parse(Inflate(level.P[3..], zlib: true)) : JsonDocument.Parse(level.P[3..]);
            return ReadLinkPicture(level, data.RootElement, attribution);
        }

        var fields = level.P.Split('|');
        var colorCount = level.P1.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? fields.Skip(1).Count(color => color.Length > 0) : ReadInt(level.P1);
        if (colorCount > 1)
            throw new InvalidDataException("Expected a monochrome Filipino crossword.");
        var mask = fields[0];
        var cell = 0;
        var offset = 0;
        var clues = new List<LinkClue>();
        while (offset < mask.Length)
        {
            var start = offset;
            while (offset < mask.Length && mask[offset] is >= '0' and <= '9' or >= 'a' and <= 'f')
                offset++;
            if (offset != start)
            {
                if (!int.TryParse(mask.AsSpan(start, offset - start), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var skipped) || skipped < 0 || skipped >= level.Sx * level.Sy - cell)
                    throw new InvalidDataException("Invalid Filipino crossword gap.");
                cell += skipped;
            }
            if (offset >= mask.Length || mask[offset] is < 'A' or > 'Z' || cell >= level.Sx * level.Sy)
                throw new InvalidDataException("Invalid Filipino crossword clue.");
            clues.Add(new(cell++, mask[offset++] - 'A' + 1));
        }
        return new(level.Sx, level.Sy, clues.ToArray(), attribution);
    }

    private static LinkImportEntry ReadLinkPicture(GrandGamesLevel level, JsonElement data, LinkAttribution attribution)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("w", out var width) || ReadInt(width) != level.Sx ||
            !data.TryGetProperty("h", out var height) || ReadInt(height) != level.Sy ||
            !data.TryGetProperty("palette", out var palette) || palette.ValueKind != JsonValueKind.Array ||
            palette.GetArrayLength() != 1 || palette[0].ValueKind != JsonValueKind.String ||
            palette[0].GetString() != "#000000" || !data.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected a black-and-white Filipino crossword with matching dimensions.");

        var area = level.Sx * level.Sy;
        var pixels = ReadLinkPixels(data, area);
        var seen = new bool[area];
        var clues = new List<LinkClue>();
        var solution = new List<int[]>();
        if (data.TryGetProperty("implicitSingles", out var singles) && singles.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Invalid Filipino crossword singleton flag.");
        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.Array || path.GetArrayLength() is 0 || path.GetArrayLength() > area)
                throw new InvalidDataException("Invalid Filipino crossword path.");
            var cells = path.EnumerateArray().Select(ReadInt).ToArray();
            foreach (var cell in cells)
            {
                if (cell < 0 || cell >= area || seen[cell] || pixels[cell] != 0)
                    throw new InvalidDataException("Invalid or overlapping Filipino crossword path.");
                seen[cell] = true;
            }
            clues.Add(new(cells[0], cells.Length));
            if (cells.Length > 1)
            {
                clues.Add(new(cells[^1], cells.Length));
                solution.Add(cells);
            }
        }
        for (var i = 0; i < area; i++)
        {
            if (pixels[i] == 255 || seen[i])
                continue;
            if (pixels[i] != 0 || singles.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Incomplete Filipino crossword paths.");
            clues.Add(new(i, 1));
        }
        var picture = new string(pixels.Select(pixel => pixel == 0 ? '#' : '.').ToArray());
        return new(level.Sx, level.Sy, clues.ToArray(), attribution, solution.ToArray(), picture);
    }

    private static byte[] ReadLinkPixels(JsonElement data, int area)
    {
        if (!data.TryGetProperty("pixels33", out var packed))
        {
            var pixels = ReadBase64(data, "pixels");
            if (pixels.Length != area || pixels.Any(pixel => pixel is not (0 or 255)))
                throw new InvalidDataException("Invalid Filipino crossword bitmap.");
            return pixels;
        }
        if (packed.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid Filipino crossword bitmap.");
        var bytes = ReadBase64(data, "pixels33");
        if (bytes.Length != 2 * ((area + 2) / 3))
            throw new InvalidDataException("Invalid Filipino crossword bitmap length.");
        var result = new byte[area];
        for (var i = 0; i < area; i += 3)
        {
            var value = (int)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 * (i / 3)));
            for (var offset = 0; offset < 3; offset++)
            {
                var digit = value % 33;
                value /= 33;
                if (i + offset >= area && digit != 32 || digit is not (0 or 32))
                    throw new InvalidDataException("Unexpected Filipino crossword pixel.");
                if (i + offset < area)
                    result[i + offset] = digit == 32 ? (byte)255 : (byte)0;
            }
            if (value != 0)
                throw new InvalidDataException("Unexpected Filipino crossword pixel data.");
        }
        return result;
    }

    private static byte[] ReadBase64(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? Convert.FromBase64String(value.GetString()!) : throw new InvalidDataException("Missing encoded pixel data.");

    private static byte[] Inflate(string encoded, bool zlib)
    {
        using var input = new MemoryStream(Convert.FromBase64String(encoded));
        using Stream decoder = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = decoder.Read(buffer)) != 0)
        {
            if (output.Length + read > 2 * 1024 * 1024)
                throw new InvalidDataException("GrandGames puzzle data exceeds the decoded size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static int[] ReadRows(JsonElement grid, int width, int height, Func<JsonElement, int> read)
    {
        CheckGrid(grid, height, width);
        return grid.EnumerateArray().SelectMany(row => row.EnumerateArray().Select(read)).ToArray();
    }

    private static void CheckGrid(JsonElement grid, int outer, int inner)
    {
        if (grid.ValueKind != JsonValueKind.Array || grid.GetArrayLength() != outer ||
            grid.EnumerateArray().Any(line => line.ValueKind != JsonValueKind.Array || line.GetArrayLength() != inner))
            throw new InvalidDataException("Unexpected GrandGames grid dimensions.");
    }

    private static int ReadInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
        _ => throw new InvalidDataException("Unexpected GrandGames numeric value."),
    };

    private static int ReadTotal(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        // The number-puzzles player uses single hexadecimal digits for large totals.
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => throw new InvalidDataException("Unexpected placement total."),
    };

    private static void CheckSize(GrandGamesLevel level, int limit)
    {
        if (level.Sx < 1 || level.Sy < 1 || level.Sx > limit || level.Sy > limit || string.IsNullOrWhiteSpace(level.P))
            throw new InvalidDataException("Unsupported GrandGames board dimensions or missing puzzle data.");
    }
}
