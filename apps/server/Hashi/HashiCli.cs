using System.Globalization;
using System.Text.Json;

namespace KoboNonogram.Server.Hashi;

internal static class HashiCli
{
    public static async Task<int> RunAsync(string command, WebApplication app, CancellationToken cancellationToken)
    {
        var inventory = app.Services.GetRequiredService<HashiInventory>();
        if (command == "hashi-status")
        {
            Console.WriteLine(JsonSerializer.Serialize(inventory.GetStatus(), JsonSerializerOptions.Web));
            return 0;
        }

        var config = app.Configuration;
        var options = app.Services.GetRequiredService<HashiGenerationOptions>();
        var seed = config["seed"] ?? HashiInventory.NewSeed();
        var diagnostic = false;
        if (!ParseNumber(config["size"], 0, out var size) || !HashiGenerationOptions.IsSupportedSize(size) ||
            !ParseNumber(config["density"], options.SelectedDensities[0], out var density) ||
            !ParseNumber(config["expansion"], options.Expansion, out var expansion) ||
            !ParseNumber(config["upstream-difficulty"], options.UpstreamDifficulty, out var difficulty) ||
            (config["diagnostic"] is { } setting && !bool.TryParse(setting, out diagnostic)))
            return Usage();

        var parameters = new HashiGenerationParameters(seed, density, expansion, difficulty);
        if (!parameters.IsValid())
            return Usage();
        var result = await inventory.GenerateOnceAsync(size, parameters, diagnostic, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonSerializerOptions.Web));
        return result.Status is "stored" or "verified" or "duplicate" ? 0 : 1;
    }

    private static bool ParseNumber(string? argument, int defaultValue, out int number)
    {
        number = defaultValue;
        return argument is null || int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: hashi-generate --size 20|25|30|35|40|45|50 [--seed lowercase-hex] [--density 1-30] " +
            "[--expansion 0-100] [--upstream-difficulty 0-2] [--diagnostic true|false]");
        return 2;
    }
}
