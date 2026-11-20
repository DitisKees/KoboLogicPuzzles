# Kobo Nonogram

Kobo Nonogram is a personal black-and-white nonogram game for the Kobo Aura H2O (N250). The first release should make large puzzles comfortable to play offline, add puzzles without a PC, and recover progress after a factory reset. Representative boards for testing are 40×40 and 80×80.

## Repository layout

| Path | Purpose |
| --- | --- |
| [`apps/kobo/`](apps/kobo/) | Lua plugin for KOReader. The `kobononogram.koplugin` directory is the installable unit. |
| [`apps/server/`](apps/server/) | ASP.NET Core app targeting .NET 10. It will handle WebPBN imports, the puzzle catalog, and progress in SQLite. |
| [`contracts/`](contracts/) | Shared puzzle and progress formats for the server and its clients. |
| [`KoboNonogram.slnx`](KoboNonogram.slnx) | Solution for the .NET projects. |

Future clients can live in `apps/web/` and `apps/android/`, with their own build tools and the same shared data contracts. A standalone Kobo client can live beside the KOReader plugin when needed.

Build the server with the .NET 10 SDK.

```sh
dotnet build KoboNonogram.slnx
```

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
| Quality | 4–5 stars, including unrated puzzles |
| Difficulty | 3–5 stars, including unrated puzzles |
| Solution | Unique |
| Solvability | Any |
| Blotted clues | Exclude |

WebPBN filters by total cells. Apply any separate width and height limits when selecting puzzles for the Kobo. [WebPBN puzzle search](https://webpbn.com/find.cgi)

## Offline play and synchronization

The Kobo keeps a configurable number of unstarted puzzles downloaded and retains every in-progress board independently of that limit. The game saves moves locally, including filled cells, crosses, and blanks. It can open and resume downloaded puzzles without a network connection.

**Sync now** uploads changed progress to the home server and tops up the offline queue. The Kobo displays the time of the last successful progress backup and keeps local progress available if the server is unreachable. The weekly catalog refresh runs on the home server independently of Kobo sync.

## Backup and recovery

Keep puzzle definitions and progress in persistent storage on the home server so they survive replacement of the server container. A fresh Kobo installation restores progress from the server before its first upload. Recovery returns the player to the most recent successful backup.

Verify recovery by saving a partially solved puzzle with fills and crosses, syncing it, then restoring into clean app data. Reopen the restored board and continue playing offline.

## Future Android and web play

An Android app or web player can use the same puzzle IDs, catalog, and versioned save format. The Kobo is the only device that edits progress in the first release. When a second client can edit, add per-puzzle revision checks and preserve both versions if the same board was edited on two devices while offline.
