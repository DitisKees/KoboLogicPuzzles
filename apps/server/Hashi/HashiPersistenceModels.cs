using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace KoboLogicPuzzles.Server.Hashi;

public sealed record HashiGenerationParameters(string Seed, int RequestedDensity, int Expansion, int UpstreamDifficulty)
{
    public const string BridgesRevision = "616da16f25da428ccda805536209095bd062676e";

    public bool IsValid() => Seed is { Length: >= 1 and <= 64 } &&
        Seed.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')) &&
        RequestedDensity is >= 1 and <= 30 && Expansion is >= 0 and <= 100 && UpstreamDifficulty is >= 0 and <= 2;
}

public sealed record HashiPuzzleRating(HashiDifficulty? Difficulty, int? DifficultyScore, HashiSolvability Solvability,
    HashiTechnique? HardestTechnique, IReadOnlyDictionary<HashiTechnique, int> Deductions, int LookaheadDepth,
    int LookaheadTrials, string? Reason);

public sealed record HashiPuzzleMetadata(HashiGenerationParameters Generation, string GeneratorVersion, string ValidatorVersion,
    int ExactNodes, string ClassifierVersion, HashiPuzzleRating Rating, DateTimeOffset GeneratedAt, int IslandCount,
    double ActualDensity);

public sealed class VerifiedHashiPuzzle
{
    private VerifiedHashiPuzzle(HashiCandidate candidate, HashiPuzzleMetadata metadata)
    {
        Puzzle = candidate.Puzzle;
        Solution = candidate.Solution;
        Metadata = metadata;
    }

    public HashiPuzzle Puzzle { get; }
    public IReadOnlyList<byte> Solution { get; }
    public HashiPuzzleMetadata Metadata { get; }

    public static (HashiValidationResult Validation, VerifiedHashiPuzzle? Puzzle) Verify(HashiCandidate candidate,
        HashiGenerationParameters generation, TimeSpan timeout, DateTimeOffset generatedAt,
        CancellationToken cancellationToken = default)
    {
        if (!generation.IsValid())
            throw new ArgumentException("Supply valid Bridges generation parameters.", nameof(generation));

        var validation = new HashiExactSolver().Validate(candidate, timeout, cancellationToken);
        if (validation.Status != HashiValidationStatus.Unique)
            return (validation, null);

        var classification = new HashiDifficultyClassifier().Classify(candidate.Puzzle, timeout,
            cancellationToken: cancellationToken);
        if (classification.Solution is { } classifiedSolution && !classifiedSolution.SequenceEqual(candidate.Solution))
            return (validation with { Status = HashiValidationStatus.SolutionMismatch, Solution = null }, null);

        var rating = new HashiPuzzleRating(classification.Difficulty, classification.Score, classification.Solvability,
            classification.HardestTechnique, new ReadOnlyDictionary<HashiTechnique, int>(
                classification.Deductions.ToDictionary(pair => pair.Key, pair => pair.Value)),
            classification.LookaheadDepth, classification.LookaheadTrials, classification.Reason);
        var puzzle = candidate.Puzzle;
        var metadata = new HashiPuzzleMetadata(generation, HashiGenerationParameters.BridgesRevision, HashiExactSolver.Version,
            validation.Nodes, HashiDifficultyClassifier.Version, rating, generatedAt, puzzle.Islands.Count,
            100.0 * puzzle.Islands.Count / (puzzle.Width * puzzle.Height));
        return (validation, new VerifiedHashiPuzzle(candidate, metadata));
    }
}

public sealed record StoredHashiPuzzle(HashiPuzzle Puzzle, IReadOnlyList<byte> Solution, HashiPuzzleMetadata Metadata);

public readonly record struct HashiEdgeMark([property: JsonRequired] int Edge, [property: JsonRequired] int Count,
    [property: JsonRequired] bool NoBridge);

public sealed record HashiProgressUpdate(string ContentHash, IReadOnlyList<HashiEdgeMark> Marks, bool Completed);

public sealed record SavedHashiProgress(string ContentHash, IReadOnlyList<HashiEdgeMark> Marks, bool Completed,
    DateTimeOffset BackedUpAt);

public sealed record SavedHashiBoard(StoredHashiPuzzle Puzzle, SavedHashiProgress Progress);
