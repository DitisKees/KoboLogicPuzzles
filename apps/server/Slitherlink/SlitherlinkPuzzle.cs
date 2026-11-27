using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoboLogicPuzzles.Server.Slitherlink;

public readonly record struct LoopVertex(int X, int Y);
public readonly record struct LoopEdge(int First, int Second);
public sealed record LoopFace(int X, int Y, int[] Edges,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Clue);

public sealed record SlitherlinkMetadata(string Tiling, string Source, string SourceName, int FaceCount,
    string? Difficulty = null, string? SourceUrl = null, string? License = null, string? Seed = null)
{
    public string SourceKind => Source == LoopyGenerator.Source ? "generated" : "collection";

    private static readonly string[] ImportedTilings = ["altair", "snowflake", "laves", "rhombille", "septistar"];

    internal static bool ValidTiling(string value) => SlitherlinkOptions.Tilings.Contains(value) || ImportedTilings.Contains(value);
}

public sealed record SlitherlinkDefinition(string Id, int Width, int Height, string Title, string? Author,
    LoopVertex[] Vertices, LoopEdge[] Edges, LoopFace[] Faces, SlitherlinkMetadata Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solution)
    : GamePuzzleDefinition(Id, Width, Height)
{
    internal bool ValidMarks(string marks) => marks.Length == Edges.Length &&
        marks.All(mark => mark is '.' or '#' or 'x');

    internal bool IsComplete(string marks)
    {
        if (!ValidMarks(marks))
            return false;
        foreach (var face in Faces)
            if (face.Clue is { } clue && face.Edges.Count(edge => marks[edge] == '#') != clue)
                return false;

        var neighbours = new (int First, int Second, int Count)[Vertices.Length];
        var start = -1;
        var numberOfLines = 0;
        for (var i = 0; i < Edges.Length; i++)
        {
            if (marks[i] != '#')
                continue;
            var (first, second) = Edges[i];
            if (!Add(first, second) || !Add(second, first))
                return false;
            start = first;
            numberOfLines++;
        }
        if (start < 0)
            return false;

        var previous = -1;
        var current = start;
        var walked = 0;
        do
        {
            var (first, second, count) = neighbours[current];
            if (count != 2)
                return false;
            (previous, current) = (current, first == previous ? second : first);
            walked++;
        } while (current != start);
        return walked == numberOfLines;

        bool Add(int vertex, int other)
        {
            ref var entry = ref neighbours[vertex];
            if (entry.Count == 0)
                entry.First = other;
            else if (entry.Count == 1)
                entry.Second = other;
            return ++entry.Count <= 2;
        }
    }
}

internal sealed record NativeLoopFace(int X, int Y, int Clue, int[] Edges);

