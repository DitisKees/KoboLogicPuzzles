using KoboLogicPuzzles.Server.LinkAPix;
using KoboLogicPuzzles.Server.Placement;
using System.Runtime.InteropServices;
using KoboLogicPuzzles.Server;
using KoboLogicPuzzles.Server.GrandGames;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Nurikabe;
using KoboLogicPuzzles.Server.Slitherlink;

var command = args.FirstOrDefault() is "refresh" or "refresh-fill-a-pix" or "hashi-generate" or "hashi-status"
    or "slitherlink-generate" or "slitherlink-import" or "slitherlink-status"
    or "refresh-extra-collections" or "nurikabe-generate" or "nurikabe-import" or "nurikabe-refresh" or "nurikabe-status"
    or "placement-generate" or "placement-refresh" or "placement-status" or "link-a-pix-import" or "link-a-pix-refresh" or "link-a-pix-status"
    or "grandgames-refresh" ? args[0] : null;
var builder = WebApplication.CreateBuilder(command is null ? args : args[1..]);
Server.AddServices(builder);
await using var app = builder.Build();
app.Services.GetRequiredService<PuzzleStore>().Initialize();

if (command is null)
{
    Server.MapEndpoints(app);
    await app.RunAsync();
    return 0;
}

using var cancellation = new CancellationTokenSource();
using var terminate = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cancellation.Cancel();
}) : null;
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (command == "refresh-extra-collections")
    {
        var refresh = app.Services.GetRequiredService<ExtraCollectionRefresh>();
        var target = app.Configuration["game"];
        var source = app.Configuration["source"];
        if (target is not (null or "nonogram" or "hashi") || source is not (null or "janko" or "world-at-play" or "sen-ltd") ||
            target == "hashi" && source == "world-at-play")
        {
            Console.Error.WriteLine("refresh-extra-collections [--game nonogram|hashi] [--source world-at-play|sen-ltd|janko] [--allow-non-commercial true]");
            return 2;
        }
        if (source != "janko")
        {
            var game = target is null ? (PuzzleGame?)null : target == "hashi" ? PuzzleGame.Hashi : PuzzleGame.Nonogram;
            var (nonograms, hashi) = await refresh.ImportBundledAsync(source, game, cancellation.Token);
            Console.WriteLine($"Bundled sources imported {nonograms} Nonograms and {hashi} Hashi boards.");
        }
        if (source is null or "janko")
        {
            var options = app.Services.GetRequiredService<ExtraCollectionOptions>();
            var nonCommercial = app.Configuration.GetValue("allow-non-commercial", options.AllowNonCommercial);
            foreach (var game in new[] { PuzzleGame.Nonogram, PuzzleGame.Hashi })
            {
                if (target is not null && !target.Equals(game.ToString(), StringComparison.OrdinalIgnoreCase))
                    continue;
                var (added, more) = await refresh.RefreshJankoAsync(game, nonCommercial, true, cancellation.Token);
                Console.WriteLine($"Janko imported {added} {game} puzzles." +
                    (more ? " Run the command again to continue the archive scan." : ""));
            }
        }
        return 0;
    }
    if (command == "refresh-fill-a-pix")
    {
        var results = await Task.WhenAll(Enum.GetValues<FillAPixSource>().Select(RefreshFillAPixAsync));
        return results.All(success => success) ? 0 : 1;
    }
    if (command == "grandgames-refresh")
    {
        var collections = app.Services.GetRequiredService<GrandGamesCollections>();
        var selected = app.Configuration["game"];
        var feeds = GrandGamesCollections.Feeds;
        if (selected is not null)
        {
            if (!PuzzleFilter.TryParseGame(selected, out var game) || !feeds.Any(feed => feed.Game == game))
            {
                Console.Error.WriteLine("grandgames-refresh [--game nonogram|hashi|nurikabe|galaxies|tents|battleships|link-a-pix]");
                return 2;
            }
            feeds = feeds.Where(feed => feed.Game == game).ToArray();
        }
        foreach (var feed in feeds)
        {
            var result = await collections.Refresh(feed, true, cancellation.Token);
            Console.WriteLine($"GrandGames {feed.Game} added {result.Added}, already had {result.Present}, skipped {result.Skipped}, and reached the proof limit on {result.Inconclusive}." +
                (result.More ? " Run the command again to continue the catalog." : ""));
        }
        return 0;
    }
    if (command.StartsWith("slitherlink-", StringComparison.Ordinal))
        return await SlitherlinkCli.RunAsync(command, app, cancellation.Token);
    if (command.StartsWith("nurikabe-", StringComparison.Ordinal))
        return await NurikabeCli.RunAsync(command, app, cancellation.Token);
    if (command.StartsWith("placement-", StringComparison.Ordinal))
        return await PlacementCli.RunAsync(command, app, cancellation.Token);
    if (command.StartsWith("link-a-pix-", StringComparison.Ordinal))
        return await LinkAPixCli.RunAsync(command, app, cancellation.Token);
    if (command != "refresh")
        return await HashiCli.RunAsync(command, app, cancellation.Token);
    var count = await app.Services.GetRequiredService<PuzzleLibrary>().RefreshAsync(cancellation.Token);
    Console.WriteLine(count is null ? "Another refresh updated the WebPBN catalog."
        : $"WebPBN catalog refreshed with {count} puzzles.");
    return 0;
}
catch (GrandGamesRateLimited exception)
{
    Console.Error.WriteLine($"GrandGames requested a pause of {exception.Delay} before continuing. The refresh position was saved.");
    return 1;
}
catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or OperationCanceledException)
{
    if (command == "refresh")
        app.Logger.LogError(exception, "WebPBN refresh failed. The previous catalog remains available.");
    else if (command == "refresh-extra-collections")
        app.Logger.LogError(exception, "Extra collection refresh stopped.");
    else if (command == "refresh-fill-a-pix")
        app.Logger.LogError(exception, "Fill-a-Pix refresh stopped.");
    else if (command == "grandgames-refresh")
        app.Logger.LogError(exception, "GrandGames refresh stopped.");
    else if (command.StartsWith("slitherlink-", StringComparison.Ordinal))
        app.Logger.LogError(exception, "Slitherlink command stopped.");
    else if (command.StartsWith("nurikabe-", StringComparison.Ordinal))
        app.Logger.LogError(exception, "Nurikabe command stopped.");
    else if (command.StartsWith("placement-", StringComparison.Ordinal))
        app.Logger.LogError(exception, "Placement command stopped.");
    else if (command.StartsWith("link-a-pix-", StringComparison.Ordinal))
        app.Logger.LogError(exception, "Link-a-Pix command stopped.");
    else
        app.Logger.LogError(exception, "Hashi command stopped.");
    return 1;
}

async Task<bool> RefreshFillAPixAsync(FillAPixSource source)
{
    try
    {
        var store = app.Services.GetRequiredService<PuzzleStore>();
        var count = await app.Services.GetRequiredService<FillAPixLibrary>()
            .RefreshAsync(source, store.GetFillAPixRefreshState(source), cancellation.Token);
        Console.WriteLine(count is null ? $"Another refresh updated {source.Slug()}."
            : $"Fill-a-Pix refreshed {source.Slug()} with {count} puzzles.");
        return true;
    }
    catch (Exception exception) when (!cancellation.IsCancellationRequested &&
        exception is (HttpRequestException or InvalidDataException or OperationCanceledException))
    {
        app.Logger.LogError(exception, "Fill-a-Pix refresh failed for {Source}. The previous catalog remains available.", source.Slug());
        return false;
    }
}

public partial class Program;
