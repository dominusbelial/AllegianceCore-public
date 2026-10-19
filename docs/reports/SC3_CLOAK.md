# SC-3 — scout and bomber cloak policy

The MCP pilot now requests a real mounted cloak while scouts travel/explore or approach enemies, and while bombers travel to a siege or approach a target without firing or boosting. It clears the request for guns, missiles, mines and afterburner. Existing steering, weapon aim/range gates, research, purchases and SC-1 counting sites remain authoritative. No Core or Server code changed.

`CloakPolicy` reads the granted inventory, current hull, energy and flight state. Missing/incompatible equipment, cargo, an unready or empty part, a non-scout/bomber hull, docking, boarding, death and ripcord keep the request off. A new request requires more than the greater of 20% hull energy or four seconds of cloak drain; an existing request ends below the greater of 5% or one second of drain. Those reserves and hysteresis are pilot policy. No timer or direct counter increment manufactures activations. Missile/mine requests are suppressed while cloaking. A new life or a neutral loiter request resets the hysteresis latch. Eligible own-ship inventory is read under the client gate; other hull roles are rejected from the existing decision snapshot.

The existing tech selector already purchases compatible cloak parts when the side's research, station, budget and obsolescence rules permit. It is unchanged. SC-1's short civ-25/civ-3 run had no cloak-bearing purchases. The legal-loadout tests cover civs 1 and 35; the final live proof mirrors civ 35 on both sides, which have reachable cloak-bearing bomber/scout loadouts; it does not override a mount mask or grant locked parts.

## Native evidence

Paths below are relative to `/home/domingo/Development/Allegiance/src/`.

| Rule | Native source |
| --- | --- |
| Mission 4 teaches toggling cloak | `training/mission4.cpp:719` |
| Nonweapon equipment uses mount 0; the hull's equipment mask must contain the part mask | `Igc/hullTypeIGC.h:163-176,199-213` |
| Cloak update is for mount 0; activation requires the request and mounted readiness | `Igc/cloakigc.cpp:62,68-83` |
| Cloak energy costs consumption × elapsed time; insufficient energy reduces target strength by the square of the available/needed ratio | `Igc/cloakigc.cpp:89-103` |
| Cloaking and decloaking ramp at the part's on/off rates; emitter deactivation follows the return to zero strength | `Igc/cloakigc.cpp:105-121` |
| Changing the cloak mount deactivates it | `Igc/cloakigc.cpp:151-159` |
| Ship state handling checks parent/dock/mounted cloak and clears the cloak request during ripcord | `Igc/shipIGC.cpp:1595-1615` |
| Active gun and afterburner add their signature; their deactivation removes it | `Igc/weaponIGC.h:103-123`; `Igc/afterburnerIGC.h:72-90` |

The brief's phrase “firing/afterburner breaks cloak” is implemented as the bot's explicit mutually exclusive control policy. These native gun/afterburner activation methods change signature and do not clear `cloakActiveIGC`; adding an automatic simulator break would invent a native rule. Clearing the request starts the native fade, so the emitter may remain active briefly while fading. The live proof requires later actual gunfire with both request and emitter off, in the same bomber sortie, against a selected enemy station.

## Qualification

Whole-solution build: **0 warnings / 0 errors**. The two new policy/catalogue tests and four affected lifecycle/combat regression methods each passed **3/3 separate exact-method invocations** (18 passes). They cover missing/unready/cargo/empty parts, missing hull, energy and hysteresis, dock/board/dead flight eligibility, ripcord, gun/missile/mine/boost exclusion, movement preservation, legal research purchases, life resets, and close-combat movement. The live test is tagged `Tier=Live`.

The final research showcase test passed **3/3 isolated invocations**. [Live summary](SC3_assets/live-summary.json), [run-1 SC-1 counters](SC3_assets/skills.txt), and [proof configuration](SC3_assets/proof-config.json) preserve the small review evidence.

| Final run | Wall duration | Side 0 cloak / minimum | Side 1 cloak / minimum | Bomber witness |
| --- | --- | --- | --- | --- |
| 1 | 124.879 s | 78 / 5 | 76 / 5 | pass |
| 2 | 156.156 s | 118 / 5 | 132 / 5 | pass |
| 3 | 137.539 s | 87 / 5 | 85 / 5 | pass |

