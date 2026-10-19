# SC-2 — nanite support pilots

SC-2 adds legal support purchases and friendly repair targeting to `allegiance-mcp`. Changes remain uncommitted in the working tree; no commit or push was made. Base: `bb0ba46e` (`codex/sc2`).

## Policy and native references

`ALLEGIANCE_BOT_SUPPORT_SHARE` controls the showcase support share, default **0.10**, clamped to `[0,1]`; invalid/nonfinite values use the default. Slots are distributed independently per side: `ceil(count × share)`, giving two of fifteen or five of forty-five. `0` disables automatic support. Only MCP-managed showcase pilots receive this assignment. Human loadouts and purchases are unchanged; active-bot telemetry exposes the support role and its current target. Spectators receive the ordinary ships, loadout exports, projectiles and health updates.

The support selector shares the combat selector's observed side tech, individual dock-local tech, successor, player-hull, mount, and wallet gates. It chooses the cheapest affordable complete support option, with stable ID ties, a negative-power fixed gun, and affordable shield/afterburner equipment. It carries no positive-power gun, missile magazine or dispenser. Per-shot energy/ammo must fit the hull; actual firing still consumes native resources. No tech grant, money grant, damage-rule change, artificial death or teleport is used by the production policy.

| Native rule | Source under `~/Development/Allegiance/src/` |
|---|---|
| Repair weapon identity is negative projectile power | `Igc/shipIGC.cpp:2608` |
| Buy at a particular station using its local mask plus side tech; reject obsolete predecessors | `Igc/stationIGC.h:383-433` |
| Check each weapon socket's part mask | `Igc/hullTypeIGC.h:163-213` |
| Purchase, debit wallet, acknowledge actual loadout, launch only after a complete purchase | `FedSrv/FedSrv.CPP:6183-6199,6247-6259` |
| Gate actual fire on available energy/ammo | `Igc/weaponIGC.cpp:336-337` |
| Lead, turn, throttle, close-range strafe and repair fire gate | `Igc/shipIGC.cpp:2608-2676` |
| Negative damage repairs ship hull, bypasses positive friendly-fire immunity and clips at full hull | `Igc/shipIGC.cpp:1222-1267` |
| Negative damage repairs station armor and clips at full hull | `Igc/stationIGC.cpp:147-156` |
| Training swaps in a nanite and repairs a station and friendly miner | `training/mission8.cpp:194,221,278` |
| Training orders a nanite wingman to repair a bomber under fire | `training/mission8.cpp:577-607` |
| Retain native payday for a later affordable refit | `FedSrv/FedSrv.CPP:5535-5544` |

The decoded native **PCore001.igc** supplies part **91, ER Nanite 1**, projectile **58**, power **−22.5**, speed **400**, lifespan **1 second**. The legal starting support hulls are Iron Coalition **410 (Scout)** with the nanite in socket **0**, and Ga'Taraan **114 (Scout)** with it in socket **1**; both observed starting purchases cost **0** and include shield **21**. Native NPC repair identifies mount zero, but these pilots are player protocol clients: scanning compatible fixed sockets preserves the native mount masks and supports Ga'Taraan's authored socket layout. Mission 8 also names nanite part 190 (`training/mission8.cpp:133`); the selector discovers repair weapons from decoded projectile power rather than hardcoding either identity. Existing native catalogue evidence also lists the starting nanite in [Ga'Taraan's tech tree](../parity/tech-tree/PCore001-civ3.md).

A requested support pilot temporarily flies a legal combat loadout while repair equipment is unavailable. It rechecks reachable, owned, landable shops every five seconds, retains payday if necessary, physically returns to a qualifying dock, and uses normal purchase/launch acknowledgements. Natural death/relaunch reselects legal equipment and retains the support role. Finale selection excludes support pilots from bomber reassignment.

A repair-equipped pilot selects damaged own stations before damaged visible friendly ships in the current sector. It excludes itself, enemies, full/dead/hidden/docked/turreted hulls and pods. With no local repair target it routes through ordinary warps to damaged own stations, otherwise accompanying the strike group or nearby combat peers. It solves projectile lead and fires at angle ≤0.10 radians and intercept time ≤0.95 of projectile lifespan; native close-range strafe/forward/backward controls apply inside 0.25 lifespan. Dodge suppresses fire. It rechecks damage each decision and stops repairing full targets. Share, ranking, target priority and escort assignment are showcase policy, distinct from the native rules above.

## Local research proof

**Three independent qualified matches passed. Each side exceeded both unscaled minima of 10 in every match.**

| Run | Side 0 ship | Side 0 station | Side 1 ship | Side 1 station | Test wall time | Evidence |
|---:|---:|---:|---:|---:|---:|---|
| 1 | 288 | 370 | 338 | 30 | 494.2 s | [snapshot](SC2_assets/showcase-1-skills.log) / [JSON](SC2_assets/showcase-1.json) |
| 2 | 385 | 1068 | 233 | 24 | 704.2 s | [snapshot](SC2_assets/showcase-2-skills.log) / [JSON](SC2_assets/showcase-2.json) |
| 3 | 245 | 59 | 135 | 20 | 473.9 s | [snapshot](SC2_assets/showcase-3-skills.log) / [JSON](SC2_assets/showcase-3.json) |

