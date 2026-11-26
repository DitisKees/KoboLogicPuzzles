using System.Text.Json;
using System.Text.Json.Serialization;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server;

public sealed record GameProgressUpload([property: JsonRequired] int SchemaVersion, GameProgressUpdate[]? Boards);

public sealed record GameProgressUpdate([property: JsonRequired] string PuzzleId, [property: JsonRequired] JsonElement Marks,
    [property: JsonRequired] bool Completed);

public sealed record GameSavedProgress(string PuzzleId, int SchemaVersion, JsonElement Marks, bool Completed, DateTimeOffset BackedUpAt);

public sealed record GameSavedBoard(GamePuzzleDefinition Puzzle, GameSavedProgress Progress);

internal sealed record FillAPixProgressUpdate(PuzzleId Id, string Marks, bool Completed);
internal sealed record SlitherlinkProgressUpdate(PuzzleId Id, string Marks, bool Completed);

public sealed class GameProgressBackup(PuzzleStore store, PuzzleLibrary library, PuzzleProviders providers, TimeProvider time)
{
    public async Task<ProgressReceipt> SaveAsync(GameProgressUpload upload, CancellationToken cancellationToken)
    {
        if (upload.SchemaVersion != 1 || upload.Boards is not { Length: <= 100 } boards)
            throw new ArgumentException("Use schemaVersion 1 and supply a boards array with at most 100 changed boards.");
        if (boards.Length == 0)
            return new ProgressReceipt(0, null);

        var nonograms = new List<ProgressUpdate>();
        var hashes = new List<HashiProgressUpdate>();
        var fillAPix = new List<FillAPixProgressUpdate>();
        var slitherlink = new List<SlitherlinkProgressUpdate>();
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
            else if (id.Game == PuzzleGame.FillAPix)
            {
                if (board.Marks.ValueKind != JsonValueKind.String || board.Marks.GetString() is not { Length: > 0 } marks ||
                    marks.Any(mark => mark is not ('.' or '#' or 'x')))
                    throw new ArgumentException($"Puzzle {id} requires one mark (., #, or x) per cell.");
                fillAPix.Add(new(id, marks, board.Completed));
            }
            else if (id.Game == PuzzleGame.Slitherlink)
            {
                if (board.Marks.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Slitherlink marks must be a string.");
                if (await providers.GetDefinitionAsync(id, false, cancellationToken) is not SlitherlinkDefinition puzzle)
                    throw new ArgumentException($"Puzzle {id} is unavailable and its progress could not be backed up.");
                var marks = board.Marks.GetString()!;
                if (!puzzle.ValidMarks(marks) || board.Completed && !puzzle.IsComplete(marks))
                    throw new ArgumentException($"Puzzle {id} has invalid edge marks or an invalid completed loop.");
                slitherlink.Add(new(id, marks, board.Completed));
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

        await NonogramProgressValidator.ValidateAsync(library, nonograms, cancellationToken);
        foreach (var board in fillAPix)
        {
            if (await providers.GetDefinitionAsync(board.Id, false, cancellationToken) is not FillAPixPuzzleDefinition puzzle)
                throw new ArgumentException($"Puzzle {board.Id} is unavailable and its progress could not be backed up.");
            if (board.Marks.Length != puzzle.Clues.Length)
                throw new ArgumentException($"Puzzle {board.Id} requires one mark (., #, or x) per cell.");
            if (board.Completed && !puzzle.MatchesClues(board.Marks))
                throw new ArgumentException($"The filled cells for puzzle {board.Id} do not satisfy its clues.");
        }
        var backedUpAt = time.GetUtcNow();
        store.SaveMixedProgress(nonograms, hashes, fillAPix, slitherlink, backedUpAt, cancellationToken);
        return new ProgressReceipt(boards.Length, backedUpAt);
    }
}
