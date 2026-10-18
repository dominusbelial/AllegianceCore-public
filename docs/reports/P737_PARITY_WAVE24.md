# P737 — Parity wave 24: simulation SOUNDS, twelfth bundle (own-ship reload / loadout feedback)

## Summary

Ported **9 open `sounds` rows → `implemented`** — the **own-ship reload /
loadout-feedback family** (weapons/ordnance): **SIM-0263** salReloadingAmmoSound,
**SIM-0264** startReloadSound, **SIM-0265** salReloadingFuelSound, **SIM-0266**
startReloadSound, **SIM-0443** salReloadingMissilesSound, **SIM-0444**
startReloadSound, **SIM-0449** salReloadingChaffSound, **SIM-0450**
salReloadingDispenserSound, **SIM-0451** startReloadSound. **No server change**
was required: the port's server already broadcasts a **SUCCESSFUL reload** as the
own ship's `FM_S_RELOAD(129)` relay (the applied cargo→pool transfers,
sector-wide; failures are never relayed). The client classifies each relayed
cargo slot's part type (exactly mirroring `Ship.TryApplyReloadEntry`) to pick the
native type-specific cue, plus the generic `startReloadSound`, and plays them on
the established `ClientSoundEvent → Game1.Audio → SoundManager` one-shot path.
Five real assets (`vo_sal_reloading{ammo,fuel,missiles,chaff,dispenser}.ogg`)
are bundled from `~/Development/Artwork` + registered in `Content.mgcb`; the
generic `startReloadSound` reuses the already-bundled `mount.ogg` (native
maps both `mountSound` and `startReloadSound` to the `mount` wave,
sounddef.mdl:286/289).

Six adjacent ordnance/loadout rows that **cannot yet be ported faithfully** are
**documented, not silently skipped**: the **depleted (reload-failure) cues**
SIM-0445/0452/0453 (missile/chaff/dispenser), SIM-0446 (chaff deploy, P717),
SIM-0267 (cargo full) and SIM-0239 (mounted) — each now carries a concrete
reason (see Limitations).

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 425/592 (71.79%) | **443/592 (74.83%)** |
| unweighted | 155/227 (68.28%) | **164/227 (72.25%)** |

## Why these rows

