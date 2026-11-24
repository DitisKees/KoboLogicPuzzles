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

## Version 2 puzzle definitions

`GET /api/v2/puzzles/{id}` returns `game`, `schemaVersion` (currently 1), `id`, `width`, `height`, and game-specific fields. IDs are canonical and case-sensitive. A malformed ID returns `400`. An unavailable puzzle returns `404`. Pass `includeSolution=true` to request a solution.

| Game | ID format | Definition fields |
| --- | --- | --- |
| `nonogram` | `nonogram-webpbn-12` for WebPBN ID 12 | `title`, `author`, `rowClues`, `columnClues`, and optional WebPBN `metadata`. The requested `solution`, when available, uses the v1 row-major string. |
| `hashi` | `hashi-local-` followed by the 64-character lowercase SHA-256 of the canonical clue layout | `islands` contains `{ x, y, clue }` objects. `edges` contains `{ first, second }` pairs of island indices. The requested `solution` is an array of counts aligned with `edges`. |

Hashi coordinates and array indices start at zero. Island indices follow `(x, y)` order and edges follow `(first, second)` order. An edge joins the nearest islands in one row or column. Hashi `metadata` carries `islandCount`, `actualDensity` as a percentage, `solvability`, and `difficulty` and `difficultyScore` when rated.

The installed KOReader plugin uses v1 for catalog, queue, and sync.
