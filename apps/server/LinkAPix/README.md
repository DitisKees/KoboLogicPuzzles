# Picture Link-a-Pix

Link-a-Pix is a collection-backed, monochrome picture game. A number above one connects to an equal number along a simple orthogonal path of exactly that many cells, counting both endpoints. A one paints its own cell. Paths cannot overlap or pass through other clues. The unused cells form the white background.

Use **Line drawing → Link-a-Pix** in KOReader. The catalog prefers boards with both dimensions at least 40, then boards with both dimensions at least 30. Use source, width, height, difficulty, and solvability filters in the Kobo catalog or per-game offline queue. Dimensions up to 200×200, including rectangular boards, fit the API and game model. Large boards can be played in a panned viewport.

## Collections

| Provider | Source ID | Format, observed size and permission |
| --- | --- | --- |
| [GrandGames](https://en.grandgames.net/filippinskie) | `grandgames` | The first 36 Large B&W listings included 28 boards with both sides at least 30 and four with both sides at least 40. The publisher's broad size categories leave exact archive-wide counts for those thresholds unavailable. The first live batch verified all eight boards. A separate check verified 31×70 and 45×48 boards. Guest player data supplies a stable numeric ID and either a legacy clue mask or a structured bitmap with paths. Personal noncommercial offline play is based on the project owner's confirmation with GrandGames. [Filtered catalog](https://en.grandgames.net/filippinskie?color=0&csize=0) · [Terms](https://en.grandgames.net/termsofuse) |
| [PlaySudoku](https://playsudoku.me/lap.html) | `playsudoku` | Public puzzle pages have stable numeric IDs and structured Ketten clue and answer grids. The current site reported 2,686 puzzles up to 60×60. Its large category showed 56 pages, and the very large category showed two entries. Both categories can contain a shorter side below 30, so those counts cannot establish the number of eligible boards. The importer checks both dimensions and retains the author in the structured puzzle. The first live 32-page batch added 26 unique boards, all at least 30×30. The old .ru address is a mirror of the migrated .me catalog. Personal noncommercial caching is an opt-in assumption. |
| [GrayCell](https://www.graycell.ru/works/filip/1.html) | `graycell` | Thirty printable picture puzzles, including 30×35, 30×45, and 40×45. The stable numbered ZIPs contain vector grid/text records and answer lines. The first complete scan verified 23 puzzles, including five with both sides at least 30. Seven reached a proof limit. The importer reads the vectors directly and credits Алексей Пискунов. The author describes downloading and printing with attribution. Personal noncommercial digital caching is an opt-in assumption. [Author FAQ](https://www.graycell.ru/FAQ.html) |
| [Janko Ketten](https://www.janko.at/Raetsel/Ketten/index.htm) | `janko` | The catalog lists 50 numbered entries with size and difficulty indexes. Nine Otto Janko pages (041–049) have both a structured picture solution and an explicit per-page CC BY-NC-SA 3.0 grant. Those nine 20×20 boards imported successfully in the live check. The importer requires that per-page grant. [License](https://www.janko.at/Raetsel/Creative-Commons.htm) |
| [Conceptis](https://www.conceptispuzzles.com/index.aspx?uri=puzzle/link-a-pix) | `conceptis` | Artist-designed B&W and color collections, including Mega sizes. The service licenses purchased puzzles for household use and serves free and paid puzzles. Import an authorized structured export as a local pack. [Conceptis FAQ](https://www.conceptispuzzles.com/index.aspx?uri=info/doc/330) |

Personal-use imports belong on a private server. The collection workers keep source IDs, titles, authors, original URLs, and reuse information in each attribution. Multiple source attributions can point to the same board. The Kobo library uses the game and content ID as the display title so it keeps the picture subject hidden before play.

### Enable refresh

For a personal noncommercial server, enable the four web collections in the Compose service environment.

```yaml
LinkAPixCollections__AllowNonCommercial: "true"
LinkAPixCollections__JankoEnabled: "true"
LinkAPixCollections__PlaySudokuEnabled: "true"
LinkAPixCollections__GrayCellEnabled: "true"
GrandGamesCollections__Enabled: "true"
```

Each enabled feed starts in the background and records its own cursor and completed check time. PlaySudoku checks up to 32 puzzle pages per pass. GrandGames checks up to eight boards per pass and pauses when the publisher returns a rate limit. Completed feeds are checked again after seven days. The dashboard and `GET /api/v2/link-a-pix/status` show the current feed state and inconclusive proofs.

```sh
dotnet run --project apps/server -- link-a-pix-refresh --source janko --allow-non-commercial true
dotnet run --project apps/server -- link-a-pix-refresh --source playsudoku --allow-non-commercial true
dotnet run --project apps/server -- link-a-pix-refresh --source graycell --allow-non-commercial true
dotnet run --project apps/server -- grandgames-refresh --game link-a-pix
dotnet run --project apps/server -- link-a-pix-status
```

Use the same arguments after `dotnet KoboLogicPuzzles.Server.dll` inside the container.

### Import a pack

Run `dotnet run --project apps/server -- link-a-pix-import --file /path/to/pack.json` for a collection file you are authorized to use. The top-level value is an array. Each item carries its own attribution and can be imported on its own. Recognized source IDs are those in the table and `local`. Set `license` to the applicable license or grant. Preserve the original publisher ID and URL for exports.

```json
[
  {
    "width": 3,
    "height": 2,
    "clues": [{"cell":0,"length":4},{"cell":1,"length":4},{"cell":5,"length":1}],
    "paths": [[0,3,4,1]],
    "picture": "##.###",
    "attribution": {
      "source": "local",
      "sourceId": "sample",
      "sourceUrl": "https://example.org/puzzles/sample",
      "author": "Example author",
      "title": "Example",
      "license": "CC0"
    }
  }
]
```

Cells are zero-based row-major indices. The optional ordered paths omit singleton clues, which are implicit. The optional picture is a flattened string of `#` and `.`. Both are checked against an independent uniqueness proof. Optional attribution fields are `licenseUrl` and `originalRating`. The importer accepts omitted, `black`, or `#000000` clue colors for monochrome. Other colors require a color-capable release.

## Verification limits

The solver enumerates paths with exact lengths, eliminates options using distance, parity and occupied cells, then searches for up to two disjoint path layouts. Reversing a path is the same layout. Different routes or pairings can count as different solutions even when they paint the same cells. Supplied answers and pictures are checked after an independent search. Invalid, ambiguous, and inconclusive boards stay outside the verified catalog.

Current proof limits are ten seconds, eight million work units, 512 cells per clue, 96 branching levels, and a bound on retained candidate routes. Difficulty is a heuristic using clue length and branching. Solvability identifies forced short paths and cases with one level of lookahead. Longer paths and deeper cases carry `unknown` solvability even when uniqueness is established. The source's rating stays separate in its attribution.


### Local benchmark

Linux x86-64, Intel i5-4200M, .NET 10 Debug CLI. Each measurement includes the cold process startup, independent proof and a temporary SQLite write. The two large-limit fixtures exercised dimensions and clue lengths without depending on a third-party export.

| Verified input | Dimensions | Longest clue | Elapsed | Peak process RSS |
| --- | --- | ---: | ---: | ---: |
| Janko picture | 20×20 | 9 | 1.29 s | 76 MiB |
| PlaySudoku picture | 45×44 | 10 | 2.95 s | 79 MiB |
| GrayCell picture | 30×45 | 20 | 4.84 s | 79 MiB |
| Rectangular viewport fixture | 120×93 | 4 | 0.95 s | 76 MiB |
| Forced long corridor fixture | 128×2 | 127 | 1.13 s | 76 MiB |

KOReader's framebuffer smoke suite checks a 120×93 fixture at the Aura H2O resolution, including panning, edge scrolling, path turns, backtracking, redraw equivalence and restoring both partial and completed boards. Physical-device testing remains pending.
