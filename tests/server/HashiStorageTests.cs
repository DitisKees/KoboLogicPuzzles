using System.Text.Json;
using KoboLogicPuzzles.Server.Hashi;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class HashiStorageTests
{
    private static readonly DateTimeOffset SavedAt = new(2026, 11, 24, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VerifiedDefinitionsAndTheirProvenanceSurviveRestartAndDeduplicate()
    {
        using var database = new TestDatabase();
        var candidate = TestData.HashiPair();
        var verified = TestData.VerifyHashi(candidate);
        Assert.True(database.Store.SaveHashiPuzzle(verified));
        Assert.False(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(candidate, "00000002")));

        var restarted = new PuzzleStore(database.Path);
        restarted.Initialize();
        var stored = Assert.IsType<StoredHashiPuzzle>(restarted.FindHashiPuzzle(candidate.Puzzle.ContentHash));
        Assert.Equal(verified.Puzzle.ContentHash, stored.Puzzle.ContentHash);
        Assert.Equal(verified.Puzzle.Islands, stored.Puzzle.Islands);
        Assert.Equal<byte>(verified.Solution, stored.Solution);
        Assert.Equal(verified.Metadata.Generation, stored.Metadata.Generation);
        Assert.Equal(verified.Metadata.GeneratedAt, stored.Metadata.GeneratedAt);
        Assert.Equal(HashiGenerationParameters.BridgesRevision, stored.Metadata.GeneratorVersion);
        Assert.Equal(HashiExactSolver.Version, stored.Metadata.ValidatorVersion);
        Assert.Equal(HashiDifficultyClassifier.Version, stored.Metadata.ClassifierVersion);
        Assert.Equal(2, stored.Metadata.IslandCount);
        Assert.Equal(0.5, stored.Metadata.ActualDensity);
        Assert.Equal(HashiSolvability.BasicLogic, stored.Metadata.Rating.Solvability);
        Assert.Equal(verified.Metadata.Rating.Deductions, stored.Metadata.Rating.Deductions);
    }

    [Fact]
    public void ValidButAmbiguousWitnessAndTimedOutCandidateCannotBeAdmitted()
    {
        var cells = Enumerable.Repeat('a', 400).ToArray();
        foreach (var cell in new[] { 0, 4, 80, 84 })
            cells[cell] = '3';
        var candidate = HashiCandidate.ParseBridges(20, new string(cells), "S;L0,0,0,4,1;L4,0,4,4,1;L0,0,4,0,2;L0,4,4,4,2");
        var generation = new HashiGenerationParameters("00000001", 10, 10, 2);
        var ambiguous = VerifiedHashiPuzzle.Verify(candidate, generation, TimeSpan.FromSeconds(10), SavedAt);
        Assert.Equal(HashiValidationStatus.Ambiguous, ambiguous.Validation.Status);
        Assert.Null(ambiguous.Puzzle);
        var timeout = VerifiedHashiPuzzle.Verify(TestData.HashiPair(), generation, TimeSpan.Zero, SavedAt);
        Assert.Equal(HashiValidationStatus.Timeout, timeout.Validation.Status);
        Assert.Null(timeout.Puzzle);
    }

    [Fact]
    public void HashiProgressSurvivesWebPbnRefreshAndRestoresBesideNonogramProgress()
    {
        using var database = new TestDatabase();
        database.SeedCatalog([TestData.Summary(12)], SavedAt);
        database.Store.CachePuzzle(TestData.Puzzle(12));
        database.Store.SaveProgress([new ProgressUpdate(12, "#x....", false)], SavedAt);

        var hashi = TestData.VerifyHashi(TestData.HashiPair());
        var id = hashi.Puzzle.ContentHash;
        Assert.True(database.Store.SaveHashiPuzzle(hashi));
        database.Store.SaveHashiProgress([new HashiProgressUpdate(id, [new HashiEdgeMark(0, 0, true)], false)], SavedAt);
        database.SeedCatalog([], SavedAt.AddDays(1));
        var restarted = new PuzzleStore(database.Path);
        restarted.Initialize();

        Assert.Empty(restarted.ListCatalog(null, 10));
        Assert.Equal("#x....", Assert.Single(restarted.RestoreProgress()).Progress.Marks);
        var saved = Assert.Single(restarted.RestoreHashiProgress());
        Assert.Equal(id, saved.Puzzle.Puzzle.ContentHash);
        Assert.Equal(id, saved.Progress.ContentHash);
        Assert.Equal(new HashiEdgeMark(0, 0, true), Assert.Single(saved.Progress.Marks));
        Assert.Equal(SavedAt, saved.Progress.BackedUpAt);
        Assert.False(saved.Progress.Completed);

        restarted.SaveHashiProgress([new HashiProgressUpdate(id, [], false)], SavedAt.AddMinutes(1));
        Assert.Empty(Assert.Single(restarted.RestoreHashiProgress()).Progress.Marks);
        restarted.SaveHashiProgress([new HashiProgressUpdate(id, [new HashiEdgeMark(0, 1, false)], true)], SavedAt.AddMinutes(2));
        Assert.True(Assert.Single(restarted.RestoreHashiProgress()).Progress.Completed);
    }

    [Fact]
    public void RejectsInvalidProgressAtomicallyAndAllowsTentativeDegreeConflicts()
    {
        using var database = new TestDatabase();
        var first = TestData.VerifyHashi(TestData.HashiPair());
        var second = TestData.VerifyHashi(TestData.HashiPair(row: 1));
        database.Store.SaveHashiPuzzle(first);
        database.Store.SaveHashiPuzzle(second);
        var firstId = first.Puzzle.ContentHash;
        var secondId = second.Puzzle.ContentHash;
        var tentative = new HashiProgressUpdate(firstId, [new HashiEdgeMark(0, 2, false)], false);
        database.Store.SaveHashiProgress([tentative], SavedAt);

        var completed = new HashiProgressUpdate(firstId, [new HashiEdgeMark(0, 1, false)], true);
        Assert.Throws<ArgumentException>(() => database.Store.SaveHashiProgress([completed, tentative with { ContentHash = secondId, Completed = true }], SavedAt));
        Assert.Throws<ArgumentException>(() => database.Store.SaveHashiProgress([completed, tentative], SavedAt));
        Assert.Throws<ArgumentException>(() => database.Store.SaveHashiProgress([tentative with { Marks = [new HashiEdgeMark(1, 1, false)] }], SavedAt));
        Assert.Throws<ArgumentException>(() => database.Store.SaveHashiProgress([tentative with { Marks = [new HashiEdgeMark(0, 1, true)] }], SavedAt));
        Assert.Throws<ArgumentException>(() => database.Store.SaveHashiProgress([tentative with { ContentHash = new string('a', 64) }], SavedAt));
        var saved = Assert.Single(database.Store.RestoreHashiProgress()).Progress;
        Assert.False(saved.Completed);
        Assert.Equal(new HashiEdgeMark(0, 2, false), Assert.Single(saved.Marks));
        Assert.Equal(SavedAt, saved.BackedUpAt);
    }

    [Fact]
    public void RejectsCrossingBridgesEvenForIncompleteProgress()
    {
        using var database = new TestDatabase();
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HashiFixtures.json")));
        var fixture = fixtures.RootElement.EnumerateArray().Single(row => row.GetProperty("size").GetInt32() == 20 &&
            row.GetProperty("seed").GetString() == "00000065");
        var candidate = HashiCandidate.ParseBridges(20, fixture.GetProperty("description").GetString()!,
            fixture.GetProperty("solution").GetString()!);
        var verified = TestData.VerifyHashi(candidate, "00000065");
        database.Store.SaveHashiPuzzle(verified);

        var first = new HashiEdgeMark(3, 1, false);
        var second = new HashiEdgeMark(8, 1, false);
        var id = verified.Puzzle.ContentHash;
        database.Store.SaveHashiProgress([new HashiProgressUpdate(id, [first], false)], SavedAt);
        Assert.Throws<ArgumentException>(() => database.Store.SaveHashiProgress(
            [new HashiProgressUpdate(id, [first, second], false)], SavedAt.AddMinutes(1)));
        var progress = Assert.Single(database.Store.RestoreHashiProgress()).Progress;
        Assert.Equal(first, Assert.Single(progress.Marks));
        Assert.Equal(SavedAt, progress.BackedUpAt);
    }
}
