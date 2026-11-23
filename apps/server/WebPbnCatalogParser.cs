using System.Globalization;
using AngleSharp.Html.Parser;

namespace KoboNonogram.Server;

public sealed record WebPbnPage(IReadOnlyList<PuzzleSummary> Puzzles, bool HasMore);

public static class WebPbnCatalogParser
{
    public static WebPbnPage Parse(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var header = document.QuerySelectorAll("tr").FirstOrDefault(row =>
            row.Children.Select(cell => cell.TextContent.Trim()).Contains("puzzle") &&
            row.Children.Select(cell => cell.TextContent.Trim()).Contains("size") &&
            row.Children.Select(cell => cell.TextContent.Trim()).Contains("colors") &&
            row.Children.Select(cell => cell.TextContent.Trim()).Contains("author"));
        if (header?.ParentElement is not { } resultRows)
            throw new InvalidDataException("WebPBN search results did not contain the catalog table.");

        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var column = 0;
        foreach (var cell in header.Children)
        {
            columns[cell.TextContent.Trim()] = column;
            column += int.TryParse(cell.GetAttribute("colspan"), out var span) ? span : 1;
        }

        var puzzles = new List<PuzzleSummary>();
        var explicitlyEmpty = false;
        var hasMore = false;
        foreach (var row in resultRows.Children.SkipWhile(row => row != header).Skip(1))
        {
            var rowText = row.TextContent.Trim();
            if (row.Children.Length == 1 && rowText.Equals("NEXT PAGE OF RESULTS", StringComparison.OrdinalIgnoreCase) &&
                row.QuerySelector("a[href^='find.cgi?'], a[href^='/find.cgi?']") is not null)
            {
                hasMore = true;
                continue;
            }

            if (rowText.Equals("None Found", StringComparison.OrdinalIgnoreCase))
            {
                explicitlyEmpty = true;
                continue;
            }

            var link = row.QuerySelector("a[href^='play.cgi?'], a[href^='/play.cgi?']");
            var label = link?.TextContent.Trim() ?? "";
            var separator = label.IndexOf(':');
            if (separator < 2 || label[0] != '#' ||
                !int.TryParse(label.AsSpan(1, separator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id <= 0 || columns.Values.Max() >= row.Children.Length)
                throw new InvalidDataException("WebPBN search results contained an unrecognized puzzle row.");

            var size = row.Children[columns["size"]].TextContent.Trim().Split('x', '×');
            if (size.Length != 2 || !int.TryParse(size[0], out var width) || !int.TryParse(size[1], out var height) ||
                width <= 0 || height <= 0 || !int.TryParse(row.Children[columns["colors"]].TextContent.Trim(), out var colors))
                throw new InvalidDataException($"WebPBN puzzle {id} has invalid dimensions or color metadata.");

            if (colors == 2)
            {
                var author = row.Children[columns["author"]].TextContent.Trim();
                puzzles.Add(new PuzzleSummary(id, label[(separator + 1)..].Trim(), author, width, height));
            }
        }

        if (puzzles.Count == 0 && (!explicitlyEmpty || hasMore) || explicitlyEmpty && puzzles.Count > 0)
            throw new InvalidDataException("WebPBN search results did not contain matching puzzle rows or an empty result.");

        return new WebPbnPage(puzzles, hasMore);
    }
}
