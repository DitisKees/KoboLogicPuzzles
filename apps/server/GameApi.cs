using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Nurikabe;
using KoboLogicPuzzles.Server.Slitherlink;
using Microsoft.AspNetCore.Mvc;

namespace KoboLogicPuzzles.Server;

internal static class GameApi
{
    public static void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api/v2");
        api.MapGet("/capabilities", (HashiGenerationOptions options, SlitherlinkOptions slitherlink, NurikabeInventory nurikabe, PuzzleStore store) =>
        {
            var sources = store.ListSlitherlinkSources();
            if (slitherlink.Enabled)
                sources.TryAdd(LoopyGenerator.Source, LoopyGenerator.SourceName);
            var nurikabeSources = nurikabe.Sources();
            var catalog = store.ReadDashboardCatalog();
            var hashiSources = Sources("hashi");
            var galaxiesSources = Sources("galaxies");
            if (options.Enabled)
                hashiSources.TryAdd("bridges", new("Bridges", "generated"));
            return new
            {
                ApiVersion = 2,
                SchemaVersion = 1,
                AdvancedFilters = true,
                ExtraCollections = true,
                Games = GetGames(options, store, sources.Count > 0, nurikabeSources.Count > 0, galaxiesSources.Count > 0),
                SlitherlinkSources = sources,
                PuzzleSources = new
                {
                    Hashi = hashiSources,
                    Nonogram = Sources("nonogram"),
                    Slitherlink = sources.ToDictionary(pair => pair.Key,
                        pair => new PuzzleSource(pair.Value, pair.Key == LoopyGenerator.Source ? "generated" : "collection")),
                    Nurikabe = nurikabeSources,
                    Galaxies = galaxiesSources,
                },
            };

            Dictionary<string, PuzzleSource> Sources(string game) => catalog.Where(row => row.Game == game && (row.Total > 0 || row.Source == "webpbn"))
                .ToDictionary(row => row.Source, row => new PuzzleSource(row.SourceName, row.SourceKind), StringComparer.Ordinal);
        });
        api.MapGet("/hashi/status", (HashiInventory inventory) => inventory.GetStatus());
        api.MapGet("/slitherlink/status", (SlitherlinkInventory inventory) => inventory.GetStatus());
        api.MapGet("/nurikabe/status", (NurikabeInventory inventory) => inventory.GetStatus());

        api.MapGet("/puzzles", (GameCatalog catalog, string? game, int? width, int? height, string? difficulty, string? solvability,
            string? widthFilter, string? heightFilter, string? difficultyFilter, string? solvabilityFilter, string? sizeFilter,
            string? beforeId, string? tiling, string? tilingFilter, string? source, string? sourceFilter, int limit = 50, bool includeCollections = false) =>
        {
            var filter = PuzzleFilter.Parse(game, width, height, difficulty, solvability,
                widthFilter, heightFilter, difficultyFilter, solvabilityFilter, sizeFilter, tiling, tilingFilter, source, sourceFilter, includeCollections);
            if (filter is null || limit is < 1 or > 100)
                return Server.BadRequest("Supply a supported game, valid filters, and a limit of 1–100.");
            PuzzleId? before = null;
            if (beforeId is not null && (!PuzzleId.TryParse(beforeId, out before) || before.Game != filter.Game))
                return Server.BadRequest("The catalog cursor must belong to the requested game.");
            return Results.Ok(catalog.List(filter, before, limit));
        });

        api.MapGet("/puzzles/{id}", async (string id, bool? includeSolution, PuzzleProviders providers,
            CancellationToken cancellationToken) =>
        {
            if (!PuzzleId.TryParse(id, out var puzzleId))
                return Server.BadRequest("Supply a namespaced puzzle ID such as nonogram-webpbn-12 or fill-a-pix-janko-12.");
            var definition = await providers.GetDefinitionAsync(puzzleId, includeSolution == true, cancellationToken);
            return definition is null ? Results.NotFound() : Results.Ok(definition);
        });

        api.MapGet("/puzzles/metadata", (string[] id, GameCatalog catalog) =>
        {
            try
            {
                return Results.Ok(catalog.ListMetadata(id));
            }
            catch (ArgumentException exception)
            {
                return Server.BadRequest(exception.Message);
            }
        });

        api.MapPost("/offline-queue", async (GameOfflineQueueRequest request, GameCatalog catalog, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await catalog.TopUpAsync(request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Server.BadRequest(exception.Message);
            }
        });

        api.MapPost("/progress", async (GameProgressUpload upload, GameProgressBackup backup, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await backup.SaveAsync(upload, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Server.BadRequest(exception.Message);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024 * 1024));

        api.MapGet("/restore", (PuzzleStore store, string? game, bool includeCollections = false) =>
        {
            if (game is null)
                return Results.Ok(store.RestoreGameProgress(null, includeCollections));
            return PuzzleFilter.TryParseGame(game, out var kind) ? Results.Ok(store.RestoreGameProgress(kind, includeCollections)) :
                Server.BadRequest("Supply a supported game.");
        });
    }

    private static string[] GetGames(HashiGenerationOptions options, PuzzleStore store, bool hasSlitherlink, bool hasNurikabe, bool hasGalaxies)
    {
        var games = new List<string> { "nonogram" };
        if (options.Enabled || store.HasHashiPuzzles())
            games.Add("hashi");
        if (store.HasFillAPixPuzzles())
            games.Add(FillAPixSources.Game);
        if (hasSlitherlink)
            games.Add("slitherlink");
        if (hasNurikabe)
            games.Add("nurikabe");
        if (hasGalaxies)
            games.Add("galaxies");
        return games.ToArray();
    }
}
