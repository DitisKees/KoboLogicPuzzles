using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KoboLogicPuzzles.Server.Galaxies;
using KoboLogicPuzzles.Server.GrandGames;
using KoboLogicPuzzles.Server.Hashi;
using KoboLogicPuzzles.Server.LinkAPix;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class GrandGamesTests
{
    [Fact]
    public void CatalogReadsAttributionAndFollowsOnlyTheSelectedFeed()
    {
        const string html = """
            <script>SiteVersion = 1234;</script>
            <div class="ag-catalog">
              <div id="cards-list">
                <article class="gg-card" data-ag-item="12">
                  <h2 class="gg-card__title"><a href="/nonograms/id12_fixture"> Picture </a></h2>
                  <div class="gg-card__meta"><span title="Author"><a href="/user/author">Author</a></span></div>
                  <div class="gg-card__stats"><span title="Rating"><b>4.8</b></span></div>
                </article>
                <article class="gg-card" data-ag-item="13">
                  <h2 class="gg-card__title"><a href="https://example.org/nonograms/id13"> Other site </a></h2>
                </article>
                <article class="gg-card" data-ag-item="14">
                  <h2 class="gg-card__title"><a href="/bridges/id14"> Other catalog </a></h2>
                </article>
              </div>
              <nav class="pagination"><a href="?page=2" rel="next">Next</a></nav>
            </div>
            """;
        var catalog = GrandGamesCollections.ReadCatalog(html, new(PuzzleGame.Nonogram, "nonograms"));
        Assert.Equal("1234", catalog.Version);
        Assert.True(catalog.HasMore);
        Assert.Equal(new(12, "https://en.grandgames.net/nonograms/id12_fixture", "Picture", "Author", "4.8"),
            Assert.Single(catalog.Entries));
    }

    [Fact]
    public void NonogramBitmapUsesColumnMajorOrderingAndRequiresMonochrome()
    {
        const string bitmap = """{"move":[[1,9],[1,1],[9,9]],"colors":{"1":"#000000"}}""";
        Assert.Equal("##..#.", GrandGamesFormats.Nonogram(new(1, "nonograms", 3, 2, Picross(bitmap))));
        Assert.Throws<InvalidDataException>(() => GrandGamesFormats.Nonogram(new(1, "nonograms", 3, 2,
            Picross(bitmap.Replace("#000000", "#f00000")))));
    }

    [Fact]
    public void NumberPuzzlesReadRowsAndPreserveFixedPieces()
    {
        var bridges = GrandGamesFormats.Hashi(new(1, "bridges", 3, 2, "2 1  1"));
        Assert.Equal(new HashiIsland(0, 0, 2), bridges.Islands[0]);
        Assert.Equal(new HashiIsland(2, 1, 1), bridges.Islands[2]);

        var (nurikabe, sea) = GrandGamesFormats.Nurikabe(new(2, "nurikabe", 3, 2,
            """[[2,".","."],[".","1","."]]""", P2: "..##.#"));
        Assert.Equal([2, 0, 0, 0, 1, 0], nurikabe);
        Assert.Equal("xx##x#", sea);

        var tents = GrandGamesFormats.Placement(new(3, "tents", 3, 2, ".t.1...0010."), PuzzleGame.Tents);
        Assert.Equal(".t....", tents.Givens);
        Assert.Equal([1, 0], tents.Rows);
        Assert.Equal([0, 1, 0], tents.Columns);

        var ships = GrandGamesFormats.Placement(new(4, "ships", 10, 10,
            "abcdefg...2" + string.Concat(Enumerable.Repeat("..........2", 9)) + "2222222222"),
            PuzzleGame.Battleships);
        Assert.StartsWith("xomnesw", ships.Givens);
        Assert.Equal([4, 3, 2, 1], ships.Fleet);
    }

    [Fact]
    public void ClassicGalaxiesReadsHalfCellCentersAndPublishedRegions()
    {
        var (board, walls) = GrandGamesFormats.Galaxies(new(1, "galaxies", 3, 1, "10010", P2: "[[4,6,6]]"));
        Assert.Equal([new GalaxyCenter(1, 1, false), new GalaxyCenter(4, 1, false)], board.Centers);
        Assert.Equal("#x", walls);
        Assert.True(board.IsSolved(walls!));
    }

    [Fact]
    public void LinkMasksAndPackedPixelsProduceIdenticalPathsAndSingletons()
    {
        var attribution = new LinkAttribution("grandgames", "1", "https://en.grandgames.net/filippinskie/id1",
            "Author", "Test", "Personal offline use");
        var legacy = GrandGamesFormats.LinkAPix(new(1, "filippinskie", 4, 2, "1B0B3A|0||||||"), attribution);
        Assert.Equal([new(1, 2), new(2, 2), new(6, 1)], legacy.Clues);

        // Pixel groups are three little-endian base-33 digits. Digit 32 is background.
        var pixels = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(2), 35936);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(4), 35904);
        var body = JsonSerializer.Serialize(new
        {
            w = 4, h = 2, palette = new[] { "#000000" }, pixels33 = Convert.ToBase64String(pixels),
            paths = new[] { new[] { 1, 2 } }, implicitSingles = true,
        });
        var modern = GrandGamesFormats.LinkAPix(new(1, "filippinskie", 4, 2, "F3:" + Deflate(body, zlib: true)), attribution);
        Assert.Equal(legacy.Clues, modern.Clues);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(modern.Paths!));
        Assert.Equal(".##...#.", modern.Picture);
        Assert.Equal(modern.Picture, new LinkAPixBoard(4, 2, modern.Clues).Picture(modern.Paths!));
        Assert.Throws<InvalidDataException>(() => GrandGamesFormats.LinkAPix(new(1, "filippinskie", 4, 2,
            "F2:" + body.Replace("#000000", "#12ab34")), attribution));
    }

    [Fact]
    public async Task RateLimitedRefreshResumesAndPublishesOnlyVerifiedDefinitions()
    {
        using var database = new TestDatabase();
        using var webPbn = TestData.Source(_ => throw new InvalidOperationException("Unexpected WebPBN request."));
        using var grandGames = new LinkFeed();
        await using var app = ApiTests.CreateApp(database, webPbn,
            configureServices: services => services.AddHttpClient("grandgames").ConfigurePrimaryHttpMessageHandler(() => grandGames));
        await app.StartAsync();
        var collections = app.Services.GetRequiredService<GrandGamesCollections>();
        var feed = GrandGamesCollections.Feeds.Single(feed => feed.Game == PuzzleGame.LinkAPix);

        var error = await Assert.ThrowsAsync<GrandGamesRateLimited>(() => collections.Refresh(feed, true, default));
        Assert.Equal(TimeSpan.FromMinutes(3), error.Delay);
        Assert.Equal(1, database.Store.CountLinkAPix().Total);
        Assert.NotEmpty(database.Store.GetLinkRefresh("grandgames").Cursor);
        Assert.Equal(new(1, 0, 0, 0, false), await collections.Refresh(feed, true, default));
        Assert.Equal(new[] { 11, 12, 12 }, grandGames.Requests);
        Assert.Empty(database.Store.GetLinkRefresh("grandgames").Cursor);

        using var http = app.GetTestClient();
        var catalog = await http.GetFromJsonAsync<GameCatalogPage>("/api/v2/puzzles?game=link-a-pix&source=grandgames");
        Assert.Equal(2, catalog!.Puzzles.Count);
        using var queue = await http.PostAsJsonAsync("/api/v2/offline-queue", new { game = "link-a-pix", source = "grandgames", targetCount = 2 });
        queue.EnsureSuccessStatusCode();
        var boards = (await queue.Content.ReadFromJsonAsync<GameOfflineQueueResponse>())!.Puzzles;
        Assert.Equal(2, boards.Count);
        Assert.All(boards, definition => Assert.Null(Assert.IsType<LinkAPixDefinition>(definition).Solution));
    }

    private sealed class LinkFeed : HttpMessageHandler
    {
        internal List<int> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("color=0", request.RequestUri!.Query);
                return TestData.Ok("""
                    <script>SiteVersion = 1234;</script>
                    <div id="cards-list">
                    <article class="gg-card" data-ag-item="11"><h2 class="gg-card__title"><a href="/filippinskie/id11">First</a></h2></article>
                    <article class="gg-card" data-ag-item="12"><h2 class="gg-card__title"><a href="/filippinskie/id12">Second</a></h2></article>
                    </div>
                    """);
            }
            Assert.Equal("https://api.grandgames.net/agmove/start", request.RequestUri!.AbsoluteUri);
            Assert.Equal("1234", Assert.Single(request.Headers.GetValues("x-version")));
            Assert.False(request.Headers.Contains("Cookie"));
            using var arguments = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var id = arguments.RootElement.GetProperty("id").GetInt32();
            Requests.Add(id);
            if (Requests.Count == 2)
                return new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3)) } };
            return TestData.Ok(JsonSerializer.Serialize(new
            {
                mode = "normal",
                level = new { id, game = "filippinskie", sx = 4, sy = 2, p = id == 11 ? "1B0B3A" : "1B0B4A" },
            }));
        }
    }

    private static string Picross(string payload) => new(Deflate(payload, zlib: false)
        .Select(symbol => symbol switch { '1' => '2', '2' => 'a', 'a' => '1', _ => symbol }).ToArray());

    private static string Deflate(string payload, bool zlib)
    {
        using var result = new MemoryStream();
        using (Stream stream = zlib ? new ZLibStream(result, CompressionLevel.Fastest, leaveOpen: true)
            : new DeflateStream(result, CompressionLevel.Fastest, leaveOpen: true))
            stream.Write(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(result.ToArray());
    }
}
