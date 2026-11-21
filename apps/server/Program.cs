using KoboNonogram.Server;

var refresh = args is ["refresh", ..];
var builder = WebApplication.CreateBuilder(refresh ? args[1..] : args);
Server.AddServices(builder);
await using var app = builder.Build();
app.Services.GetRequiredService<PuzzleStore>().Initialize();

if (!refresh)
{
    Server.MapEndpoints(app);
    await app.RunAsync();
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var count = await app.Services.GetRequiredService<PuzzleLibrary>().RefreshAsync(cancellation.Token);
    Console.WriteLine(count is null ? "Another refresh updated the WebPBN catalog."
        : $"WebPBN catalog refreshed with {count} puzzles.");
    return 0;
}
catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or OperationCanceledException)
{
    app.Logger.LogError(exception, "WebPBN refresh failed. The previous catalog remains available.");
    return 1;
}

public partial class Program;
