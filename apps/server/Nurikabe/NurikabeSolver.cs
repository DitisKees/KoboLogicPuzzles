using System.Diagnostics;

namespace KoboLogicPuzzles.Server.Nurikabe;

internal sealed record NurikabeProof(int Count, string? Solution, string? Difficulty, string? Solvability);

internal sealed class NurikabeSolver(NurikabeBoard board, TimeSpan timeout, CancellationToken token)
{
    private const byte Unknown = NurikabeBoard.Unknown, White = NurikabeBoard.White, Black = NurikabeBoard.Black;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public NurikabeProof CountSolutions()
    {
        byte[] cells = board.Clues.Select(clue => clue > 0 ? White : Unknown).ToArray();
        if (!Propagate(cells, advanced: false))
            return new(0, null, null, null);
        if (!cells.Contains(Unknown))
            return Result(cells, "easy", "basic_logic");
        var unresolved = cells.Count(cell => cell == Unknown);
        if (!Propagate(cells, advanced: true))
            return new(0, null, null, null);
        if (!cells.Contains(Unknown))
            return Result(cells, unresolved >= Math.Max(12, board.Size * 15 / 100) ? "hard" : "medium", "advanced_logic");

        // A contradiction after propagating one trial color is a sound deduction.
        // Bound the classification work. The exact search below still handles either remaining color.
        var probeBudget = Math.Min(2 * board.Size, 160);
        var probes = probeBudget;
        var lookaheadDeductions = 0;
        while (probes > 0 && cells.Contains(Unknown))
        {
            var forced = false;
            foreach (var cell in CandidateCells(cells))
            {
                if (probes <= 0)
                    break;
                probes -= 2;
                var white = Trial(cells, cell, White);
                var black = Trial(cells, cell, Black);
                if (white is not null && black is not null)
                    continue;
                if (white is null && black is null)
                    return new(0, null, null, null);
                cells = (white ?? black)!;
                lookaheadDeductions++;
                forced = true;
                break;
            }
            if (!forced)
                break;
        }

        if (!cells.Contains(Unknown))
            return Result(cells, lookaheadDeductions <= 4 ? "hard" : "expert", "bounded_lookahead");
        var remaining = new Stack<byte[]>();
        remaining.Push(cells);
        string? solution = null;
        while (remaining.TryPop(out var state))
        {
            CheckDeadline();
            var cell = CandidateCells(state).FirstOrDefault(-1);
            if (cell < 0)
            {
                if (!board.IsSolved(state))
                    continue;
                if (solution is not null)
                    return new(2, null, null, null);
                solution = Encode(state);
                continue;
            }

            foreach (var color in new[] { Black, White })
                if (Trial(state, cell, color) is { } trial)
                    remaining.Push(trial);
        }
        return solution is null ? new(0, null, null, null) : new(1, solution, "expert", "requires_search");
    }

    private NurikabeProof Result(byte[] cells, string difficulty, string solvability) =>
        board.IsSolved(cells) ? new(1, Encode(cells), difficulty, solvability) : new(0, null, null, null);

    private static string Encode(byte[] cells) => new(cells.Select(cell => cell == Black ? '#' : 'x').ToArray());

    private IEnumerable<int> CandidateCells(byte[] cells) => Enumerable.Range(0, board.Size).Where(cell => cells[cell] == Unknown)
        .OrderByDescending(cell => board.Neighbors[cell].Count(other => cells[other] != Unknown));

    private byte[]? Trial(byte[] cells, int cell, byte color)
    {
        var trial = (byte[])cells.Clone();
        trial[cell] = color;
        return Propagate(trial, advanced: true) ? trial : null;
    }

