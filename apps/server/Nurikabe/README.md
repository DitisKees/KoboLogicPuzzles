# Nurikabe on the server

The server stores square generated boards at 25, 30, 35, 40, 45, and 50 cells per side. Collection boards can be rectangular with dimensions up to 50×50. SQLite preserves the original orientation and first accepted provenance for each board. Rotations and reflections of the same clue grid share a SHA-256 ID, including across collection and generated sources.

## Generation and proof

The managed generator grows a connected sea or builds a sea around rows of islands. It varies island sizes and clue positions with a seeded PRNG. It is part of this repository under its MIT license and requires only the existing .NET runtime. Generator version `1` and the seed are stored. The regular layout is often visible in banded candidates and may make them easier for a human than their rating suggests.

The verifier applies island, pool and connectivity deductions, then tries a bounded number of single-cell hypotheses. An exhaustive two-way search counts solutions up to two when deductions leave undecided cells. Sea reachability and cut vertices constrain the search, and the final flood-fill validates every island and the whole sea. The server stores only a unique solution confirmed by these rules. It also checks any supplied answer against the proof. Candidates that run out of time are discarded.

Difficulty and solvability are separate fields. Basic rules yield **Easy**. Clue reachability and sea bottlenecks yield **Medium** or **Hard**, split by whether at least 12 cells were undecided after basic deductions. Up to four successful single-hypothesis deductions yield **Hard**, and further deductions or exhaustive search yield **Expert**. These thresholds estimate solver effort. They have no human or publisher calibration.

The MIT-licensed [sdvx Java generator](https://github.com/3892myamya/sdvx/blob/master/src/main/java/myamya/other/solver/nurikabe/NurikabeSolver.java), [tbersez C generator](https://github.com/tbersez/nurikabe), and [sen-ltd TypeScript engine](https://github.com/sen-ltd/nurikabe) were source candidates. The managed implementation reuses the server runtime and provides its own exact verification. It includes no upstream generator code or runtime dependency.

### Benchmark

.NET 10 Release on Linux x64, Intel Core i5-4200M. Twenty seeds (0–19) per size with a two-second deadline per proof, sequentially in one process. The time includes generation and proof for the whole batch. Peak working set reached 53 MiB.

| Size | Batch time | Unique | Hard or Expert without exhaustive search | Multiple | Timed out |
| --- | ---: | ---: | ---: | ---: | ---: |
| 25×25 | 1.09 s | 14 | 11 | 6 | 0 |
| 30×30 | 2.52 s | 16 | 14 | 3 | 1 |
| 35×35 | 4.18 s | 13 | 13 | 5 | 2 |
| 40×40 | 4.29 s | 14 | 12 | 4 | 2 |
| 45×45 | 4.53 s | 12 | 12 | 6 | 2 |
| 50×50 | 4.75 s | 13 | 9 | 5 | 2 |

This small seed sample covers the lower-density banded candidates and the sea-growth path. Yield varies with seed and machine. Size 50 with seed 2 is a reproducible Hard board with bounded lookahead.

## Inventory and commands

The Docker image enables `NurikabeGeneration__Enabled`. The managed defaults target five unstarted boards per size and difficulty combination for Hard and Expert. The worker tries up to five candidates per size per pass and pauses 60 seconds between passes. API requests use the stored inventory.

Set `NurikabeGeneration__Sizes__0`, `NurikabeGeneration__Difficulties__0`, and additional indexed values to configure the buckets. The other options are `TargetPerDifficulty`, `AttemptsPerSize`, `PollIntervalSeconds`, `ProofTimeoutSeconds` (default 2, maximum 30), and `IncludeRequiresSearch` (default false for automatic replenishment). Under Docker Compose use the same `NurikabeGeneration__` prefix for each option.

```sh
dotnet run --project apps/server -- nurikabe-generate --size 50 --seed 2
dotnet run --project apps/server -- nurikabe-generate --size 50 --seed 2 --diagnostic true
dotnet run --project apps/server -- nurikabe-status
dotnet run --project apps/server -- nurikabe-refresh
```

The dashboard and `/api/v2/nurikabe/status` show inventory, verification activity, and Janko refresh progress. The queue prioritizes Hard and Expert boards with logic or bounded lookahead at 25×25 and up, then includes other verified matches to fill the requested target.

## Collections and attribution

- **Janko** uses a curated list of 18 entries from its Palmer Mebane author index. The worker reads the structured puzzle and solution, and requires each page to include Palmer’s explicit redistribution permission with credit. It preserves the author, page ID and page link. Set `NurikabeCollections__RefreshEnabled=false` to disable its weekly refresh. It inherits `CatalogRefresh__Enabled` by default. Each attempted page advances a persisted cursor. A live default-budget verification imported 10 puzzles, including 18×10 and 20×20 boards. Eight proof attempts timed out, including No. 690 at 50×50. No. 690 also exceeded a separate 27-second proof run. [Author index](https://www.janko.at/Raetsel/Autoren/Palmer-Mebane.htm) · [No. 690 and permission](https://www.janko.at/Raetsel/Nurikabe/0690.a.htm)
- **puzz.link** authored puzzles can be imported from serialized Nurikabe URLs with per-entry authorization and authorship in the manifest. Public database availability provides discovery and stable links. [Database](https://puzz.link/db/) · [Nurikabe URL format implementation](https://github.com/robx/pzprjs/blob/master/src/variety/nurikabe.js)
- **PuzzleKit** can be read from an authorized case list and a downloaded Nurikabe shard. Three inspected shards held 1,091 entries with solution fields, all attributed to Janko, including one 50×50 and one 32×32. The importer requires the approval’s case ID and original URL to match the shard. The canonical board ID deduplicates direct Janko and PuzzleKit imports. Retain the original author and permission when preparing the approval list. [Dataset](https://github.com/SmilingWayne/puzzlekit-dataset/tree/main/assets/data/Nurikabe)
- **Puzzle-Nurikabe** offers regular browser boards through 20×20, plus daily, weekly and monthly specials. Regular boards show numeric site IDs. On 27 November 2026 the daily, weekly, and monthly boards were 15×15, 15×20, and 20×20 respectively and used dates as their identifiers. The public site exposes an interactive generator. The reviewed pages provided no redistribution grant or documented collection API. Use the structured manifest only for entries you have permission to cache and redistribute. [Puzzle-Nurikabe](https://www.puzzle-nurikabe.com/)

Use `nurikabe-import --file authorized.json` for a JSON array of entries. Set `source` to a lowercase slug, then supply `sourceName`, `sourceId`, `sourceUrl`, `author`, and `license` or a description of the permission in that field. Use `puzzleUrl` for a serialized puzz.link board or `width`, `height`, and a row-major integer `clues` array with zero for blank cells. `title`, `originalRating`, and a row-major `solution` using `#` for sea and `x` for white are optional. The solver can provide a missing solution.

To import a PuzzleKit shard, add `--puzzlekit Nurikabe_dataset_000.json`. In the authorization manifest, omit the board fields and set `sourceId` to the exact case key and `sourceUrl` to the original puzzle URL used by the shard. Include the original author and permission for each case.
