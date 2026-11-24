using System.Globalization;

namespace KoboNonogram.Server.Hashi;

public sealed class HashiCandidate
{
    private HashiCandidate(HashiPuzzle puzzle, byte[] solution)
    {
        Puzzle = puzzle;
        Solution = Array.AsReadOnly(solution);
    }

    public HashiPuzzle Puzzle { get; }
    public IReadOnlyList<byte> Solution { get; }

    public static HashiCandidate ParseBridges(int size, string description, string moves)
    {
        if (size is < 20 or > 50 || size % 5 != 0 || description.Length > size * size || moves.Length > 40 * size * size)
            throw new FormatException("Invalid Bridges grid size or encoding.");

        var islands = new List<HashiIsland>();
        var cell = 0;
        foreach (var character in description)
        {
            if (character is >= 'a' and <= 'z')
                cell += character - 'a' + 1;
            else if (character is >= '1' and <= '8' && cell < size * size)
            {
                islands.Add(new HashiIsland(cell % size, cell / size, character - '0'));
                cell++;
            }
            else
                throw new FormatException("Invalid Bridges clue or empty run.");
        }
        if (cell != size * size || islands.Count < 2)
            throw new FormatException("Bridges clue layout has the wrong dimensions or too few islands.");

        var puzzle = HashiPuzzle.Create(size, size, islands);
        var coordinates = puzzle.Islands.Select((island, index) => (island, index))
            .ToDictionary(pair => (pair.island.X, pair.island.Y), pair => pair.index);
        var edges = puzzle.Edges.Select((edge, index) => (edge, index)).ToDictionary(pair => pair.edge, pair => pair.index);
        var solution = new byte[puzzle.Edges.Count];
        var commands = moves.Split(';');
        if (commands[0] != "S")
            throw new FormatException("Invalid Bridges solution prefix.");

        Span<int> values = stackalloc int[5];
        foreach (var command in commands.AsSpan(1))
        {
            if (command.Length < 2)
                throw new FormatException("Invalid Bridges solution move.");
            var fields = command[1..].Split(',');
            var expected = command[0] switch { 'L' => 5, 'N' => 4, 'M' => 2, _ => 0 };
            if (fields.Length != expected)
                throw new FormatException("Invalid Bridges solution move.");
            for (var index = 0; index < fields.Length; index++)
                if (!int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                    throw new FormatException("Invalid Bridges solution move value.");

            // Island and no-bridge annotations are omitted from the complete edge assignment.
            if (command[0] != 'L')
                continue;
            if (!coordinates.TryGetValue((values[0], values[1]), out var first) ||
                !coordinates.TryGetValue((values[2], values[3]), out var second) || values[4] is < 1 or > 2 ||
                !edges.TryGetValue(new HashiEdge(Math.Min(first, second), Math.Max(first, second)), out var edge) ||
                solution[edge] != 0)
                throw new FormatException("Invalid or repeated bridge in solution.");
            solution[edge] = (byte)values[4];
        }

        if (!puzzle.IsSolution(solution))
            throw new FormatException("Bridges solution must satisfy the clues, crossing rules, and connectivity.");
        return new HashiCandidate(puzzle, solution);
    }
}