internal sealed record NativeLoopBoard(int[][] Vertices, int[][] Edges, NativeLoopFace[] Faces, string? Solution)
{
    internal SlitherlinkDefinition ToDefinition(int width, int height, SlitherlinkMetadata metadata,
        string? title = null, string? author = null)
    {
        if (Vertices is not { Length: >= 3 and <= 60000 } || Edges is not { Length: >= 3 and <= 100000 } ||
            Faces is not { Length: >= 1 and <= 40000 } || Vertices.Any(v => v is not { Length: 2 } ||
                v.Any(coordinate => coordinate is < -10000000 or > 10000000)))
            throw new InvalidDataException("Invalid Slitherlink graph dimensions.");

        var order = Enumerable.Range(0, Vertices.Length).OrderBy(i => Vertices[i][0]).ThenBy(i => Vertices[i][1]).ToArray();
        var indexes = new int[order.Length];
        var minX = Vertices[order[0]][0];
        var minY = Vertices.Min(point => point[1]);
        var vertices = new LoopVertex[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            var old = order[i];
            indexes[old] = i;
            vertices[i] = new(Vertices[old][0] - minX, Vertices[old][1] - minY);
            if (i > 0 && vertices[i] == vertices[i - 1])
                throw new InvalidDataException("Duplicate Slitherlink vertex.");
        }

        var rawEdges = new LoopEdge[Edges.Length];
        for (var i = 0; i < Edges.Length; i++)
        {
            if (Edges[i] is not { Length: 2 } pair || pair.Any(index => (uint)index >= (uint)vertices.Length) || pair[0] == pair[1])
                throw new InvalidDataException("Invalid Slitherlink edge.");
            int first = indexes[pair[0]], second = indexes[pair[1]];
            rawEdges[i] = new(Math.Min(first, second), Math.Max(first, second));
        }
        var edgeOrder = Enumerable.Range(0, rawEdges.Length).OrderBy(i => rawEdges[i].First).ThenBy(i => rawEdges[i].Second).ToArray();
        var edgeIndexes = new int[edgeOrder.Length];
        var edges = new LoopEdge[edgeOrder.Length];
        for (var i = 0; i < edgeOrder.Length; i++)
        {
            edgeIndexes[edgeOrder[i]] = i;
            edges[i] = rawEdges[edgeOrder[i]];
            if (i > 0 && edges[i] == edges[i - 1])
                throw new InvalidDataException("Duplicate Slitherlink edge.");
        }

        var incidence = new int[edges.Length];
        var faces = new LoopFace[Faces.Length];
        for (var i = 0; i < Faces.Length; i++)
        {
            var face = Faces[i];
            if (face is null || face.Edges is not { Length: >= 3 and <= 64 } boundary ||
                face.Clue < -1 || face.Clue > boundary.Length ||
                boundary.Any(index => (uint)index >= (uint)edges.Length) || boundary.Distinct().Count() != boundary.Length)
                throw new InvalidDataException("Invalid Slitherlink face.");
            var remapped = boundary.Select(index => edgeIndexes[index]).Order().ToArray();
            foreach (var index in remapped)
                incidence[index]++;
            faces[i] = new(face.X - minX, face.Y - minY, remapped, face.Clue < 0 ? null : face.Clue);
        }
        if (incidence.Any(count => count is not (1 or 2)))
            throw new InvalidDataException("Each Slitherlink edge must border one or two faces.");
        faces = faces.OrderBy(face => string.Join(',', face.Edges), StringComparer.Ordinal).ToArray();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            metadata.Source, metadata.Tiling, width, height, vertices, edges,
            Clues = faces.Select(face => new { face.Edges, face.Clue }),
        });
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var id = PuzzleId.FromSlitherlink(hash).Value;
        string? solution = null;
        if (Solution is not null)
        {
            if (Solution.Length != edges.Length || Solution.Any(mark => mark is not ('#' or '.')))
                throw new InvalidDataException("Invalid Slitherlink solution encoding.");
            solution = new string(edgeOrder.Select(index => Solution[index]).ToArray());
        }

        var result = new SlitherlinkDefinition(id, width, height,
            string.IsNullOrWhiteSpace(title) ? $"Slitherlink {hash[^6..]}" : title, author,
            vertices, edges, faces, metadata with { FaceCount = faces.Length }, solution);
        if (solution is not null && !result.IsComplete(solution))
            throw new InvalidDataException("The Slitherlink solution fails the clues or single-loop rule.");
        return result;
    }

    internal static NativeLoopBoard Square(int?[][] clues)
    {
        if (clues is not { Length: >= 1 and <= 100 } || clues[0] is not { Length: >= 1 and <= 100 } first ||
            clues.Any(row => row is null || row.Length != first.Length || row.Any(clue => clue is < 0 or > 4)))
            throw new InvalidDataException("Expected a rectangular grid of integers 0–4 and nulls.");

        int w = first.Length, h = clues.Length;
        int Vertex(int x, int y) => y * (w + 1) + x;
        int Horizontal(int x, int y) => y * w + x;
        int Vertical(int x, int y) => (h + 1) * w + y * (w + 1) + x;
        var vertices = new int[(w + 1) * (h + 1)][];
        var edges = new int[(h + 1) * w + h * (w + 1)][];
        var faces = new NativeLoopFace[w * h];
        for (var y = 0; y <= h; y++)
            for (var x = 0; x <= w; x++)
            {
                var vertex = Vertex(x, y);
                vertices[vertex] = [x * 10, y * 10];
                if (x < w)
                    edges[Horizontal(x, y)] = [vertex, Vertex(x + 1, y)];
                if (y < h)
                    edges[Vertical(x, y)] = [vertex, Vertex(x, y + 1)];
                if (x < w && y < h)
                    faces[y * w + x] = new(x * 10 + 5, y * 10 + 5, clues[y][x] ?? -1,
                        [Horizontal(x, y), Vertical(x + 1, y), Horizontal(x, y + 1), Vertical(x, y)]);
            }
        return new(vertices, edges, faces, null);
    }
}
