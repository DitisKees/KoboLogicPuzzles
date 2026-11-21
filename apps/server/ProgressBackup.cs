namespace KoboNonogram.Server;

public sealed class ProgressBackup(PuzzleStore store, TimeProvider time)
{
    public ProgressReceipt Save(ProgressUpdate[] boards)
    {
        var ids = new HashSet<int>();
        foreach (var board in boards)
        {
            if (board is null || board.PuzzleId <= 0 || !ids.Add(board.PuzzleId))
                throw new ArgumentException("Each board must have a positive, unique puzzle ID.");

            var puzzle = store.FindPuzzle(board.PuzzleId) ??
                throw new ArgumentException($"Puzzle {board.PuzzleId} must be downloaded before backing up progress.");
            if (board.Marks is null || board.Marks.Length != puzzle.Width * puzzle.Height ||
                board.Marks.Any(mark => mark is not ('.' or '#' or 'x')))
                throw new ArgumentException($"Puzzle {board.PuzzleId} requires one mark (., #, or x) per cell.");
            if (board.Completed && !MatchesClues(puzzle, board.Marks))
                throw new ArgumentException($"The filled cells for puzzle {board.PuzzleId} do not satisfy its clues.");
        }

        if (boards.Length == 0)
            return new ProgressReceipt(0, null);

        var backedUpAt = time.GetUtcNow();
        store.SaveProgress(boards, backedUpAt);
        return new ProgressReceipt(boards.Length, backedUpAt);
    }

    private static bool MatchesClues(PuzzleDefinition puzzle, string marks)
    {
        for (var row = 0; row < puzzle.Height; row++)
        {
            if (!MatchesLine(marks, row * puzzle.Width, puzzle.Width, 1, puzzle.RowClues[row]))
                return false;
        }

        for (var column = 0; column < puzzle.Width; column++)
        {
            if (!MatchesLine(marks, column, puzzle.Height, puzzle.Width, puzzle.ColumnClues[column]))
                return false;
        }

        return true;
    }

    private static bool MatchesLine(string marks, int start, int length, int stride, int[] clues)
    {
        var run = 0;
        var nextClue = 0;
        for (var index = 0; index < length; index++)
        {
            if (marks[start + index * stride] == '#')
                run++;
            else if (run > 0)
            {
                if (nextClue == clues.Length || run != clues[nextClue++])
                    return false;
                run = 0;
            }
        }

        if (run > 0 && (nextClue == clues.Length || run != clues[nextClue++]))
            return false;
        return nextClue == clues.Length;
    }
}
