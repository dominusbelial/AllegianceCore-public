# P651 — equipment signature, missile load/lock rules, and readiness

Working-tree implementation, 2026-10-14. No commits or pushes.
Native paths below are relative to `~/Development/Allegiance/src/`.

## Rules and implementation

| Rule | Native source | Port |
|---|---|---|
| Active guns and afterburners add/remove their part signature | `Igc/weaponIGC.h:103-128`; `Igc/afterburnerIGC.h:72-93` | `Ship.EffectiveSignature` reads weapon activation and afterburner activation, including the power tail. |
| Equipped shields, magazines, dispensers and chaff dispensers contribute signature | `Igc/shieldIGC.h:65-78`; `Igc/magazineigc.h:67-81`; `Igc/dispenserigc.h:67-80` | Equipped mount-zero parts contribute even while mounting or empty. Cargo contributes nothing. All additions are multiplied by cloaking (`Igc/shipIGC.h:268-270`). |
| A magazine must finish mounting and pass its strict load deadline | `Igc/magazineigc.cpp:85-103`; `Igc/magazineigc.h:71-78,100-104,132-149` | Per-part float mounting progress and `TimeLoaded`; mounting a launcher starts `1 / MountRate + LoadTime`, arming it starts ready, and firing starts `LoadTime`. |
| Local/NPC fire accepts any lock, including zero | `Igc/magazineigc.cpp:110-123` | Removed full-lock fire gate. Launch uses the old normalized lock times missile `MaxLock`; lock resets before the fire callback. Held fire waits for load readiness. |
| Early player fire consumes a round and produces a dud outside the 0.5-second tolerance | `FedSrv/FedSrv.CPP:5433-5434,5468,5507-5510`; `Igc/missileigc.cpp:128,428` | Player request has a separate path from held local fire. The missile carries and broadcasts its dud flag and ignores contact collisions, including warp triggers. Native expiry/destruction explosions remain possible (`Igc/missileigc.cpp:354-358,517-528`). |
| Lock targets include static objects; warp bombs require warps | `Igc/magazineigc.cpp:127-167` | Static station/warp/asteroid model views, hostile/alliance and visibility gates, same-sector checks, and ship-only ECM. Missile homing retains static targets while their backing object exists. |
| Changing target clears the old magazine lock | `Igc/shipIGC.h:743-746` | `Ship.CommandTarget` compares object identity/type, including colliding ship/station ids. |
| Mount changes and ammo reload reset readiness | `Igc/shipIGC.cpp:1567`; `Igc/weaponIGC.cpp:104-117`; `FedSrv/FedSrv.CPP:6574-6581,6637-6638` | Cargo/equipped swaps reset mount progress; ammo reload disables ammo guns; launcher reload also resets its load deadline. Float progress survives amount-only replacements. |
| Guns wait for mounted fraction 1; shield/cloak readiness is honored | `Igc/weaponIGC.h:33-48`; `Igc/shieldIGC.cpp:70-90`; `Igc/cloakigc.cpp:80-81` | Guns cannot fire during the ramp, shields cannot absorb/regenerate before ready, and cloaks cannot activate early. The loaded zero mount rate remains zero. |
| Selected gun receives energy first, descending cyclic fixed mounts, ascending turrets, then shield regeneration | `Igc/shipIGC.cpp:312-349` | Preserved P650's implementation and regression coverage. |

## Client and bots

The existing 35-byte port-only `MISSILE_LOCK_UPDATE(204)` uses its former padding
byte for target object type and a not-ready flag. Its default ship/ready encoding
remains byte-identical. The client retains the object type, suppresses a lost-lock
tip on a type change, projects static targets from their supplied positions, and
avoids treating an equal-numbered ship as the static target's render anchor.
The weapon HUD displays `NOT READY` from mount progress or magazine readiness;
loadout messages deliver reset/ready edges. Held player fire waits for readiness.

MCP bots keep their full-lock/evasion policy and now also require authoritative
magazine readiness. `Catalog.LauncherReadyTime` reads missile `LoadTime`, replacing
the unrelated missile flight `ReadyTime` delay.

Architecture adaptation: the port client receives normalized lock on extension
204 and reports that normalized value in its fire request; the server applies the
mounted type's `MaxLock`. The server retains its existing authoritative mission
clock and command target instead of trusting the request's tick-based time, type,
ids or launch geometry. Native uses client simulation time/geometry and an already
scaled lock. Partial/zero lock, the effective launch lock, reload cadence and the
half-second dud boundary are preserved.

## Verification

`dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings, 0 errors**.

All **28 new test cases** passed three focused repetitions. The 19 affected
existing test classes also passed three class-isolated runs: missile fire/lock,
wire lock codec, missile broadcast/combat, current-target orders, cargo reload,
auto reload, swaps, fuel/afterburner, cloak, P650 timing/order, client missile
fire/lock/tracking/warnings, launcher catalog, shop launcher mounting and HUD
reload tracking. New core equipment coverage was additionally rerun 3× as a
whole class after the final shield mounting correction.

Updated old expectations that stations were unsupported and mount fraction
stayed permanently zero after reload. The live reload tests observe the reset
in the authoritative loadout broadcast while mounting continues. Existing
missile/lock/shop fixtures now provide sensor-visible targets; the shop fixture
selects a regular missile rather than a warp bomb for its ship target. All these
corrected fixtures passed 3/3; their original deterministic failures were fixed,
not classified as load flakes.

Final unfiltered suites, sequential, using `dotnet test tests/<assembly> --no-build`:

| Assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core | 4189 | 1 | 0 |
| ClientCore | 873 | 2 | 0 |
| ClientCore.MonoGame | 1974 | 0 | 3 |

The three full-pass failures were rerun by **exact method filter**, one at a
time, after all suites finished. Every one passed **3/3 alone**, so all three
are load flakes under the brief's rule:

| Test | Full-pass symptom | Isolation |
|---|---|---|
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` | Bystander receipt collection empty | 3/3 pass |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | Logon drain order differed from connection order | 3/3 pass |
| `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` | Docker's own `StationId` receipt did not latch | 3/3 pass |

No P651 regression remains failing. The three MonoGame skips are the existing
`MeshyPackTests` checks for a missing optional built content pack. The suites ran without test filters, including available artwork,
native-source and loopback coverage. Raw full
suite TRX files are `/tmp/p651-test-results/p651-Core.trx`,
`p651-ClientCore.trx`, and `p651-ClientCore.MonoGame.trx`; isolation and failure
rerun ledgers are `/tmp/p651-isolation/ledger.json` and
`/tmp/p651-flake-ledger.json`. `git diff --check` is clean.

The local TCP battle uses the production `BotManager.MaybeFireMissile` controller
with a real `ConnectionSession` and a fixed combat geometry. The actual server
launch, collision, kill and score paths run normally. Initial proof: **two missiles,
one kill, zero duds**. Separate live TCP tests select stations and warps through
`ORDER_CHANGE`, observe type/position/readiness feedback, send an early dud, and
wait for full ready lock. No rig/221/200v200 work was run.
