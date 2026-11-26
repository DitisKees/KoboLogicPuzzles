using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace KoboLogicPuzzles.Server.Slitherlink;

internal static class SlitherlinkCli
{
    internal static async Task<int> RunAsync(string command, WebApplication app, CancellationToken token)
    {
        var inventory = app.Services.GetRequiredService<SlitherlinkInventory>();
        if (command == "slitherlink-status")
        {
            Report(inventory.GetStatus());
            return 0;
        }
        try
        {
            var config = app.Configuration;
            if (command == "slitherlink-import")
            {
                if (string.IsNullOrWhiteSpace(config["file"]))
                    return Usage("slitherlink-import --file COLLECTION_PATH [--source SOURCE --source-name NAME]");
                var outcome = await app.Services.GetRequiredService<SlitherlinkImporter>()
                    .ImportAsync(config["file"]!, config["source"], config["source-name"], token);
                Report(outcome);
                return outcome.Stored + outcome.Duplicates > 0 ? 0 : 1;
            }
            var size = config["size"];
            var tiling = config["tiling"];
            var difficulty = config["difficulty"];
            var diagnostic = false;
            if (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                tiling is null || difficulty is null ||
                config["diagnostic"] is { } setting && !bool.TryParse(setting, out diagnostic))
                return Usage("slitherlink-generate --size N --tiling TYPE --difficulty LEVEL [--seed HEX] [--diagnostic true]");
            var result = await inventory.GenerateAsync(new(number, tiling, difficulty), config["seed"] ?? SlitherlinkInventory.NewSeed(),
                diagnostic, token);
            Report(new { result.Status, result.Id });
            return result.Status is "stored" or "verified" or "duplicate" or "full" ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or TimeoutException or
            JsonException or Win32Exception or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static void Report<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));
}
