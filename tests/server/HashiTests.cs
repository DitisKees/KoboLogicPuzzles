using System.Text.Json;
using KoboNonogram.Server.Hashi;

namespace KoboNonogram.Server.Tests;

public class HashiTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    [Fact]
    public void SeededBridgesPuzzlesMatchTheReferenceAcrossAllSizes()
    {
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HashiFixtures.json")), JsonSerializerOptions.Web)!;
        Assert.Equal([20, 25, 30, 35, 40, 45, 50], fixtures.Select(fixture => fixture.Size).Distinct().Order());

        var exact = new HashiExactSolver();
        var classifier = new HashiDifficultyClassifier();
        foreach (var fixture in fixtures)
        {
            var candidate = HashiCandidate.ParseBridges(fixture.Size, fixture.Description, fixture.Solution);
            Assert.Equal(fixture.IslandCount, candidate.Puzzle.Islands.Count);
            Assert.Equal(fixture.ContentHash, candidate.Puzzle.ContentHash);
            var reordered = HashiPuzzle.Create(fixture.Size, fixture.Size, candidate.Puzzle.Islands.Reverse());
            Assert.Equal(fixture.ContentHash, reordered.ContentHash);

            var validation = exact.Validate(candidate, Budget);
            Assert.Equal(HashiValidationStatus.Unique, validation.Status);
            Assert.Equal(1, validation.SolutionsFound);
            Assert.Equal(fixture.ExactNodes, validation.Nodes);
            Assert.Equal<byte>(candidate.Solution, validation.Solution!);

            var rating = classifier.Classify(candidate.Puzzle, Budget);
            Assert.Equal(fixture.Solvability, SnakeCase(rating.Solvability));
            Assert.Equal(fixture.Difficulty, SnakeCase(rating.Difficulty!.Value));
            Assert.Equal(fixture.Score, rating.Score);
            Assert.Equal(fixture.HardestTechnique, SnakeCase(rating.HardestTechnique!.Value));
            Assert.Equal(fixture.LookaheadDepth, rating.LookaheadDepth);
            Assert.Equal(fixture.LookaheadTrials, rating.LookaheadTrials);
            Assert.Equal(fixture.Deductions.OrderBy(pair => pair.Key), rating.Deductions
                .Select(pair => new KeyValuePair<string, int>(SnakeCase(pair.Key), pair.Value)).OrderBy(pair => pair.Key));
            Assert.Equal(rating.Deductions.Values.Sum(), rating.Trace.Count);
            Assert.Equal<byte>(candidate.Solution, rating.Solution!);
            Assert.Null(rating.Reason);
        }
    }

    [Fact]
    public void ExactCountsMatchAnExhaustiveEnumerationOfACycle()
    {
        var exact = new HashiExactSolver();
        var possibleCounts = Enumerable.Range(0, 3).ToArray();
        var layouts =
            from first in possibleCounts
            from second in possibleCounts
            from third in possibleCounts
            from fourth in possibleCounts
            let counts = new[] { first, second, third, fourth }
            where counts.Count(count => count > 0) >= 3
            select (first + second, first + third, second + fourth, third + fourth);
        var expected = layouts.GroupBy(clues => clues).ToDictionary(group => group.Key, group => group.Count());

        for (var first = 1; first <= 4; first++)
        for (var second = 1; second <= 4; second++)
        for (var third = 1; third <= 4; third++)
        for (var fourth = 1; fourth <= 4; fourth++)
        {
            var puzzle = HashiPuzzle.Create(5, 5,
                [new(0, 0, first), new(0, 4, second), new(4, 0, third), new(4, 4, fourth)]);
            var count = Math.Min(2, expected.GetValueOrDefault((first, second, third, fourth)));
            var result = exact.CountSolutions(puzzle, Budget);
            Assert.Equal(count, result.SolutionsFound);
            Assert.Equal(count switch
            {
                0 => HashiValidationStatus.Unsatisfiable,
                1 => HashiValidationStatus.Unique,
                _ => HashiValidationStatus.Ambiguous,
            }, result.Status);
        }
    }

    [Fact]
    public void SolutionRulesRejectCrossingsAndDisconnectedDegreeMatchingGroups()
    {
        var square = Square(2);
        Assert.False(square.IsSolution([2, 0, 0, 2]));
        Assert.True(square.IsSolution([1, 1, 1, 1]));

        var cross = HashiPuzzle.Create(5, 5,
            [new(0, 0, 2), new(0, 2, 3), new(0, 4, 2), new(2, 0, 3), new(2, 4, 3),
             new(4, 0, 2), new(4, 2, 3), new(4, 4, 2)]);
        Assert.Equal(10, cross.Edges.Count);
        Assert.False(cross.IsSolution(Enumerable.Repeat((byte)1, cross.Edges.Count).ToArray()));
    }

    [Fact]
    public void BridgesImportRejectsInvalidMovesAndRetainsAValidAmbiguousWitness()
    {
        var description = Enumerable.Repeat('a', 400).ToArray();
        foreach (var cell in new[] { 0, 4, 80, 84 })
            description[cell] = '3';
        var layout = new string(description);
        var candidate = HashiCandidate.ParseBridges(20, layout, "S;L0,0,0,4,1;L4,0,4,4,1;L0,0,4,0,2;L0,4,4,4,2");
        Assert.Equal(HashiValidationStatus.Ambiguous, new HashiExactSolver().Validate(candidate, Budget).Status);
        Assert.Throws<FormatException>(() => HashiCandidate.ParseBridges(20, layout, "S;L0,0,4,4,2"));
        description[2] = '1';
        Assert.Throws<FormatException>(() => HashiCandidate.ParseBridges(20, new string(description), "S;L0,0,4,0,2"));
        description[2] = '9';
        Assert.Throws<FormatException>(() => HashiCandidate.ParseBridges(20, new string(description), "S"));
    }

    [Fact]
    public void TimeoutAfterOneSolutionLeavesUniquenessUnverified()
    {
        var result = new HashiExactSolver(new TickClock()).CountSolutions(Square(2), TimeSpan.FromSeconds(12));
        Assert.Equal(HashiValidationStatus.Timeout, result.Status);
        Assert.Equal(1, result.SolutionsFound);
        Assert.Null(result.Solution);
    }

    [Fact]
    public void HumanSolvabilityDistinguishesLookaheadExhaustionAndTimeout()
    {
        var puzzle = HashiPuzzle.Create(5, 5,
            [new(0, 0, 1), new(0, 2, 3), new(0, 4, 2), new(4, 0, 1), new(4, 2, 3), new(4, 4, 2)]);
        var classifier = new HashiDifficultyClassifier();
        Assert.Equal(HashiSolvability.RequiresSearch, classifier.Classify(puzzle, Budget, allowLookahead: false).Solvability);
        Assert.Equal(HashiSolvability.Unknown, classifier.Classify(puzzle, TimeSpan.Zero).Solvability);
        var rated = classifier.Classify(puzzle, Budget);
        Assert.Equal(HashiSolvability.BoundedLookahead, rated.Solvability);
        Assert.Equal(1, rated.LookaheadDepth);
        Assert.Contains(rated.Trace, step => step.Technique == HashiTechnique.Lookahead && step.Reason is not null);
        var cancelled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => classifier.Classify(puzzle, Budget, cancellationToken: cancelled));
        Assert.Throws<OperationCanceledException>(() => new HashiExactSolver().CountSolutions(puzzle, Budget, cancelled));
    }

    private static HashiPuzzle Square(int clue) => HashiPuzzle.Create(5, 5,
        [new(0, 0, clue), new(0, 4, clue), new(4, 0, clue), new(4, 4, clue)]);

    private static string SnakeCase<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private sealed record Fixture(int Size, string Description, string Solution, int IslandCount, string ContentHash,
        int ExactNodes, string Solvability, string Difficulty, int Score, string HardestTechnique,
        Dictionary<string, int> Deductions, int LookaheadDepth, int LookaheadTrials);

    private sealed class TickClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => ticks++;
    }
}
