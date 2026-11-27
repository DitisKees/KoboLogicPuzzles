using System.Diagnostics;

namespace KoboLogicPuzzles.Server.Placement;

internal sealed record PlacementProof(int Count, string? Solution, string? Difficulty, string? Solvability, long Nodes);

internal sealed class PlacementSearchLimitException : Exception;

internal sealed class PlacementSolver
{
    private const sbyte Unknown = -1, Empty = 0, Filled = 1;
    private readonly PlacementBoard board;
    private readonly CancellationToken token;
    private readonly TimeSpan timeout;
    private readonly int searchLimit;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly (int[] Cells, int Count)[] lines;
    private readonly (int[] Cells, int Count, bool Exact)[] treeGroups;
    private readonly int[][] conflicts;
    private readonly Ship[] ships;
    private int solutions;
    private string? solution;
    private long nodes;
    private int sweeps;
    private bool advanced;
    private int branchDepth;

    private sealed record Ship(int[] Cells, int[] Halo);

    internal PlacementSolver(PlacementBoard board, TimeSpan timeout, CancellationToken token, int searchLimit = int.MaxValue)
    {
        this.board = board;
        this.timeout = timeout;
        this.token = token;
        this.searchLimit = searchLimit;
        CheckBudget();
        lines = board.Rows.Select((count, row) => (Enumerable.Range(row * board.Width, board.Width).ToArray(), count))
            .Concat(board.Columns.Select((count, col) => (Enumerable.Range(0, board.Height).Select(row => row * board.Width + col).ToArray(), count)))
            .ToArray();
        var tents = board.Game == PuzzleGame.Tents;
        conflicts = tents ? board.Adjacent : board.Adjacent.Select((cells, i) =>
            cells.Except(board.Sides[i]).ToArray()).ToArray();
        treeGroups = tents ? TreeGroups() : [];
        ships = tents ? [] : ShipPlacements().ToArray();
    }

    internal PlacementProof CountSolutions()
    {
        var state = board.Givens.Select(c => c switch { '.' => Unknown, 'x' or 't' => Empty, _ => Filled }).ToArray();
        if (board.Game == PuzzleGame.Tents)
            for (var i = 0; i < state.Length; i++)
                if (state[i] == Unknown && !board.Sides[i].Any(n => board.Givens[n] == 't'))
                    state[i] = Empty;

        Search(state, 0);
        CheckBudget();
        if (solutions != 1)
            return new(solutions, solution, null, null, nodes);

        var difficulty = branchDepth > 0 || sweeps >= 15 ? "expert" :
            sweeps >= 7 ? "hard" : advanced || sweeps >= 5 ? "medium" : "easy";
        var solvability = branchDepth == 0 ? advanced ? "advanced_logic" : "basic_logic" :
            branchDepth == 1 ? "bounded_lookahead" : "unknown";
        return new(1, solution, difficulty, solvability, nodes);
    }

    private void Search(sbyte[] state, int depth)
    {
        CheckBudget();
        if (++nodes > searchLimit)
            throw new PlacementSearchLimitException();
        branchDepth = Math.Max(branchDepth, depth);
        if (!Propagate(state))
            return;

        var index = ChooseCell(state);
        if (index < 0)
        {
            var marks = new string(state.Select((value, i) =>
                value == Filled ? '#' : board.Givens[i] == 't' ? '.' : 'x').ToArray());
            if (board.IsSolved(marks))
            {
                solution ??= marks;
                solutions++;
            }
            return;
        }

        if (board.Game == PuzzleGame.Tents && !board.HasMatching(i => state[i] != Empty))
            return;

        var copy = (sbyte[])state.Clone();
        copy[index] = Filled;
        Search(copy, depth + 1);
        if (solutions < 2)
        {
            state[index] = Empty;
            Search(state, depth + 1);
        }
    }

    private bool Propagate(sbyte[] state)
    {
        bool changed;
        do
        {
            CheckBudget();
            sweeps++;
            changed = false;
            for (var i = 0; i < state.Length; i++)
            {
                if (state[i] != Filled)
                    continue;
                foreach (var next in conflicts[i])
                    if (!Set(state, next, Empty, ref changed))
                        return false;
            }

            foreach (var (cells, count) in lines)
                if (!ApplyCount(state, cells, count, true, ref changed))
                    return false;
            foreach (var (cells, count, exact) in treeGroups)
                if (!ApplyCount(state, cells, count, exact, ref changed))
                    return false;

            if (board.Game == PuzzleGame.Tents)
            {
                if (!PairTents(state, ref changed))
                    return false;
                for (var i = 0; i < lines.Length; i++)
                {
                    var (cells, count) = lines[i];
                    if (!PackTents(state, cells, count, i < board.Height, ref changed))
                        return false;
                }
            }
            else if (!ApplyFleet(state, ref changed))
                return false;
        } while (changed);
        return true;
    }

