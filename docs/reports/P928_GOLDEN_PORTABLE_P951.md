# P951 — the P928 crowded-sector golden is identical on every machine

**Status:** implemented, working tree only (no commit, no push).
**Scope:** `tools/p928-allocation-proof/P928Trace.cs` (golden format v2), the two
P928 fixture CSVs, `tests/Allegiance.Core.Tests/P928AllocationTests.cs` and the
proof-tool README. **No simulation or allocation code changed** — `git diff
-- src/` is empty (verified after the scratch mutation was reverted, below).

---

## 1. What the first hash covers, and which fields are machine-sensitive

The v1 fixture row was `tick,state-sha256,events-sha256,wire-sha256,rng-draws,
collisions,damage,kills,shots` (`tools/p928-allocation-proof/P928Trace.cs`,
v1). All three digests hashed **raw IEEE-754 float bits**:

- *state* — every model's position, velocity, collision velocity, both full
  orientation bases, hull/shield/energy/ammo/fuel fractions, weapon burst state
  and (supplemental) planner control/turn-rate floats;
- *events* — impact points, impulses `j1/j2`, damage amounts/leakage in the
  site callbacks;
- *wire* — raw `SINGLE_SHIP_UPDATE` position floats plus the compact
  quantizations of the real codec.

**Reproduction (step 1 of the brief, done, not reasoned):** the mini runner's
image source `test-runner/Dockerfile` (jobs dir) was built locally
(`p951-test-runner`, SDK 10.0.403 — same version as the workstation) and the
proof tool was run inside it against the same artwork. The v1 trace differed
immediately and massively:

| environment | state-hash mismatches | events | wire | count columns |
|---|---|---|---|---|
| workstation (Ubuntu 26.04 host) | — | — | — | — |
| runner image (Debian 13 container) | 497/500 ticks (first at tick 4) | 168/500 | 440/500 | damage ±1 on 20/500 ticks |

A scratch field probe (named dump of every hashed field per tick, in both
environments, raw evidence `~/allegiance-evidence/P951/`) localized every
difference:

1. **Root cause — the native goto planner's steering floats.** The very first
   divergences (tick 1) are 1-ULP differences in `Ship.Controls.Yaw/Pitch` of
   the twelve `IsNpc` goto-planner ships (1005…1225, `i % 20 == 5`), e.g.
   `-0.87832516` vs `-0.8783251` (`Ctl.Yaw` hex `BF60D9EB` vs `BF60D9EA`).
   These come from libm transcendentals, which differ between the workstation's
   Ubuntu 26.04 glibc and the runner image's Debian 13 glibc.
2. **Chaos amplification.** The planner ships' orientation, then position and
   velocity, diverge chaotically — measured up to **~5 m by tick 318** for one
   ship (e.g. `Position.X` 117.644 vs 122.726). All 38,662 differing state
   fields belong to exactly these 12 ships; the other 248 ships (including all
   20 turret gunners), the probes, asteroids and stations never differ by a
   single bit. Projectiles fired from planner ships or from turrets tracking
   them diverge in velocity (`ProjectileCreatedEvent` payloads), mines damaged
   by that fire jitter, and damage/hit callback floats and counts jitter
   (±1 event on 20 of 500 ticks; 12 extra event lines at tick 500).
3. **One "discrete" field is machine-sensitive too.** The goto planner toggles
   a thrust `StateM` bit (0x40) from its own machine-sensitive floats —
   measured 32 of 500 ticks (`ws-labels.txt` vs `img-labels.txt`, ship 1125,
   `stateM 0` vs `stateM 64`).

Quantizing positions/velocities to a fixed tolerance therefore **cannot** work
for the planner ships (no fixed tolerance spans a chaotic 500-tick divergence),
and the v1 damage/hit floats cannot be hashed at all. This is a materially
worse instability than the mini nightly suggested (there only the state hash
differed — its glibc happens to agree with the workstation's on the
pre-quantization control floats, so its motion stayed bit-identical).

## 2. The v2 format and its tolerances

`P928Trace.Run` keeps the scenario, seed and RNG draw order byte-for-byte
unchanged, and hashes three independent streams (row
`tick,state-discrete-sha256,events-discrete-sha256,continuous-sha256,rng-draws,
collisions,damage,kills,shots`):

- **state-discrete** — model type/id in `sector.Models` order (membership),
  Seen/Eye bits, ship `Dead`/`StationId`/`ParentShip`, per-weapon
  `MountId`/`Active`/`FiringShot`/`TurretRoundsFired`/`FiringBurst`, retired
  ships, asteroid/station ids. **Planner-ship `StateM` is omitted** (measured
  machine-sensitive, §1.3); every other discrete field measured bit-identical
  for all 739,220 model instances per environment.
- **events-discrete** — collision (`kind, a, b, sector`), kill (`kind, ship[,
  launcher]`) and projectile-creation (`kind, projectile id`) callbacks in
  actual dispatch order. Damage and projectile-hit callbacks are **not**
  hashed: a grazing hit/miss is a knife edge that ULP-scale position noise
  flips; they are covered by the damage count column instead.
- **continuous** — quantized floats of every model **except** the twelve
  planner ships (chaotic across machines, §1.2) and except ordnance/mine
  health (hit/miss knife edge): positions/velocities step **0.01**, orientation
  basis components and all health fractions step **0.001**, energy/ammo/fuel/ore
  step **0.01**. Quantization is `MathF.Round(v / step) * step` — an IEEE
  operation, identical on every machine for identical input, and the measured
  cross-environment divergence of the hashed set is **zero bits**, so the steps
  only need to absorb ULP-scale noise (≥10⁴× headroom).

