using System.Globalization;
using System.Text.Json;
using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server.Nurikabe;

internal sealed record NurikabeImportEntry(string Source, string SourceName, string SourceId, string SourceUrl,
    string License, string Author, string? Title = null, int? Width = null, int? Height = null, int[]? Clues = null,
    string? Solution = null, string? PuzzleUrl = null, string? OriginalRating = null);

internal sealed record NurikabeImportResult(int Stored, int Duplicates, int Rejected, int TimedOut);

internal sealed class NurikabeImporter(PuzzleStore store, NurikabeOptions options, ILogger<NurikabeImporter> logger)
{
    internal bool Store(NurikabeImportEntry entry, CancellationToken token)
    {
        if (entry.Source is null || !SlitherlinkOptions.ValidSource(entry.Source) || entry.Source == NurikabeGenerator.Source ||
            string.IsNullOrWhiteSpace(entry.SourceName) || string.IsNullOrWhiteSpace(entry.SourceId) ||
            string.IsNullOrWhiteSpace(entry.License) || string.IsNullOrWhiteSpace(entry.Author) ||
            !TryWebUri(entry.SourceUrl, out _))
            throw new InvalidDataException("Collection entries require a source, stable ID, URL, license or permission, and author.");

        NurikabeBoard board;
        if (entry.PuzzleUrl is { } puzzleUrl && entry.Clues is null)
            board = DecodePuzzLink(puzzleUrl);
        else if (entry is { Width: { } width, Height: { } height, Clues: { } clues })
            board = new(width, height, clues);
        else
            throw new InvalidDataException("Supply a clue grid or a serialized puzz.link URL.");

        if (entry.Solution is { } supplied && !board.IsSolved(supplied))
            throw new InvalidDataException("Supplied solution violates the Nurikabe rules.");
        var proof = new NurikabeSolver(board, TimeSpan.FromSeconds(options.ProofTimeoutSeconds), token).CountSolutions();
        if (proof.Count != 1 || entry.Solution is { } solution && proof.Solution != solution)
            throw new InvalidDataException(proof.Count > 1 ? "Clues have multiple solutions." : "Clues have no verified solution.");

        var metadata = new NurikabeMetadata(entry.Source, entry.SourceName, "collection", entry.SourceId,
            entry.SourceUrl, entry.License, null, null, proof.Difficulty!, proof.Solvability!, entry.OriginalRating);
        token.ThrowIfCancellationRequested();
        return store.SaveNurikabe(new(board.Id(), board.Width, board.Height, board.Clues,
            string.IsNullOrWhiteSpace(entry.Title) ? $"Nurikabe {board.Width}×{board.Height}" : entry.Title, entry.Author,
            metadata, proof.Solution));
    }

    internal async Task<NurikabeImportResult> ImportAsync(string file, string? puzzleKitFile, CancellationToken token)
    {
        var entries = await ReadJsonAsync<NurikabeImportEntry[]>(file, token);
        var cases = puzzleKitFile is null ? null : (await ReadJsonAsync<PuzzleKitShard>(puzzleKitFile, token)).Data
            ?? throw new InvalidDataException("The PuzzleKit shard has no data.");
        if (entries.Length > 2_000)
            throw new InvalidDataException("Import at most 2,000 authorized records at a time.");
        int stored = 0, duplicates = 0, rejected = 0, timedOut = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry is null)
            {
                rejected++;
                continue;
            }

