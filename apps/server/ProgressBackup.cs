namespace KoboNonogram.Server;

public sealed class ProgressBackup(PuzzleStore store, PuzzleLibrary library, TimeProvider time)
{
    public async Task<ProgressReceipt> SaveAsync(ProgressUpdate[] boards, CancellationToken cancellationToken)
    {
        await ValidateAsync(boards, cancellationToken);
        if (boards.Length == 0)
            return new ProgressReceipt(0, null);

        cancellationToken.ThrowIfCancellationRequested();
        var backedUpAt = time.GetUtcNow();
        store.SaveProgress(boards, backedUpAt);
        return new ProgressReceipt(boards.Length, backedUpAt);
    }

    internal async Task ValidateAsync(IReadOnlyList<ProgressUpdate> boards, CancellationToken cancellationToken)
    {
        var ids = new HashSet<int>();
        foreach (var board in boards)
        {
            if (board is null || board.PuzzleId <= 0 || !ids.Add(board.PuzzleId))
                throw new ArgumentException("Each board must have a positive, unique puzzle ID.");

            if (board.Marks is null || board.Marks.Length is < 1 or > 10000 ||
                board.Marks.Any(mark => mark is not ('.' or '#' or 'x')))
                throw new ArgumentException($"Puzzle {board.PuzzleId} requires one mark (., #, or x) per cell.");
        }

        foreach (var board in boards)
        {
            var puzzle = await library.GetPuzzleForBackupAsync(board.PuzzleId, cancellationToken) ??
                throw new ArgumentException($"Puzzle {board.PuzzleId} is unavailable from WebPBN and its progress could not be backed up.");
            if (board.Marks.Length != puzzle.Width * puzzle.Height)
                throw new ArgumentException($"Puzzle {board.PuzzleId} requires one mark (., #, or x) per cell.");
            if (board.Completed && !puzzle.MatchesClues(board.Marks))
                throw new ArgumentException($"The filled cells for puzzle {board.PuzzleId} do not satisfy its clues.");
        }

    }
}
