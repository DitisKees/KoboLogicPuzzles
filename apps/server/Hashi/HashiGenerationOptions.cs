using System.ComponentModel.DataAnnotations;

namespace KoboLogicPuzzles.Server.Hashi;

internal readonly record struct HashiStockRating(HashiDifficulty Difficulty, HashiSolvability Solvability);

public sealed class HashiGenerationOptions : IValidatableObject
{
    private static readonly int[] DefaultSizes = [20, 25, 30, 35, 40, 45, 50];
    private static readonly int[] DefaultDensities = [10, 20];
    private static readonly int[] IntroductoryDensities = [6, 10, 20];
    private static readonly int[] IntroductoryDensities20 = [8, 10, 20];
    private static readonly HashiDifficulty[] DefaultDifficulties =
        [HashiDifficulty.Easy, HashiDifficulty.Medium, HashiDifficulty.Hard, HashiDifficulty.Expert];
    private static readonly HashiSolvability[] DefaultSolvabilities =
        [HashiSolvability.BasicLogic, HashiSolvability.AdvancedLogic, HashiSolvability.BoundedLookahead];
    // The native generator has produced these ratings at every supported grid size.
    private static readonly HashiStockRating[] StandardRatings =
    [
        new(HashiDifficulty.Easy, HashiSolvability.BasicLogic),
        new(HashiDifficulty.Medium, HashiSolvability.BasicLogic),
        new(HashiDifficulty.Hard, HashiSolvability.AdvancedLogic),
        new(HashiDifficulty.Expert, HashiSolvability.AdvancedLogic),
        new(HashiDifficulty.Expert, HashiSolvability.BoundedLookahead),
    ];

    public bool Enabled { get; init; }
    public string? ExecutablePath { get; init; }
    public int[]? Sizes { get; init; }
    public int[]? Densities { get; init; }
    public HashiDifficulty[]? Difficulties { get; init; }
    public HashiSolvability[]? Solvabilities { get; init; }
    [Range(0, 100)] public int Expansion { get; init; } = 10;
    [Range(0, 2)] public int? UpstreamDifficulty { get; init; }
    [Range(2, 750)] public int MinIslands { get; init; } = 25;
    [Range(0, 30)] public int MinDensityPercent { get; init; } = 4;
    [Range(1, 100)] public int TargetPerDifficulty { get; init; } = 50;
    [Range(0, 99)] public int? LowWatermark { get; init; }
    [Range(1, 10000)] public int MaxUnstartedPerSize { get; init; } = 1000;
    [Range(1, 10)] public int AttemptsPerSize { get; init; } = 10;
    [Range(1, 86400)] public int PollIntervalSeconds { get; init; } = 600;
    [Range(1, 3600)] public int StageTimeoutSeconds { get; init; } = 180;

    internal IReadOnlyList<int> SelectedSizes => Sizes ?? DefaultSizes;
    internal IReadOnlyList<int> SelectedDensities => Densities ?? DefaultDensities;
    internal IReadOnlyList<HashiDifficulty> SelectedDifficulties => Difficulties ?? DefaultDifficulties;
    internal IReadOnlyList<HashiSolvability> SelectedSolvabilities => Solvabilities ?? DefaultSolvabilities;
    internal int RefillThreshold => LowWatermark ?? TargetPerDifficulty - 1;
    internal TimeSpan StageTimeout => TimeSpan.FromSeconds(StageTimeoutSeconds);

    internal static bool IsSupportedSize(int size) => size is >= 20 and <= 50 && size % 5 == 0;

    internal bool IncludesRating(HashiStockRating rating) =>
        SelectedDifficulties.Contains(rating.Difficulty) && SelectedSolvabilities.Contains(rating.Solvability);

    internal HashiStockRating[] StartingRatings()
    {
        var standard = StandardRatings.Where(IncludesRating).ToArray();
        if (standard.Length > 0)
            return standard;

        // Allow an operator to request rarer combinations whose score ranges overlap.
        return (from tier in SelectedDifficulties
                from solvability in SelectedSolvabilities
                where solvability switch
                {
                    HashiSolvability.BasicLogic => tier <= HashiDifficulty.Medium,
                    HashiSolvability.AdvancedLogic => true,
                    HashiSolvability.BoundedLookahead => tier >= HashiDifficulty.Medium,
                    _ => false,
                }
                select new HashiStockRating(tier, solvability)).ToArray();
    }

    internal IReadOnlyList<int> DensitiesFor(int size, HashiDifficulty tier)
    {
        if (Densities is null && (tier is HashiDifficulty.Easy or HashiDifficulty.Medium))
            return size == 20 ? IntroductoryDensities20 : IntroductoryDensities;
        return SelectedDensities;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ExecutablePath is not null && string.IsNullOrWhiteSpace(ExecutablePath))
            yield return new ValidationResult("HashiGeneration executable path must be nonempty when configured.");
        if (SelectedSizes.Count == 0 || SelectedSizes.Any(size => !IsSupportedSize(size)) ||
            SelectedSizes.Distinct().Count() != SelectedSizes.Count)
            yield return new ValidationResult("HashiGeneration sizes must be distinct and supported.");
        if (SelectedDensities.Count == 0 || SelectedDensities.Any(density => density is < 1 or > 30) ||
            SelectedDensities.Distinct().Count() != SelectedDensities.Count)
            yield return new ValidationResult("HashiGeneration densities must be distinct percentages from 1 to 30.");
        if (SelectedDifficulties.Count == 0 || SelectedDifficulties.Any(difficulty => !Enum.IsDefined(difficulty)) ||
            SelectedDifficulties.Distinct().Count() != SelectedDifficulties.Count)
            yield return new ValidationResult("HashiGeneration difficulties must be distinct rating tiers.");
        if (SelectedSolvabilities.Count == 0 || SelectedSolvabilities.Any(solvability => !Enum.IsDefined(solvability)) ||
            SelectedSolvabilities.Distinct().Count() != SelectedSolvabilities.Count)
            yield return new ValidationResult("HashiGeneration solvabilities must be distinct classifications.");
        if (RefillThreshold >= TargetPerDifficulty || TargetPerDifficulty * StartingRatings().Length > MaxUnstartedPerSize)
            yield return new ValidationResult("HashiGeneration needs a low-water mark below its target and room for every configured rating combination.");
    }
}
