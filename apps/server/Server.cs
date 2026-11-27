using KoboLogicPuzzles.Server.Galaxies;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Nurikabe;
using KoboLogicPuzzles.Server.Slitherlink;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace KoboLogicPuzzles.Server;

public static class Server
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddProblemDetails();
        services.AddHealthChecks().AddCheck<StorageHealthCheck>("sqlite");
        services.AddOptions<WebPbnOptions>().BindConfiguration("WebPbn").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<WebPbnOptions>>().Value);
        services.AddOptions<HashiGenerationOptions>().BindConfiguration("HashiGeneration").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<HashiGenerationOptions>>().Value);
        services.AddSingleton<IHashiCandidateGenerator, BridgesGenerator>();
        services.AddSingleton<HashiInventory>();
        services.AddHostedService<HashiInventoryWorker>();
        services.AddOptions<SlitherlinkOptions>().BindConfiguration("SlitherlinkGeneration").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<SlitherlinkOptions>>().Value);
        services.AddOptions<NurikabeOptions>().BindConfiguration("NurikabeGeneration").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<NurikabeOptions>>().Value);
        services.AddSingleton<NurikabeInventory>();
        services.AddHostedService<NurikabeInventoryWorker>();
        services.AddSingleton<NurikabeImporter>();
        services.AddSingleton(new NurikabeCollectionOptions
        {
            RefreshEnabled = builder.Configuration.GetValue("NurikabeCollections:RefreshEnabled",
                builder.Configuration.GetValue("CatalogRefresh:Enabled", true)),
        });
        services.AddHostedService<NurikabeCollectionsWorker>();
        services.AddHttpClient<NurikabeCollections>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KoboLogicPuzzles/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false });
        services.AddSingleton<LoopyGenerator>();
        services.AddSingleton<SlitherlinkImporter>();
        services.AddSingleton<SlitherlinkInventory>();
        services.AddHostedService<SlitherlinkInventoryWorker>();
        services.AddSingleton(provider => new PuzzleStore(
            provider.GetRequiredService<IConfiguration>()["Storage:DatabasePath"] ?? "data/kobologicpuzzles.db"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PuzzleLibrary>();
        services.AddSingleton<IPuzzleProvider, WebPbnPuzzleProvider>();
        services.AddSingleton<IPuzzleProvider, LocalHashiPuzzleProvider>();
        services.AddSingleton<IPuzzleProvider, FillAPixPuzzleProvider>();
        services.AddSingleton<IPuzzleProvider, SlitherlinkPuzzleProvider>();
        services.AddSingleton<IPuzzleProvider, NurikabePuzzleProvider>();
        services.AddSingleton<IPuzzleProvider, GalaxiesPuzzleProvider>();
        services.AddHostedService<GalaxiesCollections>();
        services.AddHttpClient("galaxies", http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KoboLogicPuzzles/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false });
        services.AddSingleton<PuzzleProviders>();
        services.AddSingleton<GameCatalog>();
        services.AddSingleton<GameProgressBackup>();
        if (builder.Configuration.GetValue("CatalogRefresh:Enabled", true))
            services.AddHostedService<CatalogRefreshWorker>();
        if (builder.Configuration.GetValue("FillAPix:RefreshEnabled", builder.Configuration.GetValue("CatalogRefresh:Enabled", true)))
            services.AddHostedService<FillAPixRefreshWorker>();
        if (builder.Configuration.GetValue("SlitherlinkCollections:RefreshEnabled", builder.Configuration.GetValue("CatalogRefresh:Enabled", true)))
            services.AddHostedService<SlitherlinkCollectionRefreshWorker>();
        services.AddSingleton(new ExtraCollectionOptions
        {
            BundledEnabled = builder.Configuration.GetValue("ExtraCollections:BundledEnabled",
                builder.Configuration.GetValue("CatalogRefresh:Enabled", true)),
            JankoEnabled = builder.Configuration.GetValue("ExtraCollections:JankoEnabled", false),
            AllowNonCommercial = builder.Configuration.GetValue("ExtraCollections:AllowNonCommercial", false),
        });
        services.AddHostedService<ExtraCollectionWorker>();
        services.AddHttpClient<ExtraCollectionRefresh>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KoboLogicPuzzles/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false });
        services.AddHttpClient<SlitherlinkCollectionRefresh>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(45);
            http.MaxResponseContentBufferSize = 32 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KoboLogicPuzzles/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        services.AddHttpClient("fill-a-pix", http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
            http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KoboLogicPuzzles/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
        services.AddSingleton(provider => new FillAPixClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("fill-a-pix")));
        services.AddSingleton<FillAPixLibrary>();
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

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapHealthChecks("/health");
        GameApi.MapEndpoints(app);
        DashboardApi.MapEndpoints(app);
    }

    internal static IResult BadRequest(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);
}

internal sealed class StorageHealthCheck(PuzzleStore store) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(store.HasCurrentSchema() ? HealthCheckResult.Healthy() :
            HealthCheckResult.Unhealthy("The puzzle database has an unexpected schema version."));
    }
}
