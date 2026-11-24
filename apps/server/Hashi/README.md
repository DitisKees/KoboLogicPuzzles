# Hashi generation and validation

The server image builds a small native wrapper around Simon Tatham’s Bridges engine. C# checks the exported solution, proves uniqueness, and rates puzzles from a separate deduction trace. The Docker build pins upstream commit `616da16f25da428ccda805536209095bd062676e`, verifies each compiled upstream source against its SHA-256 digest, and includes the upstream MIT notice beside `/app/hashi/bridgegen`. [Upstream project](https://www.chiark.greenend.org.uk/~sgtatham/puzzles/) · [Build checksums](Native/upstream.sha256)

## Exact validation and rating

The importer checks the supplied assignment against the clues, nearest-neighbor geometry, crossing constraints, and connectivity. Exact validation independently searches the possible counts of 0, 1, or 2 for every edge. A candidate is unique once the search exhausts all alternatives and the sole assignment matches the checked solution. The search stops after two solutions. A timeout after finding one solution leaves uniqueness unverified. [Candidate importer](HashiCandidate.cs) · [Exact solver](HashiExactSolver.cs)

The classifier records island quotas, crossing exclusions, isolated-pair and group exclusions, bridge budgets, and forced exits. Optional depth-one lookahead tests a count and records the contradiction that proves it impossible. The solvability labels distinguish basic and advanced logic, bounded lookahead, exhaustion of the implemented techniques, and an unknown result. The difficulty score combines technique weights and deduction effort. The ranges are 0–35 easy, 36–59 medium, 60–79 hard, and 80–99 expert. Player testing on the Kobo will calibrate these thresholds. [Difficulty classifier](HashiDifficultyClassifier.cs)

## Feasibility results

The three-minute sweep used 18 candidates for each grid size, with requested densities of 10% and 20%, expansion of 10%, upstream difficulties 0 through 2, and seeds 101 through 103. All 126 candidates proved unique. They contained 124 distinct layouts, and each size yielded at least one hard or expert board that the classifier solved with deductions alone and that met the density floor below. The timings describe the original feasibility tools on a Linux x86-64 Intel i5-4200M.

| Grid | Generation median / max | Exact max | Classifier max | Easy / medium / hard / expert | Default stock candidates |
| --- | ---: | ---: | ---: | ---: | ---: |
| 20×20 | 5.9 / 30.9 ms | 2.5 ms | 5.0 ms | 6 / 9 / 2 / 1 | 2 |
| 25×25 | 25.7 / 252.6 ms | 9.6 ms | 19.1 ms | 2 / 13 / 1 / 2 | 1 |
| 30×30 | 61.2 / 344.7 ms | 6.7 ms | 9.9 ms | 6 / 10 / 1 / 1 | 1 |
| 35×35 | 110.8 / 1442.4 ms | 18.3 ms | 17.5 ms | 2 / 13 / 2 / 1 | 3 |
| 40×40 | 757.2 / 2037.0 ms | 15.6 ms | 22.1 ms | 1 / 15 / 1 / 1 | 2 |
| 45×45 | 1204.4 / 5473.7 ms | 14.4 ms | 29.0 ms | 5 / 9 / 4 / 0 | 3 |
| 50×50 | 2974.2 / 41662.8 ms | 27.4 ms | 43.3 ms | 3 / 10 / 2 / 3 | 2 |

The generator peaked at 2.2 MiB of resident memory. The process that validated and classified the original run peaked at 20.2 MiB. The 50×50 board with seed `00000067`, requested density 10%, expansion 10%, and upstream difficulty 2 has 250 islands and rates hard using advanced logic. Repeating its seed produced the same clues and solution.

The C# port matched the reference results for 638 generated candidates, including 469 distinct layouts, and 1,884 small-board fixture cases. The comparison covered solution counts, assignments, hashes, and rating statistics. Nine seeded fixtures remain in the .NET suite, including all seven sizes. The original benchmark tools and reproduction instructions are preserved in the local `hashi-feasibility` branch at `920d58f`. [Seeded fixtures](../../../tests/server/HashiFixtures.json) · [.NET tests](../../../tests/server/HashiTests.cs)

```sh
dotnet test KoboNonogram.slnx
docker build --platform linux/amd64 -t kobononogram-hashi:local apps/server
docker run --rm --network none --entrypoint /app/hashi/bridgegen \
  kobononogram-hashi:local 50 10 10 2 00000067
```

## Starting settings for the server worker

- Request upstream difficulty 2, expansion 10%, and densities of 10% and 20% with fresh seeds. Require at least 25 islands and at least 4% actual density, rounded up. The default stock counts in the table apply this floor, deduplicate by content hash, and select hard or expert boards solved with basic or advanced logic.
- Run one candidate at a time in the background. Start with configurable three-minute budgets per stage and a 128 MiB address-space limit for the native helper. Terminate and reap the helper on timeout or worker shutdown.
- Rotate among sizes with a stock shortfall. Allow up to three attempts per size per replenishment cycle, then report the shortfall and retry in a later cycle. Store the verified definition and solution with the seed, settings, and versions so stored puzzles never depend on regeneration.
