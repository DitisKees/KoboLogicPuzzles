# Kobo Logic Puzzles for KOReader

## Install and open

1. Install KOReader on the Kobo Aura H2O using its installation guide. [KOReader installation on Kobo](https://github.com/koreader/koreader/wiki/Installation-on-Kobo-devices)
2. Copy the entire [kobologicpuzzles.koplugin](kobologicpuzzles.koplugin/) directory into the Kobo’s `.adds/koreader/plugins/` directory. Restart KOReader.
3. Open **Tools → Kobo Logic Puzzles → Settings → Home server URL**. Enter the server’s HTTP address on your home network, for example `http://192.168.1.10:8080`.
4. Choose **Sync now**. On a fresh installation, the app restores any backed-up boards and then downloads puzzles. You can also choose an individual puzzle under **Browse server catalog**.

The multi-game build requires a home server with API v2 for catalog browsing and sync. In the library, choose **Game → Check for Hashi** to discover Hashi on the server. Once Hashi puzzles are cached, you can switch to their library offline.

Download `kobologicpuzzles.koplugin.zip` from the release and extract its `kobologicpuzzles.koplugin` folder. The workflow also publishes test ZIPs as build artifacts. [Plugin releases](https://github.com/DitisKees/KoboLogicPuzzles/releases) · [Build workflow](https://github.com/DitisKees/KoboLogicPuzzles/actions/workflows/kobo-plugin.yml)

See the [home server setup](../../README.md#running-the-home-server) for running the .NET server and the [API contract](../../contracts/README.md) for how the client exchanges puzzle and progress data.

## Upgrade from Kobo Nonogram

1. Sync pending edits from the working plugin while its server is still available. Exit KOReader and connect the Kobo over USB.
2. Back up `.adds/koreader/kobononogram/` and `.adds/koreader/plugins/kobononogram.koplugin/` to a private location. The data folder may contain an updater token.
3. Move `kobononogram.koplugin` out of `plugins/` and copy the new `kobologicpuzzles.koplugin` folder from the 1.0.0 ZIP into `plugins/`. Leave `kobononogram/` beside the plugins directory so the new plugin can read it. Eject the device and restart KOReader.
4. Open **Tools → Kobo Logic Puzzles**. The first open copies settings, cached puzzles, marks, checkpoints, viewports, pending sync, backup timestamps, and the updater token to `kobologicpuzzles/`. If that folder already exists, the plugin opens its existing library. Verify an existing Nonogram offline before syncing with the renamed server.
5. Run **Sync now** after the new server is ready. Verify both game libraries and open **Settings → Update plugin** to check release access.

To roll back, exit KOReader and archive `kobologicpuzzles/` to keep any edits made since the upgrade. Move the renamed plugin out of `plugins/` and put the saved `kobononogram.koplugin` folder back with its original name. The old plugin opens `kobononogram/`. Restore the old server if that plugin requires its API. For a later retry from the original library, archive the new data folder before first open to make the copy run again. [Server rollback](../server/Hashi/README.md#roll-back-the-server)

## Update the plugin

For a manual update, exit KOReader and replace only `.adds/koreader/plugins/kobologicpuzzles.koplugin/` with the folder from the new ZIP. Restart KOReader. Your server URL, puzzle progress, and checkpoints live in the separate `.adds/koreader/kobologicpuzzles/` directory on a standard Kobo installation. You can copy that data directory to your computer for a full local backup, including checkpoints.

Open **Kobo Logic Puzzles → Settings → Update plugin** to check for a newer release. After you confirm the install, KOReader offers to restart. The installer keeps the former plugin beside the new one as `kobologicpuzzles.koplugin.previous`. With KOReader closed, you can restore it by moving the new folder aside and renaming the backup to `kobologicpuzzles.koplugin`.

For a public repository, the updater can use published releases anonymously. If a private repository denies release access, use a **fine-grained personal access token** limited to `DitisKees/KoboLogicPuzzles` with **Contents: Read-only**. Save it in `kobologicpuzzles/github-update-token.txt` under KOReader’s data directory. The plugin uses it only to call GitHub’s API. [GitHub release asset permissions](https://docs.github.com/en/rest/releases/assets#get-a-release-asset)

### Publish an update

Bump `version` in `kobologicpuzzles.koplugin/_meta.lua` and push or merge the change to `main`. After the smoke tests pass, the workflow creates the `kobo-v<VERSION>` tag on the tested commit and publishes the ZIP to its GitHub Release. Changes that keep an already published version still produce a downloadable workflow artifact. [Build KOReader plugin workflow](../../.github/workflows/kobo-plugin.yml)

The updater verifies GitHub’s TLS certificate using the bundled Mozilla CA store from curl, dated 2026-11-05. The bundle is licensed under MPL 2.0, included beside it. [CA bundle source](https://curl.se/docs/caextract.html)

## Play Nonogram

- Choose **Fill**, **Cross**, **Clear**, or **Cycle**. Tap a cell or drag along a row or column to apply the mark. In **Cycle** mode, taps fill, cross, then clear the cell. A drag applies the starting cell’s next mark along the line. In **Fill** or **Cross** mode, starting on a cell with the selected mark erases the run. Hold the drag at the edge of the grid to keep marking as it scrolls. **Undo** and **Redo** apply to the whole drag.
- Drag in **Pan** mode or start a drag in a clue gutter to move the visible grid. On devices with multitouch, a two-finger drag pans in any mode. **Overview** shows the full board. Tap it to jump to an area. Use **−** and **+** to change cell size.
- Completed clues are struck through in the grid and the clues popup. Tap a clue gutter or hold a cell to open the full list. Set the visible clue limit in **Settings → Clues to show**. Each gutter fits as many clues as its space allows, with an ellipsis when more are available. Completing a row or column crosses its remaining blank cells. **Undo** removes those automatic crosses as a separate step. A black bar beside a row or column marks a conflict between its clues and the current marks.
- Before trying a guess, choose **Checkpoint → Save checkpoint**. Each puzzle has one local checkpoint. Choose **Restore checkpoint** to return to those marks. If you restore by mistake, **Undo** brings back the marks you had before restoring.
- Tap **Check** to compare filled and crossed cells with the known solution, when available. It reports how many marks disagree without revealing their locations. For a puzzle downloaded before solution support was added, the first check needs the updated home server and network access to look for the solution. Subsequent checks work offline once the solution is cached.
- Marks and the viewport save locally as you play. The library shows the percentage of cells filled or crossed and a miniature of your marks for started and solved puzzles. It lists in-progress puzzles first. The game marks a board solved when the filled cells satisfy both sets of clues.

## Play Hashi

- Select **Bridge**, tap a numbered island, then tap its nearest neighbor in the same row or column. Repeat taps on that neighbor to cycle through one bridge, two bridges, and zero bridges. Tap the selected island or an empty cell to deselect it. **No bridge** toggles a crossed-out candidate, and **Clear** removes either kind of mark.
- A thick ring means the island has the right number of bridges. A white number on a black island means it has too many. Bridges cannot cross. To solve the puzzle, match every number and connect all islands in one network. The optional dotted guides show candidates around the selected island. Toggle them in **Hashi settings → Candidate bridges**.
- Drag to pan. Use **−** and **+** to change scale, or open **Overview** and tap an area to jump there. The game saves your view and marks as you play. **Undo**, **Redo**, **Checkpoint**, and **Restart** are available offline.
- **Check** counts decisions that disagree with the stored solution. It leaves the board intact and gives no locations. The library shows the fraction of candidate edges you have marked and a thumbnail of the current board.

## Backup and offline puzzles

**Sync now** backs up changed boards and tops up both available games. Set each game's target under **Settings → Offline queue size**. **Catalog and queue filters** set each game's size, difficulty, and solvability preferences. Sync reports open queue slots when the server has fewer matching puzzles available. Downloaded boards remain playable offline. The library shows the last successful progress backup. Run a sync before a factory reset to back up recent edits.

On a clean KOReader installation, install the plugin, enter the same server URL, and choose **Sync now** to restore your boards. Local settings and board files live in the `kobologicpuzzles/` folder in KOReader’s data directory.

## Rehearse the update on Kobo

Use the tested plugin ZIP and a test server with generated fixtures. Test migration of the existing server database separately on a private copy. To seed the three test sizes, pause the generator worker on the test server and run `hashi-generate` with the following values and the default expansion and upstream difficulty. [Generator CLI and server rollback](../server/Hashi/README.md)

| Grid | Seed | Density | Library title |
| --- | --- | ---: | --- |
| 20×20 | `00000065` | 10 | Hashi 6590e7 |
| 35×35 | `00000066` | 20 | Hashi 331046 |
| 50×50 | `00000067` | 10 | Hashi 9db7d1 |

1. On the current plugin, sync pending moves. Record the last backup time and open a large saved Nonogram to note its marks, checkpoint, and position.
2. Back up the active plugin and its data directory, then install the candidate as described in the upgrade instructions.
3. Eject the Kobo, open KOReader, and check that **Tools** has one **Kobo Logic Puzzles** entry. Before changing the server URL, reopen the large Nonogram with Wi-Fi off. Verify its marks, viewport, clues, and checkpoint.
4. Exit KOReader and set the original data folder aside. Restart with empty plugin data, enable Wi-Fi, and enter the test server URL. Choose **Game → Check for Hashi**, then download **20×20**, **35×35**, and **50×50** boards. Use **Browse server catalog** and the size filter if the offline queue fills with other sizes.
5. Disable Wi-Fi. On each size, place a single and double bridge, try a crossing, mark no bridge, undo and redo, and test zoom, panning, and overview jumps. Check that a drag never places a bridge. Note tap accuracy and whether ordinary moves avoid a full-screen flash. Reopen the game after a KOReader restart to verify marks and viewport.
6. Finish a test puzzle offline and use **Check**. Back up that board and an unfinished one to the test server. With KOReader closed, archive the test data and reinstall the candidate from the ZIP. Start with empty plugin data, connect to the test server, and sync. Verify the restored boards offline.
7. To return to the live setup, exit KOReader. Archive the test plugin and test data separately, then put both original folders back in their original locations. Eject the Kobo and verify the original server URL, the single Tools entry, and the large Nonogram.

Record the Kobo model, KOReader version, tested server and plugin revisions, and the results for each size. Record any missed taps, slow redraws, or differences between the saved and restored boards.

On 2026-11-24, the candidate and the rolled-back v0.2.3 plugin both reopened a 99×70 Nonogram saved with v0.2.3 on KOReader Linux v2026.07.1. The candidate opened the three seeded Hashi sizes, completed the last bridge of the 50×50 fixture offline, and restored the completed board alongside the four existing Nonogram saves into clean local data.

On the Kobo Aura H2O (N250) running KOReader v2026.07.2, the candidate reopened an existing Nonogram offline and the player confirmed that generated Hashi boards worked with the updated home server. The Kobo restarted during the clean-profile launch, before the restore could run. The working plugin and data, including the pending local Hashi edit, were put back and verified byte for byte. A read-only desktop check with the same home-server settings restored and opened the four Nonogram saves and one Hashi save.

On 2026-11-25, the renamed 1.0.0 ZIP copied a backup from that Kobo into the new data directory. All 20 cached boards, settings, and the updater token matched byte for byte. The new library opened with the three completed boards, three checkpoints, and one pending edit. Desktop smoke tests also covered retry after a failed copy, preservation of an existing destination, a single Tools entry, and update installation and rollback under the new plugin name.

## Desktop smoke test

The GitHub Actions workflow packages the plugin and runs these checks on pushes to `main` and pull requests targeting `main` that change the plugin, the smoke test, or the workflow. It also supports manual runs.

From an extracted KOReader Linux runtime directory, run the game and sync checks with its bundled LuaJIT. Replace the two absolute paths with paths from your checkout.

```sh
KO_HOME="$(mktemp -d)" SDL_VIDEODRIVER=dummy SDL_RENDER_DRIVER=software KO_MULTIUSER=1 \
  ./luajit /path/to/KoboLogicPuzzles/tests/kobo/smoke.lua \
  /path/to/KoboLogicPuzzles/apps/kobo/kobologicpuzzles.koplugin
```
