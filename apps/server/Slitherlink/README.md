# Slitherlink generation and imports

The server image builds the headless Loopy helper from the same pinned, MIT-licensed Simon Tatham source as Bridges. Loopy generates a candidate by removing clues while its deduction solver can still complete the board. The helper only exports a generated puzzle after the deductions produce a solution, and the server checks the exported loop and clues again. For imports, the server uses complete deductions when available and otherwise searches for up to two valid loops. It stores a solution only after verifying uniqueness. [Loopy source](https://sources.debian.org/src/sgt-puzzles/20250730.a7c7826-1/loopy.c/) · [Build checksums](Native/upstream.sha256)

## Stock

The Docker worker runs one Slitherlink attempt at a time. It keeps **three unstarted puzzles per size, tiling, and difficulty** by default. Each pass tries every combination below its target, starting with the lowest stock counts. Solving or starting a board and syncing it frees a stock slot. The stored puzzle definition stays available for restore. A failed or timed-out attempt is retried with another seed on a later pass.

| `SlitherlinkGeneration` setting | Default |
| --- | --- |
| `Enabled` | True in the server image, false in a local .NET run |
| `Sizes` | Nominal 8×8, 12×12, and 15×15 |
| `GridTypes` | All 18 Loopy tilings |
| `Difficulties` | `easy`, `normal`, `tricky`, `hard` |
| `TargetPerCombination` | 3 unstarted puzzles |
| `TimeoutSeconds` | 1790 seconds per generation attempt, also the maximum |
| `ImportTimeoutSeconds` | 30 seconds per imported puzzle, configurable up to 1790 |
| `PollIntervalSeconds` | 600 seconds after a pass without additions |
| `ExecutablePath` | `slitherlink/loopgen` beside the server DLL |

Tilings use the keys `square`, `triangular`, `honeycomb`, `snub-square`, `cairo`, `great-hexagonal`, `octagonal`, `kites`, `floret`, `dodecagonal`, `great-dodecagonal`, `penrose-kite-dart`, `penrose-rhombs`, `great-great-dodecagonal`, `kagome`, `compass-dodecagonal`, `hats`, and `spectres`. Sizes are Loopy's input dimensions. Cell counts vary with tiling and, for aperiodic tilings, can vary with the seed. For Compose array settings use indexed keys such as `SlitherlinkGeneration__Sizes__0: "10"` and `SlitherlinkGeneration__Sizes__1: "15"`.

The status endpoint is `GET /api/v2/slitherlink/status`. The CLI can store a puzzle or verify a replay without adding to stock. Generation commands share the worker's lock, so they may report `busy` during an attempt.

```sh
docker compose -f apps/server/compose.example.yaml exec server \
  dotnet KoboLogicPuzzles.Server.dll slitherlink-status
docker compose -f apps/server/compose.example.yaml exec server \
  dotnet KoboLogicPuzzles.Server.dll slitherlink-generate --size 8 --tiling kagome --difficulty easy --seed 00000065 --diagnostic true
```

## Structured collections

Pass a file or directory to `slitherlink-import --file PATH`. The importer supports these formats.

- **PuzzleKit** Slitherlink JSON shards contain a `data` object with a rectangular clue grid in each entry's `problem` field. Available source URLs, author credits, and license fields travel with the puzzle. Entries without a known origin appear as **PuzzleKit**. [Downloadable dataset shards](https://github.com/SmilingWayne/puzzlekit-dataset/tree/main/assets/data/Slitherlink)
- **Mindgames** Slitherlink ZIP or its individual square-board text files. The importer reads the square-size `puz-NNxNN.txt` and `puz-NNxNN-Nk.txt` entries and uses the collection's difficulty labels. The same import path verifies solutions from the clues. [Mindgames SlitherLink archive](https://ulhaque.co.uk/common/exe/slitherlink20k.zip)
- **JSON puzzle packs** contain a `puzzles` array. A puzzle supplies `clues` as a rectangular array of rows with integers or `null`, or a descriptive Loopy `gameId` for any supported tiling. Optional fields are `title`, `author`, `difficulty`, `source`, `sourceName`, `sourceUrl`, and `license`. Example:

```json
{
  "puzzles": [
    {
      "title": "Single loop example",
      "source": "my-collection",
      "sourceName": "My Collection",
      "clues": [[4, null]]
    }
  ]
}
```

Use `--source SOURCE --source-name NAME` to assign a known collection label to the whole import. Choose files you have permission to use, and retain author and license metadata where available. The CLI reports how many boards were stored, already present, or rejected. A malformed, unsolvable, ambiguous, or timed-out board leaves the rest of the file importable. Reimporting a source preserves existing IDs and progress.

```sh
docker compose -f apps/server/compose.example.yaml cp Slitherlink_dataset_000.json server:/data/
docker compose -f apps/server/compose.example.yaml exec server \
  dotnet KoboLogicPuzzles.Server.dll slitherlink-import --file /data/Slitherlink_dataset_000.json
```