Reconnaissance of the 72 open `sounds` rows and of waves 13–23 shows the
largest remaining asset-present cluster in the **weapons/ordnance** bucket is
the **reload / deploy-budget feedback family** (native's `BaseClient::FireMissile`
/ `FireExpendable` and `CshipIGC::SetAmmo`/`SetFuel` reload regions). All nine
implemented rows share **one code path by construction** — the own ship's
`FM_S_RELOAD(129)` relay, classified by cargo-slot part type — and are fully
testable at the `ClientWorld` wire level. The task's other listed families were
assessed and ruled out for this wave: **station/economy** completion/need-rock
rides the SIM-0399 core sound-def table (Codex P732 owns the voice-over table);
**UI/screens** mouseover/click cues are GUI-layer (not wire/world testable per
this task's verification); **ambient/positional loops** are partly wired and
mostly blocked on model/sector asset specifics; the same-side boot/quit
**salQuitSound** rows need server quit-reason broadcasts (documented P736). This
family is the coherent, feasible next slice.

## Implementation

### The wire trigger (already broadcast — no server change)

Native plays the reload cues when a fire empties a pool and a spare pack
auto-reloads from cargo (a **success**):

| native trigger | cite |
|---|---|
| ammo: `CshipIGC::SetAmmo` zero-transition → `IIgcSite::Reload(ET_Weapon)` | shipIGC.h:605-624 |
| fuel: `CshipIGC::SetFuel` zero-transition → `Reload(ET_Afterburner)` | shipIGC.h:631-651 |
| missiles: `FireMissile` empty magazine → `Reload(ET_Magazine)` | clintlib.cpp:3216-3225 |
| chaff / dispenser: `FireExpendable` empty → `Reload(ET_ChaffDispenser…)` | clintlib.cpp:3394-3417 |

The port's server applies the reload and, on **success**, broadcasts the applied
`ReloadData` entries sector-wide as `FM_S_RELOAD(129)` with `ShipID` filled
(`Server.Combat.BroadcastAutoReload`, Server.Combat.cs:488-508). This pure-
renderer client observes the **own ship's** relay (gate `reload.ShipID ==
MyShipId`, native's `pShip == m_ship` clintlib.cpp:3217/3397) and classifies each
entry's **negative cargo mount** → cargo `ExpandedPartData.PartId` →
`DataPartTypeIGC` via `PartTypeResolver`, exactly as `Ship.TryApplyReloadEntry`
(Ship.cs:3113-3173) classifies:

| cargo part | cue recorded | row(s) |
|---|---|---|
| `Pack(PackType=0)` (c_packAmmo) | SalReloadingAmmo + StartReload | SIM-0263, 0264 |
| `Pack(PackType=1)` (c_packFuel) | SalReloadingFuel + StartReload | SIM-0265, 0266 |
| `MissileLauncher` (Magazine) | SalReloadingMissiles + StartReload | SIM-0443, 0444 |
| `ChaffLauncher` | SalReloadingChaff + StartReload | SIM-0449, 0451 |
| `MineLauncher` / `ProbeLauncher` (Dispenser) | SalReloadingDispenser + StartReload | SIM-0450, 0451 |
| anything else (falls back) | StartReload only | generic |

`Game1.Audio` dispatches the six new kinds; `SoundManager` plays the five
type-specific VOs on the `Warning` path (like every other sal) and
`startReloadSound` as an action SFX reusing the already-loaded `mount` wave.

### Audio plumbing

Six new `ClientSoundEventKind` members, five `SoundManager` one-shot players +
loads + disposals (waves `vo_sal_reloading{ammo,fuel,missiles,chaff,dispenser}`),
a `PlayStartReload()` reusing `_mount`, six `Game1.Audio` dispatch cases each with
the `[monogame] audio:` console line, and five `Content.mgcb` registrations.

## Failing-first

The six new event kinds did not exist before P737: the new
`ClientWorldSoundEventTests` reference `ClientSoundEventKind.SalReloadingAmmo`
etc., which is a pre-change compile error (CS0117) — the same failing-first
mechanism P711/P716/P717/P736 documented. `P737SoundAssetTests` likewise fails
pre-change because none of the five `vo_sal_reloading*` waves was bundled (all
five were confirmed absent from `Content/Sounds` before this wave).

## Tests

- `ClientWorldSoundEventTests` (Allegiance.ClientCore.Tests) — **8 new** reload
  cases (ammo/fuel/missile/chaff/dispenser type-specific + generic
  startReloadSound, other-ship silence, non-reloadable fallback, absent-slot
  silence) + the existing suite. **67/67**.
- `P737SoundAssetTests` (Allegiance.ClientCore.MonoGame.Tests, new) — the five
  waves present in `Content/Sounds` + registered in `Content.mgcb`. **1/1**.

New/affected tests **3× clean** (67/67 ×3, 1/1 ×3).

Targeted existing tests **3× clean**: `WeaponReloadTrackerTests`, `P736/P735/P723
SoundAssetTests`, `P714AnnounceSoundTests`, `P719StationEconomySoundTests`,
`P717DeploySoundTests`, `P721ThrusterLoopSoundTests` (MonoGame, 14/14 ×3), plus
the Core reload-sim classes `AutoReloadOnEmptyTests` + `CargoBayReloadTests`
(13/13 ×3).

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0
  errors** in both Debug and Release.
- Failing-first: see above (six new kinds are pre-change compile errors; five
  new waves fail the asset test pre-change).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **5/5 pass (Release)** — 1m37s, 1m33s, 1m37s, 1m36s, 1m34s. The change records
  client-side one-shot sound events only; it cannot alter bot flight.
- **Full suite (`scripts/run-tests.sh`), exactly 1 run** (the lighter gate for
  this task), run as a single tracked pass over all four assemblies sequentially
  (MAIN lane = `Lane!=LiveTiming`, then each project's QUARANTINE lane):

  | pass | result |
  |---|---|
  | Lobby MAIN | passed, 0 fail |
  | MonoGame MAIN | passed, 0 fail |
  | ClientCore MAIN | 1197/1198 (1 skip: pre-existing `P660ShowcaseLiveTests` finale-rearm `SkippableFact`), **0 fail** |
  | Core MAIN | 4757/4757, 0 fail |
  | Quarantine | ClientCore 3/3; Core 18/18; Lobby & MonoGame none |

  **ALL TEST PROJECTS PASSED (main clean + quarantine cleared)**, exit 0 — a
  fully clean single run. No test was re-run hoping for a clean result; the
  numbers above are the single measured pass.

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — six new reload kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `RecordReloadFeedback`
  (SIM-0263/0264/0265/0266/0443/0444/0449/0450/0451) in `Apply(Reload)`.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — six dispatch cases.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — five fields + loads +
  one-shot players + disposals; `PlayStartReload()` reuses `_mount`.
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/vo_sal_reloading{ammo,fuel,
  missiles,chaff,dispenser}.ogg` (new, from Artwork) + `Content.mgcb`.
- `tests/.../ClientWorldSoundEventTests.cs` (+8),
  `tests/.../P737SoundAssetTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 9 rows → `implemented` (evidence
  `verified`); the blocked ordnance/loadout rows (SIM-0445/0446/0452/0453/0267/
  0239) annotated `partial` with the concrete reason; **LF endings kept**.
- `docs/reports/P737_PARITY_WAVE24.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| ammo reload success cue | shipIGC.h:616 | design | own-ship RELOAD(129) ammo-pack entry → SalReloadingAmmo |
| fuel reload success cue | shipIGC.h:646 | design | own-ship RELOAD(129) fuel-pack entry → SalReloadingFuel |
| missile reload success cue | clintlib.cpp:3221 | design | own-ship RELOAD(129) Magazine pack → SalReloadingMissiles |
| chaff / dispenser reload cue | clintlib.cpp:3400-3404 | design | own-ship RELOAD(129) ChaffLauncher / Dispenser pack → SalReloadingChaff / SalReloadingDispenser |
| generic startReloadSound (every reload) | clintlib.cpp:3219,3404; shipIGC.h:617,647 | design | StartReload on every own successful reload; wave `mount` (sounddef.mdl:289) |
| reload-FAILURE (depleted) cues | clintlib.cpp:3224,3412,3417 | design (trigger not reachable) | not ported: no per-magazine pool on the wire + no launcher auto-reload in the sim (documented) |

## Modern alternative considered

Native plays these as `PlayNotificationSound`/`PlaySoundEffect` from inside the
client's own IGC magazine objects as it fires; this pure-renderer client instead
uses the **authoritative success relay** the port's server already broadcasts
(`FM_S_RELOAD(129)`) and classifies by cargo part type — zero new wire and no
client-side physics/magazine sim. This is a cleaner signal than inferring
depletion from streamed fractions (which can't distinguish ammo/fuel from
missiles/chaff, and which the port doesn't stream for launchers). One honest
deviation: native's **type-specific missile/chaff/dispenser** cues fire on the
fire-emptied **auto-reload**; the port's sim auto-reloads only ammo and fuel
(no launcher magazine auto-reload), so those three fire on the same relay whether
it came from an auto or a manual reload — the player-visible intent ("your
system reloaded → hear its cue") is preserved, and a server launcher-magazine
auto-reload is noted as the ideal follow-up. No audio-engine change warranted;
the five VOs are cockpit-range on the existing `MutexSal` voice-budget path and
`startReloadSound` reuses the already-bundled `mount` SFX.

## Limitations

- **SIM-0445 / SIM-0452 / SIM-0453** (missile/chaff/dispenser **depleted** —
  reload-FAILURE cues) remain `partial`: the port's sim has no launcher magazine
  auto-reload and the client receives no per-magazine pool amounts, so the
  reload-failed edge cannot be observed client-side. Needs a server launcher
  magazine + auto-reload + a depleted signal (documented in the matrix).
- **SIM-0446** (chaff deploy) remains `partial` (P717): the `CREATE_CHAFF(109)`
  broadcast carries no launcher id, so the native own-deploy gate is undecidable.
- **SIM-0267** (cargo full) remains `partial`: no pickup-rejection wire reaches
  the client (native fires server-side shipIGC.h:1302 on a failed pickup).
- **SIM-0239** (mounted) remains `partial`: mounting progress is not streamed
  (parts arrive fully mounted, P626), so the mount-complete edge is not
  observable client-side.
- The larger open families (station completion/need-rock — SIM-0399 voice-over
  table, Codex P732; UI/screens; ambient/positional loops; same-side boot/quit
  salQuitSound) remain open **with reasons** documented in the matrix and prior
  waves; none was silently skipped.

## P738 merge onto master

Merge target: **`master`**, containing P735, P736 and P737. Resolved the eight
marked blocks in five files, plus the automatically duplicated departure
handlers in `ClientWorld` and `ConnectionSession`. No git commands, staging,
commit or push; merge metadata remains byte-for-byte unchanged.

### Resolution

- **Reload:** preserve P726's counter and authoritative inventory transfer,
  while P737 records the local type-specific cue and generic reload sound
  **before** that transfer consumes/moves the source cargo. Strengthened the
  ammo test to require exactly two cues, consumed cargo, incremented counter,
  and silence on a subsequent relay with an absent source slot. Native cargo
  consumption: `~/Development/Allegiance/src/clintlib/appmsg.cpp:1468-1566`;
  own-ship reload cues:
  `~/Development/Allegiance/src/clintlib/clintlib.cpp:3216-3225,3394-3417` and
  `~/Development/Allegiance/src/Igc/shipIGC.h:605-650`.
- **Presence:** use P718/P722 cleanup and session shutdown, with P736 cue
  classification before mutation and the actual mission-departure reason.
  Strengthened tests verify both cleanup and a single cue across repeated
  departures. P736's appended notes explain the native fallback deviation and
  the now-outdated server-broadcast blocker in the original report.
- **SoundManager:** retain every cue and P732's serialized-template
  registration through `LoadSound`. Preserve the P732 native enum ID 1342 in
  the P652 test instead of reverting to the older incoming 341 assertion;
  native enum basis: `~/Development/Allegiance/src/Igc/igc.h:456-464` and
  `~/Development/Allegiance/src/Igc/sounds.h:395`.
- **Content:** union both MGCB tables and remove nine duplicate registrations.
  Audit confirms **622 unique build entries**, **51 unique event/playback
  pairs**, and all **12 incoming waves compiled** to Release `.xnb` files.
- **Documents:** compare all parity/wiring matrices by row against the common
  base. Across differing rows, 53 take ours and 31 take incoming; no row was
  independently changed on both branches. Every matrix has LF endings. All
  pre-existing nonempty flake-report lines from both sides survive. Earlier
  sound-wave reports remain unchanged; incoming reports are append-only.

### Scorecard

`scripts/parity-scorecard.py` validates both matrices; full output is
`P738_master_assets/scorecard.txt`.

| matrix/area | weighted | complete/eligible |
|---|---|---|
| parity total | **1399/1469 (95.23%)** | **382/406 (94.09%)** |
| simulation total | **935/1331 (70.25%)** | **296/436 (67.89%)** |
| sounds | **445/592 (75.17%)** | **165/227 (72.69%)** |

Before integration, ours had 406/592 sounds credit and 148/227 complete rows;
incoming had 443/592 and 164/227. The result preserves both branches' gains.

### Measured gate

- Whole-solution Release build: **0 warnings, 0 errors**.
- Incoming/affected tests: **223 methods ×3 fresh isolated processes
  (670 invocations; 960 passing parameter cases,
  1 initial timing failure, 0 skipped; every method
  has three passes after its last failure)**. Coverage includes P735–P737
  world/assets, P652/P725 AI/wire, reload simulation/tracking, native voice
  catalog and queue, prior sound assets/alarms, P718/P722 interface receive and
  real loopbacks, flight inventory receive and parity validation.
- Showcase never-parks: **5/5 passed**, 0 failed,
  0 skipped.
- Core, ClientCore and MonoGame assemblies run once each after the build,
  including ordinary live/loopback tests and LiveTiming tests. Two repository
  guards (`MeshyGuardTests`, `PrivatePathGuardTests`) are excluded because they
  launch git, honoring the explicit prohibition. Rig-only opt-in harnesses
  remain disabled; corpus-backed tests use `local.env`. An equivalent read-only
  index scan found no private path leaks. The broad model-location guard also
  flags two manifest documents (`MODELS_MANIFEST_P287.json`/`.md`) already
  identical in the base and both heads; neither is a model output. Details:
  `P738_master_assets/hygiene-checks.json`.

| suite | passed | failed | skipped |
|---|---:|---:|---:|
| Core | 4780 | 2 | 0 |
| ClientCore | 1266 | 1 | 0 |
| ClientCore.MonoGame | 2420 | 1 | 3 |

- `Allegiance.ClientCore.MonoGame.Tests.MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship`: failed once in the measured gate; **3/3 isolated passes**, recorded as a load flake in `docs/TEST_FLAKES.md`.
- `Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`: failed once in the measured gate; **3/3 isolated passes**, recorded as a load flake in `docs/TEST_FLAKES.md`.
- `Allegiance.Core.Tests.LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it`: failed once in the measured gate; **3/3 isolated passes**, recorded as a load flake in `docs/TEST_FLAKES.md`.
- `Allegiance.Core.Tests.Transport.ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout`: failed once in the measured gate; **3/3 isolated passes**, recorded as a load flake in `docs/TEST_FLAKES.md`.
- `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`: failed once in the measured gate; **3/3 isolated passes**, recorded as a load flake in `docs/TEST_FLAKES.md`.

Commands, timings, counters and failures: `P738_master_assets/verification.json`;
per-case outcomes: `case-results.json`; compact results: `summary.json`.
Complete raw logs/TRX: `validation-logs.tar.gz`; original conflict inputs:
`conflict-inputs.tar.gz`. Row decisions, cue/content audits, source hashes and
merge-metadata fingerprints are retained in the same evidence directory.
