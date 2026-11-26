# Puzzle and progress API

## Version 2

Kobo Logic Puzzles uses `/api/v2` for both games. Nonogram definitions and saves use their WebPBN IDs in SQLite. The API exposes those IDs with the `nonogram-webpbn-` prefix.

| Method and path | Purpose |
| --- | --- |
| `GET /api/v2/capabilities` | Return `apiVersion`, `schemaVersion`, the supported `games`, and `advancedFilters: true` when ranges and multiple values are available. Hashi is advertised while generation is enabled or when stored Hashi puzzles are available. |
| `GET /api/v2/puzzles` | Browse one game. Supply `game=nonogram` or `game=hashi` and optional filters and `limit` (1–100, default 50). Returns `{ puzzles, nextBeforeId }`. Repeat the filters and pass `nextBeforeId` as `beforeId` for the next page. |
| `GET /api/v2/puzzles/{id}` | Retrieve a puzzle definition. Set `includeSolution=true` to request its solution. |
| `GET /api/v2/puzzles/metadata` | Look up ratings for up to 100 typed IDs supplied as repeated `id` parameters. Returns an object keyed by puzzle ID. |
| `POST /api/v2/offline-queue` | Top up one game’s unstarted cache. Returns `{ puzzles, remaining }`. |
| `POST /api/v2/progress` | Back up up to 100 changed boards in one transaction. Returns `{ savedCount, backedUpAt }` after commit. |
| `GET /api/v2/restore` | Return `{ puzzle, progress }` records for both games, or specify `game` for one game. |
| `GET /api/v2/hashi/status` | Show Hashi stock and unstarted counts by size and rating, configured targets, and generation outcomes for the running server. |

Catalog order is descending WebPBN ID for Nonogram and descending content hash for Hashi. Summaries carry `id`, `game`, dimensions, available metadata, and a `status` of `unstarted`, `started`, or `solved`. Nonogram filters use difficulty on a 1–5 scale in quarter-star increments and numeric WebPBN solvability levels 1–6. Metadata may also include quality on the same star scale. Hashi filters use `easy`, `medium`, `hard`, or `expert` and `basic_logic`, `advanced_logic`, `bounded_lookahead`, `requires_search`, or `unknown`. Malformed filters and cursors from the other game return `400`.

### Catalog and queue filters

`width`, `height`, `difficulty`, and `solvability` accept exact values. With `advancedFilters` enabled, their alternatives `widthFilter`, `heightFilter`, `difficultyFilter`, and `solvabilityFilter` accept comma-separated lists. Use a hyphen for inclusive ranges in width, height, or Nonogram difficulty. Dimensions use integers from 1 to 100. Nonogram difficulty uses quarter-star increments. For example, `widthFilter=20-35,50&difficultyFilter=3.5-4.25` matches widths from 20 through 35 or exactly 50, together with difficulty from 3.5 through 4.25.

Hashi also accepts `sizeFilter` for square sizes from 20 to 50 in steps of 5. For example, `sizeFilter=20-30&difficultyFilter=easy,medium&solvability=basic_logic` matches 20×20, 25×25, and 30×30 boards that are Easy or Medium and use Basic logic. `sizeFilter` takes the place of both width and height filters. Supply one form per field and omit fields that accept any value. A puzzle with missing rating metadata is excluded when the corresponding rating is filtered.

### Definitions

`GET /api/v2/puzzles/{id}` returns `game`, `schemaVersion` (currently 1), `id`, `width`, `height`, and game-specific fields. IDs are canonical and case-sensitive. A malformed ID returns `400`. An unavailable puzzle returns `404`. Pass `includeSolution=true` to request a solution.

| Game | ID format | Definition fields |
| --- | --- | --- |
| `nonogram` | `nonogram-webpbn-12` for WebPBN ID 12 | `title`, `author`, `rowClues`, `columnClues`, and optional WebPBN `metadata`. An empty clue line has an empty array. The requested `solution`, when available, is a row-major string using `#` for filled and `.` for empty cells. |
| `hashi` | `hashi-local-` followed by the 64-character lowercase SHA-256 of the canonical clue layout | `islands` contains `{ x, y, clue }` objects. `edges` contains `{ first, second }` pairs of island indices. The requested `solution` is an array of counts aligned with `edges`. |

Hashi coordinates and array indices start at zero. Island indices follow `(x, y)` order and edges follow `(first, second)` order. An edge joins the nearest islands in one row or column. Hashi `metadata` carries `islandCount`, `actualDensity` as a percentage, `solvability`, and `difficulty` and `difficultyScore` when rated.

### Offline queue and backup

Send `game` and `targetCount` (0–100) to `/offline-queue`. Optionally send the catalog filters as JSON properties. Send `width` and `height` as numbers and `difficulty`, `solvability`, and filter expressions as strings. Send up to 100 `cachedUnstartedPuzzleIds` and up to 1000 `excludedPuzzleIds`, both from the requested game. Exclusions let the client skip locally played boards that have yet to sync. Matching unstarted caches count toward the target. Server-side progress and exclusions take precedence. Queue results include the stored Hashi solution and any available Nonogram solution. `remaining` is the unmet portion of the target. Without rating filters, the Hashi queue prefers the configured generation tiers and solvability classes before other verified stock.

Send `schemaVersion: 1` and a `boards` array to `/progress`. Each board supplies a `puzzleId`, `marks`, and an explicit `completed` boolean. Nonogram uses a row-major marks string with `.` for blank, `#` for filled, and `x` for crossed. Every line clue must be satisfied when declaring a board complete. For an older save whose definition is absent from the cache, the server can request it from WebPBN. Hashi uses an array with one entry per decided edge. `edge` is the zero-based definition edge index. Counts 1 and 2 use `noBridge: false`. A count of 0 with `noBridge: true` explicitly rules the edge out. Omit undecided edges. The server validates every board in the batch and rejects invalid or incompatible uploads with `400`. An empty batch returns a count of zero and a null timestamp. The request body limit is 8 MiB.

```json
{
  "schemaVersion": 1,
  "boards": [{ "puzzleId": "nonogram-webpbn-12", "marks": "#x....", "completed": false }]
}
```

Each restored progress object carries `puzzleId`, `schemaVersion`, `marks`, `completed`, and `backedUpAt`. Restore uses one consistent database snapshot across both games. Restore before the first upload, and keep local edits pending until the server acknowledges the batch. Top up after sending changes so the server can exclude played boards.

The KOReader plugin checks v2 capabilities before catalog browsing or sync and uses the advertised games. Retriable WebPBN fetch failures return `502` or `504`.