The [selected bomber trace](SC3_assets/bomber-trace.json) follows side-0 ship 8130, hull 389 (`Hvy Bomber`), in one sortie. At sim 592.950 it requests cloak and the emitter is active (`stateM=2048`, no gunfire). Later the fire request clears cloak, the emitter fades at its native off rate, and at sim 766.650 it produces an actual weapon shot fully visible (`Cloaking=1`, emitter/request both false, `stateM=32768`) at its selected enemy station 1, about 756.57 units away. Earlier shots during the fade are retained in the trace rather than hidden. This witness proves emission toward a station target; it does not claim a station hit or kill. The run stops at qualification, and its station-damage counter is still zero.

One fast-tier run executed **8,778 cases in 270.2 s**: **8,770 passed, 5 failed, 3 skipped**. All nine infrastructure checks passed and no Fast case exceeded the calibrated 10.238-second limit.

| Project | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| Lobby | 19 | 0 | 0 |
| MonoGame | 2,483 | 0 | 3 |
| ClientCore | 1,438 | 0 | 0 |
| Core | 4,830 | 5 | 0 |

The five failures were existing `PlayerBundlePathsTests` cases: artwork executable-relative lookup; missing artwork/native defaults; blank-environment local-config precedence; and native-source fallback. With `TMPDIR` below this checkout, their nominally external temporary install directories inherit the real repository's `local.env`. Every failed case then passed **3/3 exact-case invocations**, without assertion or product changes, through a `/proc/<runner-pid>/fd/<scratch-directory-fd>/` alias to the same writable workspace scratch directory. The alias changes lexical parent discovery while all writes still land in workspace scratch; no `/tmp` data is involved. This is a scratch-topology limitation of the initial sweep, not a claimed all-green sweep or a load flake. [Validation ledger](SC3_assets/validation.json) retains the failed sweep and successful rechecks; raw exact-case evidence is in `temp-path-exact/`.

Parking comparison: base **5/5 passed**, longest stopped-with-target intervals **2.006, 2.808, 2.016, 1.914, 1.908 s**. Changed code **5/5 passed**, with intervals **2.118, 2.017, 1.908, 1.615, 2.016 s**. The unchanged assertion is five seconds; no baseline or changed-code parking failure occurred. [Parking samples](SC3_assets/parking-summary.json) preserve combat and arrival observations.

No whole Live/Perf tier or rig/221/200v200 run was made. AOT publish was not triggered because neither `src/Allegiance.Server` nor `src/Allegiance.Core` changed.

The local showcase uses a generated SingleRing map, 15 bots per side, civ 35 on both sides, rendezvous sector 1, research mode, research time scale 64, income multiplier 10000, and ordinary legal purchases. Both test-harness unlocks and ignored tech gates stay disabled. The finale is configured for three bombers per side after two minutes and at least one observed kill, with a zero relaunch-share threshold. Ordinary bomber-role siege flight can supply the witness before that finale dispatches. The proof ticker calls `TickSim` every 5 ms; simulated time advances faster than wall time. These are local proof settings, not changes to production defaults. The test observes SC-1's existing server counters and simulation emitter states and never sets bot controls or ship equipment directly.

The first setup attempt was rejected by the server's configuration limits before play (income 50000 and research scale 256). A second setup omitted the rendezvous and was stopped at roughly four minutes: its combat fleets remained at home, so the kill-triggered finale stayed armed. A third mixed-civ 1/35 run reached 4 cloak activations on side 0 and 240 on side 1 before game over, below the unchanged floor of five on side 0. A catalogue diagnostic confirms that civ 1 first buys a cloak-bearing bomber at generated-plan step 147, versus step 30 for civ 35. The final proof therefore mirrors civ 35. None of these preliminary attempts is qualification evidence. Their diagnostics are retained with the raw evidence.

Raw TRX, logs and progress samples are retained in [the ignored evidence directory](/home/domingo/allegiance-codex-sc3/.verify-sc3-evidence/). The sandbox permits writes only to this worktree and `/tmp`; attempts to create `/home/domingo/allegiance-scratch/SC3` and `/home/domingo/allegiance-evidence/SC3` both failed with “Read-only file system.” Consequently this task used ignored workspace scratch/evidence directories and did not put scratch in `/tmp`. Temporary scratch and generated project build outputs were deleted after validation; only the report-linked evidence remains. Passing live snapshots under `qualified-live/` and parking snapshots under `qualified-parking/` are authoritative; preliminary progress samples are not qualification results. No commits or pushes were made.
