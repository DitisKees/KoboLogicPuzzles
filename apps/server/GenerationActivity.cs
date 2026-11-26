namespace KoboLogicPuzzles.Server;

public sealed record GenerationJob(int Size, DateTimeOffset StartedAt, string Stage, string? Difficulty,
    string? Solvability, string? Tiling);

public sealed record GenerationResult(GenerationJob Job, string Status, DateTimeOffset FinishedAt);

public sealed record GenerationActivitySnapshot(GenerationJob? Current, GenerationResult? Last,
    IReadOnlyDictionary<string, long> Outcomes);

internal sealed class GenerationActivity(TimeProvider time)
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> outcomes = new(StringComparer.Ordinal);
    private GenerationJob? current;
    private GenerationResult? last;

    public void Begin(int size, string? difficulty = null, string? solvability = null, string? tiling = null)
    {
        lock (gate)
            current = new(size, time.GetUtcNow(), "generating", difficulty, solvability, tiling);
    }

    public void Verifying()
    {
        lock (gate)
            if (current is not null)
                current = current with { Stage = "verifying" };
    }

    public void Finish(string status, string? difficulty = null, string? solvability = null)
    {
        lock (gate)
        {
            if (current is null)
                return;
            var job = current with { Difficulty = difficulty ?? current.Difficulty, Solvability = solvability ?? current.Solvability };
            last = new(job, status, time.GetUtcNow());
            outcomes[status] = outcomes.GetValueOrDefault(status) + 1;
            current = null;
        }
    }

    public void Clear()
    {
        lock (gate)
            current = null;
    }

    public GenerationActivitySnapshot Snapshot()
    {
        lock (gate)
            return new(current, last, new Dictionary<string, long>(outcomes, StringComparer.Ordinal));
    }
}
