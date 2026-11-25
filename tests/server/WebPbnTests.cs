using System.ComponentModel.DataAnnotations;
using KoboLogicPuzzles.Server;
using Microsoft.AspNetCore.WebUtilities;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class WebPbnTests
{
    [Fact]
    public void ReadsCatalogIdsDimensionsMetadataAndPagination()
    {
        var summary = new PuzzleSummary(12, "Bricks & Steps", "M. Grey", 65, 35, new PuzzleMetadata(3.75m, 4.25m, null));
        var unrated = TestData.Summary(13) with { Metadata = new PuzzleMetadata(null, null, null) };
        var page = WebPbnCatalogParser.Parse(TestData.SearchPage(true, summary, unrated));

        Assert.True(page.HasMore);
        Assert.Equal(new[] { summary, unrated }, page.Puzzles);
    }

    [Fact]
    public void AcceptsExplicitEmptyResultsAndRejectsMalformedPages()
    {
        var empty = WebPbnCatalogParser.Parse(TestData.SearchPage(false));

        Assert.Empty(empty.Puzzles);
        Assert.False(empty.HasMore);
        Assert.Throws<InvalidDataException>(() => WebPbnCatalogParser.Parse("<h1>Please log in</h1>"));
        Assert.Throws<InvalidDataException>(() => WebPbnCatalogParser.Parse(
            TestData.SearchPage(false, TestData.Summary(12)).Replace("3x2", "unknown")));
        Assert.Throws<InvalidDataException>(() => WebPbnCatalogParser.Parse(
            TestData.SearchPage(true, TestData.Summary(12)).Replace("NEXT PAGE OF RESULTS", "Unknown row")));
    }

    [Fact]
    public async Task SendsPublicSearchFiltersAndQuarterStarRatings()
    {
        var options = new WebPbnOptions
        {
            MinQuality = 4.25m, IncludeUnratedQuality = false, Solvability = 3, RequestDelayMilliseconds = 0,
        };
        using var client = TestData.Source(uri =>
        {
            var query = QueryHelpers.ParseQuery(uri.Query);
            Assert.Equal("/find.cgi", uri.AbsolutePath);
            Assert.Equal("0", query["status"].ToString());
            Assert.Equal("2", query["mincolor"].ToString());
            Assert.Equal("2", query["maxcolor"].ToString());
            Assert.Equal("1", query["uniq"].ToString());
            Assert.Equal("2", query["blots"].ToString());
            Assert.Equal("17", query["minqual"].ToString());
            Assert.Equal("12", query["mindiff"].ToString());
            Assert.Equal("3", query["guess"].ToString());
            Assert.Equal("4", query["pageno"].ToString());
            Assert.False(query.ContainsKey("unqual"));
            Assert.Equal("1", query["undiff"].ToString());
            return TestData.Ok(TestData.SearchPage(false));
        }, options);

        await client.SearchAsync(4, CancellationToken.None);
    }

    [Fact]
    public void ValidatesBoundsAndRatingIncrements()
    {
        var options = new WebPbnOptions { MinArea = 2000, MaxArea = 1000, MinQuality = 4.1m };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), errors, true));
        Assert.Equal(2, errors.Count);
        Assert.True(new WebPbnOptions().Fits(80, 80));
        Assert.False(new WebPbnOptions().Fits(99, 70));
        Assert.False(new WebPbnOptions().Fits(20, 20));
    }

    [Fact]
    public void ReadsRectangularXmlAndVersionedProviderId()
    {
        var xml = TestData.Export(12).Replace("</author>",
            "</author><note>published,definitely unique,definitely requires moderate lookahead</note>");
        var puzzle = Assert.IsType<PuzzleDefinition>(WebPbnPuzzleParser.Parse(xml, 12));

        Assert.Equal(12, puzzle.Id);
        Assert.Equal("Bricks & Steps", puzzle.Title);
        Assert.Equal(3, puzzle.Width);
        Assert.Equal(2, puzzle.Height);
        Assert.Equal(new[] { 1, 1 }, puzzle.RowClues[0]);
        Assert.Equal(new[] { 2 }, puzzle.ColumnClues[2]);
        Assert.Equal(3, puzzle.Metadata?.Solvability);
    }

    [Theory]
    [InlineData("goal", "|X.X|\n |.XX|", "#.#.##")]
    [InlineData("goal", "|XXX||.X.|", null)]
    [InlineData("goal", "|X.X||.X?|", null)]
    [InlineData("saved", "|X.X||.XX|", null)]
    public void ImportsOnlyAValidGoalImage(string type, string image, string? solution)
    {
        var xml = TestData.Export(12).Replace("</puzzle>",
            $"<solution type=\"{type}\"><image>{image}</image></solution></puzzle>");

        var puzzle = Assert.IsType<PuzzleDefinition>(WebPbnPuzzleParser.Parse(xml, 12));

        Assert.Equal(solution, puzzle.Solution);
    }

    [Fact]
    public async Task RecognizesMissingPuzzlePageReturnedWithHttpSuccess()
    {
        using var client = TestData.Source(uri => uri.Query.Contains("id=12")
            ? TestData.Ok("<html><body>Puzzle 12 does not exist</body></html>")
            : TestData.Ok(TestData.Export(13).Replace("Bricks &amp; Steps", "Puzzle 13 does not exist")));

        Assert.Null(await client.FetchAsync(12, CancellationToken.None));
        Assert.NotNull(await client.FetchAsync(13, CancellationToken.None));
    }

    [Fact]
    public void AcceptsEmptyLinesAndRejectsUnplayableExports()
    {
        var xml = TestData.Export(12);
        var withEmptyColumn = xml.Replace("<clues type=\"columns\">", "<clues type=\"columns\"><line/>");
        var puzzle = Assert.IsType<PuzzleDefinition>(WebPbnPuzzleParser.Parse(withEmptyColumn, 12));

        Assert.Empty(puzzle.ColumnClues[0]);
        Assert.Null(WebPbnPuzzleParser.Parse(xml.Replace("000000", "FF0000"), 12));
        Assert.Null(WebPbnPuzzleParser.Parse(xml.Replace("<count>2", "<count>0"), 12));
        Assert.Throws<InvalidDataException>(() => WebPbnPuzzleParser.Parse(xml, 13));
        Assert.Throws<InvalidDataException>(() => WebPbnPuzzleParser.Parse(xml.Replace("<count>2", "<count>3"), 12));
        Assert.Throws<InvalidDataException>(() => WebPbnPuzzleParser.Parse("<html>error</html>", 12));
    }
}
