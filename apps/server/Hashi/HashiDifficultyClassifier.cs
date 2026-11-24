namespace KoboNonogram.Server.Hashi;

public enum HashiSolvability
{
    BasicLogic,
    AdvancedLogic,
    BoundedLookahead,
    RequiresSearch,
    Unknown,
}

public enum HashiDifficulty { Easy, Medium, Hard, Expert }

public enum HashiTechnique
{
    IslandQuota,
    CrossingExclusion,
    AvoidIsolatedPair,
    BridgeBudget,
    SoleExit,
    AvoidIsolatedGroup,
    Lookahead,
}

// Each mask has one bit per allowed count, with bit 0 representing no bridge.
public readonly record struct HashiEdgeChange(int Edge, byte Before, byte After);

public sealed record HashiDeduction(HashiTechnique Technique, IReadOnlyList<int> Subject,
    IReadOnlyList<HashiEdgeChange> Changes, string? Reason);

public sealed record HashiClassification(HashiSolvability Solvability, HashiDifficulty? Difficulty, int? Score,
    HashiTechnique? HardestTechnique, IReadOnlyDictionary<HashiTechnique, int> Deductions, int LookaheadDepth,
    int LookaheadTrials, IReadOnlyList<HashiDeduction> Trace, IReadOnlyList<byte>? Solution, string? Reason);

public sealed class HashiDifficultyClassifier(TimeProvider? clock = null)
{
    public const string Version = "hashi-logic-1";

    private const string TechniquesExhausted = "enabled deductions exhausted";
    private static readonly byte[][] Values = [[], [0], [1], [0, 1], [2], [0, 2], [1, 2], [0, 1, 2]];
    private static readonly (int Floor, int Weight)[] TechniqueScores =
        [(0, 1), (8, 2), (8, 1), (30, 3), (40, 4), (45, 5), (55, 8)];
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    private readonly record struct Restriction(int Edge, byte Allowed);

    private sealed class Contradiction(string message) : Exception(message);

