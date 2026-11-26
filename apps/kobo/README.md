# Kobo Logic Puzzles for KOReader

## Install and open

1. Install KOReader on the Kobo Aura H2O using its installation guide. [KOReader installation on Kobo](https://github.com/koreader/koreader/wiki/Installation-on-Kobo-devices)
2. Copy the entire [kobologicpuzzles.koplugin](kobologicpuzzles.koplugin/) directory into the Kobo’s `.adds/koreader/plugins/` directory. Restart KOReader.
3. Open **Tools → Kobo Logic Puzzles → Settings → Home server URL**. Enter the server’s HTTP address on your home network, for example `http://192.168.1.10:8080`.
4. Choose **Sync now**. On a fresh installation, the app restores any backed-up boards and then downloads puzzles. You can also choose an individual puzzle under **Browse server catalog**.

Catalog browsing and sync require a home server with API v2. In the library, choose **Game → Check server for games** to discover Hashi and Fill-a-Pix. Once puzzles are cached, you can switch to their libraries offline.

Download `kobologicpuzzles.koplugin.zip` from the release and extract its `kobologicpuzzles.koplugin` folder. The workflow also publishes test ZIPs as build artifacts. [Plugin releases](https://github.com/DitisKees/KoboLogicPuzzles/releases) · [Build workflow](https://github.com/DitisKees/KoboLogicPuzzles/actions/workflows/kobo-plugin.yml)

See the [home server setup](../../README.md#running-the-home-server) for running the .NET server and the [API contract](../../contracts/README.md) for how the client exchanges puzzle and progress data.

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

- Start near a numbered island and swipe up, down, left, or right. Even a short swipe cycles through one bridge, two bridges, and zero bridges to the next island in that direction. You can also select **Bridge**, tap an island, and repeat taps on its neighbor. Tap the selected island or an empty cell to deselect it. With taps, **No bridge** toggles a crossed-out candidate, and **Clear** removes either kind of mark.
- An island turns grey and its number is struck through when it has the right number of bridges. A white number on a black island means it has too many. Bridges cannot cross. To solve the puzzle, match every number and connect all islands in one network. The optional dotted guides show candidates around the selected island. Toggle them in **Hashi settings → Candidate bridges**.
- Drag with two fingers to pan, or start a one-finger drag away from islands. Use **−** and **+** to change scale, or open **Overview** and tap an area to jump there. The game saves your view and marks as you play. **Undo**, **Redo**, **Checkpoint**, and **Restart** are available offline.
- **Check** counts decisions that disagree with the stored solution. A single bridge on an edge that ultimately needs two is accepted. The library shows bridges placed as a percentage of the total required, alongside a thumbnail of the current board.

## Play Fill-a-Pix

- Fill cells so every clue matches the number of filled cells in its surrounding 3×3 area, including the numbered cell itself. The grid is solved when all counts match. A clue is struck through when all of its cells are marked and the count matches. A border around a number marks a conflict.
- Tap or drag with **Fill**, **Cross**, **Clear**, or **Cycle**. Numbered cells turn gray when crossed. Hold a numbered cell or select it and tap **Clue** to inspect its counts. **Check** compares your marks with the source solution when available and checks clue conflicts otherwise.
- Use **Pan**, two-finger panning, **Overview**, and **−** or **+** to navigate large grids. **Undo**, **Redo**, **Checkpoint**, and **Restart** work offline. Your marks and position save as you play.
- The catalog and library show the puzzle’s source and any author credit supplied by the server. Fill-a-Pix has its own offline queue and width and height filters, including dimensions above 100.

## Backup and offline puzzles

**Sync now** backs up changed boards and tops up each available game. Set each game's target under **Settings → Offline queue size**. **Catalog and queue filters** apply to browsing and downloads. Sync reports open queue slots when the server has fewer matching puzzles available. Downloaded boards remain playable offline. The library shows the last successful progress backup. Run a sync before a factory reset to back up recent edits.

For Nonogram width, height, and difficulty, Hashi grid size, or Fill-a-Pix width and height, type one value, an inclusive range such as `20-30`, or comma-separated values such as `20,30,50`. You can combine ranges and values in one field, for example `20-30,50`. Hashi sizes use steps of 5 and Nonogram difficulty uses quarter-star steps. For Nonogram or Hashi solvability and Hashi difficulty, select the entries you want and tap **Save**. A puzzle must match one choice in every configured field. Use **Any** or leave a numeric field blank to clear it. Ranges and multiple selections require a home server that supports them.

On a clean KOReader installation, install the plugin, enter the same server URL, and choose **Sync now** to restore your boards. Local settings and board files live in the `kobologicpuzzles/` folder in KOReader’s data directory.

## Rehearse the update on Kobo

Use the tested plugin ZIP and a test server with generated fixtures. To seed the three test sizes, pause the generator worker on the test server and run `hashi-generate` with the following values and the default expansion and upstream difficulty. [Generator CLI](../server/Hashi/README.md)

| Grid | Seed | Density | Library title |
| --- | --- | ---: | --- |
| 20×20 | `00000065` | 10 | Hashi 6590e7 |
| 35×35 | `00000066` | 20 | Hashi 331046 |
| 50×50 | `00000067` | 10 | Hashi 9db7d1 |

1. On the current plugin, sync pending moves. Record the last backup time and open a large saved Nonogram to note its marks, checkpoint, and position.
2. Back up the active plugin and its data directory, then install the candidate using the [manual update instructions](#update-the-plugin).
3. Eject the Kobo, open KOReader, and check that **Tools** has one **Kobo Logic Puzzles** entry. Before changing the server URL, reopen the large Nonogram with Wi-Fi off. Verify its marks, viewport, clues, and checkpoint.
4. Exit KOReader and set the original data folder aside. Restart with empty plugin data, enable Wi-Fi, and enter the test server URL. Choose **Game → Check server for games**, select **Hashi**, then download **20×20**, **35×35**, and **50×50** boards. Use **Browse server catalog** and the size filter if the offline queue fills with other sizes.
5. Disable Wi-Fi. On each size, drag between neighboring islands to cycle through one, two, and zero bridges. Try a crossing, mark no bridge, undo and redo, and test zoom, two-finger panning, and overview jumps. Note tap accuracy near screen edges and whether ordinary moves avoid a full-screen flash. Reopen the game after a KOReader restart to verify marks and viewport.
6. Finish a test puzzle offline and use **Check**. Back up that board and an unfinished one to the test server. With KOReader closed, archive the test data and reinstall the candidate from the ZIP. Start with empty plugin data, connect to the test server, and sync. Verify the restored boards offline.
7. To return to the live setup, exit KOReader. Archive the test plugin and test data separately, then put both original folders back in their original locations. Eject the Kobo and verify the original server URL, the single Tools entry, and the large Nonogram.

Record the Kobo model, KOReader version, tested server and plugin revisions, and the results for each size. Record any missed taps, slow redraws, or differences between the saved and restored boards.

## Desktop smoke test

The GitHub Actions workflow packages the plugin and runs these checks on pushes to `main` and pull requests targeting `main` that change the plugin, the smoke test, or the workflow. It also supports manual runs.

From an extracted KOReader Linux runtime directory, run the game and sync checks with its bundled LuaJIT. Replace the two absolute paths with paths from your checkout.

```sh
KO_HOME="$(mktemp -d)" SDL_VIDEODRIVER=dummy SDL_RENDER_DRIVER=software KO_MULTIUSER=1 \
  ./luajit /path/to/KoboLogicPuzzles/tests/kobo/smoke.lua \
  /path/to/KoboLogicPuzzles/apps/kobo/kobologicpuzzles.koplugin
```
