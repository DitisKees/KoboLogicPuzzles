using System.Text.Json.Serialization;

namespace KoboNonogram.Server;

public sealed record PuzzleSummary(int Id, string Title, string Author, int Width, int Height);

public sealed record PuzzleDefinition(
    int Id, string Title, string Author, int Width, int Height, int[][] RowClues, int[][] ColumnClues);

public sealed record OfflineQueueRequest([property: JsonRequired] int TargetCount, int[]? CachedUnstartedPuzzleIds);

public sealed record OfflineQueueResponse(IReadOnlyList<PuzzleDefinition> Puzzles);

public sealed record ProgressUpdate(int PuzzleId, string Marks, bool Completed);

public sealed record ProgressUpload(ProgressUpdate[]? Boards);

public sealed record ProgressReceipt(int SavedCount, DateTimeOffset? BackedUpAt);

public sealed record SavedProgress(int PuzzleId, string Marks, bool Completed, DateTimeOffset BackedUpAt);

public sealed record SavedBoard(PuzzleDefinition Puzzle, SavedProgress Progress);

public sealed record CatalogRefreshState(long Revision, DateTimeOffset? CompletedAt);
