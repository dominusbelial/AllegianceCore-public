# P787a (PAR-6a) — missiles, chaff and dispensers reload from cargo, with the native reload/depleted cues

Status: **implemented end-to-end** in the worktree `allegiance-hermes-p787a` (branch `pi/p787a`); **not committed, not pushed** (left in the working tree per the pi-lane rules). Base = `master` at dispatch (`6ef6d9bf` era; worktree head `058fe07f31e`). Native reference: `~/Development/Allegiance/src/`.

---

## Summary

Before this change the port's reload machinery (`Ship.TryApplyReloadEntry`, the server half of `FM_CS_RELOAD(129)`) already knew how to **apply** a launcher reload (swap/terminate/move, `FedSrv.CPP:6593-6640`), but nothing ever **triggered** it for launchers: `IIgcSite.Reload` returned `false` for every type except `Weapon`/`Afterburner` (Ship.cs reload dispatch, `shipIGC.h:603-616/636-650` analogues), so a missile magazine / chaff launcher / mine-probe dispenser ran dry and never refilled. Missiles, chaff and dispensers are now auto-reloaded from the ship's own cargo at the **native trigger** — the fire that empties the launcher — with native's exact selection rules, and the existing native cues fire (reloading **and** depleted) on the pure-renderer client.

Port adaptation (stated, as the brief requires): native runs the launcher reload on the **client** (`pship == m_ship || !connected`, `clintlib.cpp:3429`) and sends the chosen `FM_CS_RELOAD` entries to the server. This port's client is a pure renderer and the server is the sole sim authority, so the selection + application runs **server-side** at the same trigger, and the outcome rides the **existing** wires (`RELOAD(129)` on success, `DROP_PART(33)` on failure) — no new message type.

---

## 1. The native mechanism (verified)

### The trigger — fire empties a launcher → `Reload(...)`

| launcher | native site | cite |
|---|---|---|
| missile magazine | after `SetAmount(iNumRemainingMissiles)`; `0 == remaining` → `Reload(pShip, pMagazine, ET_Magazine)` | clintlib.cpp:3206-3228 |
| chaff / mine / probe | after `SetAmount(amount)`; `0 == amount` → `Reload(pShip, pDispenser, pet->GetEquipmentType())` | clintlib.cpp:3380-3422 |

On **success** the client plays `salReloadingMissilesSound` (missiles) / `salReloadingChaffSound` / `salReloadingDispenserSound` + `startReloadSound` + `PostText` ("Reloading missiles/chaff/dispenser..."); on **failure** it plays `salMissilesDepletedSound` / `salChaffDepletedSound` / `salDispenserEmptySound` ("…depleted." / "Dispenser empty.") — all gated to `pShip == m_ship` (clintlib.cpp:3215-3227, 3389-3421).

### The reload selection — `BaseClient::Reload` ET_Magazine/ET_Dispenser/ET_ChaffLauncher branch (clintlib.cpp:3560-3667)

```
ammo   = plauncher->GetAmount()              // 0 right after the emptying fire
maxAmmo= launcherType->GetAmount(pship)      // hull capacity for that launcher type
while (ammo < maxAmmo):
  scan cargo mount -1..-c_maxCargo for p where p.GetEquipmentType()==type
        and pht->CanMount(p->GetPartType(), 0):
    if pparttype == NULL:                    // empty launcher, nothing same-type found
       reloads[] = {NA, mount}              // pick ANY mountable launcher ("one will pick anything", :3599-3620)
       plauncher->Terminate()               // drop the empty one
       p->SetMountID(0); plauncher = p; ammo = p->GetAmount(); maxAmmo = …
       if ammo==maxAmmo: break
    else if ppt == pparttype:               // same part type → top-up source
       plauncherReload = p; amount = p->GetAmount()
  if plauncherReload:                        // top-up (:3632-3655)
     transfer = (ammo+amount<=maxAmmo) ? amount (Terminate cargo) : maxAmmo-ammo (leave remainder)
     plauncher->SetAmount(ammo+=transfer); SetMountedFraction(0); ResetTimeLoaded()
     reloads[] = {transfer, mount}
  else if pparttype && ammo==0: pparttype=NULL; maxAmmo=0x7fff   // retry less picky (:3656-3664)
  else break
```
Cap: `c_MaxReloads = 8` (clintlib.cpp:3431). After the loop, connected → send `FM_CS_RELOAD` with the entries; if **no** entries and the launcher is still empty → send `FM_CS_DROP_PART` and `plauncher->Terminate()` (clintlib.cpp:3672-3696).

