using KoboLogicPuzzles.Server.LinkAPix;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using KoboLogicPuzzles.Server.Placement;

namespace KoboLogicPuzzles.Server;

public enum PuzzleGame { Nonogram, Hashi, FillAPix, Slitherlink, Nurikabe, Galaxies, Tents, Battleships, LinkAPix }

public sealed record PuzzleId
{
    private const string NonogramCollectionPrefix = "nonogram-collection-";
    private const string LinkPrefix = LinkSources.Game + "-";
    private const string WebPbnPrefix = "nonogram-webpbn-";
    private const string HashiPrefix = "hashi-local-";
    private const string FillAPixPrefix = "fill-a-pix-";
    private const string GalaxiesPrefix = "galaxies-";
    private const string NurikabePrefix = "nurikabe-";
    private const string SlitherlinkPrefix = "slitherlink-";

    private PuzzleId(PuzzleGame game, string value, int? webPbnId, string? contentHash,
        FillAPixSource? fillAPixSource = null, int? fillAPixNumber = null)
    {
        Game = game;
        Value = value;
        WebPbnId = webPbnId;
        ContentHash = contentHash;
        FillAPixSource = fillAPixSource;
        FillAPixNumber = fillAPixNumber;
    }

    public PuzzleGame Game { get; }
    public string Value { get; }
    public int? WebPbnId { get; }
    public string? ContentHash { get; }
    internal FillAPixSource? FillAPixSource { get; }
    internal int? FillAPixNumber { get; }

    public static PuzzleId FromWebPbn(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return new PuzzleId(PuzzleGame.Nonogram, WebPbnPrefix + id.ToString(CultureInfo.InvariantCulture), id, null);
    }

    public static PuzzleId FromNonogramCollection(string contentHash)
    {
        if (!IsContentHash(contentHash))
            throw new ArgumentException("A nonogram content hash must have 64 lowercase hexadecimal characters.", nameof(contentHash));
        return new PuzzleId(PuzzleGame.Nonogram, NonogramCollectionPrefix + contentHash, null, contentHash);
    }

    public static PuzzleId FromHashi(string contentHash)
    {
        if (!IsContentHash(contentHash))
            throw new ArgumentException("A Hashi content hash must have 64 lowercase hexadecimal characters.", nameof(contentHash));
        return new PuzzleId(PuzzleGame.Hashi, HashiPrefix + contentHash, null, contentHash);
    }

    public static PuzzleId FromSlitherlink(string contentHash)
    {
        if (!IsContentHash(contentHash))
            throw new ArgumentException("A Slitherlink content hash must have 64 lowercase hexadecimal characters.", nameof(contentHash));
        return new PuzzleId(PuzzleGame.Slitherlink, SlitherlinkPrefix + contentHash, null, contentHash);
    }

    public static PuzzleId FromNurikabe(string contentHash)
    {
        if (!IsContentHash(contentHash))
            throw new ArgumentException("A Nurikabe content hash must have 64 lowercase hexadecimal characters.", nameof(contentHash));
        return new PuzzleId(PuzzleGame.Nurikabe, NurikabePrefix + contentHash, null, contentHash);
    }

    public static PuzzleId FromGalaxies(string contentHash)
    {
        if (!IsContentHash(contentHash))
            throw new ArgumentException("A Galaxies content hash must have 64 lowercase hexadecimal characters.", nameof(contentHash));
        return new PuzzleId(PuzzleGame.Galaxies, GalaxiesPrefix + contentHash, null, contentHash);
    }

    internal static PuzzleId FromLinkAPix(string hash)
    {
        if (!IsContentHash(hash))
            throw new ArgumentException("Invalid Link-a-Pix content hash.", nameof(hash));
        return new(PuzzleGame.LinkAPix, LinkPrefix + hash, null, hash);
    }

    internal static PuzzleId FromPlacement(PuzzleGame game, string contentHash)
    {
        if (!PlacementBoard.Games.Contains(game) || !IsContentHash(contentHash))
            throw new ArgumentException("A placement ID needs a placement game and 64 lowercase hexadecimal characters.");
        return new PuzzleId(game, PlacementBoard.Slug(game) + "-" + contentHash, null, contentHash);
    }

