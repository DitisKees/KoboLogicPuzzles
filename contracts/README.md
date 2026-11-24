# Puzzle and progress API

## Version 1

The server and clients exchange JSON over HTTP under `/api/v1`. WebPBN puzzle IDs are positive integers and identify both puzzle definitions and saves. The server keeps cached puzzle definitions for restore.

| Method and path | Purpose |
| --- | --- |
| `GET /api/v1/puzzles` | List catalog summaries with `id`, `title`, `author`, `width`, `height`, and optional `metadata`, in descending ID order. Pass `limit` from 1 to 100 (default 50) and use the last returned ID as `beforeId` for the next page. |
| `GET /api/v1/puzzles/{id}` | Download or read a cached puzzle definition. Pass `includeSolution=true` to look up the solution for an older cached definition that lacks it. Returns `404` if the puzzle is outside the catalog and cache, or its export is unavailable or unsupported. |
| `GET /api/v1/puzzles/metadata` | Look up ratings for up to 100 puzzle IDs. Pass IDs as repeated `id` parameters. Returns an object keyed by puzzle ID for entries with metadata on the server. |
| `POST /api/v1/offline-queue` | Request the puzzle definitions needed to reach `targetCount` (0–100) unstarted puzzles. Supply `cachedUnstartedPuzzleIds` for puzzles already available to play offline. The server skips saved and completed boards. The response contains a `puzzles` array with up to the requested number of additional boards. |
| `POST /api/v1/progress` | Back up an array of up to 100 changed `boards`. The server fetches missing definitions from WebPBN, including for boards outside the current catalog. It validates the whole batch and updates only the supplied puzzle IDs. The response contains `savedCount` and the UTC timestamp `backedUpAt`. An empty batch returns `savedCount` of zero and a null timestamp. |
| `GET /api/v1/restore` | Return an array of `{ puzzle, progress }` records, including completed boards. Each `progress` record includes `puzzleId`, `marks`, `completed`, and `backedUpAt`. |

A puzzle definition includes the row and column clue arrays. An empty line has an empty clue array. The optional `solution` is a row-major string of `#` (filled) and `.` (empty) cells.

When available, `metadata` includes `difficulty` and `quality` on a 1–5 star scale in quarter-star increments. The `solvability` value follows the WebPBN search levels from 1 to 6 (trivial, line and color logic, moderate lookahead, deep lookahead, some guessing, and much guessing). Missing ratings or classifications are omitted.

```json
{
  "id": 12,
  "title": "Bricks & Steps",
  "author": "Tess",
  "width": 3,
  "height": 2,
  "rowClues": [[1, 1], [2]],
  "columnClues": [[1], [1], [2]],
  "solution": "#.#.##",
  "metadata": { "difficulty": 3.75, "quality": 4.25, "solvability": 3 }
}
```

A progress request carries one mark per cell in row-major order. `.` is blank, `#` is filled, and `x` is crossed. A completed board must satisfy every row and column clue.

```json
{
  "boards": [{ "puzzleId": 12, "marks": "#x....", "completed": false }]
}
```

On a clean installation, retrieve `/restore` before sending progress. During regular sync, upload edited boards before asking the server to top up the queue. Record the last backup time when the progress endpoint acknowledges the write. A later queue failure can be retried independently. Requests that fail validation return `400`. Upstream download failures return `502` or `504` and can be retried.

## Version 2

| Method and path | Purpose |
| --- | --- |
| `GET /api/v2/capabilities` | Return `apiVersion`, `schemaVersion`, and the supported `games`. Hashi is advertised while generation is enabled or when stored Hashi puzzles are available. |
| `GET /api/v2/puzzles` | Browse one game. Supply `game=nonogram` or `game=hashi` and optional `width`, `height`, `difficulty`, `solvability`, and `limit` (1–100, default 50). Returns `{ puzzles, nextBeforeId }`. Repeat the filters and pass `nextBeforeId` as `beforeId` for the next page. |
| `GET /api/v2/puzzles/{id}` | Retrieve a puzzle definition. Set `includeSolution=true` to request its solution. |
| `GET /api/v2/puzzles/metadata` | Look up ratings for up to 100 typed IDs supplied as repeated `id` parameters. Returns an object keyed by puzzle ID. |
| `POST /api/v2/offline-queue` | Top up one game’s unstarted cache. Returns `{ puzzles, remaining }`. |
| `POST /api/v2/progress` | Back up up to 100 changed boards in one transaction. Returns `{ savedCount, backedUpAt }` after commit. |
| `GET /api/v2/restore` | Return `{ puzzle, progress }` records for both games, or specify `game` for one game. |
| `GET /api/v2/hashi/status` | Show Hashi stock and unstarted counts by size and rating, configured targets, and generation outcomes for the running server. |