    private static bool ApplyCount(sbyte[] state, int[] cells, int count, bool exact, ref bool changed)
    {
        var unknown = 0;
        foreach (var i in cells)
        {
            if (state[i] == Filled) count--;
            else if (state[i] == Unknown) unknown++;
        }

        if (count > unknown || exact && count < 0)
            return false;
        if (unknown == 0 || !(exact && count == 0 || count == unknown))
            return true;
        foreach (var i in cells)
            if (state[i] == Unknown)
                Set(state, i, count == 0 ? Empty : Filled, ref changed);
        return true;
    }

    private bool PairTents(sbyte[] state, ref bool changed)
    {
        var paired = new bool[state.Length];
        var pairedThisPass = true;
        while (pairedThisPass)
        {
            pairedThisPass = false;
            foreach (var tree in board.Trees)
            {
                if (paired[tree])
                    continue;
                var count = 0;
                var first = -1;
                var second = -1;
                foreach (var i in board.Sides[tree])
                    if (!paired[i] && state[i] != Empty)
                    {
                        if (count == 0) first = i;
                        else if (count == 1) second = i;
                        count++;
                    }
                if (count == 0)
                    return false;
                if (count == 1)
                {
                    Set(state, first, Filled, ref changed);
                    paired[tree] = paired[first] = pairedThisPass = true;
                }
                else if (count == 2)
                {
                    foreach (var i in board.Adjacent[first])
                        if (Array.IndexOf(board.Adjacent[second], i) >= 0 && state[i] != Empty)
                        {
                            advanced = true;
                            if (!Set(state, i, Empty, ref changed))
                                return false;
                        }
                }
            }
            for (var i = 0; i < state.Length; i++)
            {
                if (paired[i] || state[i] == Empty)
                    continue;
                var tree = -1;
                foreach (var next in board.Sides[i])
                    if (!paired[next] && board.Givens[next] == 't')
                    {
                        if (tree != -1)
                        {
                            tree = -2;
                            break;
                        }
                        tree = next;
                    }
                if (tree == -1)
                {
                    if (!Set(state, i, Empty, ref changed))
                        return false;
                }
                else if (tree >= 0 && state[i] == Filled)
                    paired[tree] = paired[i] = pairedThisPass = true;
            }
        }
        return true;
    }

    private bool PackTents(sbyte[] state, int[] line, int count, bool horizontal, ref bool changed)
    {
        ulong unknown = 0;
        ulong filled = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (state[line[i]] == Filled)
            {
                filled |= 1UL << i;
                count--;
            }
            else if (state[line[i]] == Unknown)
                unknown |= 1UL << i;
        }

        if (count < 0 || (filled & (filled << 1)) != 0)
            return false;
        unknown &= ~((filled << 1) | (filled >> 1));
        if (unknown == 0)
            return count == 0;
        var max = MaxTents(unknown);
        if (count > max)
            return false;
        // A line can realize every count up to its maximum independent set.
        // Removing a position or a window tests whether it must contain a tent.
        if (count < max - 2 || count == 0)
            return true;

