using KoboLogicPuzzles.Server.Hashi;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace KoboLogicPuzzles.Server;

public static class Server
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddProblemDetails();
        services.AddOptions<WebPbnOptions>().BindConfiguration("WebPbn").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<WebPbnOptions>>().Value);
        services.AddOptions<HashiGenerationOptions>().BindConfiguration("HashiGeneration").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<HashiGenerationOptions>>().Value);
        services.AddSingleton<IHashiCandidateGenerator, BridgesGenerator>();
        services.AddSingleton<HashiInventory>();
        services.AddHostedService<HashiInventoryWorker>();
        services.AddSingleton(provider => new PuzzleStore(
            provider.GetRequiredService<IConfiguration>()["Storage:DatabasePath"] ?? "data/kobologicpuzzles.db"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PuzzleLibrary>();
        services.AddSingleton<IPuzzleProvider, WebPbnPuzzleProvider>();
        services.AddSingleton<IPuzzleProvider, LocalHashiPuzzleProvider>();
        services.AddSingleton<PuzzleProviders>();
        services.AddSingleton<GameCatalog>();
        services.AddSingleton<GameProgressBackup>();
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

        GameApi.MapEndpoints(app);
    }

    internal static IResult BadRequest(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);
}
