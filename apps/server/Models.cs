using System.Text.Json.Serialization;

namespace KoboNonogram.Server;

public sealed record PuzzleSummary(int Id, string Title, string Author, int Width, int Height);

public sealed record PuzzleDefinition(
    int Id, string Title, string Author, int Width, int Height, int[][] RowClues, int[][] ColumnClues,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solution = null)
{
    public bool MatchesClues(string marks)
    {
        if (marks.Length != Width * Height)
            return false;
        for (var row = 0; row < Height; row++)
        {
            if (!MatchesLine(marks, row * Width, Width, 1, RowClues[row]))
                return false;
        }

        for (var column = 0; column < Width; column++)
        {
            if (!MatchesLine(marks, column, Height, Width, ColumnClues[column]))
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

public sealed record OfflineQueueRequest([property: JsonRequired] int TargetCount, int[]? CachedUnstartedPuzzleIds);

public sealed record OfflineQueueResponse(IReadOnlyList<PuzzleDefinition> Puzzles);

public sealed record ProgressUpdate(int PuzzleId, string Marks, bool Completed);

public sealed record ProgressUpload(ProgressUpdate[]? Boards);

public sealed record ProgressReceipt(int SavedCount, DateTimeOffset? BackedUpAt);

public sealed record SavedProgress(int PuzzleId, string Marks, bool Completed, DateTimeOffset BackedUpAt);

public sealed record SavedBoard(PuzzleDefinition Puzzle, SavedProgress Progress);

public sealed record CatalogRefreshState(long Revision, DateTimeOffset? CompletedAt);
