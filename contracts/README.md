# Puzzle and progress API

## Version 2

Kobo Logic Puzzles uses `/api/v2`. Nonogram definitions and saves use their WebPBN IDs in SQLite. The API exposes those IDs with the `nonogram-webpbn-` prefix.

| Method and path | Purpose |
| --- | --- |
| `GET /api/v2/capabilities` | Return `apiVersion`, `schemaVersion`, the supported `games`, and `advancedFilters: true` when ranges and multiple values are available. Hashi, Slitherlink, and Nurikabe are advertised when generation is enabled or puzzles are available. Fill-a-Pix and Galaxies are advertised when their first puzzles are imported. Tents and Battleships are advertised when a generator or collection is enabled or a puzzle is available. `slitherlinkSources` maps available source IDs to display names. `puzzleSources` provides a map from game and source ID to `{ name, kind }`, where `kind` is `generated` or `collection`. `extraCollections: true` advertises support for additional Nonogram and Hashi IDs or board sizes. |
| `GET /api/v2/puzzles` | Browse one game. Supply `game=nonogram`, `game=hashi`, `game=fill-a-pix`, `game=slitherlink`, `game=nurikabe`, or `game=galaxies` and optional filters and `limit` (1–100, default 50). Returns `{ puzzles, nextBeforeId }`. Repeat the filters and pass `nextBeforeId` as `beforeId` for the next page. |
| `GET /api/v2/puzzles/{id}` | Retrieve a puzzle definition. Set `includeSolution=true` to request its solution. |
| `GET /api/v2/puzzles/metadata` | Look up source metadata or ratings for up to 100 typed IDs supplied as repeated `id` parameters. Returns an object keyed by puzzle ID. |
| `POST /api/v2/offline-queue` | Top up one game’s unstarted cache. Returns `{ puzzles, remaining }`. |
| `POST /api/v2/progress` | Back up up to 100 changed boards in one transaction. Returns `{ savedCount, backedUpAt }` after commit. |
| `GET /api/v2/restore` | Return `{ puzzle, progress }` records for all games. Supply `game` to restore one game. |
| `GET /api/v2/hashi/status` | Show Hashi stock, unstarted counts and targets by size, difficulty, and solvability, and generation outcomes for the running server. |
| `GET /api/v2/slitherlink/status` | Show available sources, generation timeout, and unstarted counts and targets by size, tiling, and difficulty. |
| `GET /api/v2/nurikabe/status` | Show available sources, stock by size and difficulty, proof and generation outcomes, and Janko refresh progress. |
| `GET /api/v2/tents/status`, `GET /api/v2/battleships/status` | Show generator availability, targets and unstarted stock by size and difficulty, generation activity, and Janko refresh progress. |
| `GET /api/v2/link-a-pix/status` | Show opt-in Link-a-Pix refresh state and proof outcomes for each web collection. |
| `GET /api/v2/dashboard` | Read catalog totals and unstarted counts by source, source kinds, generation statuses, and the server's sampling time. The read-only web dashboard at `/` uses this endpoint. |

Catalog order is descending WebPBN ID for Nonogram and descending content hash for Hashi. Summaries carry `id`, `game`, dimensions, available metadata, and a `status` of `unstarted`, `started`, or `solved`. Nonogram filters use difficulty on a 1–5 scale in quarter-star increments and numeric WebPBN solvability levels 1–6. Metadata may also include quality on the same star scale. Hashi filters use `easy`, `medium`, `hard`, or `expert` and `basic_logic`, `advanced_logic`, `bounded_lookahead`, `requires_search`, or `unknown`. Malformed filters and cursors from the other game return `400`.

### Catalog and queue filters

`width`, `height`, `difficulty`, and `solvability` accept exact values. With `advancedFilters` enabled, their alternatives `widthFilter`, `heightFilter`, `difficultyFilter`, and `solvabilityFilter` accept comma-separated lists. Use a hyphen for inclusive ranges in width, height, or Nonogram difficulty. Dimensions use integers from 1 to 100. Nonogram difficulty uses quarter-star increments. For example, `widthFilter=20-35,50&difficultyFilter=3.5-4.25` matches widths from 20 through 35 or exactly 50, together with difficulty from 3.5 through 4.25.

