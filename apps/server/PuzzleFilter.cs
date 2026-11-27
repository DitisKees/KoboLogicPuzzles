using System.Globalization;
using System.Text.Json;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.Galaxies;
using KoboLogicPuzzles.Server.Nurikabe;
using KoboLogicPuzzles.Server.Slitherlink;

namespace KoboLogicPuzzles.Server;

// Nonogram difficulty values are stored in quarter-star units. Hashi ratings use their enum values.
public sealed record PuzzleFilter(PuzzleGame Game, int[]? Widths, int[]? Heights, int[]? Difficulties, int[]? Solvabilities)
{
    private static readonly int[] Dimensions = Enumerable.Range(1, 100).ToArray();
    private static readonly int[] GalaxiesDimensions = Enumerable.Range(1, GalaxiesBoard.MaxDimension).ToArray();
    private static readonly int[] HashiSizes = Dimensions.Where(HashiGenerationOptions.IsSupportedSize).ToArray();
    private static readonly int[] StarRatings = Enumerable.Range(4, 17).ToArray();
    private static readonly int[] WebPbnSolvabilities = Enumerable.Range(1, 6).ToArray();
    private static readonly int[] HashiDifficulties = Enum.GetValues<HashiDifficulty>().Select(value => (int)value).ToArray();
    private static readonly int[] HashiSolvabilities = Enum.GetValues<HashiSolvability>().Select(value => (int)value).ToArray();

    internal readonly record struct DimensionRange(int Min, int Max)
    {
        public bool Contains(int size) => size >= Min && size <= Max;
    }

    internal DimensionRange[]? FillAPixWidths { get; init; }
    internal DimensionRange[]? FillAPixHeights { get; init; }
    internal string[]? SlitherlinkDifficulties { get; init; }
    internal string[]? GalaxiesDifficulties { get; init; }
    internal string[]? GalaxiesSolvabilities { get; init; }
    internal string[]? NurikabeDifficulties { get; init; }
    internal string[]? NurikabeSolvabilities { get; init; }
    internal string[]? Tilings { get; init; }
    internal string[]? Sources { get; init; }
    internal bool SquareOnly { get; init; }

    public static bool TryParseGame(string? value, out PuzzleGame game)
    {
        game = PuzzleGame.FillAPix;
        return value == FillAPixSources.Game || TryParseName(value, out game) && game != PuzzleGame.FillAPix;
    }

