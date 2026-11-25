namespace KoboLogicPuzzles.Server.Tests;

public sealed class PuzzleIdTests
{
    [Fact]
    public void NamespacesRoundTripWithoutChangingProviderIds()
    {
        var nonogram = PuzzleId.FromWebPbn(int.MaxValue);
        Assert.Equal("nonogram-webpbn-2147483647", nonogram.Value);
        Assert.True(PuzzleId.TryParse(nonogram.Value, out var parsedNonogram));
        Assert.Equal(nonogram, parsedNonogram);
        Assert.Equal(int.MaxValue, parsedNonogram.WebPbnId);

        var hash = new string('a', 64);
        var hashi = PuzzleId.FromHashi(hash);
        Assert.True(PuzzleId.TryParse("hashi-local-" + hash, out var parsedHashi));
        Assert.Equal(hashi, parsedHashi);
        Assert.Equal(hash, parsedHashi.ContentHash);
        Assert.NotEqual(nonogram, hashi);
        Assert.False(PuzzleId.TryParse("hashi-local-" + hash.ToUpperInvariant(), out _));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("nonogram-webpbn-012")]
    [InlineData("nonogram-webpbn-0")]
    [InlineData("nonogram-webpbn-2147483648")]
    [InlineData("nonogram-webpbn-12\0")]
    [InlineData("hashi-local-12")]
    public void RejectsNonCanonicalIds(string value) => Assert.False(PuzzleId.TryParse(value, out _));
}
