# Kobo Logic Puzzles

Kobo Logic Puzzles plays Nonogram, Hashi, Fill-a-Pix, Slitherlink, Nurikabe, Picture Galaxies, Tents and Trees, Battleships, and picture Link-a-Pix offline in KOReader on the Kobo Aura H2O (N250). The home server provides the WebPBN Nonogram catalog, generates Hashi, Slitherlink, Nurikabe, Tents and Trees, and Battleships boards locally, and imports playable collections.

## Repository layout

| Path | Purpose |
| --- | --- |
| [`apps/kobo/`](apps/kobo/README.md) | KOReader plugin, with [installation and play instructions](apps/kobo/README.md). |
| [`apps/server/`](apps/server/) | ASP.NET Core app targeting .NET 10. It handles puzzle imports, generation, the catalog, and progress in SQLite. |
| [`apps/server/Hashi/`](apps/server/Hashi/README.md) | Hashi generator, C# validation and rating, and feasibility results. |
| [`apps/server/Collections/`](apps/server/Collections/README.md) | Extra Nonogram and Hashi sources, import settings, and attribution. |
| [`apps/server/Slitherlink/`](apps/server/Slitherlink/README.md) | Loopy generation, structured collections, and inventory settings. |
| [`apps/server/Nurikabe/`](apps/server/Nurikabe/README.md) | Nurikabe generation, uniqueness proof, collection imports, and benchmarks. |
| [`apps/server/Galaxies/`](apps/server/Galaxies/README.md) | Galaxies picture collections, automatic imports, and verification. |
| [`apps/server/Placement/`](apps/server/Placement/README.md) | Tents and Trees and Battleships generation, Janko collections, and benchmarks. |
| [`apps/server/LinkAPix/`](apps/server/LinkAPix/README.md) | Picture Link-a-Pix collection imports and verification. |
| [`contracts/`](contracts/) | Shared puzzle and progress formats for the server and its clients. |
| [`KoboLogicPuzzles.slnx`](KoboLogicPuzzles.slnx) | Solution for the .NET projects. |
| [`tests/server/`](tests/server/) | Server unit tests and HTTP integration tests. |

Future clients can live in `apps/web/` and `apps/android/`, with their own build tools and the same shared data contracts. A standalone Kobo client can live beside the KOReader plugin when needed.

Build and test with the .NET 10 SDK.

```sh
dotnet build KoboLogicPuzzles.slnx
dotnet test KoboLogicPuzzles.slnx
```

## Running the home server

Start the server to run the API and the catalog refresh together. The server defaults to `data/kobologicpuzzles.db`, relative to its working directory.

```sh
dotnet run --project apps/server -- --urls http://localhost:8080
```

For a home LAN deployment, build the image and keep `/data` on a persistent volume.

```sh
docker build -t kobo-logic-puzzles apps/server
docker run -d --name kobo-logic-puzzles --restart unless-stopped \
  -p 8080:8080 -v kobo-kobologicpuzzles-data:/data kobo-logic-puzzles
```

On pushes to `main` that change the server, its tests, or its workflow, GitHub Actions verifies and publishes a `linux/amd64` image at `ghcr.io/ditiskees/kobologicpuzzles`. Use `:latest` for the current build or `:sha-<full-commit>` for a specific revision. [Server image workflow](.github/workflows/server-image.yml)

The container stores the database at `/data/kobologicpuzzles.db`. For a Compose deployment, run `docker volume create kobo-kobologicpuzzles-data` once, then `docker compose -f apps/server/compose.example.yaml up -d`.

## WebPBN refresh

The server refreshes WebPBN in the background on first startup and every seven days after a successful refresh. It records that time in SQLite so the schedule survives restarts. If the server was down when a refresh came due, it refreshes on startup. A failed refresh keeps the existing catalog and retries after one hour. The API stays available during refreshes.

To refresh immediately, run the manual command against the server's database.

```sh
docker exec kobo-logic-puzzles dotnet KoboLogicPuzzles.Server.dll refresh
```

The local equivalent is `dotnet run --project apps/server -- refresh`. Set `CatalogRefresh__Enabled=false` to run in manual-only mode.

The API is intended for a trusted home LAN. See the [API and client sync formats](contracts/README.md).

## GrandGames collections

