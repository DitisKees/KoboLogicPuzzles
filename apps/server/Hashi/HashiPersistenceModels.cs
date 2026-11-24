using System.Collections.ObjectModel;

namespace KoboNonogram.Server.Hashi;

public sealed record HashiGenerationParameters(string Seed, int RequestedDensity, int Expansion, int UpstreamDifficulty)
{
    public const string BridgesRevision = "616da16f25da428ccda805536209095bd062676e";
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
        if (generation.Seed is not { Length: >= 1 and <= 64 } ||
            generation.Seed.Any(character => character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) ||
            generation.RequestedDensity is < 1 or > 30 || generation.Expansion is < 0 or > 100 ||
            generation.UpstreamDifficulty is < 0 or > 2)
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

public readonly record struct HashiEdgeMark(int Edge, int Count, bool NoBridge);

public sealed record HashiProgressUpdate(string ContentHash, IReadOnlyList<HashiEdgeMark> Marks, bool Completed);

public sealed record SavedHashiProgress(string ContentHash, IReadOnlyList<HashiEdgeMark> Marks, bool Completed,
    DateTimeOffset BackedUpAt);

public sealed record SavedHashiBoard(StoredHashiPuzzle Puzzle, SavedHashiProgress Progress);
