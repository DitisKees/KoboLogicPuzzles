using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace KoboLogicPuzzles.Server.Slitherlink;

internal sealed partial class SlitherlinkCollectionRefresh
{
    internal const string KrazyDad = "krazydad";
    private static readonly Uri KrazyDadPlayer = new("https://krazydad.com/play/vslitherlink/");
    private static readonly Dictionary<string, string> KrazyDadTilings = new(StringComparer.Ordinal)
    {
        ["penrose"] = "penrose-rhombs",
        ["altair"] = "altair",
        ["floret"] = "floret",
        ["hexagons"] = "honeycomb",
        ["snowflake"] = "snowflake",
        ["snubsquare"] = "snub-square",
        ["laves"] = "laves",
        ["rhombille1"] = "rhombille",
        ["rhombille2"] = "rhombille",
        ["cairo"] = "cairo",
        ["septistar"] = "septistar",
    };

    private sealed record KrazyDadCursor(string Kind, string Name, string Tiling, Uri Current, Uri Last);

    public async Task RefreshKrazyDadAsync(CancellationToken token)
    {
        var catalog = await ReadHtmlAsync(KrazyDadPlayer, PuzzlePageDelay, token);
        var kinds = catalog.QuerySelectorAll("select#kind option[value]")
            .Select(option => (Kind: option.GetAttribute("value")!, Name: option.TextContent.Trim()))
            .Where(option => KrazyDadKind().IsMatch(option.Kind)).DistinctBy(option => option.Kind).ToArray();
        if (kinds.Length == 0)
            throw new InvalidDataException("KrazyDad did not publish its list of Slitherlink tilings.");

        var meshes = new Dictionary<Uri, KrazyDadMesh>();
        var queue = new Queue<KrazyDadCursor>();
        foreach (var (kind, name) in kinds)
        {
            if (!KrazyDadTilings.TryGetValue(kind.Split('_')[0], out var tiling))
            {
                logger.LogWarning("Skipping unknown KrazyDad Slitherlink tiling {Kind}.", kind);
                continue;
            }
            var resource = $"{KrazyDad}/{kind}";
            var first = KrazyDadUrl($"?kind={kind}&volumeNumber=1&bookNumber=1&puzzleNumber=1", kind);
            var head = await ReadHtmlAsync(first, PuzzlePageDelay, token);
            var last = ReadKrazyDadNavigation(head, "prevPuz", kind);
            var completed = store.GetSlitherlinkCollectionRefreshState(resource)?.Version;
            if (completed == last.Query)
                continue;

            var checkpoint = store.GetSlitherlinkCollectionRefreshState($"{resource}/next")?.Version;
            if (checkpoint is null && completed is not null)
            {
                var previous = await ReadHtmlAsync(KrazyDadUrl(completed, kind), PuzzlePageDelay, token);
                checkpoint = ReadKrazyDadNavigation(previous, "nextPuz", kind).Query;
            }
            var current = checkpoint is null ? first : KrazyDadUrl(checkpoint, kind);
            var scan = new KrazyDadCursor(kind, name, tiling, current, last);
            if (current == first)
                await ImportKrazyDadPageAsync(scan, head, meshes, queue, token);
            else
                queue.Enqueue(scan);
        }
        // Visit each published tiling before continuing through the books.
        while (queue.TryDequeue(out var scan))
        {
            var document = await ReadHtmlAsync(scan.Current, PuzzlePageDelay, token);
            await ImportKrazyDadPageAsync(scan, document, meshes, queue, token);
        }
        MarkChecked(KrazyDad);
    }

