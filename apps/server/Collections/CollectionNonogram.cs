using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace KoboLogicPuzzles.Server;

internal static class CollectionNonogram
{
    public static NonogramPuzzleDefinition Create(int width, int height, string solution, PublishedPuzzle attribution,
        CancellationToken token)
    {
        if (width is < 1 or > 100 || height is < 1 or > 100 || solution.Length != width * height ||
            solution.Any(mark => mark is not ('.' or '#')) || !solution.Contains('#'))
            throw new InvalidDataException("Expected a nonempty black-and-white Nonogram grid.");

        var rows = Enumerable.Range(0, height).Select(row => Runs(row * width, width, 1)).ToArray();
        var columns = Enumerable.Range(0, width).Select(column => Runs(column, height, width)).ToArray();
        var proof = Prove(width, height, rows, columns, token);
        if (proof != solution)
            throw new InvalidDataException("The Nonogram could not be uniquely proved by line deductions.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes<object[]>(
            ["nonogram", 1, width, height, rows, columns])));
        var metadata = new PuzzleMetadata(null, null, 2, attribution.Source, attribution.SourceName, "collection",
            attribution.SourceId, attribution.SourceUrl, attribution.License);
        return new(PuzzleId.FromNonogramCollection(hash).Value, width, height, attribution.Title,
            attribution.Author, rows, columns, solution, metadata);

        int[] Runs(int start, int length, int stride)
        {
            var runs = new List<int>();
            var count = 0;
            for (var offset = 0; offset < length; offset++)
            {
                if (solution[start + offset * stride] == '#')
                    count++;
                else if (count > 0)
                {
                    runs.Add(count);
                    count = 0;
                }
            }
            if (count > 0)
                runs.Add(count);
            return runs.ToArray();
        }
    }

    internal static string? Prove(int width, int height, int[][] rows, int[][] columns, CancellationToken token)
    {
        var cells = Enumerable.Repeat('?', width * height).ToArray();
        var deadline = Stopwatch.StartNew();
        var changed = true;
        while (changed)
        {
            token.ThrowIfCancellationRequested();
            if (deadline.Elapsed >= TimeSpan.FromSeconds(3))
                return null;
            changed = false;
            for (var row = 0; row < height; row++)
                if (!Constrain(rows[row], row * width, width, 1))
                    return null;
            for (var column = 0; column < width; column++)
                if (!Constrain(columns[column], column, height, width))
                    return null;
        }
        return cells.Contains('?') ? null : new string(cells);

        bool Constrain(int[] clues, int start, int length, int stride)
        {
            var line = new char[length];
            for (var i = 0; i < length; i++)
                line[i] = cells[start + i * stride];
            if (!line.Contains('?'))
                return MatchesCompletedLine(line, clues);

            var knownWhite = new int[length + 1];
            for (var i = 0; i < length; i++)
                knownWhite[i + 1] = knownWhite[i] + (line[i] == '.' ? 1 : 0);

            // Nodes encode the next cell and the number of runs already placed.
            // A blank consumes one cell. A run consumes its cells and a separator unless it reaches the boundary.
            var forward = new bool[length + 1, clues.Length + 1];
            var backward = new bool[length + 1, clues.Length + 1];
            forward[0, 0] = backward[length, clues.Length] = true;
            for (var cell = 0; cell < length; cell++)
                for (var run = 0; run <= clues.Length; run++)
                {
                    if (!forward[cell, run])
                        continue;
                    if (line[cell] != '#') forward[cell + 1, run] = true;
                    if (End(cell, run) is { } end)
                        forward[Next(end), run + 1] = true;
                }
            if (!forward[length, clues.Length])
                return false;

            for (var cell = length - 1; cell >= 0; cell--)
                for (var run = 0; run <= clues.Length; run++)
                    backward[cell, run] = line[cell] != '#' && backward[cell + 1, run] ||
                        End(cell, run) is { } end && backward[Next(end), run + 1];

            var canBeWhite = new bool[length];
            var blackRanges = new int[length + 1];
            for (var cell = 0; cell < length; cell++)
                for (var run = 0; run <= clues.Length; run++)
                {
                    if (!forward[cell, run])
                        continue;
                    if (line[cell] != '#' && backward[cell + 1, run])
                        canBeWhite[cell] = true;
                    if (End(cell, run) is { } end && backward[Next(end), run + 1])
                    {
                        blackRanges[cell]++;
                        blackRanges[end]--;
                        if (end < length)
                            canBeWhite[end] = true;
                    }
                }

            var black = 0;
            for (var cell = 0; cell < length; cell++)
            {
                black += blackRanges[cell];
                if (line[cell] == '?' && (black == 0 || !canBeWhite[cell]))
                {
                    cells[start + cell * stride] = black == 0 ? '.' : '#';
                    changed = true;
                }
            }
            return true;

            int Next(int end) => end < length ? end + 1 : end;

            int? End(int cell, int run)
            {
                if (run == clues.Length)
                    return null;
                var end = cell + clues[run];
                return end <= length && knownWhite[end] == knownWhite[cell] && (end == length || line[end] != '#')
                    ? end : null;
            }
        }
    }

    private static bool MatchesCompletedLine(char[] line, int[] clues)
    {
        var run = 0;
        var next = 0;
        foreach (var cell in line.Append('.'))
        {
            if (cell == '#')
                run++;
            else if (run > 0)
            {
                if (next == clues.Length || clues[next++] != run)
                    return false;
                run = 0;
            }
        }
        return next == clues.Length;
    }
}