        for (var i = 0; i < line.Length; i++)
        {
            var bit = 1UL << i;
            var window = bit | (bit << 1) | (bit >> 1);
            var reduced = MaxTents(unknown & ~window);
            if ((unknown & bit) != 0)
            {
                if (max == count && MaxTents(unknown & ~bit) < count)
                {
                    var applied = false;
                    if (!Set(state, line[i], Filled, ref applied))
                        return false;
                    advanced |= applied;
                    changed |= applied;
                }
                else if (1 + reduced < count)
                {
                    var applied = false;
                    if (!Set(state, line[i], Empty, ref applied))
                        return false;
                    advanced |= applied;
                    changed |= applied;
                }
            }
            if (reduced >= count)
                continue;

            var index = line[i];
            for (var direction = -1; direction <= 1; direction += 2)
            {
                var neighbor = index + direction * (horizontal ? board.Width : 1);
                if (neighbor < 0 || neighbor >= state.Length || !horizontal && index / board.Width != neighbor / board.Width)
                    continue;
                var applied = false;
                if (!Set(state, neighbor, Empty, ref applied))
                    return false;
                advanced |= applied;
                changed |= applied;
            }
        }
        return true;
    }

    private static int MaxTents(ulong candidates)
    {
        var count = 0;
        while (candidates != 0)
        {
            var first = candidates & (~candidates + 1);
            candidates &= ~(first | (first << 1));
            count++;
        }
        return count;
    }

    private (int[], int, bool)[] TreeGroups()
    {
        var groups = new List<(int[], int, bool)>();
        var seen = new HashSet<int>();
        foreach (var tree in board.Trees)
        {
            var cells = board.Sides[tree].Where(i => board.Givens[i] is '.' or '#').ToArray();
            groups.Add((cells, 1, false));
            foreach (var other in cells.SelectMany(i => board.Sides[i]).Distinct())
                if (other > tree && board.Givens[other] == 't')
                    groups.Add((cells.Union(board.Sides[other].Where(i => board.Givens[i] is '.' or '#')).ToArray(), 2, false));

            if (!seen.Add(tree))
                continue;
            var component = new List<int> { tree };
            var spots = new HashSet<int>();
            for (var j = 0; j < component.Count; j++)
                foreach (var i in board.Sides[component[j]])
                    if ((board.Givens[i] is '.' or '#') && spots.Add(i))
                        foreach (var next in board.Sides[i])
                            if (board.Givens[next] == 't' && seen.Add(next))
                                component.Add(next);
            groups.Add((spots.ToArray(), component.Count, true));
        }
        return groups.ToArray();
    }

    private IEnumerable<Ship> ShipPlacements()
    {
        for (var index = 0; index < board.Area; index++)
        {
            CheckBudget();
            var row = index / board.Width;
            var col = index % board.Width;
            for (var length = 1; length <= board.Fleet.Length; length++)
            {
                if (board.Fleet[length - 1] == 0)
                    continue;
                if (col + length <= board.Width && board.Rows[row] >= length &&
                    MakeShip(index, length, 1) is { } across)
                    yield return across;
                if (length > 1 && row + length <= board.Height && board.Columns[col] >= length &&
                    MakeShip(index, length, board.Width) is { } down)
                    yield return down;
            }
        }
    }

    private Ship? MakeShip(int index, int length, int step)
    {
        var cells = new int[length];
        for (var j = 0; j < length; j++)
        {
            var i = index + j * step;
            if (board.Rows[i / board.Width] == 0 || board.Columns[i % board.Width] == 0 ||
                board.Givens[i] is not ('.' or '#') && board.Givens[i] != PlacementBoard.Segment(j, length, step == 1))
                return null;
            cells[j] = i;
        }
        var halo = cells.SelectMany(i => board.Adjacent[i]).Except(cells).ToArray();
        return halo.Any(i => board.Givens[i] is not ('.' or 'x')) ? null : new(cells, halo);
    }

    private bool ApplyFleet(sbyte[] state, ref bool changed)
    {
        var support = new int[state.Length];
        var only = new Ship?[state.Length];
        var possible = new List<Ship>();
        var remaining = (int[])board.Fleet.Clone();
        foreach (var ship in ships)
        {
            if (ship.Cells.Any(i => state[i] == Empty) || ship.Halo.Any(i => state[i] == Filled))
                continue;
            if (ship.Cells.All(i => state[i] == Filled) && ship.Halo.All(i => state[i] == Empty))
            {
                if (--remaining[ship.Cells.Length - 1] < 0)
                    return false;
                foreach (var i in ship.Cells) support[i] = 1;
            }
            else
                possible.Add(ship);
        }
        foreach (var ship in possible)
        {
            if (remaining[ship.Cells.Length - 1] == 0)
                continue;
            foreach (var i in ship.Cells)
            {
                support[i]++;
                only[i] = ship;
            }
        }

        for (var i = 0; i < state.Length; i++)
        {
            if (support[i] == 0)
            {
                if (state[i] == Filled)
                    return false;
                if (state[i] == Unknown)
                {
                    advanced = true;
                    Set(state, i, Empty, ref changed);
                }
            }
            else if (state[i] == Filled && support[i] == 1 && only[i] is { } ship)
            {
                var changedShip = false;
                foreach (var cell in ship.Cells)
                    if (!Set(state, cell, Filled, ref changedShip)) return false;
                foreach (var cell in ship.Halo)
                    if (!Set(state, cell, Empty, ref changedShip)) return false;
                advanced |= changedShip;
                changed |= changedShip;
            }
        }
        return true;
    }

    private int ChooseCell(sbyte[] state)
    {
        var best = -1;
        var bestScore = -1;
        for (var i = 0; i < state.Length; i++)
        {
            if (state[i] != Unknown)
                continue;
            var score = 2 * conflicts[i].Count(next => state[next] == Unknown) +
                board.Sides[i].Count(next => board.Game == PuzzleGame.Tents ? board.Givens[next] == 't' : state[next] == Filled) * 8;
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }
        }
        return best;
    }

    private static bool Set(sbyte[] state, int index, sbyte value, ref bool changed)
    {
        if (state[index] != Unknown)
            return state[index] == value;
        state[index] = value;
        changed = true;
        return true;
    }

    private void CheckBudget()
    {
        token.ThrowIfCancellationRequested();
        if (clock.Elapsed >= timeout)
            throw new TimeoutException("The placement uniqueness proof exceeded its time budget.");
    }
}
