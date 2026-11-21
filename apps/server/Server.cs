using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace KoboNonogram.Server;

public static class Server
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddProblemDetails();
        services.AddOptions<WebPbnOptions>().BindConfiguration("WebPbn").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<WebPbnOptions>>().Value);
        services.AddSingleton(provider => new PuzzleStore(
            provider.GetRequiredService<IConfiguration>()["Storage:DatabasePath"] ?? "data/kobononogram.db"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PuzzleLibrary>();
        services.AddSingleton<ProgressBackup>();
        if (builder.Configuration.GetValue("CatalogRefresh:Enabled", true))
            services.AddHostedService<CatalogRefreshWorker>();
        services.AddSingleton(provider => new WebPbnClient(new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            BaseAddress = new Uri("https://webpbn.com/"),
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 2 * 1024 * 1024,
        }, provider.GetRequiredService<WebPbnOptions>()));
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = exception => exception switch
            {
                HttpRequestException or InvalidDataException => StatusCodes.Status502BadGateway,
                TimeoutException or OperationCanceledException => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status500InternalServerError,
            },
        });

        var api = app.MapGroup("/api/v1");
        api.MapGet("/puzzles", (PuzzleStore store, int? beforeId, int limit = 50) =>
        {
            if (beforeId is <= 0 || limit is < 1 or > 100)
                return BadRequest("Use a positive beforeId and a limit between 1 and 100.");

            return Results.Ok(store.ListCatalog(beforeId, limit));
        });

        api.MapGet("/puzzles/{id:int:min(1)}", async (int id, PuzzleLibrary library, CancellationToken cancellationToken) =>
        {
            var puzzle = await library.GetPuzzleAsync(id, cancellationToken);
            return puzzle is null ? Results.NotFound() : Results.Ok(puzzle);
        });

        api.MapPost("/offline-queue", async (OfflineQueueRequest request, PuzzleLibrary library,
            CancellationToken cancellationToken) =>
        {
            var cached = request.CachedUnstartedPuzzleIds ?? [];
            if (request.TargetCount is < 0 or > 100 || cached.Length > 100 || cached.Any(id => id <= 0))
                return BadRequest("Use a targetCount between 0 and 100 and at most 100 positive cached puzzle IDs.");

            var puzzles = await library.TopUpAsync(request.TargetCount, cached, cancellationToken);
            return Results.Ok(new OfflineQueueResponse(puzzles));
        });

        api.MapPost("/progress", (ProgressUpload upload, ProgressBackup backup) =>
        {
            if (upload.Boards is null || upload.Boards.Length > 100)
                return BadRequest("Supply a boards array with at most 100 changed boards.");

            try
            {
                return Results.Ok(backup.Save(upload.Boards));
            }
            catch (ArgumentException exception)
            {
                return BadRequest(exception.Message);
            }
        });

        api.MapGet("/restore", (PuzzleStore store) => store.RestoreProgress());
    }

    private static IResult BadRequest(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);
}