    private async Task ImportKrazyDadPageAsync(KrazyDadCursor scan, IDocument document, Dictionary<Uri, KrazyDadMesh> meshes,
        Queue<KrazyDadCursor> queue, CancellationToken token)
    {
        var resource = $"{KrazyDad}/{scan.Kind}";
        var position = KrazyDadQuery().Match(scan.Current.Query);
        var expectedId = $"KD_VSlitherlink_{scan.Kind}_V{position.Groups[2].Value}-B{position.Groups[3].Value}-P{position.Groups[4].Value}";
        using var data = ReadKrazyDadRecord(document, scan.Current);
        if (!data.RootElement.TryGetProperty("puzzle_id", out var id) || id.GetString() != expectedId ||
            !data.RootElement.TryGetProperty("puzzle_data", out var puzzle) ||
            !puzzle.TryGetProperty("puzz", out var grid) || grid.GetString() is not { } clues)
            throw new InvalidDataException($"Unexpected KrazyDad puzzle record at {scan.Current}.");

        var meshUri = document.Scripts.Select(script => script.GetAttribute("src"))
            .Where(src => src is not null).Select(src => new Uri(KrazyDadPlayer, src!)).FirstOrDefault(uri =>
                uri.Scheme == Uri.UriSchemeHttps && uri.Host == "files.krazydad.com" && KrazyDadMeshPath().IsMatch(uri.AbsolutePath));
        if (meshUri is null)
            throw new InvalidDataException("Missing KrazyDad Slitherlink geometry.");
        if (!meshes.TryGetValue(meshUri, out var mesh))
        {
            using var response = await http.GetAsync(meshUri, token);
            response.EnsureSuccessStatusCode();
            mesh = KrazyDadMesh.Parse(await response.Content.ReadAsStringAsync(token));
            meshes.Add(meshUri, mesh);
        }
        var title = $"Slitherlink {scan.Name} {position.Groups[2].Value}-{position.Groups[3].Value}-{position.Groups[4].Value}";
        await importer.ImportBoardAsync(mesh.CreateBoard(clues), mesh.Width, mesh.Height, new(scan.Tiling, KrazyDad,
            "KrazyDad", 0, SourceUrl: scan.Current.AbsoluteUri, License: "Personal or school use"), title, "Jim Bumgardner", token);

        if (scan.Current == scan.Last)
        {
            MarkChecked(resource, scan.Last.Query);
            MarkChecked($"{resource}/next");
            return;
        }
        var next = ReadKrazyDadNavigation(document, "nextPuz", scan.Kind);
        if (CompareKrazyDadPositions(scan.Current, next) >= 0 || CompareKrazyDadPositions(next, scan.Last) > 0)
            throw new InvalidDataException($"KrazyDad returned an invalid next puzzle at {scan.Current}.");
        MarkChecked($"{resource}/next", next.Query);
        queue.Enqueue(scan with { Current = next });
    }

