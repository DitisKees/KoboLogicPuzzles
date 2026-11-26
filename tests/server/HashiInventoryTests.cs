using KoboLogicPuzzles.Server.Hashi;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class HashiInventoryTests
{
    private static readonly HashiGenerationParameters Parameters = new("00000001", 10, 10, 2);

    [Fact]
    public async Task ReportsCurrentGenerationAndMovesItToCompletedActivity()
    {
        using var database = new TestDatabase();
        var pending = new TaskCompletionSource<GeneratedHashiCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new Stub((_, _, _) => pending.Task);
        var inventory = Create(database, generator, [20]);

        var attempt = inventory.GenerateOnceAsync(20, Parameters, diagnostic: true, CancellationToken.None);
        var current = Assert.IsType<GenerationJob>(inventory.GetStatus().Activity.Current);
        Assert.Equal(20, current.Size);
        Assert.Equal("generating", current.Stage);

        pending.SetResult(new GeneratedHashiCandidate(TestData.HashiPair(), 1, 2000));
        Assert.Equal("verified", (await attempt).Status);
        var snapshot = inventory.GetStatus().Activity;
        Assert.Null(snapshot.Current);
        var last = Assert.IsType<GenerationResult>(snapshot.Last);
        Assert.Equal("verified", last.Status);
        Assert.Equal(current.StartedAt, last.Job.StartedAt);
        Assert.True(last.FinishedAt >= last.Job.StartedAt);
        Assert.Equal(1, snapshot.Outcomes["verified"]);
    }

    [Fact]
    public void DefaultTargetsEachVerifiedRatingCombinationAtEverySize()
    {
        using var database = new TestDatabase();
        var generator = new Stub((_, _, _) => throw new InvalidOperationException("Status must not generate puzzles."));
        var inventory = new HashiInventory(database.Store, generator, new HashiGenerationOptions(), TimeProvider.System,
            NullLogger<HashiInventory>.Instance);
        var bySize = inventory.GetStatus().Targets.GroupBy(target => target.Size).ToArray();
        Assert.Equal([20, 25, 30, 35, 40, 45, 50], bySize.Select(group => group.Key));
        foreach (var group in bySize)
        {
            Assert.Equal([("easy", "basic_logic"), ("medium", "basic_logic"), ("hard", "advanced_logic"),
                ("expert", "advanced_logic"), ("expert", "bounded_lookahead")],
                group.Select(target => (target.Difficulty, target.Solvability)));
            Assert.All(group, target => Assert.Equal((0, 50), (target.Unstarted, target.Target)));
        }
    }

    [Fact]
    public async Task DefaultInventoryAddsBasicLogicToAnExistingAdvancedLogicCatalog()
    {
        using var database = new TestDatabase();
        var easy = TestData.HashiFixture(20, "00000065", upstreamDifficulty: 0);
        var medium = TestData.HashiFixture(20, "00000066", upstreamDifficulty: 1);
        var hard = TestData.HashiFixture(20, "00000065");
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(hard)));

        var attempts = new List<(int Difficulty, int Density)>();
        var generator = new Stub((_, parameters, _) =>
        {
            attempts.Add((parameters.UpstreamDifficulty, parameters.RequestedDensity));
            var candidate = parameters.UpstreamDifficulty switch
            {
                0 => easy,
                1 => medium,
                _ => hard,
            };
            return Task.FromResult(new GeneratedHashiCandidate(candidate, 1, 2000));
        });
        var inventory = new HashiInventory(database.Store, generator, new HashiGenerationOptions
        {
            Enabled = true, Sizes = [20], TargetPerDifficulty = 1, LowWatermark = 0, AttemptsPerSize = 4,
        }, TimeProvider.System, NullLogger<HashiInventory>.Instance);

        await inventory.RunCycleAsync(CancellationToken.None);

        Assert.Equal([(0, 8), (2, 10), (2, 10), (1, 10)], attempts);
        var status = inventory.GetStatus();
        Assert.Equal([1, 1, 1, 0, 0], status.Targets.Select(target => target.Unstarted));
        Assert.Equal([("easy", "basic_logic"), ("medium", "basic_logic"), ("hard", "advanced_logic")],
            status.Stock.Select(bucket => (bucket.Difficulty, bucket.Solvability)));
        Assert.Equal(2, status.Outcomes["stored"]);
        Assert.Equal(2, status.Outcomes["rating_rejected"]);
    }

    [Fact]
    public async Task AFullExpertAdvancedBucketDoesNotSatisfyTheExpertLookaheadTarget()
    {
        using var database = new TestDatabase();
        var advanced = TestData.HashiFixture(20, "00000005");
        var lookahead = TestData.HashiFixture(20, "00000066");
        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(advanced, "00000005")));
        var candidates = new Queue<HashiCandidate>([advanced, lookahead]);
        var generator = new Stub((_, _, _) => Task.FromResult(new GeneratedHashiCandidate(candidates.Dequeue(), 1, 2000)));
        var inventory = new HashiInventory(database.Store, generator, new HashiGenerationOptions
        {
            Sizes = [20], Difficulties = [HashiDifficulty.Expert], TargetPerDifficulty = 1,
        }, TimeProvider.System, NullLogger<HashiInventory>.Instance);

        Assert.False(await inventory.RunCycleAsync(CancellationToken.None));
        Assert.Empty(candidates);
        var status = inventory.GetStatus();
        Assert.Equal([("advanced_logic", 1), ("bounded_lookahead", 1)],
            status.Targets.Select(target => (target.Solvability, target.Unstarted)));
        Assert.Equal(1, status.Outcomes["rating_rejected"]);
        Assert.Equal(1, status.Outcomes["stored"]);
    }

    [Fact]
    public async Task ProductivePassesContinueUntilFiftyAndDefaultRefillStartsAtFortyNine()
    {
        using var database = new TestDatabase();
        var generated = 0;
        var generator = new Stub((size, _, _) =>
        {
            var candidate = TestData.HashiPair(row: generated / 16, size: size, column: generated % 16);
            generated++;
            return Task.FromResult(new GeneratedHashiCandidate(candidate, 1, 2000));
        });
        var inventory = new HashiInventory(database.Store, generator, new HashiGenerationOptions
        {
            Sizes = [20], Difficulties = [HashiDifficulty.Easy], MinIslands = 2, MinDensityPercent = 0,
        }, TimeProvider.System, NullLogger<HashiInventory>.Instance);

        for (var count = 10; count < 50; count += 10)
        {
            Assert.True(await inventory.RunCycleAsync(CancellationToken.None));
            Assert.Equal(count, Assert.Single(inventory.GetStatus().Targets).Unstarted);
        }
        Assert.False(await inventory.RunCycleAsync(CancellationToken.None));
        Assert.Equal(50, Assert.Single(inventory.GetStatus().Targets).Unstarted);
        Assert.Equal(50, generated);

        var played = TestData.HashiPair();
        database.Store.SaveHashiProgress([new HashiProgressUpdate(played.Puzzle.ContentHash, [], false)], DateTimeOffset.UtcNow);
        Assert.False(await inventory.RunCycleAsync(CancellationToken.None));
        Assert.Equal(50, Assert.Single(inventory.GetStatus().Targets).Unstarted);
        Assert.Equal(51, Assert.Single(inventory.GetStatus().Stock).Total);
        Assert.Single(database.Store.RestoreHashiProgress());
    }

    [Fact]
    public async Task RefillsEverySizeAndKeepsReplenishingAfterAPartialPass()
    {
        using var database = new TestDatabase();
        var generated = new List<(int Size, HashiGenerationParameters Parameters, string Hash)>();
        var generator = new Stub((size, parameters, _) =>
        {
            var candidate = TestData.HashiPair(row: generated.Count, size: size);
            generated.Add((size, parameters, candidate.Puzzle.ContentHash));
            return Task.FromResult(new GeneratedHashiCandidate(candidate, 1, 2000));
        });
        var inventory = Create(database, generator, [20, 25]);
        await inventory.RunCycleAsync(CancellationToken.None);
        Assert.Equal(new[] { 20, 25 }, generated.Select(item => item.Size));
        Assert.All(inventory.GetStatus().Targets, target => Assert.Equal(1, target.Unstarted));
        await inventory.RunCycleAsync(CancellationToken.None);
        Assert.Equal(new[] { 20, 25, 20, 25 }, generated.Select(item => item.Size));
        Assert.Equal(new[] { 10, 10, 20, 20 }, generated.Select(item => item.Parameters.RequestedDensity));
        Assert.Equal(4, generated.Select(item => item.Parameters.Seed).Distinct().Count());
        Assert.All(inventory.GetStatus().Targets, target => Assert.Equal(2, target.Unstarted));
        await inventory.RunCycleAsync(CancellationToken.None);
        Assert.Equal(4, generated.Count);

        database.Store.SaveHashiProgress(generated.Where(item => item.Size == 20)
            .Select(item => new HashiProgressUpdate(item.Hash, [], false)).ToArray(), DateTimeOffset.UtcNow);
        await inventory.RunCycleAsync(CancellationToken.None);
        Assert.Equal(20, generated[^1].Size);
        Assert.Equal(5, generated.Count);
        Assert.Equal(2, database.Store.RestoreHashiProgress().Count);
        Assert.Equal(5, inventory.GetStatus().Outcomes["stored"]);
    }

    [Fact]
    public async Task BoundsFailuresAndDuplicatesAndProtectsExistingInventory()
    {
        using var database = new TestDatabase();
        var candidate = TestData.HashiPair();
        var attempts = 0;
        var generator = new Stub((_, _, _) => ++attempts == 1 ? throw new TimeoutException("test timeout") :
            Task.FromResult(new GeneratedHashiCandidate(candidate, 1, 2000)));
        var inventory = Create(database, generator, [20], attempts: 3);
        await inventory.RunCycleAsync(CancellationToken.None);
        Assert.Equal(3, attempts);
        var status = inventory.GetStatus();
        Assert.Equal(1, status.Outcomes["generation_timeout"]);
        Assert.Equal(1, status.Outcomes["stored"]);
        Assert.Equal(1, status.Outcomes["duplicate"]);
        Assert.Equal(1, Assert.Single(status.Stock).Unstarted);
        Assert.Equal("unique", status.LastAttempt!.Validation);

        Assert.True(database.Store.SaveHashiPuzzle(TestData.VerifyHashi(TestData.HashiPair(row: 1))));
        var manual = await inventory.GenerateOnceAsync(20, Parameters, diagnostic: false, CancellationToken.None);
        Assert.Equal("capacity", manual.Status);
        Assert.Equal(3, attempts);
        await inventory.RunCycleAsync(CancellationToken.None);
        Assert.Equal(3, attempts);
        Assert.NotNull(database.Store.FindHashiPuzzle(candidate.Puzzle.ContentHash));
    }

    [Fact]
    public async Task DiagnosticVerifiesWithoutStoringAndConcurrentGenerationUsesOneLock()
    {
        using var database = new TestDatabase();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new Stub(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The test generator should be canceled.");
        });
        var active = Create(database, generator, [20]);
        var running = active.RunCycleAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var candidate = TestData.HashiPair();
        var other = Create(database, new Stub((_, _, _) => Task.FromResult(new GeneratedHashiCandidate(candidate, 1, 2000))), [20]);
        try
        {
            var busy = await other.GenerateOnceAsync(20, Parameters, diagnostic: true, CancellationToken.None);
            Assert.Equal("busy", busy.Status);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }

        var diagnostic = await other.GenerateOnceAsync(20, Parameters, diagnostic: true, CancellationToken.None);
        Assert.Equal("verified", diagnostic.Status);
        Assert.Equal("unique", diagnostic.Validation);
        Assert.Null(database.Store.FindHashiPuzzle(candidate.Puzzle.ContentHash));
    }

    private static HashiInventory Create(TestDatabase database, IHashiCandidateGenerator generator, int[] sizes, int attempts = 1) =>
        new(new PuzzleStore(database.Path), generator, new HashiGenerationOptions
        {
            Enabled = true, Sizes = sizes, Difficulties = [HashiDifficulty.Easy], AttemptsPerSize = attempts,
            Densities = [10, 20],
            TargetPerDifficulty = 2, LowWatermark = 0, MaxUnstartedPerSize = 2, MinIslands = 2, MinDensityPercent = 0,
        }, TimeProvider.System, NullLogger<HashiInventory>.Instance);

    private sealed class Stub(Func<int, HashiGenerationParameters, CancellationToken, Task<GeneratedHashiCandidate>> generate) :
        IHashiCandidateGenerator
    {
        public Task<GeneratedHashiCandidate> GenerateAsync(int size, HashiGenerationParameters parameters, TimeSpan timeout,
            CancellationToken cancellationToken) => generate(size, parameters, cancellationToken);
    }
}