### Server applies the relayed entries — `FedSrv.CPP:6504-6653`

The per-entry applicator (`FedSrv.CPP:6529-6640`): a launcher `NA` entry terminates the empty equipped launcher and moves the cargo launcher to mount 0 (`FedSrv.CPP:6593-6640`); a positive-transfer entry tops up the equipped same-type launcher and resets its mounted fraction (`FedSrv.CPP:6637`). This is exactly the port's `Ship.TryApplyReloadEntry`.

---

## 2. Port before/after, and how data reaches the client

| row | native rule (cite) | port BEFORE | port AFTER | data to client |
|---|---|---|---|---|
| Missile reload | `FireMissile` 0-remaining → `Reload(ET_Magazine)` (clintlib.cpp:3206-3228); selection :3560-3667 | `TryFireMissile` decremented the magazine and stopped (Ship.cs:4501-4506); no launcher reload trigger. | `TryFireMissile` (Ship.cs:4506) fires `TryAutoReloadLauncher(now, Magazine)` on the empty edge; it selects/transfers and raises `AutoReloadTriggered` → server relays **RELOAD(129)** (Server.Combat.BroadcastAutoReload, existing). | existing `FM_S_RELOAD(129)`; client `RecordReloadFeedback` (ClientWorld.cs) classifies the cargo entry's part type → `SalReloadingMissiles` + `StartReload` (existing P737 path, now actually fed for launchers). |
| Chaff / dispenser reload | `FireExpendable` 0-amount → `Reload(ET_Chaff/Dispenser)` (clintlib.cpp:3380-3422); selection :3560-3667 | `FireDispenser` decremented the launcher and stopped (Ship.Equipment.cs:101-106). | `FireDispenser` (Ship.Equipment.cs:106) fires `TryAutoReloadLauncher(now, part.EquipmentType)` on the empty edge → `AutoReloadTriggered` → **RELOAD(129)**. | existing `FM_S_RELOAD(129)` → `SalReloadingChaff` / `SalReloadingDispenser` + `StartReload`. |
| Empty launcher picks any mountable part | clintlib.cpp:3599-3620 | (no launcher reload) | `TryAutoReloadLauncher` NA-swaps the first mountable same-equipment-type cargo launcher to mount 0, dropping the empty one. | `RELOAD(129)` NA entry; client `ApplyReloadInventory` (ClientWorld.FlightParts.cs:44-50) moves cargo→mount 0 and removes the old empty launcher. |
| **Depleted (reload-FAILURE) cue + native drop** | `else if (plauncher && plauncher->GetAmount()==0)` → `FM_CS_DROP_PART` + `Terminate` (clintlib.cpp:3685-3696); cue at fire-time failure (clintlib.cpp:3223-3226, 3407-3421) | no launcher reload → no depletion edge; SIM-0445/0452/0453 stuck `partial`. | `TryAutoReloadLauncher` finds no spare → removes the empty launcher (`_parts.Remove` + `BumpLoadout`, no treasure) and raises `LauncherReloadFailed(et, mount)`; `Server.Combat.BroadcastLauncherDepleted` (Server.Combat.cs:584) relays **DROP_PART(33)** sector-wide. | existing `FM_S_DROP_PART(33)`; the own-ship client infers the depletion edge from its empty launcher disappearing (ClientWorld.FlightParts.Apply(DropPart):81-111) → `SalMissilesDepleted` / `SalChaffDepleted` / `SalDispenserEmpty` (native VO, bundled). **Deviations (stated):** the cue fires on the observed drop rather than at the fire instant (native's `pShip == m_ship` fire-time play); a manual drop of an *empty* launcher also cues (rare; same observable edge). |

Constructibility: the launcher family (missile/chaff/mine/probe) was already mountable — `DataPartTypeIGC.{Missile,Mine,Chaff,Probe}Launcher` are construction-only subtypes bridged by `From{Missile,Mine,Chaff,Probe}Launcher` (DataPartTypeIGC.cs:162-358); `IsLauncherType` already enumerated magazine/dispenser/chaff (Ship.cs:3676-3678). The stale "only ChaffLauncher is constructible" remark in `TryApplyReloadEntry` (Ship.cs:3294-3295) predates P224/P226/P433/P435 and is superseded — all four are constructible/mountable, so **no constructibility change was needed**.

---

## 3. Implementation (files)

- **`src/Allegiance.Core/Sim/Ship.cs`**
  - `TryAutoReloadLauncher(double now, EquipmentType et)` (Ship.cs:3688) — the server-side port of `BaseClient::Reload`'s launcher branch (clintlib.cpp:3560-3667): empty-launcher "pick anything" NA swap, same-type top-up, `c_MaxReloads=8`, and application through the **existing** P192 per-entry applicator `TryApplyReloadEntry` (which resets the mounted fraction / load deadline, FedSrv.CPP:6637). Success → `AutoReloadTriggered(entries, true)`; failure → remove empty launcher + `LauncherReloadFailed`.
  - `LauncherCapacity` (Ship.cs:3817) = `ClampPartAmount(hull, core, type, int.MaxValue)` (native `GetAmount(pship)`, clintlib.cpp:3571).
  - `LastLauncherAutoReloadOutcome` / `LastLauncherAutoReloadAt` (Ship.cs:3807) for observability/tests.
  - Triggers: `TryFireMissile` (Ship.cs:4506) and `FireDispenser` (Ship.Equipment.cs:106) on the exact empty edge. Both are the shared authoritative fire paths, so **players and bots** both benefit.
- **`src/Allegiance.Core/Sim/IIgcSite.cs:612`** — `LauncherReloadFailed(pship, et, mount)` no-op seam; mirrored in `RecordingIgcSite.cs`.
- **`src/Allegiance.Server/ServerHost.cs:657-663`** — `LauncherDepleted` hook + `LauncherReloadFailed` impl.
- **`src/Allegiance.Server/Server.Combat.cs:584`** — `BroadcastLauncherDepleted`: relays `DROP_PART(33)` sector-wide (no treasure — native empty-launcher guard, FedSrv.CPP:6323).
- **`src/Allegiance.Server/Server.Lifecycle.cs:812`** — wires `_host.LauncherDepleted = BroadcastLauncherDepleted`.
- **Client (pure renderer), depleted cue only** — success cues were already wired (P737).
  - `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — new `SalMissilesDepleted`, `SalChaffDepleted`, `SalDispenserEmpty`.
  - `src/Allegiance.ClientCore/State/ClientWorld.FlightParts.cs:75-112` — `Apply(DropPart)` records the type-specific depleted cue when the OWN ship's empty launcher is dropped.
  - `src/Allegiance.ClientCore.Game/Game1.Audio.cs` + `Audio/SoundManager.cs` — three new one-shot players, loads, disposals. Waves `vo_sal_missilesdepleted/chaffdepleted/dispenserempty.ogg` were already bundled (Content/Sounds + Content.mgcb) — **no new media added**.

---

## 4. Open parity rows updated (from the dispatch matrix)

| row | native | previous (P737) | now | evidence |
|---|---|---|---|---|
| SIM-0445 `salMissilesDepletedSound` (clintlib.cpp:3224) | Launcher empty + Reload fails | partial (no launcher auto-reload / no wire) | **implemented** | server drop + DROP_PART(33) relay + own-ship client cue; see §2 |
| SIM-0452 `salChaffDepletedSound` (clintlib.cpp:3412) | Chaff emptied + Reload fails | partial | **implemented** | same |
| SIM-0453 `salDispenserEmptySound` (clintlib.cpp:3417) | Dispenser emptied + Reload fails | partial | **implemented** | same |
| SIM-0239 `mountedSound` (magazineigc.cpp:96) | Mount-complete edge | partial | **stays partial** | no change: mounting progress is not streamed (parts arrive fully mounted, P626); the reload *success* now streams (RELOAD(129)) but not mount-progress, so the mount-complete cue still needs a loadout/mount-progress broadcast (gap documented in matrix). No code change this wave. |
| SIM-0267 `salCargoFullSound` (shipIGC.h:1302) | Pickup can't fit | partial | **stays partial** | no change: no pickup-rejection wire reaches the client (native fires server-side on a failed pickup); would need a new server signal. Gap documented in matrix. |

Matrix: `docs/parity/sim-rules-matrix.csv` rows SIM-0445/0452/0453 → `implemented` with the P787a evidence. `scripts/parity-scorecard.py` validates both matrices (**no errors**).

---

## 5. Bots (scope 5)

No bot-code change was needed or made. The reload triggers inside `Ship.TryFireMissile` / `Ship.FireDispenser` — the same authoritative sim fire functions bots use — so missile/chaff bots refill automatically.

Quantified (sim-level, `P787aLauncherReloadTests.Bomber_with_cargo_spare_sustains_four_launches_versus_one_on_master`): a bomber with a 1-round magazine + a 3-round spare launcher in cargo fires **4** missiles before running dry (1 initial + the 3-round spare topped up), then hits the native depletion-drop. On master (no launcher reload) the same bomber fired **once** and dropped its empty launcher — a **4×** increase in sustained missiles-per-bomber from cargo alone. The NPC/bot fire path is asserted directly in `Bot_npc_path_missile_fire_also_triggers_launcher_reload` (clientRequest == false, the magazineigc.cpp:85-120 load-ready gate). A full live A/B of bombers-per-mission (before/after) is the Live-tier confirmation the mini test runner carries per merged batch, per the pi-lane rules.

---

## 6. Tests

New (both classes pass **3× alone**):

- `tests/Allegiance.Core.Tests/P787aLauncherReloadTests.cs` — **8 tests**, all `⭐ MUST-FAIL-IF-REMOVED` (fail on master: the launcher never reloads):
  1. missile top-up from same-type cargo spare → Reloaded + `AutoReloadTriggered` + cargo consumed
  2. missile depleted (no spare) → empty launcher dropped + `LauncherReloadFailed`
  3. empty missile launcher picks a *different* mountable launcher type (NA swap)
  4. chaff top-up → Reloaded
  5. mine dispenser top-up → Reloaded
  6. chaff depleted (no spare) → dropped + `LauncherReloadFailed`
  7. bot (NPC) path missile fire also reloads
  8. bomber with spare sustains 4 launches vs 1 on master (bots quantification)
- `tests/Allegiance.ClientCore.Tests/P787aClientDepletedCueTests.cs` — **5 tests**: own empty missile/chaff/dispenser DROP_PART → `SalMissilesDepleted` / `SalChaffDepleted` / `SalDispenserEmpty`; own *loaded* launcher drop → no cue; remote empty launcher drop → no cue.

Affected (re-run clean): `CargoBayReloadTests`, `AutoReloadOnEmptyTests`, `P764ReloadSiteTests`, `FuelPoolAndAfterburnerTests`, `MissileFireTests`, `P641FlightPartsSimTests`, `P651EquipmentRulesTests`, `P767WingmanAmmoTests` (Core, 82 pass), `ClientWorldSoundEventTests`, `P626InFlightUtilityLiveTests`, `P720EquipmentSwapTests` (ClientCore, 95 pass), `P737SoundAssetTests` (MonoGame, extended to also assert the three depleted waves are bundled — 1 pass).

Two existing tests were **updated** to the native-faithful depletion-drop (they previously asserted the pre-P787a "empty launcher lingers at amount 0" shape, which contradicts native's `FM_CS_DROP_PART` + `Terminate`, clintlib.cpp:3685-3695):
- `P692EquipmentLiveTests.Held_dispenser_input_uses_production_callback_and_delivers_real_deployment` (chaff/mine/probe): now asserts a 1-round launcher fired dry with no spare is **dropped** (gone from `Parts`) rather than parked at 0.
- `DispenserEquipFireLiveTests.BuyMountAndFireARealDispenser_Live` (MonoGame): the probe case (real 1-round reserve) now asserts the launcher is dropped after the emptying fire; larger-reserve kinds still assert `ammoBefore - 1`.
Both passed 3/3 alone after the update. `Hud12bReviewFixTests` flaked once in the diagnostic fast run (allocation-threshold renderer test, unrelated) and passed 3/3 alone + in the clean fast PASS — logged in `docs/TEST_FLAKES.md`.

---

## 7. Verification / gates

- Whole-solution build: `dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors** (also Debug).
- `tools/build-tools.sh` → **all 72 tool projects built** (0 fail).
- AOT publish (Ship.cs + Server changed): `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true` → **succeeds, no IL2026/IL3050** (published to a scratch dir).
- New tests **3× alone**: Core 8/8 ×3, ClientCore 5/5 ×3.
- Scorecard: `python3 scripts/parity-scorecard.py` → both matrices **validated**, 0 errors; SIM-0445/0452/0453 now count `implemented`.
- One Fast-tier run (`tools/run-tests.sh --tier fast`): **PASS — 10,142 tests, 0 failed, exit 0** (Lobby 24, MonoGame 3278+3 NotExecuted, ClientCore 1739, Core 5086; quarantines clean). A first diagnostic run surfaced two legitimately-affected tests (see §6: they asserted the pre-native "launcher lingers at amount 0" shape and were updated to the P787a drop) plus one documented load flake (`Hud12bReviewFixTests`, logged in `docs/TEST_FLAKES.md`); the clean PASS is the re-run with those fixes in the tree.
- Showcase never-parks 5× (load-sensitive, bots/flight affected): the regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` is a Live-tier run the mini test runner carries per merged batch (pi-lane rules §"Live/perf tiers run on Claude's mini test runner"). The change is additive (a reload trigger + client cue); it cannot alter bot flight paths beyond enabling a fire→reload cycle, so no flight regression is expected — this is noted for the runner rather than re-run locally on the shared box to avoid the documented simultaneous-suite hangs.

---

## 8. Files changed (working tree, uncommitted)

- `src/Allegiance.Core/Sim/Ship.cs` (+`TryAutoReloadLauncher`, `LauncherCapacity`, outcome props, `TryFireMissile` trigger)
- `src/Allegiance.Core/Sim/Ship.Equipment.cs` (`FireDispenser` trigger)
- `src/Allegiance.Core/Sim/IIgcSite.cs` (`LauncherReloadFailed`)
- `src/Allegiance.Core/Sim/RecordingIgcSite.cs` (forwarding override)
- `src/Allegiance.Server/ServerHost.cs`, `Server.Combat.cs`, `Server.Lifecycle.cs` (hook + DROP_PART relay + wiring)
- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs`, `ClientWorld.FlightParts.cs`
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs`, `Audio/SoundManager.cs`
- `tests/Allegiance.Core.Tests/P787aLauncherReloadTests.cs` (new), `tests/Allegiance.ClientCore.Tests/P787aClientDepletedCueTests.cs` (new), `tests/Allegiance.ClientCore.MonoGame.Tests/P737SoundAssetTests.cs` (+3 waves)
- `tests/Allegiance.ClientCore.Tests/P692EquipmentLiveTests.cs`, `tests/Allegiance.ClientCore.MonoGame.Tests/DispenserEquipFireLiveTests.cs` (updated to the native depletion-drop)
- `docs/parity/sim-rules-matrix.csv` (3 rows → implemented), `docs/TEST_FLAKES.md` (Hud12b flake logged)
- `docs/reports/P787A_LAUNCHER_RELOAD.md` (this report)

Nothing committed, nothing pushed. Scratch (`AOT publish`, matrix scripts) lived under the ignored `.scratch/` and was deleted after verification; the Fast-tier runner's `TestResults/` (gitignored) holds the gate summary.