    private bool Propagate(byte[] cells, bool advanced)
    {
        int[] queue = new int[board.Size], owner = new int[board.Size], reached = new int[board.Size];
        int[] visits = new int[board.Size], distance = new int[board.Size], frontier = new int[board.Size];
        var changed = true;
        var discovery = new int[board.Size];
        var low = new int[board.Size];
        var subtreeBlack = new int[board.Size];
        var edges = new int[board.Size];
        while (changed)
        {
            CheckDeadline();
            changed = false;
            var black = cells.Count(cell => cell == Black);
            var white = cells.Count(cell => cell == White);
            var whiteTarget = board.Size - board.SeaSize;
            if (black > board.SeaSize || white > whiteTarget)
                return false;
            if (black == board.SeaSize || white == whiteTarget)
                for (var cell = 0; cell < board.Size; cell++)
                    if (cells[cell] == Unknown)
                        Set(cell, black == board.SeaSize ? White : Black);

            foreach (var pool in board.Pools)
            {
                var blackInPool = 0;
                var empty = -1;
                foreach (var cell in pool)
                {
                    if (cells[cell] == Black) blackInPool++;
                    if (cells[cell] == Unknown) empty = cell;
                }
                if (blackInPool == 4)
                    return false;
                if (blackInPool == 3 && empty >= 0)
                    Set(empty, White);
            }

            Array.Fill(owner, -1);
            Array.Clear(visits);
            for (var start = 0; start < board.Size; start++)
            {
                if (cells[start] != White || visits[start] != 0)
                    continue;
                var clue = -1;
                var size = 1;
                var exits = 0;
                queue[0] = start;
                visits[start] = start + 1;
                for (var index = 0; index < size; index++)
                {
                    var cell = queue[index];
                    if (board.Clues[cell] > 0)
                    {
                        if (clue >= 0)
                            return false;
                        clue = cell;
                    }
                    foreach (var next in board.Neighbors[cell])
                    {
                        if (cells[next] == Black || visits[next] == start + 1)
                            continue;
                        visits[next] = start + 1;
                        if (cells[next] == Unknown)
                            frontier[exits++] = next;
                        else
                            queue[size++] = next;
                    }
                }

                var area = clue < 0 ? 0 : board.Clues[clue];
                if (clue < 0 && exits == 0 || clue >= 0 && (size > area || size < area && exits == 0))
                    return false;
                if (clue >= 0)
                    for (var index = 0; index < size; index++)
                        owner[queue[index]] = clue;
                if (clue >= 0 && size == area)
                    for (var index = 0; index < exits; index++)
                        Set(frontier[index], Black);
                // Apply white expansion after labeling all components.
            }

            for (var cell = 0; cell < board.Size; cell++)
            {
                if (cells[cell] != Unknown)
                    continue;
                var island = -1;
                foreach (var next in board.Neighbors[cell])
                {
                    if (owner[next] < 0)
                        continue;
                    if (island >= 0 && island != owner[next])
                    {
                        Set(cell, Black);
                        break;
                    }
                    island = owner[next];
                }
            }

            if (advanced)
            {
                Array.Clear(reached);
                Array.Clear(visits);
                for (var clue = 0; clue < board.Size; clue++)
                {
                    var area = board.Clues[clue];
                    if (area == 0)
                        continue;
                    var size = 1;
                    var exits = 0;
                    var exit = -1;
                    var owned = 0;
                    queue[0] = clue;
                    distance[clue] = 1;
                    visits[clue] = clue + 1;
                    for (var index = 0; index < size; index++)
                    {
                        var cell = queue[index];
                        reached[cell] = 1;
                        if (owner[cell] == clue)
                            owned++;
                        if (distance[cell] == area)
                            continue;
                        foreach (var next in board.Neighbors[cell])
                        {
                            if (visits[next] == clue + 1 || cells[next] == Black ||
                                owner[next] >= 0 && owner[next] != clue ||
                                board.Neighbors[next].Any(neighbor => owner[neighbor] >= 0 && owner[neighbor] != clue))
                                continue;
                            visits[next] = clue + 1;
                            distance[next] = distance[cell] + 1;
                            queue[size++] = next;
                        }
                    }
                    if (size < area)
                        return false;
                    if (owned < area)
                        for (var index = 0; index < size; index++)
                        {
                            var cell = queue[index];
                            if (cells[cell] == Unknown && board.Neighbors[cell].Any(next => owner[next] == clue))
                            {
                                exits++;
                                exit = cell;
                            }
                        }

                    // Discard the component and reachability labels as soon as a new white cell joins them.
                    if (size == area && owned < area)
                    {
                        for (var index = 0; index < size; index++)
                            if (cells[queue[index]] == Unknown)
                                Set(queue[index], White);
                        break;
                    }
                    if (exits == 1)
                    {
                        Set(exit, White);
                        break;
                    }
                }

                if (changed)
                    continue;
                for (var cell = 0; cell < board.Size; cell++)
                {
                    if (reached[cell] != 0 || cells[cell] == Black)
                        continue;
                    if (cells[cell] == White)
                        return false;
                    Set(cell, Black);
                }
            }

            var firstBlack = Array.IndexOf(cells, Black);
            if (firstBlack >= 0)
            {
                Array.Clear(visits);
                var count = 1;
                var known = 0;
                queue[0] = firstBlack;
                visits[firstBlack] = 1;
                for (var index = 0; index < count; index++)
                {
                    var cell = queue[index];
                    if (cells[cell] == Black)
                        known++;
                    foreach (var next in board.Neighbors[cell])
                    {
                        if (visits[next] != 0 || cells[next] == White)
                            continue;
                        visits[next] = 1;
                        queue[count++] = next;
                    }
                }
                if (count < board.SeaSize || known != cells.Count(cell => cell == Black))
                    return false;
                if (advanced)
                {
                    for (var cell = 0; cell < board.Size; cell++)
                        if (visits[cell] == 0 && cells[cell] == Unknown)
                            Set(cell, White);

                    // Start at a known black cell. An undecided cut vertex must be black
                    // when it separates another known black cell from that root.
                    Array.Clear(discovery);
                    queue[0] = firstBlack;
                    var top = 0;
                    var order = 1;
                    Visit(firstBlack);
                    while (top >= 0)
                    {
                        var cell = queue[top];
                        if (edges[cell] < board.Neighbors[cell].Length)
                        {
                            var next = board.Neighbors[cell][edges[cell]++];
                            if (cells[next] == White || top > 0 && queue[top - 1] == next)
                                continue;
                            if (discovery[next] != 0)
                            {
                                low[cell] = Math.Min(low[cell], discovery[next]);
                                continue;
                            }
                            queue[++top] = next;
                            Visit(next);
                        }
                        else if (--top >= 0)
                        {
                            var parent = queue[top];
                            low[parent] = Math.Min(low[parent], low[cell]);
                            subtreeBlack[parent] += subtreeBlack[cell];
                            if (cells[parent] == Unknown && subtreeBlack[cell] > 0 && low[cell] >= discovery[parent])
                                Set(parent, Black);
                        }
                    }
                    void Visit(int cell)
                    {
                        low[cell] = discovery[cell] = order++;
                        edges[cell] = 0;
                        subtreeBlack[cell] = cells[cell] == Black ? 1 : 0;
                    }
                }
            }
        }
        return true;

        void Set(int cell, byte color)
        {
            cells[cell] = color;
            changed = true;
        }
    }

    private void CheckDeadline()
    {
        token.ThrowIfCancellationRequested();
        if (clock.Elapsed >= timeout)
            throw new TimeoutException("Nurikabe uniqueness proof timed out.");
    }
}
