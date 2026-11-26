using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KoboLogicPuzzles.Server.Slitherlink;

internal sealed record SlitherlinkImportResult(int Stored, int Duplicates, int Rejected);

internal sealed partial class SlitherlinkImporter(PuzzleStore store, LoopyGenerator generator, SlitherlinkOptions options,
    ILogger<SlitherlinkImporter> logger)
{
    private sealed record Entry(string? Source, string? SourceName, string? Title, string? Author,
        string? Difficulty, string? SourceUrl, string? License, int?[][]? Clues, string? GameId);
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<SlitherlinkImportResult> ImportAsync(string path, string? source, string? sourceName, CancellationToken token)
    {
        if (source is not null)
            ValidateSource(source, sourceName ?? source);
        else if (sourceName is not null)
            throw new ArgumentException("A source name requires a source ID.");
        var files = Directory.Exists(path) ? Directory.EnumerateFiles(path).Where(IsSupportedFile)
            .Order(StringComparer.Ordinal).ToArray() : [path];
        int stored = 0, duplicate = 0, rejected = 0;
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            if (stream.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Each collection file must be under 32 MiB.");
            var filename = Path.GetFileName(file);
            if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var collections = archive.Entries.Where(entry => MindgamesFilename().IsMatch(entry.Name)).ToArray();
                if (collections.Length == 0 || collections.Sum(entry => entry.Length) > 32 * 1024 * 1024)
                    throw new InvalidDataException("Expected a Mindgames Slitherlink ZIP with less than 32 MiB of puzzle text.");
                foreach (var entry in collections)
                {
                    using var content = entry.Open();
                    await ImportMindgamesAsync(content, entry.Name);
                }
                continue;
            }
            if (Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                await ImportMindgamesAsync(stream, filename);
                continue;
            }
            if (!Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Use a JSON collection or a Mindgames text file or ZIP.");
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            var kit = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String && name.GetString() == "Slitherlink";
            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(kit ? "data" : "puzzles", out var rows)
                ? rows : root;
            if (kit ? data.ValueKind != JsonValueKind.Object : data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Expected a Slitherlink collection or a PuzzleKit Slitherlink dataset.");
            var items = kit ? data.EnumerateObject().Select(pair => (pair.Name, pair.Value))
                : data.EnumerateArray().Select((value, index) => ((index + 1).ToString(CultureInfo.InvariantCulture), value));
            foreach (var (id, value) in items)
                await ImportEntryAsync(filename, id, () => kit ? ParseKit(id, value) :
                    value.Deserialize<Entry>(Json) ?? throw new InvalidDataException("Missing puzzle definition."));
        }
        return new(stored, duplicate, rejected);

        async Task ImportMindgamesAsync(Stream content, string filename)
        {
            using var reader = new StreamReader(content, leaveOpen: true);
            var count = 0;
            while (await reader.ReadLineAsync(token) is { } line)
            {
                count++;
                if (line.Length == 0)
                    continue;
                var id = count.ToString(CultureInfo.InvariantCulture);
                await ImportEntryAsync(filename, id, () => ParseMindgames(filename, id, line));
            }
        }

        async Task ImportEntryAsync(string filename, string id, Func<Entry> read)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.ImportTimeoutSeconds));
            try
            {
                var puzzle = await VerifyAsync(read(), source, sourceName, deadline.Token);
                if (puzzle is not null && store.SaveSlitherlink(puzzle))
                    stored++;
                else
                    duplicate++;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                rejected++;
                logger.LogWarning("Skipping Slitherlink entry {File}/{Entry}: uniqueness check timed out.", filename, id);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or JsonException)
            {
                rejected++;
                logger.LogWarning("Skipping Slitherlink entry {File}/{Entry}: {Reason}", filename, id, exception.Message);
            }
        }
    }

    private async Task<SlitherlinkDefinition?> VerifyAsync(Entry entry, string? overrideSource, string? overrideName, CancellationToken token)
    {
        var source = overrideSource ?? entry.Source ?? "collection";
        var name = overrideSource is null ? entry.SourceName ?? source : overrideName ?? overrideSource;
        ValidateSource(source, name);
        if (entry.Difficulty is { } difficulty && !SlitherlinkOptions.Levels.Contains(difficulty))
            throw new InvalidDataException("Use easy, normal, tricky, or hard for a known difficulty.");
        if ((entry.Clues is null) == (entry.GameId is null))
            throw new InvalidDataException("Supply either square-grid clues or a descriptive Loopy game ID.");

        NativeLoopBoard board;
        int width, height;
        var tiling = "square";
        if (entry.Clues is { } clues)
        {
            board = NativeLoopBoard.Square(clues);
            width = clues[0].Length;
            height = clues.Length;
        }
        else
        {
            (board, width, height, tiling) = await generator.DecodeAsync(entry.GameId!, token);
        }

        var definition = board.ToDefinition(width, height,
            new(tiling, source, name, 0, entry.Difficulty, entry.SourceUrl, entry.License), entry.Title, entry.Author);
        if (store.FindSlitherlink(definition.Id) is not null)
            return null;
        if (entry.Clues is { } cells && SlitherlinkOptions.ValidDimensions(width, height, tiling) && generator.IsAvailable)
        {
            var description = new string(cells.SelectMany(row => row).Select(clue => clue is { } n ? (char)('0' + n) : 'a').ToArray());
            var (native, _, _, _) = await generator.DecodeAsync(FormattableString.Invariant($"{width}x{height}t0:{description}"), token);
            if (native.Solution is not null)
            {
                var proof = native.ToDefinition(width, height, definition.Metadata);
                if (proof.Edges.SequenceEqual(definition.Edges) && proof.Faces.Length == definition.Faces.Length &&
                    proof.Faces.Zip(definition.Faces).All(pair => pair.First.Clue == pair.Second.Clue &&
                        pair.First.Edges.SequenceEqual(pair.Second.Edges)) &&
                    definition.IsComplete(proof.Solution!))
                    definition = definition with { Solution = proof.Solution };
            }
        }
        // An imported board may have fewer deductions available than Loopy's own generated boards.
        // Count solutions if the native decoder could not finish by forced deductions.
        if (definition.Solution is null)
        {
            var (count, solution) = new SlitherlinkSolver(definition, token).Search();
            if (count != 1)
                throw new InvalidDataException(count == 0 ? "Puzzle has no solution." : "Puzzle has more than one solution.");
            definition = definition with { Solution = solution };
        }
        token.ThrowIfCancellationRequested();
        return definition;
    }

    private static Entry ParseKit(string id, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("problem", out var problem) ||
            problem.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing PuzzleKit clue grid.");
        var lines = problem.GetString()!.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var dimensions = lines.Length > 0 ? Split(lines[0]) : [];
        if (dimensions.Length != 2 || !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            height is < 1 or > 100 || width is < 1 or > 100 || lines.Length != height + 1)
            throw new InvalidDataException("Invalid PuzzleKit dimensions.");
        var clues = new int?[height][];
        for (var row = 0; row < height; row++)
        {
            var cells = Split(lines[row + 1]);
            if (cells.Length != width)
                throw new InvalidDataException("PuzzleKit row does not match the stated width.");
            clues[row] = cells.Select<string, int?>(cell => cell switch
            {
                "-" => null,
                "0" or "1" or "2" or "3" or "4" => cell[0] - '0',
                _ => throw new InvalidDataException("Invalid PuzzleKit clue."),
            }).ToArray();
        }
        var url = ReadText(value, "source");
        var (source, name) = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() switch
        {
            "www.janko.at" or "janko.at" => ("janko", "Janko"),
            "puzz.link" or "www.puzz.link" => ("puzz-link", "puzz.link"),
            "puzsq.jp" or "puzsq.sakura.ne.jp" => ("puzzle-square", "Puzzle Square JP"),
            _ => ("puzzlekit", "PuzzleKit"),
        } : ("puzzlekit", "PuzzleKit");
        return new(source, name, $"Slitherlink {id}", ReadText(value, "author"), null, url,
            ReadText(value, "license"), clues, null);
    }

    private static Entry ParseMindgames(string filename, string id, string line)
    {
        if (line.Length < 8 || !int.TryParse(line.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size < 1 || !line.AsSpan(0, 2).SequenceEqual(line.AsSpan(2, 2)) || line.Length < 6 + size * size)
            throw new InvalidDataException("Expected a square Mindgames record.");
        var difficulty = line[4..6] switch
        {
            "EA" => "easy", "ME" => "normal", "HA" => "hard",
            _ => throw new InvalidDataException("Invalid Mindgames difficulty."),
        };
        var clues = new int?[size][];
        for (var row = 0; row < size; row++)
        {
            clues[row] = new int?[size];
            for (var col = 0; col < size; col++)
                clues[row][col] = line[6 + row * size + col] switch
                {
                    ' ' => null,
                    >= '0' and <= '4' and var digit => digit - '0',
                    _ => throw new InvalidDataException("Invalid Mindgames clue."),
                };
        }
        return new("mindgames", "Mindgames", $"Slitherlink {Path.GetFileNameWithoutExtension(filename)} #{id}",
            null, difficulty, "https://ulhaque.co.uk/", null, clues, null);
    }

    private static bool IsSupportedFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".json" or ".zip" or ".txt";

    [GeneratedRegex("\\Apuz-[0-9]{2}x[0-9]{2}(?:-[0-9]+k)?\\.txt\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MindgamesFilename();

    private static string[] Split(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static string? ReadText(JsonElement value, string key) => value.TryGetProperty(key, out var entry) &&
        entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()) ? entry.GetString() : null;

    private static void ValidateSource(string source, string name)
    {
        if (!SlitherlinkOptions.ValidSource(source) || source == LoopyGenerator.Source || string.IsNullOrWhiteSpace(name) ||
            name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("Use a source ID of up to 40 lowercase letters, digits and hyphens, and a display name of up to 80 characters. Loopy is reserved for generated puzzles.");
    }
}
