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
dotnet test KoboLogicPuzzles.slnx
docker build --platform linux/amd64 -t kobologicpuzzles-hashi:local apps/server
docker run --rm --network none --entrypoint /app/hashi/bridgegen \
  kobologicpuzzles-hashi:local 50 10 10 2 00000067
```

## Background inventory

- Request upstream difficulty 2, expansion 10%, and densities of 10% and 20% with fresh seeds. Require at least 25 islands and at least 4% actual density, rounded up. The default stock counts in the table apply this floor, deduplicate by content hash, and select hard or expert boards solved with basic or advanced logic.
- Run one candidate at a time in the background. Start with configurable three-minute budgets per stage and a 128 MiB address-space limit for the native helper. Terminate and reap the helper on timeout or worker shutdown.
- Rotate among sizes with a stock shortfall. Allow up to three attempts per size per replenishment cycle, then report the shortfall and retry in a later cycle. Store the verified definition and solution with the seed, settings, and versions so stored puzzles never depend on regeneration.

The server image enables the worker by default. Local .NET runs can enable it with `HashiGeneration__Enabled=true` and set `HashiGeneration__ExecutablePath` to the compiled helper. The worker and CLI use one lock file beside the database. The operating system releases the lock when its holder exits. The inventory cap applies to unstarted stock for each size. Saved and completed boards retain their definitions.

| `HashiGeneration` setting | Default |
| --- | --- |
| `Sizes` | 20, 25, 30, 35, 40, 45, 50 |
| `Densities` | 10 and 20 percent requested, with at least 25 islands and 4% actual density |
| `Difficulties` and `Solvabilities` | `Hard`, `Expert` and `BasicLogic`, `AdvancedLogic` |
| `Expansion` and `UpstreamDifficulty` | 10 percent and 2 |
| `TargetPerDifficulty` and `LowWatermark` | 2 unstarted puzzles per size and tier, with refill triggered at 1 |
| `MaxUnstartedPerSize` and `AttemptsPerSize` | 12 unstarted puzzles per size and 3 attempts per size per pass |
| `StageTimeoutSeconds` and `PollIntervalSeconds` | 180 seconds per stage and 600 seconds between passes |

The density floor can be set with `MinIslands` and `MinDensityPercent`. For Compose array settings, set indexed environment keys such as `HashiGeneration__Sizes__0: "20"` and `HashiGeneration__Sizes__1: "50"`. A fresh server builds toward the configured targets. Later passes resume when stock reaches its low-water mark and continue to the target. To pause automatic replenishment while keeping stored Hashi available, set `HashiGeneration__Enabled=false`.

Inspect the live worker at `/api/v2/hashi/status`. The CLI reads persisted stock and reports the outcome of the requested generation. `--diagnostic true` checks the seed, uniqueness, and rating without adding stock. Remove that option to store a verified puzzle that meets the density floor and inventory cap. CLI generation shares the worker lock, so it may report `busy` during a replenishment pass.

```sh
docker compose -f apps/server/compose.example.yaml exec server \
  dotnet KoboLogicPuzzles.Server.dll hashi-status
docker compose -f apps/server/compose.example.yaml exec server \
  dotnet KoboLogicPuzzles.Server.dll hashi-generate --size 50 --seed 00000067 --density 10 --diagnostic true
```

## Database upgrade and rollback

The renamed server opens the existing `/data/kobononogram.db`. The Compose project changes to `kobo-logic-puzzles` and still defaults to the `kobo-nonogram-data` volume. Compose treats it as an existing external volume. Set `KOBOLOGICPUZZLES_VOLUME` to the actual current volume if it differs. The image override is now `KOBOLOGICPUZZLES_IMAGE`.

If the database predates Hashi, schema 1 adds `hashi_puzzles` and `hashi_progress` in one transaction and preserves existing Nonogram tables, integer IDs, and refresh state. The first startup against a legacy database writes a consistent snapshot beside it as `kobononogram.db.before-hashi` before committing the new schema. Subsequent starts reuse schema 1.

### Publish and verify the renamed packages

After the reviewed rename commit is ready, rename the existing repository to `DitisKees/KoboLogicPuzzles` in GitHub settings. Keep the existing issues, pull requests, tags, and releases. Update remotes and integrations to the new URL. Push the reviewed commit to `main` at the new URL. If it was already on `main` before the repository rename, dispatch both workflows from `main` in the renamed repository.

```sh
git remote set-url origin https://github.com/DitisKees/KoboLogicPuzzles.git
gh issue view 1 --repo DitisKees/KoboLogicPuzzles
gh workflow run server-image.yml --ref main --repo DitisKees/KoboLogicPuzzles
gh workflow run kobo-plugin.yml --ref main --repo DitisKees/KoboLogicPuzzles
```

Check that both workflows succeeded on the intended commit. Verify the older releases and the new `kobo-v1.0.0` release are visible at the renamed URL. If publishing to an existing GHCR package, grant the renamed repository Actions access to that package before retrying. Keep its visibility appropriate for the home server's pull credentials. [GitHub Packages access control](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility)

```sh
revision="$(git rev-parse HEAD)"
export KOBOLOGICPUZZLES_IMAGE="ghcr.io/ditiskees/kobologicpuzzles:sha-$revision"
docker pull "$KOBOLOGICPUZZLES_IMAGE"
test "$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$KOBOLOGICPUZZLES_IMAGE")" = "$revision"
gh release download kobo-v1.0.0 --repo DitisKees/KoboLogicPuzzles --pattern kobologicpuzzles.koplugin.zip
digest="$(gh api repos/DitisKees/KoboLogicPuzzles/releases/tags/kobo-v1.0.0 \
  --jq '.assets[] | select(.name == "kobologicpuzzles.koplugin.zip") | .digest')"
