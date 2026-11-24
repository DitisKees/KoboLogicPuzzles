using System.Security.Cryptography;
using System.Text.Json;

namespace KoboNonogram.Server.Hashi;

public readonly record struct HashiIsland(int X, int Y, int Clue);

public readonly record struct HashiEdge(int First, int Second);

internal readonly record struct HashiNeighbor(int Edge, int Island);

internal readonly record struct HashiCut(int Edge, byte Count);

public sealed class HashiPuzzle
{
    private HashiPuzzle(int width, int height, HashiIsland[] islands, HashiEdge[] edges,
        HashiNeighbor[][] neighbors, int[][] crossings)
    {
        Width = width;
        Height = height;
        Islands = Array.AsReadOnly(islands);
        Edges = Array.AsReadOnly(edges);
        Neighbors = neighbors;
        Crossings = crossings;

        var coordinates = islands.Select(island => new[] { island.X, island.Y, island.Clue }).ToArray();
        var canonical = JsonSerializer.SerializeToUtf8Bytes<object[]>(["hashi", 1, width, height, coordinates]);
        ContentHash = Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<HashiIsland> Islands { get; }
    public IReadOnlyList<HashiEdge> Edges { get; }
    public string ContentHash { get; }
    internal HashiNeighbor[][] Neighbors { get; }
    internal int[][] Crossings { get; }

    public static HashiPuzzle Create(int width, int height, IEnumerable<HashiIsland> clues)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 50);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 50);

        var islands = clues.OrderBy(island => island.X).ThenBy(island => island.Y).ToArray();
        if (islands.Length < 2 || islands.Any(island => island.X < 0 || island.X >= width ||
                island.Y < 0 || island.Y >= height || island.Clue is < 1 or > 8))
            throw new ArgumentException("Supply at least two in-bounds islands with clues from 1 to 8.", nameof(clues));

        var lastInRow = new Dictionary<int, int>();
        var lastInColumn = new Dictionary<int, int>();
        var edges = new List<HashiEdge>();
        for (var index = 0; index < islands.Length; index++)
        {
            var island = islands[index];
            if (lastInRow.TryGetValue(island.Y, out var rowNeighbor))
            {
                if (islands[rowNeighbor].X == island.X)
                    throw new ArgumentException("Island coordinates must be unique.", nameof(clues));
                edges.Add(new HashiEdge(rowNeighbor, index));
            }
            if (lastInColumn.TryGetValue(island.X, out var columnNeighbor))
                edges.Add(new HashiEdge(columnNeighbor, index));
            lastInRow[island.Y] = index;
            lastInColumn[island.X] = index;
        }

        var orderedEdges = edges.OrderBy(edge => edge.First).ThenBy(edge => edge.Second).ToArray();
        var neighbors = islands.Select(_ => new List<HashiNeighbor>()).ToArray();
        var crossings = orderedEdges.Select(_ => new List<int>()).ToArray();
        var horizontal = new Dictionary<(int X, int Y), int>();
        var vertical = new Dictionary<(int X, int Y), int>();

        for (var index = 0; index < orderedEdges.Length; index++)
        {
            var (first, second) = orderedEdges[index];
            neighbors[first].Add(new HashiNeighbor(index, second));
            neighbors[second].Add(new HashiNeighbor(index, first));
            var (x1, y1, _) = islands[first];
            var (x2, y2, _) = islands[second];
            if (y1 == y2)
            {
                for (var x = x1 + 1; x < x2; x++)
                    horizontal.Add((x, y1), index);
            }
            else
            {
                for (var y = y1 + 1; y < y2; y++)
                    vertical.Add((x1, y), index);
            }
        }

        foreach (var (cell, horizontalEdge) in horizontal)
        {
            if (!vertical.TryGetValue(cell, out var verticalEdge))
                continue;
            crossings[horizontalEdge].Add(verticalEdge);
            crossings[verticalEdge].Add(horizontalEdge);
        }

        return new HashiPuzzle(width, height, islands, orderedEdges,
            neighbors.Select(list => list.ToArray()).ToArray(), crossings.Select(list => list.Order().ToArray()).ToArray());
    }

    public bool IsSolution(ReadOnlySpan<byte> bridges)
    {
        if (bridges.Length != Edges.Count)
            return false;

        var degrees = new int[Islands.Count];
        for (var index = 0; index < bridges.Length; index++)
        {
            var count = bridges[index];
            if (count > 2)
                return false;
            if (count == 0)
                continue;
            foreach (var other in Crossings[index])
                if (bridges[other] != 0)
                    return false;
            var (first, second) = Edges[index];
            degrees[first] += count;
            degrees[second] += count;
        }

        for (var index = 0; index < Islands.Count; index++)
            if (Islands[index].Clue != degrees[index])
                return false;

        var reached = new bool[Islands.Count];
        reached[0] = true;
        var pending = new Stack<int>();
        pending.Push(0);
        var visited = 1;
        while (pending.TryPop(out var current))
        {
            foreach (var (edge, other) in Neighbors[current])
            {
                if (bridges[edge] == 0 || reached[other])
                    continue;
                reached[other] = true;
                visited++;
                pending.Push(other);
            }
        }
        return visited == Islands.Count;
    }

    // A sole exit must carry one or two bridges according to the clue parity on one side.
    internal List<HashiCut>? FindCuts(ReadOnlySpan<byte> allowed)
    {
        var discovered = new int[Islands.Count];
        Array.Fill(discovered, -1);
        discovered[0] = 0;
        var low = new int[Islands.Count];
        var totals = Islands.Select(island => island.Clue).ToArray();
        var nextNeighbor = new int[Islands.Count];
        var parentEdge = new int[Islands.Count];
        parentEdge[0] = -1;
        var cuts = new List<HashiCut>();
        var stack = new Stack<int>();
        stack.Push(0);
        var visited = 1;

        while (stack.TryPeek(out var island))
        {
            if (nextNeighbor[island] == Neighbors[island].Length)
            {
                stack.Pop();
                if (stack.TryPeek(out var parent))
                {
                    low[parent] = Math.Min(low[parent], low[island]);
                    totals[parent] += totals[island];
                    if (low[island] > discovered[parent])
                        cuts.Add(new HashiCut(parentEdge[island], (byte)(totals[island] % 2 == 1 ? 1 : 2)));
                }
                continue;
            }

            var (edge, other) = Neighbors[island][nextNeighbor[island]++];
            if (allowed[edge] == 0 || edge == parentEdge[island])
                continue;
            if (discovered[other] >= 0)
            {
                low[island] = Math.Min(low[island], discovered[other]);
                continue;
            }

            parentEdge[other] = edge;
            discovered[other] = low[other] = visited++;
            stack.Push(other);
        }

        return visited == Islands.Count ? cuts : null;
    }
}
