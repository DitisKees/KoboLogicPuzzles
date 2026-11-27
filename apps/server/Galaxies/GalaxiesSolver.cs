using System.Diagnostics;

namespace KoboLogicPuzzles.Server.Galaxies;

internal sealed record GalaxiesProof(int Count, string? Solution, string Difficulty, string Solvability);

// Each variable is a cell and its domain is the set of centers it can belong to.
// A choice always includes its rotated partner. A center must reach every cell it owns.
internal sealed class GalaxiesSolver(GalaxiesBoard board, TimeSpan timeout, CancellationToken token)
{
    private readonly Stopwatch clock = new();
    private readonly int[] queue = new int[board.CellCount];
    private readonly int[] visited = new int[board.CellCount];
    private readonly List<(int Cell, int Center)> trail = [];
    private int stamp;
    private bool searched;
    private int count;
    private string? solution;

    internal GalaxiesProof CountSolutions()
    {
        clock.Start();
        CheckBudget();
        var domains = Enumerable.Range(0, board.CellCount).Select(_ => new HashSet<int>()).ToArray();
        var reachable = new int[board.Centers.Length][];
        for (var center = 0; center < board.Centers.Length; center++)
        {
            CheckBudget();
            var owner = center;
            var size = Flood(center, cell => BoardAllows(cell, owner));
            reachable[center] = queue[..size];
            foreach (var cell in reachable[center])
                domains[cell].Add(center);
        }

        var undecided = domains.Count(domain => domain.Count > 1);
        Search(domains, reachable, 0);
        var solvability = searched ? "requires_search" : undecided > 0 ? "advanced_logic" : "basic_logic";
        var difficulty = (searched, board.CellCount) switch
        {
            (true, >= 900) => "expert",
            (true, _) or (false, >= 1600) => "hard",
            (false, >= 225) => "medium",
            (false, _) when undecided >= 15 => "medium",
            _ => "easy",
        };
        CheckBudget();
        return new(count, solution, count == 1 ? difficulty : "unknown", count == 1 ? solvability : "unknown");
    }

    private bool BoardAllows(int cell, int center)
    {
        var mirror = board.Reflect(cell, center);
        return mirror >= 0 && (board.CoreOwners[cell] == -1 || board.CoreOwners[cell] == center) &&
            (board.CoreOwners[mirror] == -1 || board.CoreOwners[mirror] == center);
    }

    private int Flood(int center, Func<int, bool> allowed)
    {
        var start = board.Core[center][0];
        if (!allowed(start))
            return 0;
        var visit = ++stamp;
        visited[start] = visit;
        queue[0] = start;
        var end = 1;
        for (var i = 0; i < end; i++)
        {
            if ((i & 255) == 0)
                CheckBudget();
            foreach (var cell in board.Neighbors[queue[i]])
            {
                if (visited[cell] == visit || !allowed(cell))
                    continue;
                visited[cell] = visit;
                queue[end++] = cell;
            }
        }
        return end;
    }

    private void Search(HashSet<int>[] domains, int[][] reachable, int depth)
    {
        CheckBudget();
        if (depth > 256)
            throw new TimeoutException("Galaxies uniqueness proof reached its search depth limit.");
        if (!Propagate(domains, reachable))
            return;
        var next = -1;
        for (var cell = 0; cell < domains.Length; cell++)
            if (domains[cell].Count > 1 && (next < 0 || domains[cell].Count < domains[next].Count))
                next = cell;
        if (next < 0)
        {
            var answer = board.ToEdges(domains.Select(domain => domain.Single()).ToArray());
            if (board.IsSolved(answer))
            {
                count++;
                solution ??= answer;
            }
            return;
        }

        searched = true;
        var bookmark = trail.Count;
        foreach (var center in domains[next].ToArray())
        {
            Keep(domains, next, center);
            Search(domains, reachable, depth + 1);
            for (var i = trail.Count - 1; i >= bookmark; i--)
                domains[trail[i].Cell].Add(trail[i].Center);
            trail.RemoveRange(bookmark, trail.Count - bookmark);
            if (count >= 2)
                break;
        }
    }

    private bool Propagate(HashSet<int>[] domains, int[][] reachable)
    {
        bool changed;
        do
        {
            CheckBudget();
            changed = false;
            for (var cell = 0; cell < domains.Length; cell++)
            {
                var domain = domains[cell];
                changed |= domain.RemoveWhere(center =>
                {
                    if (domains[board.Reflect(cell, center)].Contains(center))
                        return false;
                    trail.Add((cell, center));
                    return true;
                }) != 0;
                if (domain.Count == 0)
                    return false;
                if (domain.Count != 1)
                    continue;

                var center = domain.Single();
                changed |= Keep(domains, board.Reflect(cell, center), center);
            }

            for (var center = 0; center < reachable.Length; center++)
            {
                var owner = center;
                if (Flood(center, cell => domains[cell].Contains(owner) &&
                    domains[board.Reflect(cell, owner)].Contains(owner)) == 0)
                    return false;
                foreach (var cell in reachable[center])
                {
                    if (visited[cell] == stamp || !domains[cell].Remove(center))
                        continue;
                    trail.Add((cell, center));
                    changed = true;
                }
            }
        } while (changed);
        return true;
    }

    private bool Keep(HashSet<int>[] domains, int cell, int owner) =>
        domains[cell].Count > 1 && domains[cell].RemoveWhere(center =>
        {
            if (center == owner)
                return false;
            trail.Add((cell, center));
            return true;
        }) != 0;

    private void CheckBudget()
    {
        token.ThrowIfCancellationRequested();
        if (clock.Elapsed >= timeout)
            throw new TimeoutException("Galaxies uniqueness proof timed out.");
    }
}