    private static JsonDocument ReadKrazyDadRecord(IDocument document, Uri url)
    {
        foreach (var script in document.Scripts)
        {
            var text = script.TextContent;
            var assignment = KrazyDadRecord().Match(text);
            if (assignment.Success)
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[(assignment.Index + assignment.Length)..]));
                return JsonDocument.ParseValue(ref reader);
            }
        }
        throw new InvalidDataException($"Missing KrazyDad puzzle record at {url}.");
    }

    private static Uri ReadKrazyDadNavigation(IDocument document, string name, string kind)
    {
        var link = document.Scripts.SelectMany(script => KrazyDadNavigation().Matches(script.TextContent))
            .FirstOrDefault(match => match.Groups[1].Value == name);
        return KrazyDadUrl(WebUtility.HtmlDecode(link?.Groups[2].Value ?? ""), kind);
    }

    private static Uri KrazyDadUrl(string query, string kind)
    {
        var match = KrazyDadQuery().Match(query);
        if (!match.Success || match.Groups[1].Value != kind)
            throw new InvalidDataException("Unexpected KrazyDad puzzle link.");
        return new Uri(KrazyDadPlayer, query);
    }

    private static int CompareKrazyDadPositions(Uri first, Uri second)
    {
        var left = KrazyDadQuery().Match(first.Query);
        var right = KrazyDadQuery().Match(second.Query);
        for (var part = 2; part <= 4; part++)
        {
            var order = int.Parse(left.Groups[part].Value, CultureInfo.InvariantCulture)
                .CompareTo(int.Parse(right.Groups[part].Value, CultureInfo.InvariantCulture));
            if (order != 0)
                return order;
        }
        return 0;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_]*_d\d+\z")]
    private static partial Regex KrazyDadKind();

    [GeneratedRegex(@"\A\?kind=([a-z0-9_]+)&volumeNumber=([1-9]\d{0,4})&bookNumber=([1-9]\d{0,4})&puzzleNumber=([1-9]\d{0,4})\z")]
    private static partial Regex KrazyDadQuery();

    [GeneratedRegex(@"\b(prevPuz|nextPuz)\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex KrazyDadNavigation();

    [GeneratedRegex(@"\bconst\s+pRec\s*=\s*(?=\{)")]
    private static partial Regex KrazyDadRecord();

    [GeneratedRegex(@"\A/play/js/tiling_[a-zA-Z0-9_]+(?:\.min)?\.js\z")]
    private static partial Regex KrazyDadMeshPath();
}

internal sealed partial class KrazyDadMesh(int[][] vertices, int[][] edges, NativeLoopFace[] faces, int width, int height)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private sealed record Mesh(Dot[] Dots, Cell[] Cells);
    private sealed record Dot(int Id, double X, double Y);
    private sealed record Cell(int Id, int[] Dots);

    internal int Width => width;
    internal int Height => height;

    internal static KrazyDadMesh Parse(string script)
    {
        var match = MeshAssignment().Match(script);
        if (!match.Success)
            throw new InvalidDataException("Unexpected KrazyDad geometry format.");
        // Geometry is a JavaScript object literal. Convert its identifiers to JSON without executing it.
        var json = MeshProperty().Replace(match.Groups[1].Value, "\"$1\":");
        var mesh = JsonSerializer.Deserialize<Mesh>(json, Json);
        if (mesh?.Dots is not { Length: >= 3 and <= 60000 } dots || mesh.Cells is not { Length: >= 1 and <= 40000 } cells)
            throw new InvalidDataException("Unexpected KrazyDad mesh dimensions.");

        var vertices = new int[dots.Length][];
        for (var i = 0; i < dots.Length; i++)
        {
            var dot = dots[i];
            if (dot.Id != i || !double.IsFinite(dot.X) || !double.IsFinite(dot.Y) || Math.Abs(dot.X) > 10000 || Math.Abs(dot.Y) > 10000)
                throw new InvalidDataException("Unexpected KrazyDad vertex.");
            vertices[i] = [(int)Math.Round(dot.X * 1000), (int)Math.Round(dot.Y * 1000)];
        }

        var edgeIndexes = new Dictionary<(int First, int Second), int>();
        var edges = new List<int[]>();
        var faces = new NativeLoopFace[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            if (cell.Id != i || cell.Dots is not { Length: >= 3 and <= 64 } boundary ||
                boundary.Any(index => (uint)index >= vertices.Length) || boundary.Distinct().Count() != boundary.Length)
                throw new InvalidDataException("Unexpected KrazyDad face.");
            var faceEdges = new int[boundary.Length];
            for (var j = 0; j < boundary.Length; j++)
            {
                int a = boundary[j], b = boundary[(j + 1) % boundary.Length];
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!edgeIndexes.TryGetValue(key, out var index))
                {
                    index = edges.Count;
                    edgeIndexes.Add(key, index);
                    edges.Add([a, b]);
                }
                faceEdges[j] = index;
            }
            faces[i] = new((int)Math.Round(boundary.Average(index => vertices[index][0])),
                (int)Math.Round(boundary.Average(index => vertices[index][1])), -1, faceEdges);
        }

        // The source has no row or column count. Use the aspect ratio and face count for nominal dimensions.
        var dx = vertices.Max(point => point[0]) - vertices.Min(point => point[0]);
        var dy = vertices.Max(point => point[1]) - vertices.Min(point => point[1]);
        if (dx == 0 || dy == 0)
            throw new InvalidDataException("KrazyDad mesh has zero area.");
        int Size(double value) => Math.Clamp((int)Math.Round(Math.Sqrt(cells.Length * value)), 1, 100);
        return new(vertices, edges.ToArray(), faces, Size((double)dx / dy), Size((double)dy / dx));
    }

    internal NativeLoopBoard CreateBoard(string clues)
    {
        if (clues.Length != faces.Length || clues.Any(clue => clue is not ('.' or >= '0' and <= '9')))
            throw new InvalidDataException("KrazyDad clues do not match the mesh.");
        return new(vertices, edges, faces.Select((face, index) => face with
        {
            Clue = clues[index] == '.' ? -1 : clues[index] - '0',
        }).ToArray(), null);
    }

    [GeneratedRegex(@"\A\s*(?:var\s+)?mesh\s*=\s*(\{.*\})\s*;?\s*\z", RegexOptions.Singleline)]
    private static partial Regex MeshAssignment();

    [GeneratedRegex(@"(?<=[{,])\s*([a-zA-Z][a-zA-Z0-9_]*)\s*:")]
    private static partial Regex MeshProperty();
}
