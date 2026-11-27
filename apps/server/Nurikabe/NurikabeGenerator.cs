namespace KoboLogicPuzzles.Server.Nurikabe;

internal static class NurikabeGenerator
{
    public const string Source = "local";
    public const string SourceName = "KoboLogicPuzzles";
    public const string Version = "1";
    public static readonly int[] Sizes = [25, 30, 35, 40, 45, 50];
    public static readonly string[] Difficulties = ["easy", "medium", "hard", "expert"];
    public static readonly string[] Solvabilities = ["basic_logic", "advanced_logic", "bounded_lookahead", "requires_search"];

    public static (NurikabeBoard Board, string Solution) Candidate(int size, int seed, CancellationToken token)
    {
        if (seed % 10 != 0)
            return BandedCandidate(size, seed, token);

        // Start with one water cell and extend the connected sea without creating any pools.
        // Growth ends when every shoreline cell would complete a pool.
        var layout = new NurikabeBoard(size, size, [1, .. new int[size * size - 1]]);
        var random = new Random(seed);
        var sea = new bool[layout.Size];
        var queued = new bool[layout.Size];
        var frontier = new List<int>();
        var start = random.Next(layout.Size);
        sea[start] = true;
        EnqueueNeighbors(start);
        while (frontier.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var index = random.Next(frontier.Count);
            var cell = frontier[index];
            frontier[index] = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (layout.CellPools[cell].Any(pool => layout.Pools[pool].Count(corner => sea[corner]) == 3))
                continue;
            sea[cell] = true;
            EnqueueNeighbors(cell);
        }

        var clues = new int[layout.Size];
        var seen = new bool[layout.Size];
        var island = new List<int>();
        for (var cell = 0; cell < layout.Size; cell++)
        {
            if (sea[cell] || seen[cell])
                continue;
            island.Clear();
            island.Add(cell);
            seen[cell] = true;
            for (var index = 0; index < island.Count; index++)
                foreach (var next in layout.Neighbors[island[index]])
                {
                    if (sea[next] || seen[next])
                        continue;
                    seen[next] = true;
                    island.Add(next);
                }

            clues[island[random.Next(island.Count)]] = island.Count;
        }

        return (new NurikabeBoard(size, size, clues), new string(sea.Select(water => water ? '#' : 'x').ToArray()));

        void EnqueueNeighbors(int cell)
        {
            foreach (var neighbor in layout.Neighbors[cell])
            {
                if (sea[neighbor] || queued[neighbor])
                    continue;
                queued[neighbor] = true;
                frontier.Add(neighbor);
            }
        }
    }
    private static (NurikabeBoard Board, string Solution) BandedCandidate(int size, int seed, CancellationToken token)
    {
        var random = new Random(seed);
        var clues = new int[size * size];
        var multiCellPercent = 1 + (int)((uint)seed / 10 % 4);
        var solution = Enumerable.Repeat('#', clues.Length).ToArray();
        for (var row = 0; row < size; row += 2)
        {
            token.ThrowIfCancellationRequested();
            var col = random.Next(2);
            while (col < size)
            {
                var length = Math.Min(size - col, random.Next(100) < multiCellPercent ? random.Next(2, 6) : 1);
                var start = row * size + col;
                Array.Fill(solution, 'x', start, length);
                clues[start + random.Next(length)] = length;
                col += length + 1;
            }
        }

        // Vary the orientation without changing island or sea connectivity.
        if (random.Next(2) == 0)
            for (var row = 0; row < size; row++)
                for (var col = row + 1; col < size; col++)
                {
                    var a = row * size + col;
                    var b = col * size + row;
                    (clues[a], clues[b]) = (clues[b], clues[a]);
                    (solution[a], solution[b]) = (solution[b], solution[a]);
                }

        return (new NurikabeBoard(size, size, clues), new string(solution));
    }

}
