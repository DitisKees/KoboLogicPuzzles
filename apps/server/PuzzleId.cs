using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace KoboLogicPuzzles.Server;

public enum PuzzleGame { Nonogram, Hashi }

public sealed record PuzzleId
{
    private const string WebPbnPrefix = "nonogram-webpbn-";
    private const string HashiPrefix = "hashi-local-";

    private PuzzleId(PuzzleGame game, string value, int? webPbnId, string? contentHash)
    {
        Game = game;
        Value = value;
        WebPbnId = webPbnId;
        ContentHash = contentHash;
    }

    public PuzzleGame Game { get; }
    public string Value { get; }
    public int? WebPbnId { get; }
    public string? ContentHash { get; }

    public static PuzzleId FromWebPbn(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return new PuzzleId(PuzzleGame.Nonogram, WebPbnPrefix + id.ToString(CultureInfo.InvariantCulture), id, null);
    }

    public static PuzzleId FromHashi(string contentHash)
    {
        if (!IsContentHash(contentHash))
            throw new ArgumentException("A Hashi content hash must have 64 lowercase hexadecimal characters.", nameof(contentHash));
        return new PuzzleId(PuzzleGame.Hashi, HashiPrefix + contentHash, null, contentHash);
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
        else if (value?.Length == HashiPrefix.Length + 64 && value.StartsWith(HashiPrefix, StringComparison.Ordinal))
        {
            var suffix = value[HashiPrefix.Length..];
            if (IsContentHash(suffix))
            {
                id = FromHashi(suffix);
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