    internal static PuzzleId FromFillAPix(FillAPixSource source, int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        return new PuzzleId(PuzzleGame.FillAPix, FillAPixPrefix + source.Slug() + "-" +
            number.ToString(CultureInfo.InvariantCulture), null, null, source, number);
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out PuzzleId? id)
    {
        if (value?.StartsWith(WebPbnPrefix, StringComparison.Ordinal) is true)
        {
            var suffix = value.AsSpan(WebPbnPrefix.Length);
            if (suffix.Length is > 0 and <= 10 && suffix[0] is >= '1' and <= '9' &&
                int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                var parsed = FromWebPbn(number);
                if (parsed.Value == value)
                {
                    id = parsed;
                    return true;
                }
            }
        }
        else if (value?.StartsWith(NonogramCollectionPrefix, StringComparison.Ordinal) is true &&
                 IsContentHash(value[NonogramCollectionPrefix.Length..]))
        {
            id = FromNonogramCollection(value[NonogramCollectionPrefix.Length..]);
            return true;
        }
        else if (value?.Length == HashiPrefix.Length + 64 && value.StartsWith(HashiPrefix, StringComparison.Ordinal))
        {
            var suffix = value[HashiPrefix.Length..];
            if (IsContentHash(suffix))
            {
                id = FromHashi(suffix);
                return true;
            }
        }
        else if (value?.StartsWith(SlitherlinkPrefix, StringComparison.Ordinal) is true &&
                 IsContentHash(value[SlitherlinkPrefix.Length..]))
        {
            id = FromSlitherlink(value[SlitherlinkPrefix.Length..]);
            return true;
        }
        else if (value?.StartsWith(NurikabePrefix, StringComparison.Ordinal) is true &&
                 IsContentHash(value[NurikabePrefix.Length..]))
        {
            id = FromNurikabe(value[NurikabePrefix.Length..]);
            return true;
        }
        else if (value?.StartsWith(GalaxiesPrefix, StringComparison.Ordinal) is true &&
                 IsContentHash(value[GalaxiesPrefix.Length..]))
        {
            id = FromGalaxies(value[GalaxiesPrefix.Length..]);
            return true;
        }
        else if (value?.Length == LinkPrefix.Length + 64 && value.StartsWith(LinkPrefix, StringComparison.Ordinal) &&
            IsContentHash(value[LinkPrefix.Length..]))
        {
            id = FromLinkAPix(value[LinkPrefix.Length..]);
            return true;
        }
        else if (value is not null && TryParsePlacement(value, out id))
            return true;
        else if (value?.StartsWith(FillAPixPrefix, StringComparison.Ordinal) is true)
        {
            var separator = value.LastIndexOf('-');
            var suffix = value.AsSpan(separator + 1);
            if (separator >= FillAPixPrefix.Length && suffix.Length is > 0 and <= 10 && suffix[0] is >= '1' and <= '9' &&
                int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                suffix.SequenceEqual(number.ToString(CultureInfo.InvariantCulture)))
            {
                var sourceName = value.AsSpan(FillAPixPrefix.Length, separator - FillAPixPrefix.Length);
                foreach (var source in Enum.GetValues<FillAPixSource>())
                {
                    if (!sourceName.SequenceEqual(source.Slug()))
                        continue;
                    id = FromFillAPix(source, number);
                    return true;
                }
            }
        }

        id = null;
        return false;
    }

    private static bool TryParsePlacement(string value, [NotNullWhen(true)] out PuzzleId? id)
    {
        foreach (var game in PlacementBoard.Games)
        {
            var prefix = PlacementBoard.Slug(game) + "-";
            if (value.Length == prefix.Length + 64 && value.StartsWith(prefix, StringComparison.Ordinal) &&
                IsContentHash(value[prefix.Length..]))
            {
                id = FromPlacement(game, value[prefix.Length..]);
                return true;
            }
        }
        id = null;
        return false;
    }

    internal static bool IsContentHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public override string ToString() => Value;
}
