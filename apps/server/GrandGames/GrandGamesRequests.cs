using System.Net;

namespace KoboLogicPuzzles.Server.GrandGames;

internal sealed class GrandGamesRateLimited(TimeSpan delay)
    : HttpRequestException("GrandGames asked the importer to slow down.", null, HttpStatusCode.TooManyRequests)
{
    internal TimeSpan Delay { get; } = delay;
    internal TimeSpan CheckAgainIn => Delay < TimeSpan.FromDays(1) ? Delay : TimeSpan.FromDays(1);
}

internal sealed class GrandGamesRequests(PuzzleStore store, TimeProvider time, TimeSpan? minimumInterval = null) : IDisposable
{
    private const string Resource = "grandgames-requests";
    internal static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MissingRetryAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumCooldown = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim pending = new(1);

    internal async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken token)
    {
        await pending.WaitAsync(token);
        try
        {
            using var lease = await LockAsync(token);
            // The shared refresh row stores the next request time in milliseconds and a flag for publisher cooldown.
            var (limited, nextRequestAt) = store.ReadExtraCollectionRefresh(Resource);
            var wait = DateTimeOffset.FromUnixTimeMilliseconds(nextRequestAt) - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                if (limited != 0)
                    throw new GrandGamesRateLimited(wait);
                await Task.Delay(wait, time, token);
            }

            token.ThrowIfCancellationRequested();
            SaveWindow(time.GetUtcNow() + (minimumInterval ?? RequestInterval), limited: false);
            var response = await http.SendAsync(request, token);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;

            using (response)
            {
                var now = time.GetUtcNow();
                var header = response.Headers.RetryAfter;
                var delay = header?.Delta ?? (header?.Date - now) ?? MissingRetryAfter;
                if (delay < MinimumCooldown)
                    delay = MinimumCooldown;
                SaveWindow(now + delay, limited: true);
                throw new GrandGamesRateLimited(delay);
            }
        }
        finally
        {
            pending.Release();
        }
    }

    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        // The sidecar lock also coordinates a manual command with the running server.
        while (true)
        {
            if (store.TryLockExtraCollection(Resource) is { } lease)
                return lease;
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, token);
        }
    }

    private void SaveWindow(DateTimeOffset nextRequest, bool limited) =>
        store.SaveExtraCollectionRefresh(Resource, limited ? 1 : 0, nextRequest.ToUnixTimeMilliseconds());

    public void Dispose() => pending.Dispose();
}
