using System.Text.Json.Serialization;
using KoboLogicPuzzles.Server.Hashi;

namespace KoboLogicPuzzles.Server;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "game")]
[JsonDerivedType(typeof(NonogramPuzzleDefinition), "nonogram")]
[JsonDerivedType(typeof(HashiPuzzleDefinition), "hashi")]
[JsonDerivedType(typeof(FillAPixPuzzleDefinition), "fill-a-pix")]
public abstract record GamePuzzleDefinition(string Id, int Width, int Height)
{
    public int SchemaVersion => 1;
}

public sealed record NonogramPuzzleDefinition(string Id, int Width, int Height, string Title, string Author,
    int[][] RowClues, int[][] ColumnClues,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solution,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PuzzleMetadata? Metadata)
    : GamePuzzleDefinition(Id, Width, Height);

public sealed record HashiDefinitionMetadata(string Solvability, int IslandCount, double ActualDensity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Difficulty,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DifficultyScore);

public sealed record HashiPuzzleDefinition(string Id, int Width, int Height, IReadOnlyList<HashiIsland> Islands,
    IReadOnlyList<HashiEdge> Edges, HashiDefinitionMetadata Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<byte>? Solution)
    : GamePuzzleDefinition(Id, Width, Height);
