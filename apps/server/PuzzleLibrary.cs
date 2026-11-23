namespace KoboNonogram.Server;

public sealed class PuzzleLibrary(PuzzleStore store, WebPbnClient webPbn, WebPbnOptions options, TimeProvider time)
{
    public Task<int?> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshAsync(store.GetCatalogRefreshState(), cancellationToken);

    public async Task<int?> RefreshAsync(CatalogRefreshState previous, CancellationToken cancellationToken)
    {
        var catalog = new Dictionary<int, PuzzleSummary>();
        var seen = new HashSet<int>();
        for (var pageNumber = 0; pageNumber < 1000; pageNumber++)
        {
            var page = await webPbn.SearchAsync(pageNumber, cancellationToken);
            var newCount = 0;
            foreach (var puzzle in page.Puzzles)
            {
                if (seen.Add(puzzle.Id))
                    newCount++;
                if (options.Fits(puzzle.Width, puzzle.Height))
                    catalog[puzzle.Id] = puzzle;
            }

            if (page.Puzzles.Count > 0 && newCount == 0)
                throw new InvalidDataException("WebPBN repeated a page of catalog results.");

            if (!page.HasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return store.TryReplaceCatalog(catalog.Values, previous.Revision, time.GetUtcNow()) ? catalog.Count : null;
            }
        }

        throw new InvalidDataException("WebPBN search exceeded 1,000 pages.");
    }

    public async Task<PuzzleDefinition?> GetPuzzleAsync(int id, CancellationToken cancellationToken,
        bool includeSolution = false)
    {
        var cached = store.FindPuzzle(id);
        if (cached is not null)
        {
            if (includeSolution && cached.Solution is null)
            {
                var refreshed = await webPbn.FetchAsync(id, cancellationToken);
                if (refreshed?.Solution is { } solution && refreshed.Width == cached.Width &&
                    refreshed.Height == cached.Height && cached.MatchesClues(solution))
                    return store.CacheSolution(cached, solution);
            }
            return cached;
        }
        if (!store.IsInCatalog(id))
            return null;

        var puzzle = await webPbn.FetchAsync(id, cancellationToken);
        return puzzle is not null && options.Fits(puzzle.Width, puzzle.Height) ? store.CachePuzzle(puzzle) : null;
    }

    public async Task<IReadOnlyList<PuzzleDefinition>> TopUpAsync(int targetCount, int[] cachedUnstartedIds,
        CancellationToken cancellationToken)
    {
        var cached = cachedUnstartedIds.ToHashSet();
        var needed = targetCount - cached.Count;
        var puzzles = new List<PuzzleDefinition>();
        if (needed <= 0)
            return puzzles;

        foreach (var id in store.ListUnstartedIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cached.Contains(id))
                continue;

            var puzzle = await GetPuzzleAsync(id, cancellationToken);
            if (puzzle is not null)
                puzzles.Add(puzzle);
            if (puzzles.Count == needed)
                break;
        }

        return puzzles;
    }
}