Qualification uses a generated four-sector SingleRing, civs **25/3**, research **×16**, native income multiplied by **5000**, **45 combat peers per side**, five support pilots each, plus ordinary research commanders/NPCs. The local fixture sets its per-IP connection cap to 128 so all 90 pilots plus probe/commanders can join; production defaults are unchanged. The existing finale arms after one minute with sustained combat, reserves six strike pilots per side, and has a 2400-simulation-second bound. The test stops once both repair minimums are met, or fails on game over / 1200 wall seconds. There is no scripted damage, placement, equipment grant or tech bypass in this showcase proof. Snapshots are SC-1 event counts; passive regeneration and ineffective full-health shots do not count. This is local coverage evidence, not rig or 107v107 qualification.

The smaller exploratory 15v15 runs are retained in the validation summary: two passed, but another ended with only **8 station repairs on side 1**. The qualification profile increases exposure to 45v45 while preserving default support share, research ×16 and the original minimum **10**. Its counts are not pooled with exploratory matches. Setup diagnostics also identified and corrected a queued old-binary launch and the initial 64-connection loopback cap; those attempts are excluded from qualification.

## One pilot, both target kinds

The separate controlled loopback test purchases a real support ship through the legal research shop and exercises the production controller, wire controls and projectile collisions. Like native training fixtures, the test places the two ships and lowers target hulls to 50%; it does not grant equipment or tech. This fixture is separate from the natural showcase above.

One isolated run traced:

```text
[repair-trace] pilot=mcp-b002-4691 ship=8099 side=0 kind=ship target=8096 hull=0.500->0.89079 nativeRepairCredit=0.39079 effectiveHits=3
[repair-trace] pilot=mcp-b002-4691 ship=8099 side=0 kind=station target=0 hull=0.500->0.51427 effectiveHits=3
```

Both actions come from the same pilot. The ship also receives native repair credit; each target kind increments its effective SC-1 counter by at least three. Station hull deltas include any passive regeneration during the observation window; the event counter independently establishes actual nanite repair. All three repetitions passed. See [repair traces](SC2_assets/repair-traces.txt). The natural showcase also captured [a support pilot death and relaunch](SC2_assets/support-lifecycle.txt), retaining its support role and legal repair equipment.

## Validation

- Whole-solution build: **0 warnings / 0 errors**. Core and Server source were unchanged, so the brief's conditional Native AOT publish gate does not apply.
- New policy/target/refit/finale tests: **3 isolated invocations per test**, including both civilization cases. Controlled live repair proof: **3/3**; the long showcase qualification is also run **3 times alone**. Live tests are explicitly tagged `Tier=Live`.
- Never-parks comparison: unchanged base **5/5**, changed **5/5**. Maximum stopped-with-target interval: base **2.104 s**, changed **2.105 s**, against the unchanged five-second limit.
- Exactly **one fast-tier sweep**: **8,774 passed / 5 failed / 3 skipped**, plus **9/9 infrastructure checks**; no fast timing violations. Lobby 19 passed; MonoGame 2483 passed / 3 optional skips; ClientCore 1442 passed; Core 4830 passed / 5 failed. The runner's original overall **FAIL** is retained.

The five fast failures are existing `PlayerBundlePathsTests` cases. This sandbox permits scratch writes only in the workspace or `/tmp`; putting temporary install fixtures below the repository made their ancestor search discover the real `local.env`. They are **environment-context failures, not classified as load flakes**. An isolated path context using `/proc/<parent>/fd/3` as an alias to the same workspace disk directory removes the repository from their logical ancestor chain. All nine path tests passed in that context; the four affected methods, covering every failed theory row, then passed **3/3 individual invocations each** with assertions unchanged. No Core/product path resolver or test assertion was edited, and no second fast sweep was run.

Evidence: [validation.json](SC2_assets/validation.json), [fast sweep summary](SC2_assets/fast-summary.json), [never-parks samples](SC2_assets/parking.json), and the per-match skill snapshots linked above. Reproduction commands and configuration are in [the showcase guide](../showcase/README.md).

## Scratch and evidence

The requested `~/allegiance-scratch/SC2/` and `~/allegiance-evidence/SC2/` destinations are outside this sandbox's writable roots. Task scratch, caches, baseline binaries, logs and TRX files therefore used the workspace's `.sc2-scratch/` on the NVMe filesystem; none of that data was stored on `/tmp`'s RAM disk. Path-fixture isolation used a `/proc` file-descriptor alias to that same disk directory. The task scratch directory and 76 generated project `bin`/`obj` directories were deleted. Only **54,194 bytes** of extracted evidence remain in `SC2_assets/` (below the 2 MB limit); raw logs/TRX are not retained because the requested external evidence location cannot be written here. No rig/221/200v200 run was made.
