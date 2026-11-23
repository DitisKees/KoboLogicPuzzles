using System.Runtime.CompilerServices;

namespace KoboNonogram.Server;

public sealed class PuzzleLibrary(PuzzleStore store, WebPbnClient webPbn, WebPbnOptions options, TimeProvider time)
{
    public Task<int?> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshAsync(store.GetCatalogRefreshState(), cancellationToken);

    public async Task<int?> RefreshAsync(CatalogRefreshState previous, CancellationToken cancellationToken)
    {
        var catalog = new Dictionary<int, PuzzleSummary>();
        await foreach (var puzzle in ReadCatalogAsync(options.Solvability, cancellationToken))
            catalog[puzzle.Id] = puzzle;

        // The result table groups some solvability levels under the same icon.
        // Search by level to use WebPBN's exact classification.
        if (options.Solvability == 0)
        {
            var unclassified = catalog.Keys.ToHashSet();
            for (var level = 1; level <= 6 && unclassified.Count > 0; level++)
            {
                await foreach (var puzzle in ReadCatalogAsync(level, cancellationToken))
                {
                    if (unclassified.Remove(puzzle.Id))
                        catalog[puzzle.Id] = puzzle;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return store.TryReplaceCatalog(catalog.Values, previous.Revision, time.GetUtcNow()) ? catalog.Count : null;
    }

    private async IAsyncEnumerable<PuzzleSummary> ReadCatalogAsync(int solvability,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<int>();
        for (var pageNumber = 0; pageNumber < 1000; pageNumber++)
        {
            var page = await webPbn.SearchAsync(pageNumber, cancellationToken, solvability);
            var newCount = 0;
            foreach (var puzzle in page.Puzzles)
            {
                if (seen.Add(puzzle.Id))
                    newCount++;
                if (options.Fits(puzzle.Width, puzzle.Height))
                    yield return solvability == 0 ? puzzle :
                        puzzle with { Metadata = puzzle.Metadata! with { Solvability = solvability } };
            }

            if (page.Puzzles.Count > 0 && newCount == 0)
                throw new InvalidDataException("WebPBN repeated a page of catalog results.");

            if (!page.HasMore)
                yield break;
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
                    return store.CacheSolution(cached.Id, solution);
            }
            return cached;
        }
        if (!store.IsInCatalog(id))
            return null;

        var puzzle = await webPbn.FetchAsync(id, cancellationToken);
        return puzzle is not null && options.Fits(puzzle.Width, puzzle.Height) ? store.CachePuzzle(puzzle) : null;
    }

    public async Task<PuzzleDefinition?> GetPuzzleForBackupAsync(int id, CancellationToken cancellationToken)
    {
        var cached = store.FindPuzzle(id);
        if (cached is not null)
            return cached;

        // Progress can refer to a puzzle downloaded before a catalog change or database reset.
        var puzzle = await webPbn.FetchAsync(id, cancellationToken);
        return puzzle is null ? null : store.CachePuzzle(puzzle);
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