            try
            {
                var candidate = cases is null ? entry : ReadPuzzleKitEntry(entry, cases);
                if (Store(candidate, token)) stored++;
                else duplicates++;
            }
            catch (InvalidDataException exception)
            {
                rejected++;
                logger.LogWarning("Rejected Nurikabe entry {Id}: {Reason}", entry.SourceId, exception.Message);
            }
            catch (TimeoutException)
            {
                timedOut++;
                logger.LogWarning("Uniqueness proof timed out for Nurikabe entry {Id}.", entry.SourceId);
            }
        }
        return new(stored, duplicates, rejected, timedOut);
    }

    private static async Task<T> ReadJsonAsync<T>(string file, CancellationToken token)
    {
        await using var stream = File.OpenRead(file);
        if (stream.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Collection input must fit within 16 MiB.");
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonSerializerOptions.Web, token)
            ?? throw new InvalidDataException("The collection input is empty.");
    }

    private sealed record PuzzleKitShard(Dictionary<string, PuzzleKitCase> Data);
    private sealed record PuzzleKitCase(string? Problem, string? Solution, string? Source);

    private static NurikabeImportEntry ReadPuzzleKitEntry(NurikabeImportEntry entry, Dictionary<string, PuzzleKitCase> cases)
    {
        if (entry.SourceId is null || !cases.TryGetValue(entry.SourceId, out var record) || record?.Problem is null ||
            record.Source != entry.SourceUrl)
            throw new InvalidDataException("The approval must match the exact case ID and original URL in the PuzzleKit shard.");
        var lines = Lines(record.Problem);
        var dimensions = lines.Length == 0 ? [] : Tokens(lines[0]);
        if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var height) || !int.TryParse(dimensions[1], out var width))
            throw new InvalidDataException("The PuzzleKit record has invalid dimensions.");
        var clues = ReadGrid(width, height, lines.Skip(1).ToArray(), ParseClue);
        string? solution = null;
        if (!string.IsNullOrWhiteSpace(record.Solution))
        {
            var answer = Lines(record.Solution);
            if (answer.Length == 0 || !Tokens(answer[0]).SequenceEqual(dimensions))
                throw new InvalidDataException("The PuzzleKit solution has different dimensions.");
            solution = DecodeAnswer(width, height, answer.Skip(1).ToArray());
        }
        return entry with { Width = width, Height = height, Clues = clues, Solution = solution };
    }

    internal static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    internal static T[] ReadGrid<T>(int width, int height, string[] rows, Func<string, T> convert)
    {
        if (width is < 1 or > 50 || height is < 1 or > 50 || rows.Length != height)
            throw new InvalidDataException("The Nurikabe grid has invalid dimensions.");
        var values = rows.Select(Tokens).ToArray();
        if (values.Any(row => row.Length != width))
            throw new InvalidDataException("The Nurikabe grid is not rectangular.");
        return values.SelectMany(row => row.Select(convert)).ToArray();
    }

    internal static int ParseClue(string token) => token is "-" or "." ? 0 :
        int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value :
        throw new InvalidDataException("Unexpected Nurikabe clue.");

    internal static string DecodeAnswer(int width, int height, string[] rows) =>
        new(ReadGrid(width, height, rows, token => token switch
        {
            "x" or "#" => '#',
            "-" or "." => 'x',
            _ => throw new InvalidDataException("Unexpected collection solution mark."),
        }));

    internal static bool TryWebUri(string? text, out Uri uri)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" && parsed.UserInfo.Length == 0)
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    internal static NurikabeBoard DecodePuzzLink(string text)
    {
        if (!TryWebUri(text, out var url) || url.Host is not ("puzz.link" or "pzv.jp") ||
            url.AbsolutePath is not ("/p" or "/p.html") || url.Fragment.Length != 0)
            throw new InvalidDataException("Expected a serialized puzz.link or pzv.jp Nurikabe URL.");
        var parts = url.Query.TrimStart('?').TrimEnd('/').Split('/');
        if (parts is not ["nurikabe", var w, var h, var grid] ||
            !int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is < 1 or > 50 ||
            !int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height is < 1 or > 50)
            throw new InvalidDataException("The puzz.link URL has invalid Nurikabe dimensions.");

        var clues = new int[width * height];
        var cell = 0;
        for (var index = 0; index < grid.Length; index++)
        {
            var symbol = grid[index];
            if (symbol is >= 'g' and <= 'z')
                cell += symbol - 'g' + 1;
            else
            {
                var digits = symbol switch { '-' => 2, '+' => 3, _ => 1 };
                var first = index + (digits == 1 ? 0 : 1);
                if (cell >= clues.Length || first + digits > grid.Length ||
                    !int.TryParse(grid.AsSpan(first, digits), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var clue) || clue <= 0)
                    throw new InvalidDataException("Invalid or unnumbered clue in the puzz.link URL.");
                clues[cell++] = clue;
                index = first + digits - 1;
            }
            if (cell > clues.Length)
                throw new InvalidDataException("puzz.link clue data overruns the board.");
        }

        if (cell != clues.Length)
            throw new InvalidDataException("puzz.link clue data does not cover the board.");
        return new(width, height, clues);
    }
}
