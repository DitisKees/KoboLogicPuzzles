using System.Diagnostics;

namespace KoboLogicPuzzles.Server.LinkAPix;

internal sealed record LinkProof(int Count, int[][]? Paths, string Difficulty, string Solvability, int Work);

internal sealed class LinkAPixSolver(LinkAPixBoard board, TimeSpan timeout, CancellationToken token)
{
    private const int CandidateLimit = 96;
    private const int WorkLimit = 8_000_000;
    private const int MaxProofLength = 512;
    private const int MaxSearchDepth = 96;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly LinkClue[] endpoints = board.Clues.Where(c => c.Length > 1).OrderBy(c => c.Length).ToArray();
    private readonly Dictionary<(int Length, string? Color), LinkClue[]> classes = board.Clues
        .Where(c => c.Length > 1).GroupBy(c => (c.Length, c.Color))
        .ToDictionary(group => group.Key, group => group.ToArray());
    private readonly bool[] occupied = new bool[board.Area];
    private readonly int[] distances = new int[board.Area];
    private readonly int[] queue = new int[board.Area];
    private readonly bool[] visited = new bool[board.Area];
    private readonly List<int[]> chosen = [];
    private int[][]? solution;
    private int count, work, maximumDepth, maximumAlternatives;

    internal LinkProof CountSolutions()
    {
        CheckBudget();
        if (endpoints.Any(c => c.Length > MaxProofLength))
            throw new TimeoutException("A clue exceeds the Link-a-Pix path proof bound.");
        Search(0);
        CheckBudget();

        var longest = endpoints.LastOrDefault()?.Length ?? 1;
        var difficulty = longest >= 20 || maximumDepth >= 3 ? "expert" :
            longest >= 10 || maximumDepth > 0 || maximumAlternatives >= 8 ? "hard" :
            longest > 2 ? "medium" : "easy";
        var solvability = (maximumDepth, longest) switch
        {
            (0, <= 2) => "basic_logic",
            (0, <= 8) => "advanced_logic",
            (1, <= 8) => "bounded_lookahead",
            _ => "unknown",
        };
        return new(count, solution, difficulty, solvability, work);
    }

    private void Search(int depth)
    {
        if (depth > MaxSearchDepth)
            throw new TimeoutException("Link-a-Pix branching depth reached its proof bound.");
        maximumDepth = Math.Max(maximumDepth, depth);
        var initial = chosen.Count;
        try
        {
            var first = 0;
            while (count < 2)
            {
                while (first < endpoints.Length && occupied[endpoints[first].Cell])
                    first++;
                List<int[]>? candidates = null;
                var unresolved = false;
                for (var i = first; i < endpoints.Length; i++)
                {
                    var endpoint = endpoints[i];
                    if (occupied[endpoint.Cell])
                        continue;
                    unresolved = true;
                    var cap = candidates?.Count ?? CandidateLimit;
                    var options = PathsFrom(endpoint, cap);
                    if (options.Count == 0)
                        return;
                    if (options.Count >= cap)
                        continue; // Another clue already has at least as few choices.
                    candidates = options;
                    if (candidates.Count == 1)
                        break;
                }
                if (!unresolved)
                {
                    count++;
                    solution ??= LinkAPixBoard.CanonicalPaths(chosen);
                    return;
                }
                if (candidates is null)
                    throw new TimeoutException("Too many routes remain for every unresolved Link-a-Pix clue.");
                if (candidates.Count == 1)
                {
                    Place(candidates[0]);
                    continue;
                }

                maximumAlternatives = Math.Max(maximumAlternatives, candidates.Count);
                foreach (var path in candidates)
                {
                    Place(path);
                    Search(depth + 1);
                    RemoveLast();
                    if (count >= 2)
                        break;
                }
                return;
            }
        }
        finally
        {
            while (chosen.Count > initial)
                RemoveLast();
        }
    }

    private List<int[]> PathsFrom(LinkClue start, int limit)
    {
        Tick();
        var options = new List<int[]>();
        if (start.Length == 2)
        {
            foreach (var cell in board.Neighbors[start.Cell])
                if (cell >= 0 && !occupied[cell] && board.ByCell[cell] is { } end && LinkAPixBoard.SameClass(start, end))
                    options.Add([start.Cell, cell]);
            return options;
        }

        // Distance to the nearest compatible endpoint gives an admissible lower
        // bound ignoring only the cells already walked by this candidate.
        var destinations = 0;
        Array.Fill(distances, -1);
        foreach (var end in classes[(start.Length, start.Color)])
        {
            Tick();
            if (end.Cell == start.Cell || occupied[end.Cell])
                continue;
            var distance = Math.Abs(end.Cell / board.Width - start.Cell / board.Width) +
                Math.Abs(end.Cell % board.Width - start.Cell % board.Width);
            var steps = start.Length - 1;
            if (distance > steps || (steps - distance) % 2 != 0)
                continue;
            distances[end.Cell] = 0;
            queue[destinations++] = end.Cell;
        }
        for (var front = 0; front < destinations; front++)
        {
            Tick();
            var cell = queue[front];
            var distance = distances[cell] + 1;
            if (distance >= start.Length)
                break;
            foreach (var next in board.Neighbors[cell])
            {
                if (next < 0 || occupied[next] || distances[next] >= 0 || next != start.Cell && board.ByCell[next] is not null)
                    continue;
                distances[next] = distance;
                queue[destinations++] = next;
            }
        }
        if (distances[start.Cell] < 0)
            return options;

        var path = new int[start.Length];
        Walk(start.Cell, 0);
        return options;

        void Walk(int cell, int offset)
        {
            Tick();
            path[offset] = cell;
            if (offset + 1 == path.Length)
            {
                options.Add((int[])path.Clone());
                return;
            }
            visited[cell] = true;
            var remaining = path.Length - offset - 2;
            foreach (var next in board.Neighbors[cell])
            {
                if (next < 0 || visited[next] || distances[next] < 0 || distances[next] > remaining ||
                    (distances[next] == 0) != (remaining == 0))
                    continue;
                Walk(next, offset + 1);
                if (options.Count >= limit)
                    break;
            }
            visited[cell] = false;
        }
    }

    private void Place(int[] path)
    {
        chosen.Add(path);
        foreach (var cell in path) occupied[cell] = true;
    }

    private void RemoveLast()
    {
        foreach (var cell in chosen[^1]) occupied[cell] = false;
        chosen.RemoveAt(chosen.Count - 1);
    }

    private void Tick()
    {
        if ((++work & 511) == 0)
            CheckBudget();
    }

    private void CheckBudget()
    {
        token.ThrowIfCancellationRequested();
        if (clock.Elapsed >= timeout || work > WorkLimit)
            throw new TimeoutException("The Link-a-Pix verification budget was exhausted.");
    }
}
