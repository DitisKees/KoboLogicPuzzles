# Tents and Trees and Battleships (Bimaru)

The server generates both games locally and can import independently verified boards from Janko. Boards use stable content IDs and can be downloaded, played, checked, backed up, and restored through API v2.

## Generation

The Docker image contains the Tents executable and enables both workers. For a local .NET checkout, Battleships needs only .NET. Build the native Tents helper on Linux with the same MIT-licensed revision and wrapper as Docker.

```sh
project="$PWD"
source_dir="$(mktemp -d)"
git -C "$source_dir" init
git -C "$source_dir" fetch --depth=1 https://git.tartarus.org/simon/puzzles.git 616da16f25da428ccda805536209095bd062676e
git -C "$source_dir" checkout --detach FETCH_HEAD
(
  cd "$source_dir"
  sha256sum --strict --check "$project/apps/server/Placement/Native/upstream.sha256"
  git apply "$project/apps/server/Placement/Native/large-boards.patch"
  cc -std=c11 -O2 -ffunction-sections -fdata-sections -Wl,--gc-sections -I . \
    -o tentsgen "$project/apps/server/Placement/Native/tentsgen.c" \
    tents.c matching.c dsf.c malloc.c misc.c nullfe.c random.c sort.c -lm
)
export TentsGeneration__ExecutablePath="$source_dir/tentsgen"

dotnet run --project apps/server -- placement-generate --game tents --size 50 --seed 2
dotnet run --project apps/server -- placement-generate --game battleships --size 30 --seed 2
dotnet run --project apps/server -- placement-status --game tents
```

Use `placement-generate --dry-run true` to verify without storing. The default requested difficulty is `hard`. Pass `--require-difficulty true` to accept only the requested tier. For a local server, set `TentsGeneration__Enabled=true` and `BattleshipsGeneration__Enabled=true` to replenish stock in the background.

| Setting suffix | Default |
| --- | --- |
| `Sizes__0`, `Sizes__1`, … | Tents 25, 30, 35, 40, 45, 50. Battleships 20, 25, 30. The verified 35, 40, 45, and 50 sizes can also be selected for Battleships. |
| `Difficulties__0`, … | `hard`, `expert` |
| `TargetPerDifficulty` | 3 unstarted generated boards per size and difficulty |
| `AttemptsPerSize` | 3 attempts per size and difficulty in a pass |
| `PollIntervalSeconds` | 300 seconds between passes |
| `TimeoutSeconds` | 10 seconds per generated candidate |
| `IncludeUnclassified` | `false` |

