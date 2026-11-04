# P926 — per-match showcase map rotation

Implemented as default-off operator orchestration. Changes remain in the working
tree; no commits, pushes, remote contact or rig configuration changes.

## Setting and replay

`showcase.mapRotation` defaults to null. With it absent, the original fixed-map
assembly path and matchup receipt format remain in use. The full operator
reference is in [GUIDE.md](../GUIDE.md#showcase-map-rotation).

```json
{
  "showcase": {
    "mapRotation": {
      "mode": "cycle",
      "seed": 926,
      "entries": [
        { "map": "m2xspiderw.igc" },
        { "mapGen": { "type": "SingleRing" } },
        { "mapGen": { "type": "PinWheel" } }
      ]
    }
  }
}
```

Entries contain exactly one file name `map` or `mapGen` block. There must be at
least two entries. `cycle` walks them in order; `random` excludes the previous
entry. Each configured game owns an independent stream initialized from `seed`
(or a logged cryptographic draw when absent). This stream never consumes the
civ, goal or simulation RNG. Every match consumes a layout seed, including
file/pinned entries, so layout generation cannot affect entry selection.
`mapGen.seed` pins one generated entry; `--map-seed N` overlays all generated
entries. File-map seeds reproduce runtime asteroid top-ups; rotation also seeds
the cosmetic model choice for research-added build rocks.

Every opted-in `[matchup]` and authoritative `[gameover]` receipt appends
`map=<file|gen:Type> mapSeed=N rotation=<mode>#<zero-based-entry-index>
rotationMatch=<one-based-match> rotationSeed=N`. Keep configuration, core version,
civ and goal receipts to replay the geography. Restarting with the same stream
seed replays the sequence. Combat scheduling and treasure drops remain outside
this geography replay guarantee. `matchups.py` preserves map receipt fields and
adds `byMap` completed-match/draw/winner-side counts, retaining old-log support.

Boot preflights **all** entries for **every** configured game, before listening:
file existence/parse, generated type and tunables, owner/team compatibility,
existing generator topology constraints, CLUSTERINFO capacity, and every fixed
or rotated goal's map constraints. Numeric/unknown topology strings are rejected.
Counts are bounded to 0–1000, mapSize to 0/1, and finite He3 density to 0–100.
No extra native rule is claimed for these operator limits.

Rotation with debug mode is rejected. File-only rotation permits P924 homeSwap;
any generated entry requires homeSwap=off. This deliberately avoids combining
that ownership experiment with generated civilization-specific home placement.
Player-created worlds keep their chosen map on boot and subsequent rebuilds.

## Battle-sector rule

Showcase march honors a target name present on the current map. Missing/absent
targets use the public CLUSTERINFO graph: consider reachable neutral sectors,
minimize worst hop distance from all flagged homes, then distance spread, total
hop distance, and sector id. With no neutral sector, consider all sectors;
without flagged homes, use the smallest sector id (Brawl). No name/id is baked
into the selector. A disconnected candidate is ineligible.

Bots resolve this again from the fresh world each match, clear their old route,
and log `[battle-sector]` with mission generation, id/name, requested/derived
source and original query. Numeric overrides retain their resolved name across
resets. The log key uses generation because the native mission cookie survives
rotation. Bomber, scout, support and mission-goal duties retain their own rules.

The pinned authored-map test selects **Alderan (1052)**. Generated coverage below
uses map seed 926 and two teams, except DiamondRing's required three teams; names
change with seed, and the chosen sector is always reachable from every home.

| Type | Sector | Name |
|---|---:|---|
| SingleRing | 1 | Dunatis |
| DoubleRing | 1 | Dunatis |
| PinWheel | 2 | Loviatar |
| DiamondRing (3 teams) | 1 | Dunatis |
| SnowFlake | 1 | Dunatis |
| SplitBase | 2 | Loviatar |
| Brawl | 0 | Soron |
| BigRing | 1 | Dunatis |
| HiLo | 1 | Dunatis |
| HiHigher | 2 | Leighton |
| Star | 4 | Torc |
| InsideOut | 12 | Gobo |
| Grid | 1 | Dunatis |
| EastWest | 11 | Aeroflex |
| LargeSplit | 3 | Kassano |

## Map-coupled audit

| Area | Result |
|---|---|
| MCP scenario target | Fixed: showcase missing/absent name falls back on any map, including the rig's retained `Alderan` query; per-match refresh uses the fresh graph. Non-showcase explicit target errors retain their existing semantics. |
| MCP home lookup | Fixed: sector 0 is legal; `FindSideHomeSector` formerly discarded it. |
| Support destinations | Fixed: finale, strike-wave and rendezvous anchors now accept sector 0. Negative ids remain unavailable. |
| Small skirmishes | Preserved: a derived rendezvous still honors the existing meeting-engagement flag and disables the wing hold for small fleets. |
| Route/staging state | Existing fresh-life reset clears old route data; the new target is resolved before route rebuilding. Recovery's existing named-target replay also enters this path. |
| Scout intel/opening | Fixed: after a derived target or changed public-map signature activates map adaptation, each connection's mission generation clears cached home/rock/warp intel and resets explorer/lane/clash state. Late joiners do not reset the fleet. A valid named target on an unchanged authored map retains the live trial's existing opening behavior. |
| Research homes/resources | Existing station/side-based preparation works on rotated file/generated maps. Added replay coverage for generated civ plans (18/4), home station types and all required rock capabilities/reserves. Seeded only the rotated research rock model draw. |
| Research-plan cache | Remains keyed by game/side/civ, independent of geography. Plans are selected before assembly and regenerated home/resource geometry is prepared per match. |
| LAN discovery / boot labels | Fixed: display the map actually selected for the mission, rather than the fixed fallback filename. Legacy labels remain unchanged when rotation is off. |
| Debug topology | Rejected with rotation, rather than applying m2xspiderw ids to another graph. |
| Benchmark Alderan lookups | `Game1.Benchmark.cs` and `SkirmishSmoke.cs` retain fixed-map benchmark setups. No MapRotation property is forwarded by SkirmishOptions; benchmarks cannot opt into this operator setting. |
| Remaining authored names/ids in MCP | Historical comments/examples and the scout sweep's documented conservative 6000-unit default. No remaining executable hard-coded sector name/id was found. The sweep scans current observed geometry; large-map tuning is a rig follow-up, not changed here. |
| Multi-side fleets | Existing MCP showcase stages its first two sides. DiamondRing generation is validated with three teams, but this task does not add a three-plus-side showcase driver; such configurations need their other seats/policy supplied separately. |
| Home assumptions | Multiple flagged home sectors are included in minimax ranking. Existing per-side home inference can remain ambiguous under fog on multi-home maps; scouts still resolve observed stations. No universal 200v200 completion claim is made. |
| P819/P924 audit tooling | Matchup tally now has byMap. The historical spider-specific commute/position audit scripts remain spider-specific; do not apply their home labels or aggregate byAuthoredHome across mixed maps. |

## Validation and proof

Small evidence: [verification.json](P926_assets/verification.json). Raw logs/TRX
remain under `.scratch/p926/` in this worktree, intentionally ignored. `local.env`
was present throughout corpus-backed validation. **Skipped: zero in the new
focused/proof tests; three in Fast**, all existing optional private Meshy-pack
cases. No media was added.

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **PASS**, 0 warnings / 0 errors, final source build. |
| `tools/build-tools.sh` | **PASS**, all 114 tool projects. |
| Server Release/linux-x64/self-contained Native AOT publish | **PASS**, no IL2026/IL3050 warnings. |
| Published AOT config smoke | **PASS**, both cycle/file-first and seeded-random/generated-first configs loaded; server GC and actual map labels confirmed; SIGINT shutdown clean after eight seconds. |
| New/affected focused cohort | **29/29 × 3 PASS**, zero skips. Covers source-generated JSON/CLI seed override, cycle/random replay, pinned/fresh seeds, full rotated layout bytes, authored-map off-path bytes/receipt, validation, player-created map preservation, all 15 generated types, role/objective refresh, fixed valid-target compatibility, small meeting engagement, and scout generation handling. |
| Python matchup tally | **4/4 × 3 PASS**, including old/interleaved logs and byMap. |
| Fogged rotating research showcase | **3/3 PASS**; nine naturally completed matches and nine game-over rotations; zero stage failures or forced endings. |
| Rotation-off never-parks series | Required five attempts: **4/5 full-test passes; 5/5 parking bounds held**. Run 4 missed its separate arrival deadline. Three subsequent filtered invocations (5–7) passed 3/3; all seven runs held the parking bound. See the ledger qualification below. |
| Exactly one Fast sweep | **QUALIFIED FAIL**: 13,030 passed, nine failed, three skipped; one 10.745 s timing overrun against 10.000 s. Infrastructure 9/9 passed. No P926 execution failed. |
| Glossary, parity, Compose, hygiene | Glossary links valid; parity scorecard valid and byte-identical before/after; `docker compose config --quiet` passed; diff whitespace clean; no private paths/secrets/media added. |

The final loopback test runs **15 vs 15**, civ 25 vs 25, `preset=showcase`,
`target_sector=Alderan`, `staging=true`, `loadout_mode=best`, `showMap=false`,
`showHomeSector=true`, research income ×5000 / time ×16, **no test-harness tech
or wallet grants**, and finale disabled. Native five-kill and 600-second goals
bound each match. Real weapons, ordinary purchases, research/constructor orders,
warps, side reseating and automatic intermissions perform the work. The existing
native update/pool/parallel immutable-broadcast settings and server GC are used.
The ticker uses the existing test fixture's 20 ms pacing; these wall times are
functional evidence, not a rig throughput comparison.

| Map / actual seed | Battle sector | Wall seconds across three proofs | Real impacts in battle across proofs |
|---|---|---|---|
| m2xspiderw / 1056742792 | 1052, Alderan | 182.5 / 131.4 / 143.7 | 1,873 / 911 / 1,480 |
| gen:SingleRing / 1123059318 | 1, Cirt | 36.3 / 49.0 / 40.1 | 728 / 1,216 / 1,225 |
| gen:PinWheel / 66361264 | 2, Dunatis | 39.1 / 39.8 / 41.4 | 996 / 1,077 / 1,110 |

Every proof observed both sides in each battle sector, positive shooter-attributed
ship hits, native combat kills, a replacement mission after each game over, and
only current-map ids in scout intel. The longest sampled idle-with-target interval
across these nine matches was **2.01 s**. Each run produced three authoritative
GAME_OVER receipts and assembled the fourth file-map match; byMap reports one
completion for each entry. Research logs include real tower/mine-layer bucket
completions and constructor purchases; generated-civ plan resource coverage is
also replay-tested with civs 18/4.

The one arrival-limit miss and all Fast exceptions are retained in
[TEST_FLAKES.md](../TEST_FLAKES.md#p926-validation--single-fast-sweep-and-filtered-isolations-2026-11-04):
world-population and lifepod relay cases pass 3/3 in isolation; profile editing
passes 3/3 with a writable scratch `XDG_CONFIG_HOME`; five bundle-path fixtures
fail 3/3 because mandated in-worktree TMPDIR exposes ancestor `local.env`; the
unchanged wingman assertion fails 3/3, as already reproduced on dispatch-base
sources in P923. P777 passes all three isolated timing samples (9.344 / 9.531 /
9.301 s). No unrelated source, assertion, timeout, tier or guard was changed,
and no second broad Fast run was made. The original failed Fast result is not
presented as a green gate. The initial off diagnostic used workstation GC;
subsequent off runs and all final research proofs use server GC.

## Rig follow-up and limits

Leave mapRotation absent during the live P924 home-swap trial. After that trial,
use the three-entry cycle above with a recorded seed and games[].homeSwap=off.
Retain the rig baseline: immutableSnapshot/encodeOnce, parallelBuild, pool
transport, native ship updates/nativeGotoPlan, server GC and existing clash tick
budget. Keep discovery/broadcast/receive experiments at their established values.
The rig may continue sending target_sector=Alderan; generated matches fall back
automatically. Do not combine this pilot with a goal-rotation configuration that
includes territory on a two-team SingleRing (boot correctly rejects it).

The recorded arrival/timing/environment qualifications remain open; a clean
whole-suite result is not claimed. Run enough cycles to compare maps separately
with byMap. Geography can alter
routes, choke points and engagement timing; the local skirmish-sized proof is
functional evidence, not a rig performance or balance result. Runtime file
changes are not hot-loaded into an already validated rotation; restart to reload
file entries. No authored maps, balance or finale policy were changed.

## Native intent

Classification: **design extension**, not an era-constraint workaround or native
bug fix. Native stores one creator-selected custom map and generator type in
`Igc/igc.h:1119,1186`; generation selects the custom-file versus mapmaker path at
`Igc/missionigc.cpp:3903`. These citations were re-grepped locally. The existing
territory exclusions reused here are `Igc/igc.h:1494-1503`; their behavior is
unchanged. The sector-zero exclusions and cookie-only scout cache were adjacent port/harness
bugs, corrected without changing native simulation rules. Modern approach:
independent, reproducible operator preset selection
before the existing native-derived assembly, plus a topology-based bot
rendezvous. No new native parity rows or parity credit.
