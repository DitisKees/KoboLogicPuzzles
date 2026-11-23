# Kobo Nonogram for KOReader

## Install and open

1. Install KOReader on the Kobo Aura H2O using its installation guide. [KOReader installation on Kobo](https://github.com/koreader/koreader/wiki/Installation-on-Kobo-devices)
2. Copy the entire [kobononogram.koplugin](kobononogram.koplugin/) directory into the Kobo’s `.adds/koreader/plugins/` directory. Restart KOReader.
3. Open **Tools → Kobo Nonogram → Settings → Home server URL**. Enter the server’s HTTP address on your home network, for example `http://192.168.1.10:8080`.
4. Choose **Sync now**. On a fresh installation, the app restores any backed-up boards and then downloads puzzles. You can also choose an individual puzzle under **Browse server catalog**.

See the [home server setup](../../README.md#running-the-home-server) for running the .NET server and the [API contract](../../contracts/README.md) for how the client exchanges puzzle and progress data.

## Play large boards

- Choose **Fill**, **Cross**, or **Clear**. Tap a cell or drag along a row or column to apply the mark. Starting on a cell with the selected mark erases the run. **Undo** and **Redo** apply to the whole drag.
- Drag in **Pan** mode or start a drag in a clue gutter to move the visible grid. **Overview** shows the full board. Tap it to jump to an area. Use **−** and **+** to change cell size.
- Row and column clues stay visible as the grid moves. Tap a clue gutter or hold a cell to read the full clues. An ellipsis indicates that more clues are available.
- Marks and the viewport save locally as you play. The library lists in-progress boards first. The game marks a board solved when the filled cells satisfy both sets of clues.

## Backup and offline puzzles

**Sync now** sends changed boards to the home server and tops up your offline queue. Set the target number of unstarted boards under **Settings → Offline queue size**. You can play previously downloaded boards offline, including partially solved boards. The library shows the last successful progress backup. Run a sync before a factory reset to back up recent edits.

On a clean KOReader installation, install the plugin, enter the same server URL, and choose **Sync now** to restore your boards. Local settings and board files live in the `kobononogram/` folder in KOReader’s data directory.

## Desktop smoke test

From an extracted KOReader Linux runtime directory, run the game and sync checks with its bundled LuaJIT. Replace the two absolute paths with paths from your checkout.

```sh
KO_HOME="$(mktemp -d)" SDL_VIDEODRIVER=dummy SDL_RENDER_DRIVER=software KO_MULTIUSER=1 \
  ./luajit /path/to/KoboNonogram/tests/kobo/smoke.lua \
  /path/to/KoboNonogram/apps/kobo/kobononogram.koplugin
```