printf '%s  %s\n' "${digest#sha256:}" kobologicpuzzles.koplugin.zip | sha256sum --strict --check -
unzip -tq kobologicpuzzles.koplugin.zip
```

### Back up and replace the server

Sync pending moves from the old plugin, then pause play during the cutover. Use the published image verified above. Set `OLD_KOBO_CONTAINER` to the currently deployed server container's name or ID. The examples use the standard `/data` mount and port 8080. Carry over any custom WebPBN, Hashi, port, or storage settings from your current configuration.

```sh
set -eu
umask 077
: "${OLD_KOBO_CONTAINER:?Set OLD_KOBO_CONTAINER to the current server container name or ID}"
: "${KOBOLOGICPUZZLES_IMAGE:?Set KOBOLOGICPUZZLES_IMAGE to the verified image}"
compose_file=apps/server/compose.example.yaml
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_dir="${XDG_STATE_HOME:-$HOME/.local/state}/kobo-logic-puzzles/$stamp"
mkdir -p "$backup_dir"

old_container="$(docker inspect --format '{{.Id}}' "$OLD_KOBO_CONTAINER")"
volume="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Name}}{{end}}{{end}}' "$old_container")"
test -n "$volume"
docker volume inspect "$volume" >/dev/null
export KOBOLOGICPUZZLES_VOLUME="$volume"
old_image="kobo-nonogram:before-cutover-$stamp"
docker image tag "$(docker inspect --format '{{.Image}}' "$old_container")" "$old_image"
printf '%s\n' "$old_image" "$old_container" "$volume" > "$backup_dir/rollback.txt"

docker stop "$old_container"
archive_data() {
  docker run --rm --network none --user 0 --entrypoint tar \
    --mount "type=volume,source=$volume,target=/data,readonly" \
    "$old_image" -C /data -cf - .
}
archive_data > "$backup_dir/server-data.tar"
tar -tf "$backup_dir/server-data.tar" ./kobononogram.db

docker compose -f "$compose_file" up -d --no-deps --pull never server
curl -fsS --retry 10 --retry-delay 1 --retry-all-errors --max-time 5 http://localhost:8080/api/v2/capabilities
curl -fsS http://localhost:8080/api/v2/restore > "$backup_dir/v2-restore.json"
```

Confirm the expected saves for both games before syncing from the renamed plugin. Check `/api/v2/hashi/status` for generated stock. The first replenishment can take time. [Manual Kobo upgrade](../../kobo/README.md#upgrade-from-kobo-nonogram)

### Roll back the server

Keep the stopped old container through verification. Stop the renamed server and restart the old container on the same volume. A server that already used schema 1 can open the database directly. Verify the restore endpoint for the old server's API version.

```sh
docker compose -f "$compose_file" stop server
docker start "$old_container"
```

If the old server cannot use the current database, stop it again and use the pre-upgrade archive from the same backup. The upgraded archive preserves later edits and generated puzzles. Run these commands with the variables and `archive_data` function from the cutover.

```sh
docker stop "$old_container"
archive_data > "$backup_dir/server-upgraded.tar"
tar -tf "$backup_dir/server-data.tar" ./kobononogram.db
docker run --rm -i --network none --user 0 --entrypoint sh \
  --mount "type=volume,source=$volume,target=/data" "$old_image" -ec '
    cd /data
    mkdir .rollback-cutover
    for file in kobononogram.db kobononogram.db-wal kobononogram.db-shm kobononogram.db.before-hashi; do
      if [ -e "$file" ]; then mv "$file" .rollback-cutover/; fi
    done
    tar -xpf -
  ' < "$backup_dir/server-data.tar"
docker start "$old_container"
```

Reinstall the previous KOReader plugin and confirm that its local library and the old server's restore endpoint contain the expected progress. [Kobo plugin rollback](../../kobo/README.md#upgrade-from-kobo-nonogram)

### Rehearsal results

On 2026-11-24, the upgrade and rollback ran against a private copy of the deployed database on one disposable Docker volume. The original, upgraded database, automatic snapshot, restarted database, and rolled-back database had identical rows for 840 catalog entries, 840 metadata entries, 14 cached definitions, four saved boards, and the catalog refresh state. SQLite integrity and foreign-key checks passed. The original image returned the same v1 restore response after rollback.

On 2026-11-25, the renamed Compose service reused a disposable volume seeded with the upgraded database, including three generated Hashi definitions and one Hashi save. The preceding image, the renamed image, and the preceding image after rollback returned identical v2 restores. SQLite integrity checks passed and all database rows matched the pre-cutover copy.
