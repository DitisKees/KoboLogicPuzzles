using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KoboLogicPuzzles.Server.Slitherlink;

public sealed class SlitherlinkOptions : IValidatableObject
{
    internal static readonly string[] Tilings = [
        "square", "triangular", "honeycomb", "snub-square", "cairo", "great-hexagonal", "octagonal",
        "kites", "floret", "dodecagonal", "great-dodecagonal", "penrose-kite-dart", "penrose-rhombs",
        "great-great-dodecagonal", "kagome", "compass-dodecagonal", "hats", "spectres",
    ];

    internal static readonly string[] Levels = ["easy", "normal", "tricky", "hard"];
    internal const int MaximumSeconds = 3590;

    internal static bool ValidSource(string source) => source.Length is >= 1 and <= 40 &&
        source[0] is >= 'a' and <= 'z' &&
        source.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public bool Enabled { get; set; }
    public string? ExecutablePath { get; set; }
    public int[] Sizes { get; set; } = [];
    public string[] GridTypes { get; set; } = [];
    public string[] Difficulties { get; set; } = [];
    [Range(1, 100)] public int TargetPerCombination { get; set; } = 3;
    [Range(1, MaximumSeconds)] public int TimeoutSeconds { get; set; } = MaximumSeconds;
    [Range(1, MaximumSeconds)] public int ImportTimeoutSeconds { get; set; } = 30;
    [Range(1, 86400)] public int PollIntervalSeconds { get; set; } = 600;

    internal IEnumerable<SlitherlinkBucket> Buckets()
    {
        foreach (var size in Sizes.Length == 0 ? [8, 12, 15] : Sizes.Distinct())
            foreach (var tiling in GridTypes.Length == 0 ? Tilings : GridTypes.Distinct(StringComparer.Ordinal))
                foreach (var difficulty in Difficulties.Length == 0 ? Levels : Difficulties.Distinct(StringComparer.Ordinal))
                    yield return new SlitherlinkBucket(size, tiling, difficulty);
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Sizes.Any(size => size is < 1 or > 100) || GridTypes.Any(tiling => !Tilings.Contains(tiling)) ||
            Difficulties.Any(difficulty => !Levels.Contains(difficulty)))
        {
            yield return new ValidationResult("Use supported Slitherlink sizes, grid types, and difficulty levels.");
            yield break;
        }
        foreach (var bucket in Buckets().DistinctBy(bucket => (bucket.Size, bucket.Tiling)))
            if (!ValidDimensions(bucket.Size, bucket.Size, bucket.Tiling))
                yield return new ValidationResult($"{bucket.Size}×{bucket.Size} is too small for {bucket.Tiling}.");
    }

    internal static bool ValidDimensions(int width, int height, string tiling)
    {
        var index = Array.IndexOf(Tilings, tiling);
        if (index < 0 || width is < 1 or > 100 || height is < 1 or > 100)
            return false;
        var minimum = index switch { 8 => 1, 9 or 10 or 13 or 15 => 2, 16 or 17 => 6, _ => 3 };
        return width >= minimum && height >= minimum &&
            Math.Max(width, height) >= (index switch { 4 => 4, 8 => 2, _ => minimum });
    }
}

internal sealed record SlitherlinkBucket(int Size, string Tiling, string Difficulty);

internal sealed partial class LoopyGenerator(SlitherlinkOptions options)
{
    internal const string Source = "loopy";
    internal const string SourceName = "Loopy";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private string Executable => Path.GetFullPath(options.ExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "slitherlink", "loopgen"));
    internal bool IsAvailable => File.Exists(Executable);

    public async Task<SlitherlinkDefinition> GenerateAsync(SlitherlinkBucket bucket, string seed, CancellationToken token)
    {
        if (!SlitherlinkOptions.ValidDimensions(bucket.Size, bucket.Size, bucket.Tiling) ||
            !SlitherlinkOptions.Levels.Contains(bucket.Difficulty) || !SeedPattern().IsMatch(seed))
            throw new ArgumentException("Use a valid Slitherlink size, tiling, difficulty, and hexadecimal seed.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            var board = await RunAsync("generate", bucket.Size, bucket.Size, bucket.Tiling, bucket.Difficulty, seed, budget.Token);
            if (board.Solution is null)
                throw new InvalidDataException("Loopy omitted its verified solution.");
            // SOLVER_SOLVED requires complete deductions at the requested level.
            // The native helper also checks the single loop; verify it again after remapping.
            var puzzle = board.ToDefinition(bucket.Size, bucket.Size,
                new(bucket.Tiling, Source, SourceName, 0, bucket.Difficulty,
                    "https://www.chiark.greenend.org.uk/~sgtatham/puzzles/js/loopy.html", "MIT", seed));
            budget.Token.ThrowIfCancellationRequested();
            return puzzle;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Loopy ran out of generation time.");
        }
    }

    internal async Task<(NativeLoopBoard Board, int Width, int Height, string Tiling)> DecodeAsync(string gameId, CancellationToken token)
    {
        var match = GameIdPattern().Match(gameId);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var width) ||
            !int.TryParse(match.Groups[2].Value, out var height))
            throw new InvalidDataException("Expected a descriptive Loopy game ID.");
        var index = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        if ((uint)index >= SlitherlinkOptions.Tilings.Length ||
            !SlitherlinkOptions.ValidDimensions(width, height, SlitherlinkOptions.Tilings[index]))
            throw new InvalidDataException("Invalid dimensions or tiling in Loopy game ID.");
        var tiling = SlitherlinkOptions.Tilings[index];
        var board = await RunAsync("decode", width, height, tiling, "easy", match.Groups[4].Value, token);
        return (board, width, height, tiling);
    }

    private async Task<NativeLoopBoard> RunAsync(string mode, int width, int height, string tiling, string difficulty,
        string input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(mode);
        foreach (var argument in new[] { width, height, Array.IndexOf(SlitherlinkOptions.Tilings, tiling),
            Array.IndexOf(SlitherlinkOptions.Levels, difficulty) })
            start.ArgumentList.Add(argument.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(input);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Loopy.");
        var output = ReadAsync(process.StandardOutput, 8 * 1024 * 1024);
        var errors = ReadAsync(process.StandardError, 4096);
        try
        {
            await Task.WhenAll(output, errors, process.WaitForExitAsync(token));
        }
        finally
        {
            Stop();
            await process.WaitForExitAsync(CancellationToken.None);
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Loopy exited with code {process.ExitCode}: {(await errors).Trim()}");
        try
        {
            return JsonSerializer.Deserialize<NativeLoopBoard>(await output, Json)
                ?? throw new InvalidDataException("Loopy returned no board.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Loopy returned invalid JSON.", exception);
        }

        async Task<string> ReadAsync(StreamReader stream, int limit)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                if (count > limit - text.Length)
                {
                    Stop();
                    throw new InvalidDataException("Loopy exceeded its output limit.");
                }
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }

        void Stop()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited before it could be killed.
            }
        }
    }

    [GeneratedRegex("\\A[0-9a-f]{1,64}\\z")]
    private static partial Regex SeedPattern();

    [GeneratedRegex("\\A([0-9]{1,3})x([0-9]{1,3})(?:t([0-9]{1,2}))?(?:d[enth])?:([^\\x00-\\x20]{1,64000})\\z")]
    private static partial Regex GameIdPattern();
}