The **damage count** is compared by the test with tolerance: **±2 per tick**
(measured max deviation 1) and **≤32 total** (measured drift 20 of 1,673 ≈ 1.2%
— the tolerance is 1.6× that headroom, still two orders of magnitude below what a
real damage-rule break would produce). RNG draws, collisions, kills and shots
remain strict (measured identical in both environment pairs, and the kill
shots are lethally overpowered seeded shots). The v1 wire digest is retired;
codec identity stays covered by the broadcast/wire tests.

Nothing weaker where it matters: the P928 negative control (below) fails the
new golden at the same tick as the old one, and discrete state still pins
membership, order, ids, weapon state and death across all 500 ticks.

## 3. Regeneration from the pre-refactor reference and identity proof

The v2 fixtures were generated on a `--shared` clone of the repo checked out
at **5ca9f37a** (the pre-refactor base recorded in
`docs/reports/P928_assets/fixture-provenance.json`), with the v2 tool copied
in; the tool compiles unchanged against the pre-refactor `Allegiance.Core`
(`ServerShipUpdate`/`IIgcSite` are untouched by P928). Results:

| comparison | outcome |
|---|---|
| base 5ca9f37a v2 trace vs master (post-refactor) v2 trace | **byte-identical**, both fixtures |
| workstation vs runner image (v2 tool, both fixtures) | identical except the damage column on 20/500 ticks (max Δ1, total 20) |
| `P928AllocationTests` (both cases) 3× alone, workstation | **pass / pass / pass**, 0 skipped (artwork present) |
| `P928AllocationTests` (both cases) 3× alone, **inside the runner image** | **pass / pass / pass** |

So the allocation-refactor identity proof carries over to the new format from
the pre-refactor reference, and the golden now passes in the exact environment
class that failed every nightly since 826a974d.

## 4. Mutation proof

Scratch mutation (reverted before finishing; `git diff -- src/` empty):
`CollisionQueue.SortOrdinalEvents` — the deterministic collision orderer P928's
refactor rebuilt — was made to reverse the sorted stream:

```
if (i == _events.Count || _events.Count == 0) { _events.Reverse(); return; }
```

`P928AllocationTests` (both cases) then failed immediately:
`Golden events-discrete mismatch at line 60` — the reordered collision
callbacks change the events-discrete digest at tick 58, exactly like the v1
golden's tick-1 negative control. A damage/placement rule change would in
addition move the quantized continuous digest or the strict counts.

## 5. Test counts and gate

- Whole solution `~/.dotnet/dotnet build AllegianceCore.slnx` (-m:1 -nr:false):
  **0 warnings / 0 errors**; `tools/build-tools.sh`: **all 118 tool projects
  built**.
- `P928AllocationTests` (both cases): **6/6 pass** (3× workstation, 3× runner
  image), **0 skipped**; fast-suite Skipped count for this worktree: 3
  (optional Meshy-pack MonoGame skips).
- One fast sweep (`tools/run-tests.sh --tier fast`): **13,363 passed / 4
  failed / 3 skipped**. All 4 failures are pre-existing non-task flakes,
  re-run 3× alone per the ledger rule:
  - `NpcWingmanDefendLiveTests.Defender_disengages…` fail/fail/fail — the
    ledgered real regression (TEST_FLAKES "Recurring nightly failures —
    isolation 2026-11-07");
  - `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent`
    fail/fail/fail — environmental: a Tailscale host
    (`domingo-um690.tail…ts.net`, 13 players) is currently discoverable on
    this workstation's tailnet; the test asserts an empty discovery result;
    no discovery code changed;
  - `LiveSideSelectTests.Flag_on_auto_seats…` fail once (pass/pass/fail
    alone) — the ledgered receipt/clock boundary [28,31];
  - `BallotVotingLiveTests.Draw_end_to_end…` failed only in the sweep
    (pass/pass/pass alone) — the ledgered receipt-ordering intermittency
    (TEST_FLAKES:5043).
- Showcase never-parks (`P924_swapped_showcase_never_parks…`): **6 pass / 2
  fail** over 8 runs; both failures are the two slowest runs (~8 m 3 s, load
  average 6.3–8.8 with other lanes building/testing concurrently); every fast
  run (3 m 43 s–7 m 38 s) passed. Matches the ledgered load-sensitivity
  (TEST_FLAKES "Recurring nightly failures — isolation 2026-11-07": load-only
  on the runner); recorded in TEST_FLAKES.

Raw evidence: `~/allegiance-evidence/P951/` (v2 traces from the base, master
workstation and runner image for both fixtures; the field-probe label dumps
`ws-labels.txt`/`img-labels.txt`; v1 environment comparison CSVs). Small
machine-readable summary: `P928_GOLDEN_PORTABLE_P951_assets/provenance.json`.

## 6. Remaining risk

The runner image on an **arm64** mini could in principle diverge more than the
two x64 environments measured here (no arm64 evidence could be collected — the
brief forbids contacting the mini). The design bounds that risk: the only
fields that needed exemption are the planner-ship `StateM` bit, the
planner-ship continuous state and the knife-edge damage counts, all identified
from first divergence at tick 1 of a fresh environment pair; the strict
columns (discrete state minus `StateM`, event identity, RNG/collision/kill/
shot counts) were bit-stable in the worst pair measured. If the mini ever
shows a new mismatch, the failing tick's row pinpoints the column immediately.
