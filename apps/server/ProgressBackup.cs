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
            if (board.Completed && !puzzle.MatchesClues(board.Marks))
                throw new ArgumentException($"The filled cells for puzzle {board.PuzzleId} do not satisfy its clues.");
        }

        if (boards.Length == 0)
            return new ProgressReceipt(0, null);

        var backedUpAt = time.GetUtcNow();
        store.SaveProgress(boards, backedUpAt);
        return new ProgressReceipt(boards.Length, backedUpAt);
    }
}
