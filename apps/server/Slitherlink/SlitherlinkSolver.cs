namespace KoboLogicPuzzles.Server.Slitherlink;

// For imported boards, search all single loops up to a second solution.
internal sealed class SlitherlinkSolver(SlitherlinkDefinition puzzle, CancellationToken cancellationToken)
{
    private readonly int[][] incident = IncidentEdges(puzzle);
    private readonly sbyte[] marks = Enumerable.Repeat((sbyte)-1, puzzle.Edges.Length).ToArray();
    private readonly List<int> trail = [];
    private readonly int[] visited = new int[puzzle.Vertices.Length];
    private readonly Stack<int> pending = new();
    private int visit;

    public (int Count, string? Solution) Search()
    {
        var branches = new Stack<(int Edge, int TrailLength, bool TriedOff)>();
        string? solution = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Propagate())
            {
                var edge = ChooseEdge();
                if (edge >= 0)
                {
                    branches.Push((edge, trail.Count, false));
                    Assign(edge, 1);
                    continue;
                }
                var candidate = new string(marks.Select(mark => mark == 1 ? '#' : '.').ToArray());
                if (puzzle.IsComplete(candidate))
                {
                    if (solution is not null)
                        return (2, solution);
                    solution = candidate;
                }
            }

            var resumed = false;
            while (branches.TryPop(out var branch))
            {
                while (trail.Count > branch.TrailLength)
                {
                    marks[trail[^1]] = -1;
                    trail.RemoveAt(trail.Count - 1);
                }
                if (branch.TriedOff)
                    continue;
                branches.Push((branch.Edge, branch.TrailLength, true));
                Assign(branch.Edge, 0);
                resumed = true;
                break;
            }
            if (!resumed)
                return (solution is null ? 0 : 1, solution);
        }
    }

    private bool Propagate()
    {
        int before;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            before = trail.Count;
            foreach (var face in puzzle.Faces)
            {
                if (face.Clue is not { } clue)
                    continue;
                var (on, unknown) = Count(face.Edges);
                if (on > clue || on + unknown < clue)
                    return false;
                if (on == clue)
                    Fill(face.Edges, 0);
                else if (on + unknown == clue)
                    Fill(face.Edges, 1);
            }
            foreach (var edges in incident)
            {
                var (on, unknown) = Count(edges);
                if (on > 2 || (on == 1 && unknown == 0))
                    return false;
                if (on == 2 || (on == 0 && unknown == 1))
                    Fill(edges, 0);
                else if (on == 1 && unknown == 1)
                    Fill(edges, 1);
            }
        } while (trail.Count != before);

        return CheckClosedLoop();
    }

    private bool CheckClosedLoop()
    {
        // A closed component has no free exit. It must be the entire loop.
        var lines = 0;
        foreach (var mark in marks)
            if (mark == 1)
                lines++;
        if (lines == 0)
            return true;
        visit++;
        for (var vertex = 0; vertex < incident.Length; vertex++)
        {
            if (visited[vertex] == visit || !incident[vertex].Any(edge => marks[edge] == 1))
                continue;
            pending.Push(vertex);
            visited[vertex] = visit;
            var closed = true;
            var component = 0;
            while (pending.TryPop(out var current))
            {
                var degree = 0;
                foreach (var index in incident[current])
                {
                    if (marks[index] != 1)
                        continue;
                    degree++;
                    var edge = puzzle.Edges[index];
                    var next = edge.First == current ? edge.Second : edge.First;
                    if (visited[next] == visit)
                        continue;
                    pending.Push(next);
                    visited[next] = visit;
                }
                closed &= degree == 2;
                component += degree;
            }
            if (!closed)
                continue;
            if (component != 2 * lines)
                return false;
            foreach (var face in puzzle.Faces)
                if (face.Clue is { } clue && Count(face.Edges).On != clue)
                    return false;
            for (var i = 0; i < marks.Length; i++)
                if (marks[i] < 0)
                    Assign(i, 0);
            return true;
        }
        return true;
    }

    private int ChooseEdge()
    {
        var best = Array.IndexOf(marks, (sbyte)-1);
        if (best < 0)
            return best;

        var bestScore = int.MaxValue;
        foreach (var face in puzzle.Faces)
            if (face.Clue is not null)
                Consider(face.Edges, 0, false);
        foreach (var edges in incident)
            Consider(edges, 1, true);
        return best;

        void Consider(int[] edges, int penalty, bool needsLine)
        {
            var (on, unknown) = Count(edges);
            var score = 2 * unknown + penalty;
            if (unknown == 0 || score >= bestScore || (needsLine && on == 0))
                return;
            bestScore = score;
            best = Array.Find(edges, edge => marks[edge] < 0);
        }
    }

    private (int On, int Unknown) Count(int[] edges)
    {
        int on = 0, unknown = 0;
        foreach (var edge in edges)
        {
            if (marks[edge] == 1)
                on++;
            else if (marks[edge] < 0)
                unknown++;
        }
        return (on, unknown);
    }

    private void Fill(int[] edges, sbyte mark)
    {
        foreach (var edge in edges)
            if (marks[edge] < 0)
                Assign(edge, mark);
    }

    private void Assign(int edge, sbyte mark)
    {
        marks[edge] = mark;
        trail.Add(edge);
    }

    private static int[][] IncidentEdges(SlitherlinkDefinition puzzle)
    {
        var edges = Enumerable.Range(0, puzzle.Vertices.Length).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < puzzle.Edges.Length; i++)
        {
            edges[puzzle.Edges[i].First].Add(i);
            edges[puzzle.Edges[i].Second].Add(i);
        }
        return edges.Select(list => list.ToArray()).ToArray();
    }
}