The [Tatham layout generator](https://www.chiark.greenend.org.uk/~sgtatham/puzzles/js/tents.html) places the tents and matches them to tree cells. The pinned source is checked before applying a headless patch that uses one tree per six cells and returns a candidate before the upstream rating pass. This sidesteps both the costly stock generator retries on large boards and the requirement for resolving every tree assignment when a tent layout itself is unique. The managed generator prunes premarked grass around trees while proving that the board still has exactly one solution. Distributed copies carry the upstream MIT notice in `/app/tents/LICENCE`.

Battleships places a scalable fleet and then removes ship segment givens in seeded batches. The fleet starts with counts of 7, 5, 4, 3, and 2 for lengths 1 through 5 at 20×20 and scales by area, rounded down. The generator uses repository MIT code. The [danielstjules Python implementation](https://github.com/danielstjules/battleship-puzzles) reports impractical runtime above 7×7 and supplies no repository license. [t2ym5u’s standalone plugin](https://github.com/t2ym5u/battleship.koplugin) is GPL-3.0 and documents 8×8 and 10×10. [OnofreTZK’s generator](https://github.com/OnofreTZK/BattleshipPuzzle) is experimental.

The verifier counts occupied-cell layouts up to two. Tents uses a bipartite matching to check that distinct tents can be assigned to all trees. Battleships verifies fleet composition, fixed ship shapes, straightness, and separation. Both check all row and column counts. Generation limits candidate proofs to three search nodes. The importer allows deeper search under a separate eight-second budget. A failed, ambiguous, or timed-out proof cannot enter the catalog.

Difficulty uses the number of deduction passes and branching. A proof at the root is rated Easy, Medium, Hard from seven passes, or Expert from fifteen passes. A board that uses a branch is rated Expert. Solvability reports basic or advanced according to the deductions used, bounded lookahead for a single binary split, and unknown when deeper search was needed by this solver. This calibration measures the implemented solver and can differ from an archive rating or a player’s experience. The default offline queue prioritizes large Hard and Expert boards with a logic or bounded-lookahead proof.

### Benchmarks

Linux x86-64, Intel i5-4200M, .NET 10 Debug CLI. Each size used seeds 2, 12, and 42 for each requested tier. Every attempt below proved a unique solution. A requested tier can yield a less difficult verified puzzle in the manual CLI. Stock workers require the exact tier.

| Game | Square size | Hard hits / 3 | Expert hits / 3 | End-to-end range (s) | Max CLI RSS (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tents | 25 | 3 | 0 | 0.24–0.52 | 83 |
| Tents | 30 | 3 | 2 | 0.23–0.57 | 85 |
| Tents | 35 | 3 | 1 | 0.32–0.86 | 89 |
| Tents | 40 | 3 | 0 | 0.32–0.66 | 92 |
| Tents | 45 | 3 | 1 | 0.35–0.39 | 92 |
| Tents | 50 | 3 | 2 | 0.42–0.47 | 101 |
| Battleships | 20 | 3 | 2 | 0.18–0.24 | 82 |
| Battleships | 25 | 3 | 2 | 0.31–1.14 | 86 |
| Battleships | 30 | 3 | 1 | 0.65–1.01 | 92 |
| Battleships | 35 | 3 | 3 | 0.86–5.18 | 102 |
| Battleships | 40 | 2 | 1 | 1.05–2.90 | 110 |
| Battleships | 45 | 1 | 0 | 1.23–3.59 | 111 |
| Battleships | 50 | 1 | 1 | 1.72–2.26 | 112 |

The patched native Tents process completed in under 0.01 seconds at every size for seed 2, with about 1.8 MiB peak RSS measured by the launcher. The unmodified native generator reached an eight-second cutoff at 30×30 and larger for that seed. Square generator sizes outside the table have no generation support. The importer accepts rectangular boards up to 50 cells per side.

## Published collections and rights

Enable automatic import on noncommercial deployments with `PlacementCollections__AllowNonCommercial=true`. Set `TentsCollections__JankoEnabled=true`, `BattleshipsCollections__JankoEnabled=true`, or both. Each game scans the numbered pages newest first, in resumable batches of 40, and starts a new scan one week after completing the archive. Every row keeps the source game, numbered page ID, author, original URL, and license. The server only caches boards carrying Otto Janko’s per-puzzle CC BY-NC-SA 3.0 grant, verifies published answers against the proof, and retains the author’s license. [Janko’s license terms](https://www.janko.at/Raetsel/Creative-Commons.htm)

```sh
dotnet run --project apps/server -- placement-refresh --game tents --allow-non-commercial true
dotnet run --project apps/server -- placement-refresh --game battleships --allow-non-commercial true
```

| Source | Format and decision |
| --- | --- |
| [Janko Zeltlager](https://www.janko.at/Raetsel/Zeltlager/index.htm) and [Janko Battleships](https://www.janko.at/Raetsel/Battleships/index.htm) | The indexes listed 740 and 920 numbered pages respectively, before checking authorship, license, format, and uniqueness. Pages have structured `application/x-janko` data, often with solution grids, including rectangular Tents boards. The indexes group boards by size and human solve time. The importer preserves a per-puzzle rating when provided in the structured header. A live pass stored all 40 checked pages from each archive. |
| [Puzzle-Tents](https://www.puzzle-tents.com/) and [puzz.link](https://puzz.link/db/) | Puzzle-Tents exposes public puzzle IDs, 6×6 to 15×15 categories and rotating specials. puzz.link lists authored puzzle URLs. Automated import requires a documented format and a reuse grant from the rights holder. |
| [KrazyDad Battleships](https://krazydad.com/battleships/) | The public collection offers printable PDFs at 6×6 through 12×12 and author-specific difficulty bands. This implementation imports structured Janko pages with a per-puzzle grant. |
| [PuzzleKit Battleship data](https://github.com/SmilingWayne/puzzlekit-dataset) | The index lists 859 boards from 6×6 to 30×30 and 858 solutions. The aggregated data requires verification of the original rights before import. |

Imported files use source `janko`. Generated files use `tatham` or `kobo-logic-puzzles`. Use those IDs in the catalog and offline queue `source` filter.