Catalog order is descending WebPBN ID for Nonogram and descending content hash for Hashi. Summaries carry `id`, `game`, dimensions, available metadata, and a `status` of `unstarted`, `started`, or `solved`. Nonogram filters use exact quarter-star difficulty and the same numeric solvability levels as v1. Hashi filters use `easy`, `medium`, `hard`, or `expert` and `basic_logic`, `advanced_logic`, `bounded_lookahead`, `requires_search`, or `unknown`. Malformed filters and cursors from the other game return `400`.

### Definitions

`GET /api/v2/puzzles/{id}` returns `game`, `schemaVersion` (currently 1), `id`, `width`, `height`, and game-specific fields. IDs are canonical and case-sensitive. A malformed ID returns `400`. An unavailable puzzle returns `404`. Pass `includeSolution=true` to request a solution.

| Game | ID format | Definition fields |
| --- | --- | --- |
| `nonogram` | `nonogram-webpbn-12` for WebPBN ID 12 | `title`, `author`, `rowClues`, `columnClues`, and optional WebPBN `metadata`. The requested `solution`, when available, uses the v1 row-major string. |
| `hashi` | `hashi-local-` followed by the 64-character lowercase SHA-256 of the canonical clue layout | `islands` contains `{ x, y, clue }` objects. `edges` contains `{ first, second }` pairs of island indices. The requested `solution` is an array of counts aligned with `edges`. |

Hashi coordinates and array indices start at zero. Island indices follow `(x, y)` order and edges follow `(first, second)` order. An edge joins the nearest islands in one row or column. Hashi `metadata` carries `islandCount`, `actualDensity` as a percentage, `solvability`, and `difficulty` and `difficultyScore` when rated.

### Offline queue and backup

Send `game` and `targetCount` (0–100) to `/offline-queue`. Optionally send `width`, `height`, `difficulty`, and `solvability` using the catalog filter values. In JSON, send `difficulty` and `solvability` as strings. Send up to 100 `cachedUnstartedPuzzleIds` and up to 1000 `excludedPuzzleIds`, both from the requested game. Exclusions let the client skip locally played boards that have yet to sync. Matching unstarted caches count toward the target. Server-side progress and exclusions take precedence. Queue results include the stored Hashi solution and any available Nonogram solution. `remaining` is the unmet portion of the target. Without rating filters, the Hashi queue prefers the configured generation tiers and solvability classes before other verified stock.

Send `schemaVersion: 1` and a `boards` array to `/progress`. Each board supplies a `puzzleId`, `marks`, and an explicit `completed` boolean. Nonogram uses the same marks string as v1. Hashi uses an array with one entry per decided edge. `edge` is the zero-based definition edge index. Counts 1 and 2 use `noBridge: false`. A count of 0 with `noBridge: true` explicitly rules the edge out. Omit undecided edges. The server validates every board in the batch and rejects invalid or incompatible uploads with `400`. An empty batch returns a count of zero and a null timestamp. The request body limit is 8 MiB.

```json
{
  "schemaVersion": 1,
  "boards": [{ "puzzleId": "nonogram-webpbn-12", "marks": "#x....", "completed": false }]
}
```

Each restored progress object carries `puzzleId`, `schemaVersion`, `marks`, `completed`, and `backedUpAt`. Restore uses one consistent database snapshot across both games. Restore before the first upload, and keep local edits pending until the server acknowledges the batch. Top up after sending changes so the server can exclude played boards.

The installed KOReader plugin uses v1 for catalog, queue, and sync.
