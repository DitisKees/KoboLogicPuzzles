using System.Text.Json;
using System.Text.Json.Serialization;
using KoboNonogram.Server.Hashi;

namespace KoboNonogram.Server;

public sealed record GameProgressUpload([property: JsonRequired] int SchemaVersion, GameProgressUpdate[]? Boards);

public sealed record GameProgressUpdate([property: JsonRequired] string PuzzleId, [property: JsonRequired] JsonElement Marks,
    [property: JsonRequired] bool Completed);

public sealed record GameSavedProgress(string PuzzleId, int SchemaVersion, JsonElement Marks, bool Completed, DateTimeOffset BackedUpAt);

public sealed record GameSavedBoard(GamePuzzleDefinition Puzzle, GameSavedProgress Progress);

public sealed class GameProgressBackup(PuzzleStore store, ProgressBackup nonogramBackup, TimeProvider time)
{
    public async Task<ProgressReceipt> SaveAsync(GameProgressUpload upload, CancellationToken cancellationToken)
    {
        if (upload.SchemaVersion != 1 || upload.Boards is not { Length: <= 100 } boards)
            throw new ArgumentException("Use schemaVersion 1 and supply a boards array with at most 100 changed boards.");
        if (boards.Length == 0)
            return new ProgressReceipt(0, null);

        var nonograms = new List<ProgressUpdate>();
        var hashes = new List<HashiProgressUpdate>();
        var seen = new HashSet<PuzzleId>();
        foreach (var board in boards)
        {
            if (board is null || !PuzzleId.TryParse(board.PuzzleId, out var id) || !seen.Add(id))
                throw new ArgumentException("Each board must have a valid, unique namespaced puzzle ID.");
            if (id.WebPbnId is { } number)
            {
                if (board.Marks.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Nonogram marks must be a string.");
                nonograms.Add(new ProgressUpdate(number, board.Marks.GetString()!, board.Completed));
            }
            else
            {
                if (board.Marks.ValueKind != JsonValueKind.Array || board.Marks.GetArrayLength() > 5000)
                    throw new ArgumentException("Hashi marks must be an array of at most 5000 edge marks.");
                try
                {
                    var marks = board.Marks.Deserialize<HashiEdgeMark[]>(JsonSerializerOptions.Web)!;
                    hashes.Add(new HashiProgressUpdate(id.ContentHash!, marks, board.Completed));
                }
                catch (JsonException exception)
                {
                    throw new ArgumentException("Each Hashi mark needs an edge index, count, and noBridge flag.", exception);
                }
            }
        }

        await nonogramBackup.ValidateAsync(nonograms, cancellationToken);
        var backedUpAt = time.GetUtcNow();
        store.SaveMixedProgress(nonograms, hashes, backedUpAt, cancellationToken);
        return new ProgressReceipt(boards.Length, backedUpAt);
    }
}
