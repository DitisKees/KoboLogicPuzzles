using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace KoboNonogram.Server.Hashi;

public sealed record GeneratedHashiCandidate(HashiCandidate Candidate, double GenerationMilliseconds, long PeakRssKiB);

public interface IHashiCandidateGenerator
{
    Task<GeneratedHashiCandidate> GenerateAsync(int size, HashiGenerationParameters parameters, TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class BridgesGenerator(HashiGenerationOptions options) : IHashiCandidateGenerator
{
    public async Task<GeneratedHashiCandidate> GenerateAsync(int size, HashiGenerationParameters parameters, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!HashiGenerationOptions.IsSupportedSize(size) || !parameters.IsValid() || timeout <= TimeSpan.Zero)
            throw new ArgumentException("Supply a supported Hashi size, valid generator parameters, and positive timeout.");
        cancellationToken.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(options.ExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "hashi", "bridgegen")),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { size, parameters.RequestedDensity, parameters.Expansion, parameters.UpstreamDifficulty })
            start.ArgumentList.Add(argument.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parameters.Seed);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Bridges.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var output = ReadBoundedAsync(process.StandardOutput, 42 * size * size + 100, process, deadline.Token);
        var errors = ReadBoundedAsync(process.StandardError, 4096, process, deadline.Token);
        try
        {
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Bridges ran out of generation time.");
        }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Bridges exited with code {process.ExitCode}: {(await errors).Trim()}");

        using var lines = new StringReader(await output);
        var description = lines.ReadLine();
        var solution = lines.ReadLine();
        var measurements = lines.ReadLine()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (description is null || solution is null || lines.ReadLine() is not null || measurements is not { Length: 2 } ||
            !double.TryParse(measurements[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var milliseconds) ||
            !double.IsFinite(milliseconds) || milliseconds < 0 ||
            !long.TryParse(measurements[1], NumberStyles.None, CultureInfo.InvariantCulture, out var peak) || peak < 0)
            throw new InvalidDataException("Bridges returned invalid output or measurements.");
        var candidate = HashiCandidate.ParseBridges(size, description, solution);
        if (candidate.Puzzle.Islands.Count > size * size * parameters.RequestedDensity / 100)
            throw new InvalidDataException("Bridges returned more islands than requested.");
        return new GeneratedHashiCandidate(candidate, milliseconds, peak);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, Process process, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (read > limit - text.Length)
            {
                Stop(process);
                throw new InvalidDataException("Bridges exceeded its output limit.");
            }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    private static void Stop(Process process)
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
