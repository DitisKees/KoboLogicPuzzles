using System.Text.Json.Serialization;

namespace KoboLogicPuzzles.Server;

internal enum FillAPixSource { Janko, GrandGames, FillAPixNet }

internal static class FillAPixSources
{
    public const string Game = "fill-a-pix";

    public static string Slug(this FillAPixSource source) => source switch
    {
        FillAPixSource.Janko => "janko",
        FillAPixSource.GrandGames => "grandgames",
        FillAPixSource.FillAPixNet => "fillapixnet",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}

public sealed record FillAPixMetadata(string Source, string SourceUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? License = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Quality = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Complexity = null);

public sealed record FillAPixPuzzleDefinition(string Id, int Width, int Height, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Author,
    int?[] Clues, FillAPixMetadata Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solution)
    : GamePuzzleDefinition(Id, Width, Height)
{
    internal bool MatchesClues(string marks)
    {
        if (marks.Length != Clues.Length)
            return false;

        for (var index = 0; index < Clues.Length; index++)
        {
            if (Clues[index] is not { } clue)
                continue;
            var (row, column) = Math.DivRem(index, Width);
            var filled = 0;
            for (var y = Math.Max(0, row - 1); y <= Math.Min(row + 1, Height - 1); y++)
                for (var x = Math.Max(0, column - 1); x <= Math.Min(column + 1, Width - 1); x++)
                    if (marks[y * Width + x] == '#')
                        filled++;
            if (filled != clue)
                return false;
        }
        return true;
    }
}

internal sealed record FillAPixCatalogEntry(PuzzleId Id, int Width, int Height, string Title, string? Author,
    FillAPixMetadata Metadata, FillAPixPuzzleDefinition? Definition = null)
{
    public static FillAPixCatalogEntry FromDefinition(PuzzleId id, FillAPixPuzzleDefinition definition) =>
        new(id, definition.Width, definition.Height, definition.Title, definition.Author, definition.Metadata, definition);

    public FillAPixPuzzleDefinition Define(int?[] clues, string? solution)
    {
        if (Width <= 0 || Height <= 0 || (long)Width * Height != clues.Length || !clues.Any(clue => clue.HasValue))
            throw new InvalidDataException($"{Id} has invalid dimensions or no clues.");

        for (var index = 0; index < clues.Length; index++)
        {
            if (clues[index] is not { } clue)
                continue;
            var (row, column) = Math.DivRem(index, Width);
            var neighborhoodWidth = 1 + (column > 0 ? 1 : 0) + (column < Width - 1 ? 1 : 0);
            var neighborhoodHeight = 1 + (row > 0 ? 1 : 0) + (row < Height - 1 ? 1 : 0);
            if (clue < 0 || clue > neighborhoodWidth * neighborhoodHeight)
                throw new InvalidDataException($"{Id} has a clue outside its neighborhood's range.");
        }

        var definition = new FillAPixPuzzleDefinition(Id.Value, Width, Height, Title, Author, clues, Metadata, solution);
        if (solution is not null && (solution.Any(cell => cell is not ('#' or '.')) || !definition.MatchesClues(solution)))
            throw new InvalidDataException($"{Id} has a solution that disagrees with its clues.");
        return definition;
    }
}
