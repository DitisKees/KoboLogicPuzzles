using System.Diagnostics;
using System.Globalization;

namespace KoboLogicPuzzles.Server.Placement;

internal sealed class PlacementGenerator
{
    internal const string TentsVersion = "616da16f25da428ccda805536209095bd062676e+kobo.1";
    internal const string BattleshipsVersion = "1";
    internal const string Tatham = "tatham";
    internal const string Local = "kobo-logic-puzzles";
    internal const string TentsUrl = "https://www.chiark.greenend.org.uk/~sgtatham/puzzles/js/tents.html";

    internal static int[] Sizes(PuzzleGame game) => game == PuzzleGame.Tents ? [25, 30, 35, 40, 45, 50] : [20, 25, 30, 35, 40, 45, 50];
    internal static string Source(PuzzleGame game) => game == PuzzleGame.Tents ? Tatham : Local;
    internal static string SourceName(PuzzleGame game) => game == PuzzleGame.Tents ? "Simon Tatham's Tents" : "KoboLogicPuzzles";
    internal static string TentsExecutable(string? path) => Path.GetFullPath(path ?? Path.Combine(AppContext.BaseDirectory, "tents", "tentsgen"));

    internal static async Task<(PlacementBoard Board, string Solution)> TentsAsync(string executable, int size, int seed,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add(size.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(seed.ToString("x", CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start tentsgen.");
        string? description, answer;
        try
        {
            description = await process.StandardOutput.ReadLineAsync(token);
            answer = await process.StandardOutput.ReadLineAsync(token);
            await process.WaitForExitAsync(token);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        if (process.ExitCode != 0 || description is null || answer is null)
            throw new InvalidDataException("tentsgen returned no valid candidate.");
        return DecodeTents(size, size, description, answer);
    }

    internal static (PlacementBoard Board, PlacementProof Proof) TuneTents(PlacementBoard layout, string solution, int seed,
        string difficulty, TimeSpan budget, CancellationToken token)
    {
        if (!layout.IsSolved(solution))
            throw new InvalidDataException("Native Tents layout does not satisfy the rules.");

        // Only cells beside a tree can contain a tent. Start with water clues on
        // unused candidates, then identify which clues can be removed.
        var removable = layout.Trees.SelectMany(i => layout.Sides[i]).Distinct()
            .Where(i => layout.Givens[i] == '.' && solution[i] == 'x').ToArray();
        var givens = layout.Givens.ToCharArray();
        foreach (var i in removable)
            givens[i] = 'x';
        return Tune(layout.Game, layout.Width, layout.Height, layout.Rows, layout.Columns, [],
            givens, removable, solution, difficulty, new Random(seed), budget, token);
    }

    internal static (PlacementBoard Board, string Solution) DecodeTents(int width, int height, string description, string answer)
    {
        var fields = description.Split(',');
        var moves = answer.Split(';');
        if (fields.Length != 1 + width + height || moves[0] != "S" ||
            width is < 1 or > PlacementBoard.MaxDimension || height is < 1 or > PlacementBoard.MaxDimension)
            throw new InvalidDataException("Invalid Tatham Tents dimensions or fields.");

        var givens = new string('.', width * height).ToCharArray();
        var cell = 0;
        var sentinel = false;
        foreach (var c in fields[0])
        {
            var skip = c switch { '_' => 0, >= 'a' and <= 'y' => c - 'a' + 1, 'z' => 25, _ => -1 };
            cell += skip;
            if (skip < 0 || sentinel || cell > givens.Length)
                throw new InvalidDataException("Invalid Tatham tree run.");
            if (c == 'z')
                continue;
            if (cell == givens.Length)
                sentinel = true;
            else
                givens[cell++] = 't';
        }
        if (!sentinel)
            throw new InvalidDataException("Missing end of Tatham tree grid.");

        var totals = fields.Skip(1).Select(ParseNumber).ToArray();
        var board = new PlacementBoard(PuzzleGame.Tents, width, height, totals[width..], totals[..width], new string(givens), []);
        var solution = givens.Select(c => c == 't' ? '.' : 'x').ToArray();
        foreach (var move in moves.Skip(1))
        {
            var xy = move.StartsWith('T') ? move[1..].Split(',') : [];
            if (xy.Length != 2)
                throw new InvalidDataException("Invalid move in Tatham answer.");
            var x = ParseNumber(xy[0]);
            var y = ParseNumber(xy[1]);
            if (x >= width || y >= height || solution[y * width + x] != 'x')
                throw new InvalidDataException("Invalid tent in Tatham answer.");
            solution[y * width + x] = '#';
        }
        return (board, new string(solution));
    }

    internal static int ParseNumber(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        ? n : throw new InvalidDataException("Expected a nonnegative integer.");

    internal static (PlacementBoard Board, PlacementProof Proof) Battleships(int size, int seed, string target,
        TimeSpan budget, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var random = new Random(seed);
        // Scale by board area, keeping a mix of fleet lengths from submarines through carriers.
        var fleet = new[] { 7, 5, 4, 3, 2 }.Select(n => Math.Max(1, n * size * size / 400)).ToArray();
        var layout = new string('.', size * size).ToCharArray();
        var blocked = new bool[layout.Length];
        for (var length = fleet.Length; length > 0; length--)
        {
            for (var count = 0; count < fleet[length - 1]; count++)
            {
                token.ThrowIfCancellationRequested();
                var placed = false;
                for (var attempt = 0; attempt < 2000 && !placed; attempt++)
                {
                    var horizontal = random.Next(2) == 0;
                    var row = random.Next(size - (horizontal ? 0 : length - 1));
                    var col = random.Next(size - (horizontal ? length - 1 : 0));
                    var step = horizontal ? 1 : size;
                    var start = row * size + col;
                    if (Enumerable.Range(0, length).Any(j => blocked[start + j * step]))
                        continue;
                    for (var j = 0; j < length; j++)
                    {
                        var index = start + j * step;
                        layout[index] = PlacementBoard.Segment(j, length, horizontal);
                        var y = index / size;
                        var x = index % size;
                        for (var dy = Math.Max(0, y - 1); dy <= Math.Min(size - 1, y + 1); dy++)
                            for (var dx = Math.Max(0, x - 1); dx <= Math.Min(size - 1, x + 1); dx++)
                                blocked[dy * size + dx] = true;
                    }
                    placed = true;
                }
                if (!placed)
                    throw new InvalidDataException("Could not place the scaled fleet.");
            }
        }

        var solution = new string(layout.Select(c => c == '.' ? 'x' : '#').ToArray());
        var (rows, columns) = PlacementBoard.Totals(size, size, solution);
        var shipCells = Enumerable.Range(0, layout.Length).Where(i => layout[i] != '.').ToArray();
        return Tune(PuzzleGame.Battleships, size, size, rows, columns, fleet, layout, shipCells, solution, target,
            random, budget - clock.Elapsed, token);
    }

    private static (PlacementBoard Board, PlacementProof Proof) Tune(PuzzleGame game, int width, int height,
        int[] rows, int[] columns, int[] fleet, char[] givens, int[] removable, string solution, string target,
        Random random, TimeSpan budget, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var initial = (char[])givens.Clone();
        random.Shuffle(removable);
        var board = MakeBoard();
        var proof = Prove(board);
        if (proof.Count != 1 || !board.IsSolved(solution) || proof.Solution != solution)
            throw new InvalidDataException("The starting clues do not describe the generated layout.");

        var targetRank = Array.IndexOf(PlacementBoard.Difficulties, target);
        var batchSize = Math.Max(1, (removable.Length + 11) / 12);
        foreach (var group in removable.Chunk(batchSize))
        {
            if (!Remove(group) && group.Length > 1)
                Remove(group[..(group.Length / 2)]);
            if (proof.Difficulty == target && givens.Count(c => c != '.' && c != 't') <= removable.Length / 3)
                break;
        }
        return (board, proof);

        bool Remove(int[] group)
        {
            foreach (var i in group)
                givens[i] = '.';
            try
            {
                var candidate = MakeBoard();
                var result = Prove(candidate);
                if (result.Count == 1 && result.Solution == solution && result.Solvability != "unknown" &&
                    Array.IndexOf(PlacementBoard.Difficulties, result.Difficulty) <= targetRank)
                {
                    board = candidate;
                    proof = result;
                    return true;
                }
            }
            catch (PlacementSearchLimitException)
            {
                // Larger searches can be useful when importing. Generation keeps shallow proofs.
            }

            foreach (var i in group)
                givens[i] = initial[i];
            return false;
        }

        PlacementBoard MakeBoard() => new(game, width, height, rows, columns, new string(givens), fleet);
        PlacementProof Prove(PlacementBoard candidate) =>
            new PlacementSolver(candidate, budget - clock.Elapsed, token, searchLimit: 3).CountSolutions();
    }
}