    public HashiClassification Classify(HashiPuzzle puzzle, TimeSpan timeout, bool allowLookahead = true,
        CancellationToken cancellationToken = default)
    {
        var deadline = new HashiDeadline(time, timeout, cancellationToken);
        var domains = new byte[puzzle.Edges.Count];
        Array.Fill(domains, (byte)7);
        var trace = new List<HashiDeduction>();
        var trials = 0;
        byte[]? solution = null;
        string? reason = null;

        try
        {
            while (true)
            {
                Deduce(puzzle, domains, deadline, trace);
                if (domains.All(mask => Values[mask].Length == 1))
                {
                    solution = Array.ConvertAll(domains, mask => Values[mask][0]);
                    break;
                }
                if (!allowLookahead || !ApplyLookahead(puzzle, domains, deadline, trace, ref trials))
                {
                    reason = TechniquesExhausted;
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            reason = "classification deadline reached";
        }
        catch (Contradiction error)
        {
            reason = error.Message;
        }

        var counts = trace.GroupBy(step => step.Technique).ToDictionary(group => group.Key, group => group.Count());
        var hardest = counts.Count > 0 ? (HashiTechnique?)counts.Keys.Max() : null;
        var depth = trials == 0 ? 0 : 1;
        if (solution is null)
        {
            var outcome = reason == TechniquesExhausted ? HashiSolvability.RequiresSearch : HashiSolvability.Unknown;
            return new HashiClassification(outcome, null, null, hardest, counts, depth, trials, trace.AsReadOnly(), null, reason);
        }

        var advanced = counts.GetValueOrDefault(HashiTechnique.BridgeBudget) + counts.GetValueOrDefault(HashiTechnique.SoleExit)
            + counts.GetValueOrDefault(HashiTechnique.AvoidIsolatedGroup);
        var lookahead = counts.GetValueOrDefault(HashiTechnique.Lookahead);
        var solvability = lookahead > 0 ? HashiSolvability.BoundedLookahead :
            advanced > 0 ? HashiSolvability.AdvancedLogic : HashiSolvability.BasicLogic;
        var weight = counts.Sum(pair => pair.Value * TechniqueScores[(int)pair.Key].Weight);
        var effort = Math.Min(39, 24 * weight / Math.Max(16, puzzle.Edges.Count));
        var difficultSteps = Math.Min(15, 2 * advanced + 3 * lookahead);
        var score = Math.Min(99, TechniqueScores[(int)hardest.GetValueOrDefault()].Floor + effort + difficultSteps);
        var difficulty = score switch
        {
            < 36 => HashiDifficulty.Easy,
            < 60 => HashiDifficulty.Medium,
            < 80 => HashiDifficulty.Hard,
            _ => HashiDifficulty.Expert,
        };
        return new HashiClassification(solvability, difficulty, score, hardest, counts, depth, trials,
            trace.AsReadOnly(), Array.AsReadOnly(solution), null);
    }

    private static bool Apply(byte[] domains, ReadOnlySpan<Restriction> updates, HashiTechnique technique,
        int[] subject, List<HashiDeduction>? trace, string? reason = null)
    {
        List<HashiEdgeChange>? changes = null;
        var changed = false;
        foreach (var (edge, allowed) in updates)
        {
            var before = domains[edge];
            var after = (byte)(before & allowed);
            if (after == 0)
                throw new Contradiction($"{technique}: no count remains for edge {edge}");
            if (after == before)
                continue;
            domains[edge] = after;
            changed = true;
            if (trace is not null)
                (changes ??= []).Add(new HashiEdgeChange(edge, before, after));
        }
        if (changes is not null)
            trace!.Add(new HashiDeduction(technique, subject, changes.AsReadOnly(), reason));
        return changed;
    }

    private static bool ApplyQuotas(HashiPuzzle puzzle, byte[] domains, List<HashiDeduction>? trace)
    {
        var changed = false;
        Span<Restriction> updates = stackalloc Restriction[4];
        for (var island = 0; island < puzzle.Islands.Count; island++)
        {
            var clue = puzzle.Islands[island].Clue;
            var edges = puzzle.Neighbors[island];
            if (edges.Length == 0)
                throw new Contradiction($"Island {island} has no neighbor");
            for (var index = 0; index < edges.Length; index++)
            {
                var edge = edges[index].Edge;
                var reachable = 1;
                foreach (var (other, _) in edges)
                {
                    if (other == edge)
                        continue;
                    var totals = 0;
                    foreach (var count in Values[domains[other]])
                        totals |= reachable << count;
                    reachable = totals;
                }
                byte allowed = 0;
                foreach (var count in Values[domains[edge]])
                    if (clue >= count && (reachable & (1 << (clue - count))) != 0)
                        allowed |= (byte)(1 << count);
                updates[index] = new Restriction(edge, allowed);
            }
            changed |= Apply(domains, updates[..edges.Length], HashiTechnique.IslandQuota, [island], trace);
        }
        return changed;
    }

    private static bool ApplyCrossings(HashiPuzzle puzzle, byte[] domains, List<HashiDeduction>? trace)
    {
        var changed = false;
        for (var edge = 0; edge < domains.Length; edge++)
        {
            var crossings = puzzle.Crossings[edge];
            if (crossings.Length > 0 && (domains[edge] & 1) == 0)
                changed |= Apply(domains, Array.ConvertAll(crossings, other => new Restriction(other, 1)),
                    HashiTechnique.CrossingExclusion, [edge], trace);
        }
        return changed;
    }

    private static bool ApplyBridgeBudget(byte[] domains, int maxDoubles, List<HashiDeduction>? trace)
    {
        var forcedDoubles = domains.Count(mask => mask == 4);
        if (forcedDoubles > maxDoubles)
            throw new Contradiction("Too few bridges remain to connect all islands");
        if (forcedDoubles < maxDoubles)
            return false;

        var updates = new List<Restriction>();
        for (var edge = 0; edge < domains.Length; edge++)
            if (domains[edge] != 4 && (domains[edge] & 4) != 0)
                updates.Add(new Restriction(edge, 3));
        return Apply(domains, updates.ToArray(), HashiTechnique.BridgeBudget, [], trace);
    }

    private static bool AvoidIsolation(HashiPuzzle puzzle, byte[] domains, List<HashiDeduction>? trace)
    {
        var groups = new int[puzzle.Islands.Count];
        Array.Fill(groups, -1);
        var remaining = new int[groups.Length];
        var sizes = new int[groups.Length];
        var groupCount = 0;
        var pending = new Stack<int>();
        for (var island = 0; island < groups.Length; island++)
        {
            if (groups[island] >= 0)
                continue;
            var group = groupCount++;
            groups[island] = group;
            pending.Push(island);
            while (pending.TryPop(out var current))
            {
                sizes[group]++;
                remaining[group] += puzzle.Islands[current].Clue;
                foreach (var (edge, other) in puzzle.Neighbors[current])
                {
                    var minimum = Values[domains[edge]][0];
                    remaining[group] -= minimum;
                    if (minimum == 0 || groups[other] >= 0)
                        continue;
                    groups[other] = group;
                    pending.Push(other);
                }
            }
        }

        if (groupCount <= 2)
            return false;
        var changed = false;
        for (var edge = 0; edge < domains.Length; edge++)
        {
            var (a, b) = puzzle.Edges[edge];
            var first = groups[a];
            var second = groups[b];
            if (domains[edge] == 1 || first == second)
                continue;
            // A bridge joining two groups must leave capacity for a connection to the rest of the board.
            var capacity = remaining[first] + remaining[second];
            var allowed = (byte)(capacity > 4 ? 7 : capacity > 2 ? 3 : 1);
            var technique = sizes[first] == 1 && sizes[second] == 1
                ? HashiTechnique.AvoidIsolatedPair : HashiTechnique.AvoidIsolatedGroup;
            changed |= Apply(domains, [new Restriction(edge, allowed)], technique, [a, b], trace);
        }
        return changed;
    }

    private static void Deduce(HashiPuzzle puzzle, byte[] domains, HashiDeadline deadline, List<HashiDeduction>? trace)
    {
        var clueTotal = puzzle.Islands.Sum(island => island.Clue);
        if (clueTotal % 2 != 0)
            throw new Contradiction("Total clue is odd");
        var maxDoubles = clueTotal / 2 - (puzzle.Islands.Count - 1);
        while (true)
        {
            deadline.Check();
            var quotas = ApplyQuotas(puzzle, domains, trace);
            var crossings = ApplyCrossings(puzzle, domains, trace);
            if (quotas || crossings || ApplyBridgeBudget(domains, maxDoubles, trace))
                continue;

            var cuts = puzzle.FindCuts(Array.ConvertAll(domains, mask => (byte)(mask & 6)))
                ?? throw new Contradiction("The remaining edges cannot connect the islands");
            var changed = false;
            foreach (var (edge, count) in cuts)
                changed |= Apply(domains, [new Restriction(edge, (byte)(1 << count))], HashiTechnique.SoleExit,
                    [edge], trace);
            if (!changed && !AvoidIsolation(puzzle, domains, trace))
                return;
        }
    }

    private static bool ApplyLookahead(HashiPuzzle puzzle, byte[] domains, HashiDeadline deadline,
        List<HashiDeduction> trace, ref int trials)
    {
        for (var edge = 0; edge < domains.Length; edge++)
        {
            var values = Values[domains[edge]];
            if (values.Length == 1)
                continue;
            foreach (var count in values)
            {
                var trial = (byte[])domains.Clone();
                trial[edge] = (byte)(1 << count);
                trials++;
                try
                {
                    Deduce(puzzle, trial, deadline, null);
                }
                catch (Contradiction error)
                {
                    var allowed = (byte)(domains[edge] & ~(1 << count));
                    return Apply(domains, [new Restriction(edge, allowed)], HashiTechnique.Lookahead, [edge, count], trace,
                        error.Message);
                }
            }
        }
        return false;
    }
}
