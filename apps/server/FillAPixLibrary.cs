namespace KoboLogicPuzzles.Server;

internal sealed class FillAPixLibrary(PuzzleStore store, FillAPixClient client, TimeProvider time)
{
    public async Task<int?> RefreshAsync(FillAPixSource source, CatalogRefreshState previous, CancellationToken token)
    {
        if (source == FillAPixSource.GrandGames)
            return await RefreshGrandGamesAsync(previous.Revision, token);

        var index = await client.ReadIndexAsync(source, token);
        var cached = store.ListCachedFillAPix(source);
        var entries = new List<FillAPixCatalogEntry>(index.Count);
        foreach (var (number, title) in index)
        {
            token.ThrowIfCancellationRequested();
            var id = PuzzleId.FromFillAPix(source, number);
            if (!cached.TryGetValue(id, out var entry))
                entry = FillAPixCatalogEntry.FromDefinition(id, await client.FetchIndexedAsync(id, title, token));
            entries.Add(entry);
        }
        return store.ReplaceFillAPixCatalog(source, entries, previous.Revision, time.GetUtcNow(), token);
    }

    private async Task<int?> RefreshGrandGamesAsync(long revision, CancellationToken token)
    {
        while (store.GetGrandGamesNextPage(revision) is { } page)
        {
            var (entries, hasMore) = await client.ReadGrandGamesCatalogPageAsync(page, token);
            if (store.SaveGrandGamesCatalogPage(entries, hasMore, page, revision, time.GetUtcNow(), token) is { } completedCount)
                return completedCount;
        }
        return null;
    }

    public async ValueTask<FillAPixPuzzleDefinition?> GetDefinitionAsync(PuzzleId id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (store.FindFillAPix(id) is not { } stored)
            return null;
        if (stored.Entry.Definition is { } cached)
            return cached;
        if (!stored.Active || id.FillAPixSource != FillAPixSource.GrandGames)
            return null;
        var fetched = await client.FetchGrandGamesAsync(stored.Entry, token);
        token.ThrowIfCancellationRequested();
        return fetched is null ? null : store.CacheFillAPix(id, fetched);
    }
}

internal sealed class FillAPixRefreshWorker(PuzzleStore store, FillAPixLibrary library, TimeProvider time,
    ILogger<FillAPixRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enum.GetValues<FillAPixSource>().Select(source => RunSourceAsync(source, stoppingToken)));

    private async Task RunSourceAsync(FillAPixSource source, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var previous = store.GetFillAPixRefreshState(source);
                var delay = previous.CompletedAt is { } completed ? Interval - (time.GetUtcNow() - completed) : TimeSpan.Zero;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay < Interval ? delay : Interval, time, token);
                    continue;
                }
                var count = await library.RefreshAsync(source, previous, token);
                if (count is not null)
                    logger.LogInformation("Fill-a-Pix refreshed {Source} with {PuzzleCount} puzzles.", source.Slug(), count);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Fill-a-Pix refresh failed for {Source}. Retrying in one hour.", source.Slug());
                await Task.Delay(RetryDelay, time, token);
            }
        }
    }
}

internal sealed class FillAPixPuzzleProvider(FillAPixLibrary library, PuzzleStore store) : IPuzzleProvider
{
    public PuzzleGame Game => PuzzleGame.FillAPix;

    public async ValueTask<GamePuzzleDefinition?> GetDefinitionAsync(PuzzleId id, bool includeSolution, CancellationToken cancellationToken)
    {
        var puzzle = await library.GetDefinitionAsync(id, cancellationToken);
        return includeSolution || puzzle is null ? puzzle : puzzle with { Solution = null };
    }

    public ValueTask<GamePuzzleDefinition?> GetOfflineDefinitionAsync(PuzzleId id, CancellationToken cancellationToken) =>
        GetDefinitionAsync(id, includeSolution: true, cancellationToken);

    public IReadOnlyList<GamePuzzleSummary> ListCatalog(PuzzleFilter filter, PuzzleId? before, int limit, bool unstartedOnly) =>
        store.ListFillAPixCatalog(filter, before, limit, unstartedOnly);

    public bool IsMatchingUnstartedCache(PuzzleId id, PuzzleFilter filter) =>
        store.ListFillAPixCatalog(filter, null, 1, unstartedOnly: true, exactId: id).Count > 0;

    public IReadOnlyDictionary<string, object> ListMetadata(IReadOnlyList<PuzzleId> ids) => store.ListFillAPixMetadata(ids);
}
