using KoboLogicPuzzles.Server.Hashi;
using Microsoft.AspNetCore.Mvc;

namespace KoboLogicPuzzles.Server;

internal static class GameApi
{
    public static void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api/v2");
        api.MapGet("/capabilities", (HashiGenerationOptions options, PuzzleStore store) => new
        {
            ApiVersion = 2,
            SchemaVersion = 1,
            AdvancedFilters = true,
            Games = GetGames(options, store),
        });
        api.MapGet("/hashi/status", (HashiInventory inventory) => inventory.GetStatus());

        api.MapGet("/puzzles", (GameCatalog catalog, string? game, int? width, int? height, string? difficulty, string? solvability,
            string? widthFilter, string? heightFilter, string? difficultyFilter, string? solvabilityFilter, string? sizeFilter,
            string? beforeId, int limit = 50) =>
        {
            var filter = PuzzleFilter.Parse(game, width, height, difficulty, solvability,
                widthFilter, heightFilter, difficultyFilter, solvabilityFilter, sizeFilter);
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
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));

        api.MapGet("/restore", (PuzzleStore store, string? game) =>
        {
            if (game is null)
                return Results.Ok(store.RestoreGameProgress(null));
            return PuzzleFilter.TryParseGame(game, out var kind) ? Results.Ok(store.RestoreGameProgress(kind)) :
                Server.BadRequest("Supply a supported game.");
        });
    }

    private static string[] GetGames(HashiGenerationOptions options, PuzzleStore store)
    {
        var games = new List<string> { "nonogram" };
        if (options.Enabled || store.HasHashiPuzzles())
            games.Add("hashi");
        if (store.HasFillAPixPuzzles())
            games.Add(FillAPixSources.Game);
        return games.ToArray();
    }
}
