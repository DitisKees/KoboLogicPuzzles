using System.ComponentModel.DataAnnotations;

namespace KoboNonogram.Server;

public sealed class WebPbnOptions : IValidatableObject
{
    [Range(1, 10000)] public int MinArea { get; init; } = 1225;
    [Range(1, 10000)] public int MaxArea { get; init; } = 10000;
    [Range(1, 100)] public int MaxWidth { get; init; } = 80;
    [Range(1, 100)] public int MaxHeight { get; init; } = 80;
    [Range(typeof(decimal), "1", "5")] public decimal MinQuality { get; init; } = 4;
    [Range(typeof(decimal), "1", "5")] public decimal MaxQuality { get; init; } = 5;
    [Range(typeof(decimal), "1", "5")] public decimal MinDifficulty { get; init; } = 3;
    [Range(typeof(decimal), "1", "5")] public decimal MaxDifficulty { get; init; } = 5;
    public bool IncludeUnratedQuality { get; init; } = true;
    public bool IncludeUnratedDifficulty { get; init; } = true;
    [Range(0, 6)] public int Solvability { get; init; }
    [Range(0, 5000)] public int RequestDelayMilliseconds { get; init; } = 250;

    public bool Fits(int width, int height) =>
        width > 0 && width <= MaxWidth && height > 0 && height <= MaxHeight &&
        width * height >= MinArea && width * height <= MaxArea;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinArea > MaxArea)
            yield return new ValidationResult("WebPbn:MinArea must be at most MaxArea.");
        if (MinQuality > MaxQuality || MinDifficulty > MaxDifficulty)
            yield return new ValidationResult("WebPbn rating ranges must be in ascending order.");
        if (new[] { MinQuality, MaxQuality, MinDifficulty, MaxDifficulty }.Any(rating => rating * 4 % 1 != 0))
            yield return new ValidationResult("WebPbn ratings must be in quarter-star increments.");
    }
}