Hashi also accepts `sizeFilter` for square sizes from 20 to 50 in steps of 5. For example, `sizeFilter=20-30&difficultyFilter=easy,medium&solvability=basic_logic` matches 20×20, 25×25, and 30×30 boards that are Easy or Medium and use Basic logic. `sizeFilter` takes the place of both width and height filters. Supply one form per field and omit fields that accept any value. A puzzle with missing rating metadata is excluded when the corresponding rating is filtered.

Fill-a-Pix accepts `width`, `height`, `widthFilter`, and `heightFilter` with any positive integer dimensions. It orders catalog entries by descending string ID. Source ratings are available as metadata when supplied upstream.

Slitherlink supports nominal dimensions 1–100, `sizeFilter` for equal nominal width and height, and difficulty levels `easy`, `normal`, `tricky`, and `hard`. The `tiling` and `source` filters accept a single key. `tilingFilter` and `sourceFilter` accept comma-separated keys. For example, `game=slitherlink&tilingFilter=triangular,kagome&source=loopy`. Entries are ordered by descending namespaced ID. The metadata's `faceCount` gives the actual number of cells on the board.

Nurikabe accepts dimensions from 1 to 50 and filters on source, difficulty, and solvability. The difficulty tiers are `easy`, `medium`, `hard`, and `expert`. Solvability uses `basic_logic`, `advanced_logic`, `bounded_lookahead`, or `requires_search`. For example, `game=nurikabe&widthFilter=25-50&sourceFilter=local,janko&difficultyFilter=hard,expert`. Entries are ordered by descending namespaced ID.

Galaxies accepts dimensions from 1 to 250 and supports source, difficulty, and solvability filters. Use `sizeFilter` for square boards. Difficulty uses `easy`, `medium`, `hard`, and `expert`. The catalog and offline queue list large picture boards first, then smaller pictures, then classic boards, with descending namespaced IDs within each group.

Tents and Battleships accept dimensions from 1 to 50, including rectangular boards. They accept `sizeFilter` for square boards and filters for `source`, `difficulty`, and `solvability` with the Hashi choice names. Entries sort by descending namespaced ID. Offline queues without rating filters prefer large Hard and Expert boards with basic logic, advanced logic, or bounded lookahead proofs.

Link-a-Pix accepts dimensions from 1 to 200 and supports source, difficulty, solvability, and square size filters. Its catalog orders boards with both dimensions at least 40 ahead of those measuring at least 30, then lists smaller boards. Entries within each group use descending namespaced IDs. Each attribution can match a source filter.

With `extraCollections`, pass `includeCollections=true` to open the full Nonogram or Hashi catalog and restore additional saved boards. Send `includeCollections: true` in the offline-queue JSON for those games. An explicit `source` or `sourceFilter` also selects the requested collections for catalog and queue calls. Older clients get WebPBN and local Bridges boards on these endpoints by default. The Nonogram catalog orders additional boards by descending namespaced ID and then lists WebPBN boards by descending numeric ID.

Known Nonogram source IDs are `webpbn`, `world-at-play`, `sen-ltd`, `janko`, and `grandgames`. Known Hashi source IDs are `bridges`, `sen-ltd`, `janko`, and `grandgames`. For additional Nonograms, `nonogram-collection-` is followed by a 64-character SHA-256 of the row and column clue layout. These definitions use the same marks and clue schema and add source, authorship and license metadata. Imported Hashi definitions use the existing content-based IDs and may have smaller or rectangular dimensions up to 50×50. Their added metadata identifies the original source, including whether the publisher generated the board.

### Definitions

`GET /api/v2/puzzles/{id}` returns `game`, `schemaVersion` (currently 1), `id`, `width`, `height`, and game-specific fields. IDs are canonical and case-sensitive. A malformed ID returns `400`. An unavailable puzzle returns `404`. Pass `includeSolution=true` to request a solution.

