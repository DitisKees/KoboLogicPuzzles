using System.Text.Json.Serialization;
using KoboLogicPuzzles.Server.Galaxies;
using KoboLogicPuzzles.Server.Nurikabe;
using KoboLogicPuzzles.Server.Placement;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "game")]
[JsonDerivedType(typeof(NonogramPuzzleDefinition), "nonogram")]
[JsonDerivedType(typeof(HashiPuzzleDefinition), "hashi")]
[JsonDerivedType(typeof(FillAPixPuzzleDefinition), "fill-a-pix")]
[JsonDerivedType(typeof(SlitherlinkDefinition), "slitherlink")]
[JsonDerivedType(typeof(NurikabeDefinition), "nurikabe")]
[JsonDerivedType(typeof(GalaxiesDefinition), "galaxies")]
[JsonDerivedType(typeof(TentsDefinition), "tents")]
[JsonDerivedType(typeof(BattleshipsDefinition), "battleships")]
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DifficultyScore,
    string Source = "bridges", string SourceName = "Bridges", string SourceKind = "generated",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? License = null);

public sealed record HashiPuzzleDefinition(string Id, int Width, int Height, IReadOnlyList<HashiIsland> Islands,
    IReadOnlyList<HashiEdge> Edges, HashiDefinitionMetadata Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<byte>? Solution,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Author = null)
    : GamePuzzleDefinition(Id, Width, Height);
