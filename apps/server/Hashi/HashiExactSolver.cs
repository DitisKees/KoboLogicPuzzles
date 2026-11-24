namespace KoboNonogram.Server.Hashi;

public enum HashiValidationStatus
{
    Unsatisfiable,
    Unique,
    Ambiguous,
    Timeout,
    SolutionMismatch,
}

public sealed record HashiValidationResult(HashiValidationStatus Status, int SolutionsFound, int Nodes,
    IReadOnlyList<byte>? Solution = null);

public sealed class HashiExactSolver(TimeProvider? clock = null)
{
    public const string Version = "hashi-exact-1";

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public HashiValidationResult Validate(HashiCandidate candidate, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = CountSolutions(candidate.Puzzle, timeout, cancellationToken);
        return result.Solution is not null && !result.Solution.SequenceEqual(candidate.Solution)
            ? result with { Status = HashiValidationStatus.SolutionMismatch, Solution = null }
            : result;
    }

    public HashiValidationResult CountSolutions(HashiPuzzle puzzle, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = new HashiDeadline(time, timeout, cancellationToken);
        var pending = new Stack<(byte[] Minimum, byte[] Maximum)>();
        var maximums = new byte[puzzle.Edges.Count];
        Array.Fill(maximums, (byte)2);
        pending.Push((new byte[maximums.Length], maximums));
        var nodes = 0;
        byte[]? solution = null;

        try
        {
            while (pending.TryPop(out var bounds))
            {
                deadline.Check();
                nodes++;
                var (minimum, maximum) = bounds;
                if (!Constrain(puzzle, minimum, maximum, deadline))
                    continue;

                var edge = ChooseEdge(puzzle, minimum, maximum);
                if (edge < 0)
                {
                    if (solution is not null)
                        return new HashiValidationResult(HashiValidationStatus.Ambiguous, 2, nodes);
                    solution = minimum;
                    continue;
                }

                // Split the domain into disjoint intervals so each complete assignment is counted once.
                var restMinimum = (byte[])minimum.Clone();
                restMinimum[edge]++;
                pending.Push((restMinimum, (byte[])maximum.Clone()));
                maximum[edge] = minimum[edge];
                pending.Push(bounds);
            }
        }
        catch (TimeoutException)
        {
            return new HashiValidationResult(HashiValidationStatus.Timeout, solution is null ? 0 : 1, nodes);
        }

        return solution is null
            ? new HashiValidationResult(HashiValidationStatus.Unsatisfiable, 0, nodes)
            : new HashiValidationResult(HashiValidationStatus.Unique, 1, nodes, Array.AsReadOnly(solution));
    }

    private static int ChooseEdge(HashiPuzzle puzzle, ReadOnlySpan<byte> minimum, ReadOnlySpan<byte> maximum)
    {
        var best = -1;
        var bestWidth = int.MaxValue;
        var mostCrossings = -1;
        for (var edge = 0; edge < minimum.Length; edge++)
        {
            var width = maximum[edge] - minimum[edge];
            var crossings = puzzle.Crossings[edge].Length;
            if (width == 0 || width > bestWidth || (width == bestWidth && crossings <= mostCrossings))
                continue;
            best = edge;
            bestWidth = width;
            mostCrossings = crossings;
        }
        return best;
    }

    private static bool Constrain(HashiPuzzle puzzle, byte[] minimum, byte[] maximum, HashiDeadline deadline)
    {
        while (true)
        {
            deadline.Check();
            var changed = false;
            for (var island = 0; island < puzzle.Islands.Count; island++)
            {
                var clue = puzzle.Islands[island].Clue;
                var neighbors = puzzle.Neighbors[island];
                var low = 0;
                var high = 0;
                foreach (var (edge, _) in neighbors)
                {
                    low += minimum[edge];
                    high += maximum[edge];
                }
                if (clue < low || clue > high)
                    return false;

                foreach (var (edge, _) in neighbors)
                {
                    var lower = Math.Max(minimum[edge], clue - high + maximum[edge]);
                    var upper = Math.Min(maximum[edge], clue - low + minimum[edge]);
                    if (lower > upper)
                        return false;
                    if (lower == minimum[edge] && upper == maximum[edge])
                        continue;
                    minimum[edge] = (byte)lower;
                    maximum[edge] = (byte)upper;
                    changed = true;
                }
            }

            for (var edge = 0; edge < minimum.Length; edge++)
            {
                if (minimum[edge] == 0)
                    continue;
                foreach (var other in puzzle.Crossings[edge])
                {
                    if (minimum[other] != 0)
                        return false;
                    if (maximum[other] != 0)
                    {
                        maximum[other] = 0;
                        changed = true;
                    }
                }
            }
            if (changed)
                continue;

            var cuts = puzzle.FindCuts(maximum);
            if (cuts is null)
                return false;
            foreach (var (edge, count) in cuts)
            {
                if (count < minimum[edge] || count > maximum[edge])
                    return false;
                if (count == minimum[edge] && count == maximum[edge])
                    continue;
                minimum[edge] = maximum[edge] = count;
                changed = true;
            }
            if (!changed)
                return true;
        }
    }
}