| Game | ID format | Definition fields |
| --- | --- | --- |
| `nonogram` | `nonogram-webpbn-12` for WebPBN ID 12 | `title`, `author`, `rowClues`, `columnClues`, and optional WebPBN `metadata`. An empty clue line has an empty array. The requested `solution`, when available, is a row-major string using `#` for filled and `.` for empty cells. |
| `hashi` | `hashi-local-` followed by the 64-character lowercase SHA-256 of the canonical clue layout | `islands` contains `{ x, y, clue }` objects. `edges` contains `{ first, second }` pairs of island indices. The requested `solution` is an array of counts aligned with `edges`. |
| `fill-a-pix` | `fill-a-pix-janko-12`, `fill-a-pix-grandgames-7178`, or `fill-a-pix-fillapixnet-1`, using the source’s numeric ID | `title`, optional `author`, and a flat row-major `clues` array with `null` for a cell without a clue and 0–9 for numbered cells. Metadata carries `source`, `sourceUrl`, and any available `license`, `quality`, or `complexity`. The requested `solution`, when available, is a row-major string with `#` for filled and `.` for empty. |
| `slitherlink` | `slitherlink-` followed by a 64-character lowercase SHA-256 of the source and canonical board | `title`, optional `author`, `vertices` as `{ x, y }`, `edges` as `{ first, second }` vertex indices, and `faces` as `{ x, y, edges, clue }` with a clue position, edge indices, and nullable clue. Metadata carries `tiling`, `source`, `sourceName`, `faceCount`, and optional difficulty, source URL, license, and seed. The requested `solution` uses one character per edge. |
| `nurikabe` | `nurikabe-` followed by a 64-character lowercase SHA-256 of the canonical clue layout | `title`, optional `author`, and flat row-major `clues` with zero for a cell without a clue. `metadata` contains `source`, `sourceName`, `sourceKind`, `sourceId`, `unique`, difficulty and solvability. It may include a source URL, license, seed, generator version or original rating. A solution is included on request. |
| `galaxies` | `galaxies-` followed by the 64-character lowercase SHA-256 of the dimensions and colored centers | `title`, `author`, `centers`, and `metadata` with `picture`, source ID and URL, license and URL, source version, content hash, difficulty, and solvability. The requested `solution` uses one character per interior edge. |
| `tents` | `tents-<64 lowercase hex digits>` | `title`, optional `author`, `rowClues`, `columnClues`, `givens`, `metadata`, optional `solution` |
| `battleships` | `battleships-<64 lowercase hex digits>` | Same fields plus `fleet` |
| `link-a-pix` | `link-a-pix-<64 lowercase hex digits>` | `title`, `author`, `clues`, `metadata`, and optional `solution` and `picture` |

Hashi coordinates and array indices start at zero. Island indices follow `(x, y)` order and edges follow `(first, second)` order. An edge joins the nearest islands in one row or column. Hashi `metadata` carries `islandCount`, `actualDensity` as a percentage, `solvability`, and `difficulty` and `difficultyScore` when rated.

Slitherlink vertices are sorted by `(x, y)` in nonnegative drawing coordinates. Edges are sorted by their ascending zero-based vertex indices. Each face's edges are sorted zero-based indices into the edge array. The face clue counts incident loop edges and may be larger than 3 on non-square tilings.

Galaxies centers are `{ x, y, black }` objects sorted by row then column. Coordinates use half-cell units from the top-left corner of the grid. Odd pairs are cell centers, one even coordinate indicates an edge, and two even coordinates indicate a corner. `metadata.picture` distinguishes pictures from classic puzzles.

### Offline queue and backup

Send `game` and `targetCount` (0–100) to `/offline-queue`. Optionally send the catalog filters as JSON properties. Send `width` and `height` as numbers and `difficulty`, `solvability`, and filter expressions as strings. Send up to 100 `cachedUnstartedPuzzleIds` and up to 1000 `excludedPuzzleIds`, both from the requested game. Exclusions let the client skip locally played boards that have yet to sync. Matching unstarted caches count toward the target. Server-side progress and exclusions take precedence. Queue results include the stored Hashi solution and any available Nonogram solution. `remaining` is the unmet portion of the target. Without rating filters, the Hashi queue prefers the configured generation tiers and solvability classes before other verified stock.

Send `schemaVersion: 1` and a `boards` array to `/progress`. Each board supplies a `puzzleId`, `marks`, and an explicit `completed` boolean. Nonogram uses a row-major marks string with `.` for blank, `#` for filled, and `x` for crossed. Every line clue must be satisfied when declaring a board complete. For an older save whose definition is absent from the cache, the server can request it from WebPBN. Hashi uses an array with one entry per decided edge. `edge` is the zero-based definition edge index. Counts 1 and 2 use `noBridge: false`. A count of 0 with `noBridge: true` explicitly rules the edge out. Omit undecided edges. The server validates every board in the batch and rejects invalid or incompatible uploads with `400`. An empty batch returns a count of zero and a null timestamp. The request body limit is 16 MiB.

