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
