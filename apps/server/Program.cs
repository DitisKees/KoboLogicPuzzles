using System.Runtime.InteropServices;
using KoboLogicPuzzles.Server;
using KoboLogicPuzzles.Server.Hashi;

var command = args.FirstOrDefault() is "refresh" or "hashi-generate" or "hashi-status" ? args[0] : null;
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
    else
        app.Logger.LogError(exception, "Hashi command stopped.");
    return 1;
}

public partial class Program;
