using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace KoboLogicPuzzles.Server.Nurikabe;

public sealed record NurikabeMetadata(string Source, string SourceName, string SourceKind, string SourceId,
    string? SourceUrl, string? License, string? Seed, string? GeneratorVersion, string Difficulty, string Solvability, string? OriginalRating = null)
{
    public bool Unique => true;
}

public sealed record NurikabeDefinition(string Id, int Width, int Height, int[] Clues, string Title, string? Author,
    NurikabeMetadata Metadata, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solution)
    : GamePuzzleDefinition(Id, Width, Height);

internal sealed class NurikabeBoard
{
    internal const byte Unknown = 0, White = 1, Black = 2;
    public int Width { get; }
    public int Height { get; }
    public int[] Clues { get; }
    public int[][] Neighbors { get; }
    public int[][] Pools { get; }
    public int[][] CellPools { get; }
    public int SeaSize { get; }
    public int Size => Clues.Length;

    public NurikabeBoard(int width, int height, int[] clues)
    {
        if (width is < 1 or > 50 || height is < 1 or > 50 || clues.Length != width * height ||
            clues.Any(clue => clue < 0 || clue > clues.Length) || clues.Sum() <= 0 || clues.Sum() >= clues.Length)
            throw new InvalidDataException("Invalid Nurikabe dimensions or island areas.");

        Width = width;
        Height = height;
        Clues = clues;
        SeaSize = clues.Length - clues.Sum();
        Neighbors = new int[Size][];
        var pools = new List<int[]>();
        var cellPools = Enumerable.Range(0, Size).Select(_ => new List<int>()).ToArray();
        for (var cell = 0; cell < Size; cell++)
        {
            var row = cell / width;
            var col = cell % width;
            var neighbors = new List<int>(4);
            if (row > 0) neighbors.Add(cell - width);
            if (col > 0) neighbors.Add(cell - 1);
            if (col + 1 < width) neighbors.Add(cell + 1);
            if (row + 1 < height) neighbors.Add(cell + width);
            Neighbors[cell] = neighbors.ToArray();

            if (row + 1 == height || col + 1 == width)
                continue;
            int[] pool = [cell, cell + 1, cell + width, cell + width + 1];
            foreach (var corner in pool)
                cellPools[corner].Add(pools.Count);
            pools.Add(pool);
        }
        Pools = pools.ToArray();
        CellPools = cellPools.Select(items => items.ToArray()).ToArray();
    }

    public string Id()
    {
        var variants = new List<string>(8);
        // Rotate and reflect the clue grid so equivalent rectangular layouts share an ID.
        for (var transform = 0; transform < 8; transform++)
        {
            var transpose = (transform & 4) != 0;
            var columns = transpose ? Height : Width;
            var rows = transpose ? Width : Height;
            var cells = new int[Size];
            for (var cell = 0; cell < Size; cell++)
            {
                var row = transpose ? cell % Width : cell / Width;
                var col = transpose ? cell / Width : cell % Width;
                if ((transform & 1) != 0) row = rows - 1 - row;
                if ((transform & 2) != 0) col = columns - 1 - col;
                cells[row * columns + col] = Clues[cell];
            }

            variants.Add(FormattableString.Invariant($"{columns}x{rows}:") +
                string.Join(',', cells.Select(value => value.ToString(CultureInfo.InvariantCulture))));
        }

        var canonical = variants.Min(StringComparer.Ordinal)!;
        return PuzzleId.FromNurikabe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))).Value;
    }

    public bool ValidMarks(string marks) => marks.Length == Size && marks
        .Select((mark, i) => Clues[i] > 0 ? mark == 'x' : mark is '.' or '#' or 'x').All(valid => valid);

    public bool IsSolved(string marks) => ValidMarks(marks) && !marks.Contains('.') &&
        IsSolved(marks.Select(mark => mark == '#' ? Black : White).ToArray());

    public bool IsSolved(byte[] cells)
    {
        if (cells.Contains(Unknown) || Pools.Any(pool => pool.All(cell => cells[cell] == Black)))
            return false;

        var seen = new bool[Size];
        var seas = 0;
        var queue = new int[Size];
        for (var start = 0; start < Size; start++)
        {
            if (seen[start])
                continue;
            var color = cells[start];
            if (color == Black && ++seas > 1)
                return false;
            var clueArea = 0;
            var count = 1;
            seen[start] = true;
            queue[0] = start;
            for (var position = 0; position < count; position++)
            {
                var cell = queue[position];
                if (Clues[cell] > 0)
                {
                    if (clueArea > 0 || color != White)
                        return false;
                    clueArea = Clues[cell];
                }
                foreach (var other in Neighbors[cell])
                {
                    if (seen[other] || cells[other] != color)
                        continue;
                    seen[other] = true;
                    queue[count++] = other;
                }
            }
            if (color == White && clueArea != count)
                return false;
        }
        return seas == 1;
    }
}
