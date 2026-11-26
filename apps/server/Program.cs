using System.Runtime.InteropServices;
using KoboLogicPuzzles.Server;
using KoboLogicPuzzles.Server.Hashi;

var command = args.FirstOrDefault() is "refresh" or "refresh-fill-a-pix" or "hashi-generate" or "hashi-status" ? args[0] : null;
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
    if (command == "refresh-fill-a-pix")
    {
        var results = await Task.WhenAll(Enum.GetValues<FillAPixSource>().Select(RefreshFillAPixAsync));
        return results.All(success => success) ? 0 : 1;
    }
    if (command != "refresh")
        return await HashiCli.RunAsync(command, app, cancellation.Token);
    var count = await app.Services.GetRequiredService<PuzzleLibrary>().RefreshAsync(cancellation.Token);
    Console.WriteLine(count is null ? "Another refresh updated the WebPBN catalog."
        : $"WebPBN catalog refreshed with {count} puzzles.");
    return 0;
}
catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or OperationCanceledException)
{
    if (command == "refresh")
        app.Logger.LogError(exception, "WebPBN refresh failed. The previous catalog remains available.");
    else if (command == "refresh-fill-a-pix")
        app.Logger.LogError(exception, "Fill-a-Pix refresh stopped.");
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