```json
{
  "schemaVersion": 1,
  "boards": [{ "puzzleId": "nonogram-webpbn-12", "marks": "#x....", "completed": false }]
}
```

Fill-a-Pix queue results include the source solution when available. Fill-a-Pix progress uses the same row-major marks as Nonogram. A completed board must satisfy every numbered cell’s count of filled cells in its surrounding 3×3 neighborhood, including the numbered cell itself.

Slitherlink queue results include the verified solution. The marks string uses `.` for an undecided edge, `#` for a drawn edge, and `x` for an excluded edge in definition order. A completed board must satisfy each clue and contain one nonempty closed loop. Source and tiling filters apply to cached IDs as well as newly selected puzzles.

Galaxies marks contain `width * (height - 1) + height * (width - 1)` characters. Horizontal interior edges come first in row-major order, followed by vertical interior edges in row-major order. `#` is a wall, `x` is an explicitly open edge, and `.` is unmarked. Open and unmarked edges both connect cells. A completed board tiles the grid with connected rotationally symmetric regions. Each region contains all cells incident to its one center and has no internal wall. Queue and restore include the verified solution, which uses only `#` and `x`.

Each restored progress object carries `puzzleId`, `schemaVersion`, `marks`, `completed`, and `backedUpAt`. Restore uses one consistent database snapshot. Restore before the first upload, and keep local edits pending until the server acknowledges the batch. Top up after sending changes so the server can exclude played boards.

The KOReader plugin checks v2 capabilities before catalog browsing or sync. It restores games supported by the installed plugin from the combined response. Retriable upstream fetch failures return `502` or `504`.

Nurikabe uses a row-major marks and solution string with `.` for undecided, `#` for sea, and `x` for white. Clues always have the mark `x`. Complete boards have one connected sea, no 2×2 pool, and correctly sized islands with exactly one clue each. Queue and restore responses include the verified solution. Hashi metadata identifies `bridges` as generated, and Slitherlink metadata identifies `loopy` as generated.


### Tents and Battleships marks

`rowClues` and `columnClues` contain one-element arrays with the nonnegative occupied-cell total for each line. `givens` has one character per cell in row-major order. A dot is editable, `x` is fixed grass or water, and `#` is fixed occupied. Tents additionally uses `t` for an immutable tree. Battleships uses `o` for a single-cell ship, `n`, `e`, `s`, and `w` for ship ends facing those directions, and `m` for an interior segment. `fleet[0]` counts one-cell ships, `fleet[1]` counts two-cell ships, and so on.

Progress uses `.` for undecided, `x` for marked empty, and `#` for occupied. Trees always have progress mark `.`. Fixed empty cells require `x`, and fixed occupied cells or ship shapes require `#`. Completion accepts undecided empty cells after the occupied layout satisfies all rules. Solutions use `#` and `x` on playable cells and `.` on trees. Queue and restore include the verified solution. The offline **Check** uses the current marks and rules.

Metadata includes the canonical `contentHash`, `unique: true`, `difficulty`, `solvability`, and source attribution with source ID, URL, and license where available. Generated boards also carry the seed and generator version. The hash includes the game, dimensions, given grid, totals, and fleet, with trailing zero fleet counts removed.


### Link-a-Pix paths

`clues` contains `{cell,length}` objects in row-major order. `cell` is zero based. `length` counts every cell of the path, including both endpoints. A clue of length one paints its own cell. The `metadata.attributions` array lists original source, source ID, URL, author, title, license and original rating if available. Canonical hashes cover dimensions, clue positions, lengths and color class equivalence.

With `includeSolution=true`, `solution` contains zero-based ordered cell paths, oriented from the lower endpoint index to the higher. `picture` is the derived row-major black `#` and white `.` image. The offline queue and restore omit these answer fields. The client derives the evolving picture from the player's moves.

Progress is a row-major string. `.` is undecided, `x` is optional marked white, and a lowercase hexadecimal digit encodes reciprocal path directions using north=1, east=2, south=4, west=8. A partial path starts at a clue and ends in an unnumbered cell, before reaching the full length. Completed paths end at a matching clue at the exact required length. Singleton clues have no encoded path. Completion requires every pair to be connected. Marking all the white cells is optional.
