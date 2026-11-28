using System.Net;
using System.Net.Http.Headers;
using KoboLogicPuzzles.Server.GrandGames;

namespace KoboLogicPuzzles.Server.Slitherlink;

internal sealed partial class SlitherlinkCollectionRefresh(PuzzleStore store, SlitherlinkImporter importer, HttpClient http,
    TimeProvider time, GrandGamesRequests grandGamesRequests, ILogger<SlitherlinkCollectionRefresh> logger)
{
    internal const string PuzzleKit = "puzzlekit";
    internal const string Mindgames = "mindgames";
    private const string PuzzleKitRepository = "https://api.github.com/repos/SmilingWayne/puzzlekit-dataset";
    private const string PuzzleKitIndex = PuzzleKitRepository + "/contents/assets/data/Slitherlink";
    private const string MindgamesArchive = "https://ulhaque.co.uk/common/exe/slitherlink20k.zip";

    private sealed record PuzzleKitFile(string? Name, string? Type, string? Sha);

    public async Task RefreshPuzzleKitAsync(CancellationToken token)
    {
        using var indexResponse = await http.GetAsync(PuzzleKitIndex, token);
        indexResponse.EnsureSuccessStatusCode();
        var index = await indexResponse.Content.ReadFromJsonAsync<PuzzleKitFile[]>(cancellationToken: token);
        var files = index?.Where(file => file.Type == "file" && file.Name is { } name &&
            name.StartsWith("Slitherlink_dataset", StringComparison.Ordinal) &&
            name.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        if (files is not { Length: > 0 })
            throw new InvalidDataException("The PuzzleKit index has no Slitherlink JSON files.");

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (file.Sha is not { Length: 40 } sha || !sha.All(Uri.IsHexDigit))
                throw new InvalidDataException("The PuzzleKit index has an invalid file revision.");
            var resource = $"{PuzzleKit}/{file.Name}";
            if (store.GetSlitherlinkCollectionRefreshState(resource)?.Version == sha)
                continue;

            // Read the indexed blob so a repository update cannot change the content mid-refresh.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{PuzzleKitRepository}/git/blobs/{sha}");
            request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
            using var response = await http.SendAsync(request, token);
            await ImportAsync(response, PuzzleKit, ".json", token);
            store.SaveSlitherlinkCollectionRefreshState(resource, time.GetUtcNow(), sha);
        }
        store.SaveSlitherlinkCollectionRefreshState(PuzzleKit, time.GetUtcNow(), null);
    }

    public async Task RefreshMindgamesAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MindgamesArchive);
        var current = store.GetSlitherlinkCollectionRefreshState(Mindgames);
        if (EntityTagHeaderValue.TryParse(current?.Version, out var tag))
            request.Headers.IfNoneMatch.Add(tag);

        using var response = await http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotModified && tag is not null)
        {
            store.SaveSlitherlinkCollectionRefreshState(Mindgames, time.GetUtcNow(), tag.ToString());
            return;
        }
        await ImportAsync(response, Mindgames, ".zip", token);
        store.SaveSlitherlinkCollectionRefreshState(Mindgames, time.GetUtcNow(), response.Headers.ETag?.ToString());
    }

    private async Task ImportAsync(HttpResponseMessage response, string collection, string extension, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        var directory = Directory.CreateTempSubdirectory("kobo-slitherlink-");
        try
        {
            var path = Path.Combine(directory.FullName, collection + extension);
            await using (var stream = File.Create(path))
                await response.Content.CopyToAsync(stream, token);
            var result = await importer.ImportAsync(path, null, null, token);
            logger.LogInformation("Imported Slitherlink {Collection}: {Stored} stored, {Duplicates} already present, {Rejected} rejected.",
                collection, result.Stored, result.Duplicates, result.Rejected);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

internal sealed class SlitherlinkCollectionRefreshWorker(PuzzleStore store, SlitherlinkCollectionRefresh collections,
    TimeProvider time, ILogger<SlitherlinkCollectionRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunSourceAsync(SlitherlinkCollectionRefresh.PuzzleKit, collections.RefreshPuzzleKitAsync, stoppingToken),
            RunSourceAsync(SlitherlinkCollectionRefresh.Mindgames, collections.RefreshMindgamesAsync, stoppingToken),
            RunSourceAsync(SlitherlinkCollectionRefresh.Janko, collections.RefreshJankoAsync, stoppingToken),
            RunSourceAsync(SlitherlinkCollectionRefresh.GrandGames, collections.RefreshGrandGamesAsync, stoppingToken),
            RunSourceAsync(SlitherlinkCollectionRefresh.KrazyDad, collections.RefreshKrazyDadAsync, stoppingToken));

    private async Task RunSourceAsync(string source, Func<CancellationToken, Task> refresh, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var state = store.GetSlitherlinkCollectionRefreshState(source);
                var delay = state is null ? TimeSpan.Zero : Interval - (time.GetUtcNow() - state.CompletedAt);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay < Interval ? delay : Interval, time, token);
                    continue;
                }
                await refresh(token);
            }
            catch (GrandGamesRateLimited exception) when (!token.IsCancellationRequested)
            {
                logger.LogInformation("GrandGames paused Slitherlink requests for {Delay}.", exception.Delay);
                await Task.Delay(exception.CheckAgainIn, time, token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Slitherlink refresh failed for {Source}. Retrying in one hour.", source);
                await Task.Delay(RetryDelay, time, token);
            }
        }
    }
}
