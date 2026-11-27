using KoboLogicPuzzles.Server.Placement;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Nurikabe;
using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server;

internal static class DashboardApi
{
    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/dashboard", (HttpRequest request) => Results.Redirect($"{request.PathBase}/"));
        app.MapGet("/api/v2/dashboard", (HttpContext context, PuzzleStore store, HashiInventory hashi,
            SlitherlinkInventory slitherlink, NurikabeInventory nurikabe, PlacementInventory placement, TimeProvider time) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return new
            {
                Catalog = store.ReadDashboardCatalog(),
                Hashi = hashi.GetStatus(),
                Slitherlink = slitherlink.GetStatus(),
                Nurikabe = nurikabe.GetStatus(),
                Tents = placement.GetStatus(PuzzleGame.Tents),
                Battleships = placement.GetStatus(PuzzleGame.Battleships),
                SampledAt = time.GetUtcNow(),
            };
        });
    }
}
