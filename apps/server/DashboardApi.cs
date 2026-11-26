using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server;

internal static class DashboardApi
{
    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/dashboard", (HttpRequest request) => Results.Redirect($"{request.PathBase}/"));
        app.MapGet("/api/v2/dashboard", (HttpContext context, PuzzleStore store, HashiInventory hashi,
            SlitherlinkInventory slitherlink, TimeProvider time) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return new
            {
                Catalog = store.ReadDashboardCatalog(),
                Hashi = hashi.GetStatus(),
                Slitherlink = slitherlink.GetStatus(),
                SampledAt = time.GetUtcNow(),
            };
        });
    }
}