Set `GrandGamesCollections__Enabled: "true"` in the Compose service environment for personal noncommercial offline play. This enables the GrandGames catalogs for Nonogram, Hashi (Bridges), Nurikabe, classic Galaxies, Tents and Trees, Battleships, and Link-a-Pix. Fill-a-Pix (Mosaic) and Slitherlink (Wall of China) already include GrandGames through their catalog refreshes. Those refreshes are enabled by default and can be controlled with `FillAPix__RefreshEnabled` and `SlitherlinkCollections__RefreshEnabled`. [GrandGames catalog](https://en.grandgames.net/brainteasers) · [Compose settings](apps/server/compose.example.yaml)

The seven added feeds verify and cache boards on the server in small batches. They keep their position across restarts and pause when GrandGames asks the server to slow down. For an immediate batch, run `dotnet run --project apps/server -- grandgames-refresh --game link-a-pix`. The same command works with `nonogram`, `hashi`, `nurikabe`, `galaxies`, `tents`, or `battleships`. Omit `--game` to run a batch for each of the seven.

## Server dashboard

Open the home server address in a browser to see the dashboard. It shows the puzzle catalog by source, stock for each local generator against generation targets, and each generator’s current attempt and recent outcomes. Use the inventory tabs and the solvability or tiling filter to inspect an individual group. The dashboard refreshes every 30 seconds while visible.

Generation activity and attempt counts cover the server session. Catalog and stock counts come from the database and survive a restart.

## Puzzle catalog and weekly refresh

- The home server runs a filtered WebPBN search once a week. It pages through all matches, records their IDs and metadata, and deduplicates by WebPBN puzzle ID. Rerunning the search also picks up older puzzles whose ratings now match the filters. [WebPBN puzzle search](https://webpbn.com/find.cgi)
- The initial search uses public filters. The home server tracks play history for the Kobo game. If existing WebPBN play history matters, its puzzle IDs can be used to seed exclusions separately.
- The server draws from the matching catalog and fetches full puzzle definitions from WebPBN’s XML export as needed to fill the offline queue. It caches each definition for reuse. [WebPBN puzzle export](https://webpbn.com/export.cgi)
- The WebPBN puzzle ID is the key across the catalog, Kobo, and progress records. Preserve cached definitions for puzzles with saved progress so they remain playable if WebPBN withdraws them. A failed refresh leaves the last successful catalog available. [WebPBN FAQ](https://webpbn.com/faq.html)

### Starting filters

Use the filters from the original WebPBN search as the starting configuration. Adjust them on the server as needed.

| Filter | Starting value |
| --- | --- |
| Colors | Black and white |
| Board area | 1,225–10,000 cells |
| Board width and height | Up to 80 cells each |
| Quality | 4–5 stars, including unrated puzzles |
| Difficulty | 3–5 stars, including unrated puzzles |
| Solution | Unique |
| Solvability | Any |
| Blotted clues | Exclude |

WebPBN filters by total cells. The server applies the width and height limits when refreshing the catalog and importing new definitions. [WebPBN puzzle search](https://webpbn.com/find.cgi)

`WebPbn__MaxWidth` and `WebPbn__MaxHeight` each accept an integer from 1 to 100 cells. An out-of-range value prevents the server from starting. To restore the defaults, remove those overrides or set both to `80`, then apply the Compose changes with `docker compose up -d`.

Configure the filters through the server configuration or environment variables such as `WebPbn__MinQuality=4.25`. Settings include `MinArea`, `MaxArea`, `MaxWidth`, `MaxHeight`, `MinQuality`, `MaxQuality`, `MinDifficulty`, `MaxDifficulty`, `IncludeUnratedQuality`, `IncludeUnratedDifficulty`, `Solvability`, and `RequestDelayMilliseconds`. Solvability uses WebPBN’s values 0–6, with 0 accepting any rating. Requests are spaced by 250 ms by default.

## Fill-a-Pix catalog

The server combines Janko’s Mosaik, GrandGames, and Fill-a-Pix.net under `game=fill-a-pix`. Each puzzle retains its source ID and link. The catalog accepts every valid board size supplied by the three sources. [Janko](https://www.janko.at/Raetsel/Mosaik/index.htm) · [GrandGames](https://en.grandgames.net/mosaic) · [Fill-a-Pix.net](https://fill-a-pix.net/collection/index.html)

Each source refreshes independently at startup and seven days after its last success. Failed refreshes retry after one hour. GrandGames saves each catalog page as it arrives and resumes from the next page after an interruption. The server caches Janko and Fill-a-Pix.net definitions during refresh and GrandGames definitions when first requested. Cached puzzles with progress remain available if a source withdraws them.

## Slitherlink catalog

The server replenishes a separate queue of unique Loopy puzzles for each configured size, tiling, and difficulty. It imports PuzzleKit, Mindgames, Janko, GrandGames Wall of China, and KrazyDad Variety Slitherlink at startup and checks for updates weekly. You can import other structured collections and JSON packs locally. The Kobo catalog and offline queue can filter by source, tiling, dimensions, or difficulty. [Generation and import instructions](apps/server/Slitherlink/README.md)

Run `dotnet run --project apps/server -- refresh-fill-a-pix` to refresh manually, or pass `refresh-fill-a-pix` to the server DLL in Docker. `FillAPix__RefreshEnabled` controls the background import and defaults to the value of `CatalogRefresh__Enabled`. See the [API contract](contracts/README.md) for definitions, downloads, and progress.

## Offline play and synchronization

The Kobo keeps a configurable number of unstarted puzzles downloaded and retains every in-progress board independently of that limit. The game saves moves locally, including filled cells, crosses, and blanks. It can open and resume downloaded puzzles without a network connection.

**Sync now** uploads changed progress to the home server and tops up the offline queue. The Kobo displays the time of the last successful progress backup and keeps local progress available if the server is unreachable. The weekly catalog refresh runs on the home server independently of Kobo sync.

## Backup and recovery

Keep puzzle definitions and progress in persistent storage on the home server so they survive replacement of the server container. A fresh Kobo installation restores progress from the server before its first upload. Recovery returns the player to the most recent successful backup.

Verify recovery by saving a partially solved puzzle with fills and crosses, syncing it, then restoring into clean app data. Reopen the restored board and continue playing offline.

## Future Android and web play

An Android app or web player can use the same puzzle IDs, catalog, and versioned save format. The Kobo is the only device that edits progress in the first release. When a second client can edit, add per-puzzle revision checks and preserve both versions if the same board was edited on two devices while offline.

## License

The original Kobo Logic Puzzles code and documentation are available under the MIT License. [MIT License](LICENSE)

The bundled Mozilla CA certificates are licensed under MPL 2.0. Downloaded WebPBN puzzles and solution images remain subject to their authors’ rights. [CA bundle license](apps/kobo/kobologicpuzzles.koplugin/LICENSE-MPL-2.0.txt) · [WebPBN export terms](https://webpbn.com/export.cgi)

## Nurikabe catalog

The server generates verified 25×25 through 50×50 boards in steps of five and refreshes an authorized Janko collection. The Kobo catalog and offline queue support filters for source, dimensions, difficulty, and solvability. The Kobo library and dashboard identify the generators as **Generated by Bridges**, **Generated by Loopy**, and **Generated by KoboLogicPuzzles**. [Inventory, import, and verification](apps/server/Nurikabe/README.md)

## Picture Galaxies

From **Region partitioning** in the Kobo plugin, download a Galaxies puzzle and draw walls to enclose regions with 180° rotational symmetry. Closed black-center regions reveal the picture. Bundled sources import automatically, including original picture boards from 30×30 to 80×80. Noncommercial deployments can also enable automatic Artist’s Block and Janko imports. [Collections, attribution and settings](apps/server/Galaxies/README.md)

## Extra Nonogram and Hashi sources

The server bundles 36 distinct Nonograms from The World at Play and Sen Ltd, and six generated Hashi puzzles from Sen Ltd. Home servers can also enable the Janko archive refresh for eligible puzzles. Kobo and the dashboard show the source, and Kobo can filter the catalog and offline queue by source. [Source list and setup](apps/server/Collections/README.md)

## Tents and Trees and Battleships

Open **Object placement** in the Kobo plugin. Both games use row and column totals, with fixed trees or ship clues. The server can generate unique large boards in the background and import Janko and GrandGames collections for personal noncommercial use. [Sizes, settings, collections, and benchmarks](apps/server/Placement/README.md)


## Picture Link-a-Pix

Open **Line drawing → Link-a-Pix** to connect equal numbers and reveal a picture. A personal noncommercial server can opt in to GrandGames, PlaySudoku, GrayCell, and licensed Janko imports. Authorized packs can add other collections. [Source settings, formats, and verification](apps/server/LinkAPix/README.md)
