# Kobo Nonogram for KOReader

## Install and open

1. Install KOReader on the Kobo Aura H2O using its installation guide. [KOReader installation on Kobo](https://github.com/koreader/koreader/wiki/Installation-on-Kobo-devices)
2. Copy the entire [kobononogram.koplugin](kobononogram.koplugin/) directory into the Kobo’s `.adds/koreader/plugins/` directory. Restart KOReader.
3. Open **Tools → Kobo Nonogram → Settings → Home server URL**. Enter the server’s HTTP address on your home network, for example `http://192.168.1.10:8080`.
4. Choose **Sync now**. On a fresh installation, the app restores any backed-up boards and then downloads puzzles. You can also choose an individual puzzle under **Browse server catalog**.

For a tested build, download `kobononogram.koplugin.zip` under **Artifacts** on a successful run. Extract the ZIP and use its `kobononogram.koplugin` folder in step 2. [Build KOReader plugin workflow](https://github.com/DitisKees/KoboNonogram/actions/workflows/kobo-plugin.yml)

See the [home server setup](../../README.md#running-the-home-server) for running the .NET server and the [API contract](../../contracts/README.md) for how the client exchanges puzzle and progress data.

## Update the plugin

For a manual update, exit KOReader and replace only `.adds/koreader/plugins/kobononogram.koplugin/` with the folder from the new ZIP. Restart KOReader. Your server URL, puzzle progress, and checkpoints live in the separate `.adds/koreader/kobononogram/` directory on a standard Kobo installation. You can copy that data directory to your computer for a full local backup, including checkpoints.

Install an update-capable ZIP manually once. Then open **Kobo Nonogram → Settings → Update plugin** to check for a newer GitHub Release. After you confirm the install, KOReader offers to restart. The installer keeps the former plugin beside the new one as `kobononogram.koplugin.previous`. With KOReader closed, you can restore it by moving the new folder aside and renaming the backup to `kobononogram.koplugin`.

For a public repository, the updater can use published releases anonymously. To keep this repository private, create a **fine-grained personal access token** limited to `DitisKees/KoboNonogram` with **Contents: Read-only** and save just the token in `github-update-token.txt` inside Kobo Nonogram’s data directory. The plugin uses it only to call GitHub’s API. [GitHub release asset permissions](https://docs.github.com/en/rest/releases/assets#get-a-release-asset)

### Publish an update

Bump `version` in `kobononogram.koplugin/_meta.lua` and push or merge the change to `main`. After the smoke tests pass, the workflow creates the `kobo-v<VERSION>` tag on the tested commit and publishes the ZIP to its GitHub Release. Changes that keep an already published version still produce a downloadable workflow artifact. [Build KOReader plugin workflow](../../.github/workflows/kobo-plugin.yml)

The updater verifies GitHub’s TLS certificate using the bundled Mozilla CA store from curl, dated 2026-11-05. The bundle is licensed under MPL 2.0, included beside it. [CA bundle source](https://curl.se/docs/caextract.html)

## Play large boards

- Choose **Fill**, **Cross**, or **Clear**. Tap a cell or drag along a row or column to apply the mark. Hold the drag at the edge of the grid to keep marking as it scrolls. Starting on a cell with the selected mark erases the run. **Undo** and **Redo** apply to the whole drag.
- Drag in **Pan** mode or start a drag in a clue gutter to move the visible grid. On devices with multitouch, a two-finger drag pans in any mode. **Overview** shows the full board. Tap it to jump to an area. Use **−** and **+** to change cell size.
- Completed clues are struck through automatically. Completing a row or column crosses its remaining blank cells. **Undo** removes those automatic crosses as a separate step. A black bar beside a row or column marks a conflict between its clues and the current marks. Tap a clue gutter or hold a cell to read the full clues. An ellipsis indicates that more clues are available.
- Before trying a guess, choose **Checkpoint → Save checkpoint**. Each puzzle has one local checkpoint. Choose **Restore checkpoint** to return to those marks. If you restore by mistake, **Undo** brings back the marks you had before restoring.
- Tap **Check** to compare filled and crossed cells with the known solution, when available. It reports how many marks disagree without revealing their locations. For a puzzle downloaded before solution support was added, the first check needs the updated home server and network access to look for the solution. Subsequent checks work offline once the solution is cached.
- Marks and the viewport save locally as you play. The library lists in-progress boards first. The game marks a board solved when the filled cells satisfy both sets of clues.

## Backup and offline puzzles

**Sync now** sends changed boards to the home server and tops up your offline queue. Set the target number of unstarted boards under **Settings → Offline queue size**. You can play previously downloaded boards offline, including partially solved boards. The library shows the last successful progress backup. Run a sync before a factory reset to back up recent edits.

On a clean KOReader installation, install the plugin, enter the same server URL, and choose **Sync now** to restore your boards. Local settings and board files live in the `kobononogram/` folder in KOReader’s data directory.

## Desktop smoke test

The GitHub Actions workflow packages the plugin and runs these checks on pushes to `main` and pull requests targeting `main` that change the plugin, the smoke test, or the workflow. It also supports manual runs.

From an extracted KOReader Linux runtime directory, run the game and sync checks with its bundled LuaJIT. Replace the two absolute paths with paths from your checkout.

```sh
KO_HOME="$(mktemp -d)" SDL_VIDEODRIVER=dummy SDL_RENDER_DRIVER=software KO_MULTIUSER=1 \
  ./luajit /path/to/KoboNonogram/tests/kobo/smoke.lua \
  /path/to/KoboNonogram/apps/kobo/kobononogram.koplugin
```
