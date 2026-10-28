# P864c — file-map mission generation and verified duplicate logons

Both assigned production rows move from `partial` to `implemented`. Changes are
left in the working tree; no commits, pushes, remote hosts or synthetic load were
used. Native sources were read locally and line numbers rechecked against this
checkout. The file-map population call is at `Igc/missionigc.cpp:4048-4051` here
(the brief's `4051-4055` includes its end and cluster activation).

## Mission generation (SIM-0748)

`Mission.PopulateFileMap` runs from the server's `AssembleGameMission`, on initial
boot and mission rebuilds. It adds the missing custom-map branch without changing
the generated-map preset defaults.

- The station type decoder **already** reads the three 50-byte masks: required
  at byte 262, effects at 312, local at 396. The brief's claim that these fields
  were absent was stale. A raw-payload test now proves the offsets; the native
  declarations are `Igc/igc.h:1753-1763,2649-2659`.
- Station replacement uses `side.DevelopmentTechs | original.LocalTechs |
  original.EffectTechs`. A compatible original stays. Otherwise the first
  same-class type whose required techs are a subset is selected, in loaded core
  order. Candidate effects and other buildings cannot supply eligibility.
  No match preserves the original. Reverse traversal and moving replaced stations
  to the end preserve native ordering (`Igc/missionigc.cpp:4011-4046`;
  `Igc/stationIGC.cpp:210-276`). The runtime/static station records synchronize;
  authored map records, station names, pose and damage fractions are retained.
- Asteroid and treasure emptiness are independent gates, captured before flags
  are created (`Igc/missionigc.cpp:4048-4051`; `Igc/common.cpp:2963-3107`). Existing
  lists are preserved even when below the requested count. Starting flags do not
  suppress initial treasures. Neutral artifacts accompany treasure population
  only when the original treasure list was empty.
- New rocks share generated-map type/geometry placement helpers. File generation
  counts existing He3 rocks plus planned He3 counts in empty clusters for the
  denominator, including the zero-denominator case
  (`Igc/missionigc.cpp:3978-4007`). Existing ore is unchanged. New IDs avoid all
  imported IDs. Placement includes existing rocks, stations, warps and initial
  flag treasures (`Igc/common.cpp:2803-2841`). Special resources cycle through
  enabled expansion/tactical/supremacy paths, or all three when none are enabled
  (`Igc/common.cpp:3054-3057`; `Igc/asteroidigc.cpp:179-228`).
- File-map resource inputs have no separate config surface in this port. They
  now use native `MissionParams::Reset` defaults: home/neutral mineable 2/4,
  special 1/1, generic 20/20, treasures 0/5, density 1
  (`Igc/igc.h:1284,1315-1328`). Generated-map presets retain their existing settings.

The optional AGC override-array branch is **n/a because its input cannot exist**:
`Igc/missionigc.cpp:3885-3894` consumes per-side altered/value bit masks supplied
by native AGC parameters (`FedSrv/fsmission.cpp:228-237,1954,2718-2734`). This
port has no AGC/COM admin object, per-side override-mask JSON fields, wire message,
or HTTP endpoint that can supply those two arrays. Ordinary research path
booleans and global test-unlock grants do not encode arbitrary per-side masks.
No existing input was discarded or interpreted as n/a.

## Duplicate logons (SIM-0496)

After normal version and lobby identity validation, a duplicate seated character
is removed only if **both** peers have the same nonempty verified lobby AccountId.
Names match case-insensitively; AccountIds match exactly. The native
`RemovePlayerByName` primitive is now reachable from the authenticated logon
caller (`FedSrv/FedSrv.CPP:629`, `FedSrv/fsmission.cpp:1248-1273`).

The primitive finds the first player in that mission, sends the old peer
`QUIT_MISSION` with `DuplicateLocalLogon`, and runs normal departure cleanup,
including survivor notices and leadership/score bookkeeping. Deliberate removal
bypasses the optional reconnect parking path. A nonexistent player returns false.
The newcomer then receives a fresh successful logon/ship reservation and can
select a side. Different accounts, anonymous identities, invalid/replayed tickets,
wrong client versions and existing pending-name reservations cannot evict players.
The claimed CD key and Steam ID establish no ownership.

Server JSON `alwaysRefuseDuplicateLogons` defaults to **false**. Setting it true
retains the previous always-refuse policy. Environment
`ALLEGIANCE_ALWAYS_REFUSE_DUPLICATE_LOGONS` overrides JSON (`1`/`true`/`yes`/`on`,
`0`/`false`/`no`/`off`; empty preserves JSON). It is wired in Compose and documented
in [docker/README.md](../../docker/README.md). JSON continues to use the existing
source-generated serializer; no reflection serialization was added.

The native lobby caller `FM_L_REMOVE_PLAYER`
(`FedSrv/fslobby.cpp:164-203`, lookup/call at `:190-200`) has **no equivalent
input/transport** in this port. The modern lobby directory supports registration,
heartbeat, listing removal and lookup (`src/Allegiance.Lobby/LobbyEndpoints.cs`),
identity/squad/career HTTP APIs; it has neither a player moderation endpoint nor
a downstream server-command queue. P762's moderator path is an authenticated
**game-server** `QUIT_MISSION` handler (`Server.Moderation.cs`), not a lobby admin
path. Thus there is no lobby command to wire to the primitive. The native caller
is unavailable here; existing game-server moderation remains available.

## Verification

Base: `f6e9e480fab8f446d0fafa0ec3caed61675ee3c6`. [Small machine-readable summary](P864c_assets/verification.json).

Raw logs/TRX are retained in the [verification directory](../../.scratch/p864c/)
because the sandbox permits
writes only inside this worktree and `/tmp`, and the lane rules prohibit `/tmp`
scratch. No files were written to other checkouts or evidence roots.

| Check | Result | Evidence |
|---|---|---|
| Final solution build | **0 warnings / 0 errors** | [build log](../../.scratch/p864c/build-final-validated.log) |
| All tools | **91 projects, 0 warnings / 0 errors** | [tool build log](../../.scratch/p864c/build-tools-final.log) |
| Linux x64 self-contained AOT publish | **PASS; no IL2026/IL3050 or other warnings** | [publish log](../../.scratch/p864c/aot-final.log) |
| New regression cases | **16 cases × 3 = 48/48**, including survivor removal-message receipt | [final new-test driver](../../.scratch/p864c/final-new-driver.log), [TRX/logs](../../.scratch/p864c/final-new/) |
| Selected existing assembly, map, treasure, duplicate, identity and moderation tests | **3/3 isolated runs per group** | [initial isolation](../../.scratch/p864c/isolation-driver.log), [final identity/moderation](../../.scratch/p864c/final-new-driver.log) |
| Repaired population-dependent fixtures | **3/3 per affected group** | [fixture reruns](../../.scratch/p864c/final-isolation-driver.log), [final strike reruns](../../.scratch/p864c/strike-final-driver.log) (the final strike run supersedes its intermediate fixture failure) |
| One Fast sweep | **11,300 passed / 28 failed / 3 skipped; 483.6 s; infrastructure 9/9**; resolutions below | [summary](../../.scratch/p864c/fast/fast/summary.json) |
| P730 two-match showcase | **5/5 passes (10 matches), 3m12s–4m43s per run** | [showcase driver](../../.scratch/p864c/showcase-driver.log), [receipts](../../.scratch/p864c/showcase/) |
| Compose, glossary links, parity CSVs, whitespace/public-code scan | PASS | [Compose](../../.scratch/p864c/compose.log), [glossary](../../.scratch/p864c/glossary.log), [scorecard](../../.scratch/p864c/after-scorecard.log) |

The Fast sweep was run **once**, as requested. It is retained as a failed sweep,
not relabeled green after targeted reruns. Every failure was investigated and
isolated three times; the final results are documented in
[TEST_FLAKES.md](../TEST_FLAKES.md):

- **19 affected fixture cases were repaired.** Native population changes the
  game world, so export/targeting tests now include authored plus top-up rocks;
  rotation tests prove old treasures are retired while accepting newborn
  treasures; precision NPC/drop/missile scenes explicitly clear ambient objects;
  and the station volley fixture compares identical station types and side
  attributes. The existing native armor-first regeneration rule can leave a
  surviving station's shield at zero (`Igc/stationIGC.h:189-213`).
- **7 environment/build-order failures:** five path-resolution cases, one
  read-only profile case and the initially missing GPU proof DLL all passed
  three times with the known scratch `/proc` alias, scratch `XDG_CONFIG_HOME`,
  and completed tool build respectively. The initial NuGet HTTP cache was also
  read-only; a scratch-local cache enabled normal package auditing.
- **2 unchanged receipt/load flakes:** shop launcher grant and same-side combat
  visibility both passed three exact-method runs alone. No deadlines or assertions
  were relaxed. The measured host load average reached 21.29 on 24 CPUs during
  legitimate verification work.
- The passing legacy slow-hull test exceeded Fast's 10.066 s calibrated ceiling
  (19.437 s in the sweep; 18–20 s alone), so it is now `Tier=Live`, with three
  more isolated passes. P651's other timing offender was resolved by restoring
  its deliberately obstacle-free missile corridor.


Scorecard validates all 851 sim rows. Assigned weight increases **0/7 → 7/7**.
The mission-rules family moves **525/603 (87.06%) → 532/603 (88.23%)**, completed
rows **222/256 → 224/256**. Total sim parity moves **1813/1903 (95.27%) →
1820/1903 (95.64%)**, completed rows **643/687 → 645/687**. Denominators and
reachability remain unchanged. [Before](../../.scratch/p864c/before-scorecard.log)
and [after](../../.scratch/p864c/after-scorecard.log) scorecards are retained.

## Per-row closeout

| Row | Native source | Before → after | Tests | Native intent | Modern/better-approach note |
|---|---|---|---|---|---|
| SIM-0748 (weight 5) | `Igc/missionigc.cpp:3830-4123`, especially `3978-4051`; `Igc/common.cpp:2963-3107` | partial → implemented | `P864cMissionGenerationTests` (7 cases); `P864cDuplicateLogonTests.Server_file_map_boot_runs_native_empty_sector_population`; existing assembly/map/treasure tests | Intentional gameplay compatibility and initial population rules (`native_class=design`). AGC arrays unavailable as explained above. | Reuse decoded masks and shared asteroid placement; retain authored content and use explicit native defaults instead of inventing a second resource policy. |
| SIM-0496 (weight 2) | `FedSrv/fsmission.cpp:1248-1273,460-488`; `FedSrv/FedSrv.CPP:629`; unavailable lobby caller `FedSrv/fslobby.cpp:164-203` | partial → implemented | `P864cDuplicateLogonTests` (8 cases); `P864cDuplicateLogonConfigTests`; existing duplicate/identity tests | Intentional authenticated single-character ownership rule (`native_class=design`). Unverified names follow refusal semantics. | Verified immutable AccountId replaces native trust in character-name authentication; retain an explicit opt-in always-refuse policy. A future lobby moderation service would need an authenticated command channel before exposing this primitive. |