    public static PuzzleFilter? Parse(string? game, int? width, int? height, string? difficulty, string? solvability,
        string? widthFilter = null, string? heightFilter = null, string? difficultyFilter = null,
        string? solvabilityFilter = null, string? sizeFilter = null, string? tiling = null, string? tilingFilter = null,
        string? source = null, string? sourceFilter = null, bool includeCollections = false)
    {
        if (!TryParseGame(game, out var kind))
            return null;
        if (kind != PuzzleGame.Slitherlink && (tiling is not null || tilingFilter is not null) ||
            kind == PuzzleGame.FillAPix && (source is not null || sourceFilter is not null) ||
            !TryChoices(source, sourceFilter, value => ValidSource(kind, value), out var sources))
            return null;
        if (kind == PuzzleGame.FillAPix)
        {
            if (difficulty is not null || solvability is not null || difficultyFilter is not null ||
                solvabilityFilter is not null || sizeFilter is not null ||
                !TryDimensionRanges(width, widthFilter, out var widthRanges) || !TryDimensionRanges(height, heightFilter, out var heightRanges))
                return null;
            return new PuzzleFilter(kind, null, null, null, null) { FillAPixWidths = widthRanges, FillAPixHeights = heightRanges };
        }
        var dimensions = kind switch
        {
            PuzzleGame.Nurikabe => Dimensions[..50],
            PuzzleGame.Galaxies => GalaxiesDimensions,
            _ => Dimensions,
        };
        if (!TrySelect(width?.ToString(CultureInfo.InvariantCulture), widthFilter, dimensions, Integer, true, out var widths) ||
            !TrySelect(height?.ToString(CultureInfo.InvariantCulture), heightFilter, dimensions, Integer, true, out var heights))
            return null;

        if (sizeFilter is not null)
        {
            if (kind is not (PuzzleGame.Hashi or PuzzleGame.Slitherlink or PuzzleGame.Nurikabe or PuzzleGame.Galaxies) || widths is not null || heights is not null ||
                !TrySelect(null, sizeFilter, kind == PuzzleGame.Hashi ? HashiSizes : dimensions, Integer, true, out widths))
                return null;
            heights = widths;
        }

        if (kind == PuzzleGame.Slitherlink)
        {
            if (solvability is not null || solvabilityFilter is not null ||
                !TryChoices(difficulty, difficultyFilter, SlitherlinkOptions.Levels.Contains, out var levels) ||
                !TryChoices(tiling, tilingFilter, SlitherlinkMetadata.ValidTiling, out var tilings))
                return null;
            return new PuzzleFilter(kind, widths, heights, null, null)
            { SlitherlinkDifficulties = levels, Tilings = tilings, Sources = sources };
        }

        if (kind == PuzzleGame.Nurikabe)
        {
            if (!TryChoices(difficulty, difficultyFilter, NurikabeGenerator.Difficulties.Contains, out var levels) ||
                !TryChoices(solvability, solvabilityFilter, NurikabeGenerator.Solvabilities.Contains, out var logic))
                return null;
            return new PuzzleFilter(kind, widths, heights, null, null)
            { NurikabeDifficulties = levels, NurikabeSolvabilities = logic, Sources = sources };
        }

        if (kind == PuzzleGame.Galaxies)
        {
            if (!TryChoices(difficulty, difficultyFilter, value => value is "easy" or "medium" or "hard" or "expert", out var levels) ||
                !TryChoices(solvability, solvabilityFilter, value => value is "basic_logic" or "advanced_logic" or
                    "bounded_lookahead" or "requires_search" or "unknown", out var logic))
                return null;
            return new PuzzleFilter(kind, widths, heights, null, null)
            { GalaxiesDifficulties = levels, GalaxiesSolvabilities = logic, Sources = sources, SquareOnly = sizeFilter is not null };
        }

        if (sources is null && !includeCollections)
            sources = kind == PuzzleGame.Hashi ? ["bridges"] : ["webpbn"];
        int[]? difficulties, solvabilities;
        if (kind == PuzzleGame.Nonogram)
        {
            if (!TrySelect(difficulty, difficultyFilter, StarRatings, Stars, true, out difficulties) ||
                !TrySelect(solvability, solvabilityFilter, WebPbnSolvabilities, Integer, false, out solvabilities))
                return null;
        }
        else if (!TrySelect(difficulty, difficultyFilter, HashiDifficulties, Rating<HashiDifficulty>, false, out difficulties) ||
                 !TrySelect(solvability, solvabilityFilter, HashiSolvabilities, Rating<HashiSolvability>, false, out solvabilities))
            return null;

        return new PuzzleFilter(kind, widths, heights, difficulties, solvabilities) { Sources = sources, SquareOnly = sizeFilter is not null };
    }

    private static bool ValidSource(PuzzleGame game, string value) => game switch
    {
        PuzzleGame.Nonogram => value is "webpbn" or "world-at-play" or "sen-ltd" or "janko",
        PuzzleGame.Hashi => value is "bridges" or "sen-ltd" or "janko",
        _ => SlitherlinkOptions.ValidSource(value),
    };

    private static bool TryChoices(string? exact, string? expression, Func<string, bool> valid, out string[]? choices)
    {
        choices = null;
        if (exact is not null)
        {
            if (expression is not null || !valid(exact))
                return false;
            choices = [exact];
        }
        if (expression is not null)
        {
            if (expression.Length is 0 or > 1024)
                return false;
            choices = expression.Split(',', StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (!choices.All(valid))
                return false;
        }
        return true;
    }

    private static bool TryDimensionRanges(int? exact, string? expression, out DimensionRange[]? ranges)
    {
        ranges = null;
        if (exact is { } size)
        {
            if (expression is not null || size < 1)
                return false;
            ranges = [new(size, size)];
        }
        if (expression is null)
            return true;
        if (expression.Length is 0 or > 1024)
            return false;

        var parsed = new List<DimensionRange>();
        foreach (var term in expression.Split(','))
        {
            var bounds = term.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length > 2 || Integer(bounds[0]) is not { } first || first < 1)
                return false;
            var last = bounds.Length == 1 ? first : Integer(bounds[1]);
            if (last is null || last < first)
                return false;
            parsed.Add(new(first, last.Value));
        }
        ranges = parsed.ToArray();
        return true;
    }

