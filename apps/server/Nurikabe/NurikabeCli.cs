using System.Globalization;
using System.Text.Json;

namespace KoboLogicPuzzles.Server.Nurikabe;

internal static class NurikabeCli
{
    internal static async Task<int> RunAsync(string command, WebApplication app, CancellationToken token)
    {
        var inventory = app.Services.GetRequiredService<NurikabeInventory>();
        var config = app.Configuration;
        try
        {
            if (command == "nurikabe-status")
            {
                Report(inventory.GetStatus());
                return 0;
            }
            if (command == "nurikabe-refresh")
            {
                Report(new { Stored = await app.Services.GetRequiredService<NurikabeCollections>().RefreshAsync(true, token) });
                return 0;
            }
            if (command == "nurikabe-import")
            {
                if (string.IsNullOrWhiteSpace(config["file"]))
                    return Usage("nurikabe-import --file AUTHORIZED_ENTRIES.json [--puzzlekit SHARD.json]");
                var result = await app.Services.GetRequiredService<NurikabeImporter>().ImportAsync(config["file"]!, config["puzzlekit"], token);
                Report(result);
                return result.Stored + result.Duplicates > 0 ? 0 : 1;
            }

            var diagnostic = false;
            var seed = NurikabeInventory.NewSeed();
            var difficulty = config["difficulty"];
            if (!int.TryParse(config["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                config["seed"] is { } value && !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed) ||
                config["diagnostic"] is { } check && !bool.TryParse(check, out diagnostic) ||
                difficulty is not null && !NurikabeGenerator.Difficulties.Contains(difficulty))
                return Usage("nurikabe-generate --size N [--seed INTEGER] [--difficulty LEVEL] [--diagnostic true]");

            using var generation = app.Services.GetRequiredService<PuzzleStore>().TryLockNurikabe("generation");
            if (generation is null)
                return Usage("Another process is generating Nurikabe inventory.");
            var (status, id) = inventory.Generate(size, seed, difficulty is null ? null : [difficulty], diagnostic, token);
            Report(new { Status = status, Id = id, Seed = seed });
            return status is "stored" or "verified" or "duplicate" ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            HttpRequestException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void Report<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
