# Kobo Logic Puzzles

Kobo Logic Puzzles plays Nonogram and Hashi offline in KOReader on the Kobo Aura H2O (N250). A home server provides the WebPBN Nonogram catalog, generates Hashi puzzles locally, and backs up progress for both games.

## Repository layout

| Path | Purpose |
| --- | --- |
| [`apps/kobo/`](apps/kobo/README.md) | KOReader plugin, with [installation and play instructions](apps/kobo/README.md). |
| [`apps/server/`](apps/server/) | ASP.NET Core app targeting .NET 10. It handles WebPBN imports, Hashi generation, the catalog, and progress in SQLite. |
| [`apps/server/Hashi/`](apps/server/Hashi/README.md) | Hashi generator, C# validation and rating, and feasibility results. |
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

Start the server to run the API and the catalog refresh together. The server defaults to `data/kobononogram.db`, relative to its working directory. It keeps the existing SQLite filename so upgrades can use the same database.

```sh
dotnet run --project apps/server -- --urls http://localhost:8080
```

For a home LAN deployment, build the image and keep `/data` on a persistent volume.

```sh
docker build -t kobo-logic-puzzles apps/server
docker run -d --name kobo-logic-puzzles --restart unless-stopped \
  -p 8080:8080 -v kobo-nonogram-data:/data kobo-logic-puzzles
```

On pushes to `main` that change the server, its tests, or its workflow, GitHub Actions verifies and publishes a `linux/amd64` image at `ghcr.io/ditiskees/kobologicpuzzles`. Use `:latest` for the current build or `:sha-<full-commit>` for a specific revision. [Server image workflow](.github/workflows/server-image.yml)

For a fresh Compose deployment, run `docker volume create kobo-nonogram-data` once, then `docker compose -f apps/server/compose.example.yaml up -d`. An upgrade uses the existing volume.

## Upgrade from Kobo Nonogram

The 1.0.0 release renames the repository, container image, and KOReader plugin. Install the server image and replace the old plugin folder manually in the same maintenance window. The server opens the existing volume and database. On first open, the new plugin copies your downloaded boards, progress, settings, and updater token to its new data directory. [Server cutover and rollback](apps/server/Hashi/README.md#database-upgrade-and-rollback) · [Kobo plugin cutover and rollback](apps/kobo/README.md#upgrade-from-kobo-nonogram)

## WebPBN refresh

The server refreshes WebPBN in the background on first startup and every seven days after a successful refresh. It records that time in SQLite so the schedule survives restarts. If the server was down when a refresh came due, it refreshes on startup. A failed refresh keeps the existing catalog and retries after one hour. The API stays available during refreshes.

To refresh immediately, run the manual command against the server's database.

```sh
docker exec kobo-logic-puzzles dotnet KoboLogicPuzzles.Server.dll refresh
```

The local equivalent is `dotnet run --project apps/server -- refresh`. Set `CatalogRefresh__Enabled=false` to run in manual-only mode. If you installed a host cron entry for the earlier setup, remove it when switching to the built-in schedule.

The API is intended for a trusted home LAN. See the [API and client sync formats](contracts/README.md).

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

## Offline play and synchronization

The Kobo keeps a configurable number of unstarted puzzles downloaded and retains every in-progress board independently of that limit. The game saves moves locally, including filled cells, crosses, and blanks. It can open and resume downloaded puzzles without a network connection.

**Sync now** uploads changed progress to the home server and tops up the offline queue. The Kobo displays the time of the last successful progress backup and keeps local progress available if the server is unreachable. The weekly catalog refresh runs on the home server independently of Kobo sync.

## Backup and recovery

Keep puzzle definitions and progress in persistent storage on the home server so they survive replacement of the server container. A fresh Kobo installation restores progress from the server before its first upload. Recovery returns the player to the most recent successful backup.

Verify recovery by saving a partially solved puzzle with fills and crosses, syncing it, then restoring into clean app data. Reopen the restored board and continue playing offline.

The server upgrade uses the existing volume and database path. [Database upgrade and rollback](apps/server/Hashi/README.md#database-upgrade-and-rollback)

## Future Android and web play

An Android app or web player can use the same puzzle IDs, catalog, and versioned save format. The Kobo is the only device that edits progress in the first release. When a second client can edit, add per-puzzle revision checks and preserve both versions if the same board was edited on two devices while offline.

## License

The original Kobo Logic Puzzles code and documentation are available under the MIT License. [MIT License](LICENSE)

The bundled Mozilla CA certificates are licensed under MPL 2.0. Downloaded WebPBN puzzles and solution images remain subject to their authors’ rights. [CA bundle license](apps/kobo/kobologicpuzzles.koplugin/LICENSE-MPL-2.0.txt) · [WebPBN export terms](https://webpbn.com/export.cgi)