    private static bool TrySelect(string? exact, string? expression, int[] domain, Func<string, int?> parse,
        bool allowRanges, out int[]? values)
    {
        values = null;
        if (exact is not null)
        {
            if (expression is not null || parse(exact) is not { } single || !domain.Contains(single))
                return false;
            values = [single];
            return true;
        }
        if (expression is null)
            return true;
        if (expression.Length is 0 or > 1024)
            return false;

        var selected = new HashSet<int>();
        foreach (var term in expression.Split(','))
        {
            var bounds = term.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length > 2 || parse(bounds[0]) is not { } first || !domain.Contains(first))
                return false;
            if (bounds.Length == 1)
                selected.Add(first);
            else
            {
                if (!allowRanges || parse(bounds[1]) is not { } last || !domain.Contains(last) || last < first)
                    return false;
                selected.UnionWith(domain.Where(value => value >= first && value <= last));
            }
        }
        values = selected.Order().ToArray();
        return true;
    }

    private static int? Integer(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : null;

    private static int? Stars(string text) => decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
        out var value) ? StarUnits(value) : null;

    private static int? StarUnits(decimal? value) => value is { } stars && stars is >= 1 and <= 5 && stars * 4 % 1 == 0
        ? (int)(stars * 4) : null;

    private static int? Rating<T>(string? text) where T : struct, Enum => TryParseName(text, out T value)
        ? Convert.ToInt32(value) : null;

    private static bool TryParseName<T>(string? value, out T result) where T : struct, Enum
    {
        foreach (var member in Enum.GetValues<T>())
        {
            if (JsonNamingPolicy.SnakeCaseLower.ConvertName(member.ToString()) == value)
            {
                result = member;
                return true;
            }
        }
        result = default;
        return false;
    }

    private bool Matches(int width, int height, int? difficulty, int? solvability) =>
        (!SquareOnly || width == height) && Includes(Widths, width) && Includes(Heights, height) && Includes(Difficulties, difficulty) && Includes(Solvabilities, solvability);

    private static bool Includes(int[]? values, int? value) => values is null || (value is { } known && values.Contains(known));

    internal bool IncludesSource(string source) => Sources is null || Sources.Contains(source);

    internal bool Matches(PuzzleDefinition puzzle) => IncludesSource("webpbn") && Matches(puzzle.Width, puzzle.Height, StarUnits(puzzle.Metadata?.Difficulty),
        puzzle.Metadata?.Solvability);

    internal bool Matches(StoredHashiPuzzle puzzle) => IncludesSource(puzzle.Metadata.Attribution?.Source ?? "bridges") && Matches(puzzle.Puzzle.Width, puzzle.Puzzle.Height,
        (int?)puzzle.Metadata.Rating.Difficulty, (int)puzzle.Metadata.Rating.Solvability);

    internal bool Matches(GamePuzzleDefinition puzzle) => puzzle switch
    {
        NonogramPuzzleDefinition nonogram => IncludesSource(nonogram.Metadata?.Source ?? "webpbn") && Matches(nonogram.Width, nonogram.Height, StarUnits(nonogram.Metadata?.Difficulty),
            nonogram.Metadata?.Solvability),
        HashiPuzzleDefinition hashi => IncludesSource(hashi.Metadata.Source) && Matches(hashi.Width, hashi.Height, Rating<HashiDifficulty>(hashi.Metadata.Difficulty),
            Rating<HashiSolvability>(hashi.Metadata.Solvability)),
        FillAPixPuzzleDefinition fillAPix => (FillAPixWidths is null || FillAPixWidths.Any(range => range.Contains(fillAPix.Width))) &&
            (FillAPixHeights is null || FillAPixHeights.Any(range => range.Contains(fillAPix.Height))),
        SlitherlinkDefinition slitherlink => Matches(slitherlink.Width, slitherlink.Height, null, null) &&
            (SlitherlinkDifficulties is null || SlitherlinkDifficulties.Contains(slitherlink.Metadata.Difficulty)) &&
            (Tilings is null || Tilings.Contains(slitherlink.Metadata.Tiling)) &&
            (Sources is null || Sources.Contains(slitherlink.Metadata.Source)),
        GalaxiesDefinition galaxies => Matches(galaxies.Width, galaxies.Height, null, null) &&
            (GalaxiesDifficulties is null || GalaxiesDifficulties.Contains(galaxies.Metadata.Difficulty)) &&
            (GalaxiesSolvabilities is null || GalaxiesSolvabilities.Contains(galaxies.Metadata.Solvability)) &&
            IncludesSource(galaxies.Metadata.Source),
        NurikabeDefinition nurikabe => Matches(nurikabe.Width, nurikabe.Height, null, null) &&
            (NurikabeDifficulties is null || NurikabeDifficulties.Contains(nurikabe.Metadata.Difficulty)) &&
            (NurikabeSolvabilities is null || NurikabeSolvabilities.Contains(nurikabe.Metadata.Solvability)) &&
            (Sources is null || Sources.Contains(nurikabe.Metadata.Source)),
        _ => false,
    };
}
