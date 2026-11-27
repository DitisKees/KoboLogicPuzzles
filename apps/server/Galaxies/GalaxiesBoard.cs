using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace KoboLogicPuzzles.Server.Galaxies;

// Coordinates are in half-cell units measured from the top-left corner of the board.
public sealed record GalaxyCenter(int X, int Y, bool Black);
public sealed record GalaxiesMetadata(string Source, string SourceName, string SourceKind, string SourceId,
    string SourceUrl, string License, string LicenseUrl, string SourceVersion, string ContentHash, string Difficulty, string Solvability,
    bool Picture);
public sealed record GalaxiesDefinition(string Id, int Width, int Height, GalaxyCenter[] Centers,
    string Title, string Author, GalaxiesMetadata Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solution)
    : GamePuzzleDefinition(Id, Width, Height);

internal sealed class GalaxiesBoard
{
    internal const int MaxDimension = 250;
    internal int Width { get; }
    internal int Height { get; }
    internal GalaxyCenter[] Centers { get; }
    internal int[][] Core { get; }
    internal int[] CoreOwners { get; }
    internal int[][] Neighbors { get; }
    internal (int A, int B)[] Edges { get; }
    internal int CellCount => Width * Height;

    internal GalaxiesBoard(int width, int height, IEnumerable<GalaxyCenter> centers)
    {
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
            throw new InvalidDataException("Invalid Galaxies dimensions.");
        Width = width;
        Height = height;
        Centers = centers.OrderBy(center => center.Y).ThenBy(center => center.X).ToArray();
        if (Centers.Length == 0 || Centers.Length > CellCount)
            throw new InvalidDataException("Invalid number of galaxy centers.");

        CoreOwners = new int[CellCount];
        Array.Fill(CoreOwners, -1);
        Core = new int[Centers.Length][];
        for (var i = 0; i < Centers.Length; i++)
        {
            var (x, y, _) = Centers[i];
            if (x <= 0 || x >= 2 * width || y <= 0 || y >= 2 * height)
                throw new InvalidDataException("Galaxy centers must be inside the board.");
            var cells = new List<int>(4);
            for (var row = (y - 1) / 2; row <= y / 2; row++)
                for (var col = (x - 1) / 2; col <= x / 2; col++)
                {
                    var cell = row * width + col;
                    if (CoreOwners[cell] != -1)
                        throw new InvalidDataException("Two galaxy centers touch the same cell.");
                    CoreOwners[cell] = i;
                    cells.Add(cell);
                }
            Core[i] = cells.ToArray();
        }

        var edges = new List<(int, int)>();
        var adjacent = Enumerable.Range(0, CellCount).Select(_ => new List<int>(4)).ToArray();
        for (var y = 0; y + 1 < height; y++)
            for (var x = 0; x < width; x++)
                AddEdge(y * width + x, (y + 1) * width + x);
        for (var y = 0; y < height; y++)
            for (var x = 0; x + 1 < width; x++)
                AddEdge(y * width + x, y * width + x + 1);
        Edges = edges.ToArray();
        Neighbors = adjacent.Select(list => list.ToArray()).ToArray();

        void AddEdge(int a, int b)
        {
            edges.Add((a, b));
            adjacent[a].Add(b);
            adjacent[b].Add(a);
        }
    }

    internal string ContentHash()
    {
        var content = new StringBuilder(FormattableString.Invariant($"galaxies-v1/{Width}/{Height}/"));
        foreach (var center in Centers)
            content.Append(FormattableString.Invariant($"{center.X},{center.Y},{(center.Black ? 1 : 0)};"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }

    internal int Reflect(int cell, int center)
    {
        var (x, y, _) = Centers[center];
        var col = x - 1 - cell % Width;
        var row = y - 1 - cell / Width;
        return col >= 0 && col < Width && row >= 0 && row < Height ? row * Width + col : -1;
    }

    internal bool ValidMarks(string marks) =>
        marks.Length == Edges.Length && marks.All(mark => mark is '.' or 'x' or '#');

    internal string ToEdges(int[] owners) => new(Edges.Select(e => owners[e.A] == owners[e.B] ? 'x' : '#').ToArray());

    internal bool IsSolved(string marks) => Picture(marks) is not null;

    // Open and unmarked edges connect cells. A wall inside a connected component is invalid.
    internal string? Picture(string marks)
    {
        if (!ValidMarks(marks))
            return null;
        var parent = Enumerable.Range(0, CellCount).ToArray();
        int Root(int cell)
        {
            while (parent[cell] != cell)
            {
                parent[cell] = parent[parent[cell]];
                cell = parent[cell];
            }
            return cell;
        }

        for (var i = 0; i < Edges.Length; i++)
            if (marks[i] != '#')
                parent[Root(Edges[i].B)] = Root(Edges[i].A);
        for (var i = 0; i < Edges.Length; i++)
            if (marks[i] == '#' && Root(Edges[i].A) == Root(Edges[i].B))
                return null;

        var owners = new int[CellCount];
        Array.Fill(owners, -1);
        for (var i = 0; i < Core.Length; i++)
        {
            var root = Root(Core[i][0]);
            if (owners[root] != -1 || Core[i].Any(cell => Root(cell) != root))
                return null;
            owners[root] = i;
        }
        var pixels = new char[CellCount];
        for (var cell = 0; cell < CellCount; cell++)
        {
            var root = Root(cell);
            var center = owners[root];
            if (center < 0 || Reflect(cell, center) is not (>= 0 and var mirror) || Root(mirror) != root)
                return null;
            pixels[cell] = Centers[center].Black ? '#' : '.';
        }
        return new(pixels);
    }
}
