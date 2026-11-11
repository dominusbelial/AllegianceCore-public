# P998 — A dying ship's parts must die with it (no pod, no respawned pod, inherits the dead loadout)

**Task.** P996's review found a pre-existing divergence: the port's kill loot
block (`DropKillShipLoot`, P411) rolled the dead ship's treasures and zeroed
ammo/fuel, but **never removed the parts** — while native `KillShipEvent` walks
the part list, rolls one 1-in-5 treasure per part and `p->Terminate()`s **every**
part before the eject/restart hull swap. P996's capture
(`docs/reports/P996_assets/loadout-after-pod-death-*.png`) shows the result: the
hangar's CURRENT SHIP pane listed the dead **fighter's** guns, shield and
missiles under the pod's `hull: #33`. This task ports the native strip, on every
death path, in the native order, and closes the client half (the native client
terminates the parts itself on EJECT / ENTER_LIFEPOD) without breaking P996's
saved-loadout restore.

This closes the follow-up P996 recorded in `docs/reports/LIFEPOD_RESPAWN_P996.md`
(§5's last row and the review note: "the port keeps the dead ship's parts on the
pod (native terminates them in the kill loot loop, `FedSrv.CPP:11691-11702`);
visible as the pod's 'CURRENT SHIP' list in the loadout capture").

> **Claude review (2026-11-11):** verdict good. Native citations re-grepped (FedSrv.CPP 11634-11763 loot/terminate block,
> shipIGC.cpp:1377 assert and :1428-1437 resets, appmsg.cpp:193-201 EJECT and :1973-1981 ENTER_LIFEPOD terminate loops);
> one off-by-five fixed (native's "ignore own part changes" branch is appmsg.cpp:2023-2024, not :2028). Build 0/0; P998 +
> P996 + P411 + lifepod/oxygen tests 3× alone: Core 52/0/0, ClientCore 10/0/0, MonoGame P996 5/0/0 each run. Screenshots
> viewed: CURRENT SHIP is the bare pod (#33, no parts), PENDING is the restored Adv Fighter; both pod HUDs show no weapons.
> Pre-existing, not P998: the loadout pane's hull-info panel overlaps the hull list at 1280×720 (the proof uses 1920×1080).

---

## 1. Native study (every line re-grepped in `~/Development/Allegiance/src`)

### 1.1 The death loot/terminate block — `FedSrv/FedSrv.CPP:11634-11763`

| step | native | line(s) |
|---|---|---|
| `FedSrvSiteBase::KillShipEvent` head; `pshipParent = pship->GetParentShip()` | — | `11634`, `11641` |
| The whole loot block is inside `if (pshipParent == NULL)` — **root ships only** | — | `11644` |
| First, "if we die, all of our children die": recursive `KillShipEvent(child)` | — | `11678-11685` |
| Then "Dead ship ... create treasures for all of its parts": `while (plink = plist->first())` (removal-safe), `if (randomInt(0,4) == 0) CreateTreasure(..., p, p->GetPartType(), p1, 100.0f, 60.0f)` **and** `p->Terminate()` either way | — | `11686-11696` |
| Ammo: `if (ammo > 0)` → 1-in-5 ammo-pack treasure (dv 100, lifespan 30), then `pship->SetAmmo(0)` | — | `11698-11712` |
| Fuel: ditto with `GetFuelPack()`, `short(fuel)`, then `pship->SetFuel(0.0f)` | — | `11714-11728` |
| Carried flag jettison/respawn | — | `11730-11763` |

After that block the `bKilled` gate decides eject vs true death (`11765-11788`),
and **only then** does the hull swap happen:

* eject: `ShipStatusHullChange(pht)` then `pship->SetBaseHullType(pht)`
  (`11938`, `11942`), positions/spins the pod and sends `FM_S_EJECT` to
  `GetGroupSectorFlying(pcluster)` (`12011-12021`);
* true death: `AnnounceExit(NULL, SDR_KILLED)` (`12025`), `SetCluster(NULL)`
  (`12045`), `SetBaseHullType(pht)` (`12052`), `ShipStatusRestart` (`12057`),
  `ENTER_LIFEPOD` to that player's connection (`12059-12061`),
  `SetStation(pstationDest)` (`12064`), `ShipStatusDocked` (`12065`).

### 1.2 What `IpartIGC::Terminate()` does to the ship

* `IpartIGC` is one flat interface (`Igc/igc.h:3890-3916`); every subtype
  implements `Terminate()` (`CweaponIGC::Terminate` `Igc/weaponIGC.cpp:53`,
  `CshieldIGC::Terminate` `Igc/shieldIGC.cpp:44`, `CafterburnerIGC::Terminate`
  `Igc/afterburnerIGC.cpp:45`, `CcloakIGC` `Igc/cloakigc.cpp:44`,
  `CdispenserIGC` `Igc/dispenserigc.cpp:63`, `CmagazineIGC`
  `Igc/magazineigc.cpp:56`, `CpackIGC` `Igc/packigc.cpp:49`), all of them
  `SetShip(NULL, NA)`-ing (→ `CshipIGC::DeletePart`, `Igc/shipIGC.cpp:1480-1512`,
  which subtracts the part's mass and raises
  `LoadoutChangeEvent(c_lcRemoved)` at `:1510`).
* **Cargo is a part too.** The part list holds equipped mounts *and* the cargo
  bay: `PartLocation` indexes `m_mountedCargos[mountID + c_maxCargo]` for a
  negative mount (`Igc/shipIGC.cpp:1516-1549`), so terminating the list empties
  the hold as well.
* `SetBaseHullType` therefore *asserts* the ship is bare —
  `assert (m_parts.n() == 0)` (`Igc/shipIGC.cpp:1377`) — and resets the scalar
  pools on the same call: `m_fOre = 0; m_fuel = 0; m_ammo = 0;`
  (`:1428-1430`), `m_fraction = 1.0f` (`:1435`), `m_energy = 0.0f` (`:1437`).
  It also `SetMass(hull mass)` (`:1402`), undoing the parts' committed mass.

### 1.3 What the clients are told (the part-removal messages)

The server sends **no loadout message for the strip**:
`LoadoutChangeEvent` on the server side only re-sends an area-effect weapon's
target (`FedSrv/FedSrv.CPP:13530-13545`), and a whole `LOADOUT_CHANGE` is built
only by `CFSShip::QueueLoadoutChange` on a cluster (re-)entry
(`FedSrv/fsship.cpp:511-542`, `:708-735`) or by `ForceLoadoutChange`
(`fsship.cpp:228-234`, a `VacateStation` path, `fsmission.cpp:4817`). Instead the
**clients remove the parts themselves**:

* `FM_S_EJECT`: "Ship 'died'." → `while (plink = plist->first())
  plink->data()->Terminate();` → `SetAmmo(0); SetFuel(0.0f);` → `SetBaseHullType(
  side's lifepod)` (`clintlib/appmsg.cpp:193-201`, `:216`). Sent to everyone
  flying in the sector, so teammates and enemies drop the parts too.
* `FM_S_ENTER_LIFEPOD` (own client only): unboards children, then
  `while (ppl = pparts->first()) ppl->data()->Terminate();` → `SetBaseHullType(
  lifepod)` (`clintlib/appmsg.cpp:1973-1981`).
* For the *true death* path the other clients simply delete the ship
  (`AnnounceExit` → `SHIP_DELETE`, `fsship.cpp:196-223`), so nothing else is
  needed.

**A native pod's loadout on the wire** is therefore: the pod hull and **zero
parts**, zero ammo/fuel/ore/energy — the same shape as a freshly created ship.
Whatever the player had is replayed later by the *client's* cached `m_loadouts`
head (`RestoreLoadout`, `clintlib/clintlib.h:1876-1886`), which is saved on
`BUY_LOADOUT_ACK` (`clintlib/appmsg.cpp:3185`) and never touched by the strip. The
native client also *ignores* its own `LOADOUT_CHANGE` part list ("Ignore most part
changes for ourselves", `appmsg.cpp:2023-2024`).

### 1.4 Non-player deaths and child ships

* **AI/drone root ships** take the same path: `bKilled = true` for every
  non-player (`FedSrv.CPP:11781-11785`), so their parts are terminated in the same
  root-ship block, then `delete pfsShip->GetDrone()` (`12080-12085`).
* **Child ships (turret gunners)** get no independent terminate block — their own
  `KillShipEvent` call has `pshipParent != NULL`, which skips `11644-11763`
  entirely. They do not need one: boarding terminates the gunner's parts ("No
  parts allowed on an observer or turret", `CshipIGC::SetParentShip`,
  `Igc/shipIGC.cpp:3619-3633`), which is why the ejecting gunner's
  `SetBaseHullType` assert holds.
* The parent's death recurses into its children first (`11678-11685`), so a
  destroyed carrier kills its gunners before the carrier's own parts are rolled.

---

## 2. Port audit (what survived, and what a pod could actually do)

| path | file:line before P998 | verdict |
|---|---|---|
| loot roll | `src/Allegiance.Server/Server.Combat.cs:2215-2290` `DropKillShipLoot` — rolled parts/ammo/fuel/flag, zeroed ammo/fuel, **no terminate** | the misleading comment claimed "the dying ship's loadout is cleared by the death/respawn path (ChangeHull to Lifepod / FinalizeDeadShip)" — **false** |
| hull swap primitive | `src/Allegiance.Core/Sim/Ship.cs:345-371` `ChangeHull` — hull record only (deliberate, pinned contract; the P334 purchase path re-seeds the pools instead) | parts untouched |
| eject | `Server.Combat.cs:2665` `EjectSurvivor` → `ChangeHull(pht)` then EJECT | pod kept the fighter's parts |
| pod death → docked pod | `Server.Combat.cs:2126` respawn leg → `ChangeHull(lp)`, `StationId = dest` then `ENTER_LIFEPOD` | docked pod kept the fighter's parts |
| non-eject death | `Server.Combat.cs:2454` `FinalizeDeadShip` (ship removed, `SHIP_DELETE`) | parts kept until the object is dropped (no visible effect, but not native) |
| bots/NPCs | same `BroadcastKillShip` path; no owning peer → `FinalizeDeadShip`, native `delete GetDrone()` (`FedSrv.CPP:12084`) | parts kept until deletion |
| child/turret death | `BroadcastKillShip` guards the loot block on `victim.ParentShip is null` (`Server.Combat.cs:1815`) | correct already (a boarded child has no parts — `PerformBoard` → `RefundHardware` → `ClearLoadout`, `Server.Docking.cs:137,1719`) |
| client EJECT | `ClientWorld.Apply(Eject)` cleared `Loadout` but **not** `Cargo` (`ClientWorld.cs:3092-3096`) | half of native `appmsg.cpp:193-201` |
| client ENTER_LIFEPOD | `ClientWorld.Apply(EnterLifepod)` (`ClientWorld.cs:5172-5182`) cleared **nothing** | missing native `appmsg.cpp:1973-1981` — this is what kept the CURRENT SHIP list populated after a pod death |

**What a pod could do with the inherited parts (measured, see §5).**

* **Parts/weapons:** the realized `Weapon` objects and the whole `_parts` list
  survived the hull swap; the pod reports `Weapons.Count == 1` (a live gun) and
  `Parts.Count == 3` after being podded with a gun + shield + booster.
* **Fire:** *conditionally*. Shipped pod hulls carry `MaxEnergy 0` and
  `RechargeRate 0`, and the port's per-tick energy clamp (`Ship.cs:9629-9635`)
  pins the inherited pool to 0, so a stock pod could not actually shoot its
  inherited gun — but the gun stayed mounted, selected and realized
  (`Weapons` non-empty, `IsWeaponSelected()` true). Given a pod hull with an
  energy pool the inherited gun fires exactly like the fighter's
  (`P998DeadShipPartsTests.An_inherited_gun_fires_on_a_pod_until_the_death_strip_removes_it`).
* **Shield:** yes, live. `MountedShieldType()` still found the shield part, so a
  podded ship absorbed damage on the dead fighter's shield
  (`Ship.cs:5511-5516`), and the charge survived.
* **Boost:** yes, live. `_mountedAfterburner` still found the booster, so with
  fuel the pod got the fighter's afterburner thrust
  (`Ship.cs:5830-5833`); the loot block's own `SetFuel(0)` even auto-reloaded the
  tank from an inherited **cargo fuel pack** (`SetFuel` zero-transition →
  `SiteReload(Afterburner)`, `Ship.cs:493-509`), which native cannot leave behind
  because `SetBaseHullType` zeroes the tank again (`shipIGC.cpp:1429`) and the
  cargo parts are gone.
* **Cargo:** yes, live — an ammo/fuel pack in the hold survived, was usable by the
  reload paths and was droppable as a treasure.
* **Mass/thrust:** *not* affected in this port — `Ship.Mass` is the hull record's
  alone (`Ship.cs:209`, parts commit no mass here), so nothing part-related ever
  moved a pod's flight envelope. Native adjusts mass in `AddPart`/`DeletePart` and
  resets it in `SetBaseHullType` (`shipIGC.cpp:1401`), so both sides agree on the
  outcome.

Also audited and unchanged: the eject/respawn *clock* behaviour is P996's (this
task does not touch `LifepodTimeToDie`), the P411 loot odds/RNG sequence, and the
rescue/allied-re-home/teleport docking paths (`Server.Docking.cs:1990-2040`,
`:2495-2530`) — they all run `ChangeHull` and now inherit a bare ship, which is
native.

---

## 3. Changes

### 3.1 Core — the strip primitive (`src/Allegiance.Core/Sim/Ship.cs`)

* `ClearLoadout()` is now `ClearParts(raiseLoadoutChange: true)` (`:5277`); the
  mechanics (weapons, equipped parts, cargo, shield charge, P173 event) moved into
  `ClearParts(bool)` (`:5339-5356`) unchanged.
* **New `TerminatePartsOnDeath()` (`:5280-5333`)** — the port of native's
  `p->Terminate()` over the whole part list plus `SetBaseHullType`'s scalar
  resets: `ClearParts(false)`, then `Ore = 0`, `Ammo = 0`, `Energy = 0`,
  `SetFuel(0)` (last, so the zero-transition reload finds no cargo pack this same
  call removed). It is deliberately **wire-silent**: native's per-part
  `c_lcRemoved` has no wire effect on the server (`FedSrv.CPP:13530-13545`) and the
  hull swap that follows publishes the real (pod, zero-part) loadout — see §1.3.
  `ChangeHull` keeps its pinned "hull record only" contract, so the pool reset
  lives in the death path (the purchase path re-seeds instead, P334).

### 3.2 Server — the strip runs after the loot, on every root death (`src/Allegiance.Server/Server.Combat.cs`)

* `BroadcastKillShip`: inside the existing `victim.ParentShip is null` guard
  (`:1815-1821`) `DropKillShipLoot(victim, now)` is now followed by
  `victim.TerminatePartsOnDeath()` (`:1818`), i.e. **loot roll → terminate →
  ammo/fuel (+ore/energy) zero → hull swap** — the native order, with a log line.
  Because the guard is exactly native's `pshipParent == NULL`, this covers the
  eject, the death→docked-lifepod respawn, `FinalizeDeadShip`, and bot/NPC deaths;
  child ships are untouched (native's `11644`).
* `DropKillShipLoot`'s misleading comment is replaced with the truth plus a pointer
  to `Ship.TerminatePartsOnDeath` (`:2245-2253`); the P411 rolls themselves are
  byte-identical (no new `_rand` draws, same order, same dv/lifespan).

### 3.3 Client — the native handlers now drop the parts (`src/Allegiance.ClientCore/State/ClientWorld.cs`)

* `Apply(EnterLifepod)` (`:5189-5210`) now clears `Loadout` **and** `Cargo` before
  the pod hull swap, the port of `while (ppl = pparts->first())
  ppl->data()->Terminate();` (`clintlib/appmsg.cpp:1973-1981`). This was the missing
  half that left the dead fighter's parts in the hangar's CURRENT SHIP pane.
* `Apply(Eject)` (`:3093-3114`) also clears `Cargo` (native terminates the cargo
  parts in the same loop, `appmsg.cpp:193-197`, `shipIGC.cpp:1516-1549`).
* `RememberOwnLoadout` (`:400-425`) now refuses a loadout with **no mounted parts**:
  native's own client never takes parts from its own `LOADOUT_CHANGE`
  (`appmsg.cpp:2023-2024`) and only re-saves on a purchase ACK (`:3185`), so a
  parts-less refresh (a dropped last part, a strip notification) can never replace
  the P996 `m_loadouts` head the hangar restores (`clintlib.h:1876-1886`). Stated
  deviation: a purchase of a deliberately bare hull keeps the previous entry.
* The hangar (`Game1.Panes` → `ShopModel`) and the flight HUD need no change: they
  already read `ShipEntity.Loadout`/`Cargo`, which are now really empty — proved by
  the captures in §6.

### 3.4 Bots / MCP — nothing relied on the inherited parts

* A bot's pod state is *hull*-derived, never loadout-derived:
  `Catalog.IsLifepodHull(me.HullId)` in `BotManager.UpdatePodState`
  (`tools/allegiance-mcp/BotManager.cs:3667`) drives `ClassifyLifeTransition`
  (`:3640-3646`) — a pod drops its order and flies home (`:4949`), and relaunches by
  sending a **fresh real hull + its full parts**: `bot.HullId` with
  `bot.Parts ?? []` and `fLaunch: true` (`BotManager.Lifecycle.cs:216`), the hull
  picked by `RepickRelaunchLoadout`/`TechLoadoutSelector` (`BotManager.TechLoadouts.cs`).
* The only reads of a bot's own loadout are the expendable/chaff path — which
  early-returns for a pod hull (`BotManager.Dispensers.cs:28`) — and the cloak
  policy (`BotManager.Cloak.cs:32`), which needs a *mounted cloak part* that a bare
  pod no longer has (before the fix a pod with an inherited cloak could be told to
  cloak). Bot scoring/coverage uses hull + granted-part records
  (`WeaponCoverage.RecordPurchase` on the relaunch ACK), never the pod's inventory.
* The P996 bots regression (`P996BotsLifepodNoOxygenLoopLiveTests`) stays green
  (§5).

### 3.5 Docs / parity

* `GLOSSARY.md` — new "Dead-ship parts die with the ship (P998)" entry.
* `docs/parity/sim-rules-matrix.csv` **SIM-0860** (the native `KillShipEvent`
  body; `updates` family) and `docs/parity/p977-eject-rescue.csv` (a
  "server eject/death" row). `scripts/parity-scorecard.py`,
  `tools/p832a-mission-rules-audit.py --check` and
  `tools/check-glossary-links.py` all pass.

---

## 4. Tests

| file | what it proves |
|---|---|
| `tests/Allegiance.Core.Tests/P998DeadShipPartsTests.cs` (7 facts) | **what the pod could do before the fix** (a hull swap alone keeps gun+shield+booster+cargo; an inherited gun fires on a pod that has an energy pool; the inherited shield absorbs; the inherited afterburner boosts); the strip empties parts/weapons/cargo and zeroes ammo/fuel/energy/ore; the strip is wire-silent while `ClearLoadout` still publishes |
| `tests/Allegiance.Core.Tests/P998DeadShipPartsLiveTests.cs` (4 facts) | **loopback wire proof**: a real TCP victim + same-side teammate buy a full loadout through `BUY_LOADOUT`, both `ClientWorld`s hold the parts, the fighter is killed → EJECT → pod is bare on the server **and in both worlds** (loadout and cargo), ammo/fuel/shield zero; the pod death → docked Lifepod is bare and the P996 saved loadout is intact, and Launch restores the real hull + parts; a pod cannot fire nor use the shield; **seeded** (`BenchmarkSeed 998`) loot counts still see parts/ammo/fuel (12/17/14 over 60 victims — pinned; a strip-before-roll would zero them) and every root victim is stripped on the non-eject path too; a **child/turret ship** death neither loots nor strips (native's `pshipParent == NULL` gate, `FedSrv.CPP:11644`) |
| `tests/Allegiance.ClientCore.Tests/P998DeadShipPartsClientTests.cs` (4 facts) | `ENTER_LIFEPOD` and `EJECT` clear the local parts **and cargo** (native `appmsg.cpp:1973-1981`, `:193-201`); a hull-only own `LOADOUT_CHANGE` and the pod-hull refresh never clobber `OwnSavedLoadout` (P996 kept) |
| `tools/p998-ui-proof/` | the real-client screenshot driver (real `Game1` + real loopback server): it **fails** unless CURRENT SHIP is the bare pod *and* PENDING is the restored real ship *and* the flying pod's client loadout/cargo are empty; writes `proof-<style>.json` next to the PNGs |

**Repro proof.** With the server strip commented out (`victim.TerminatePartsOnDeath()`
disabled) and everything else in place, all three originally-written live facts fail with exactly the
pre-P998 state — `A_pod_cannot_fire…`: "Collection was not empty: [Weapon { Active =
True … }]"; the bare-pod test: the pod's `Parts` = `[gun, shield, afterburner]`;
the seeded loot test: "60 out of 60 items … Assert.Empty() Failure: Collection was
not empty". Restored, all pass.

Test counts (`Passed/Failed/Skipped`):

| run | result |
|---|---|
| `P998DeadShipPartsTests` (Core, unit) | 7 / 0 / 0 |
| `P998DeadShipPartsLiveTests` (Core, loopback) | 4 / 0 / 0 |
| `P998DeadShipPartsClientTests` (ClientCore) | 4 / 0 / 0 |
| existing neighbours (Core: P411 loot, P412 fuel, P663 treasure parity, P746 eject credit, lifepod/pod/rescue/eject/P955/P977/P323 death-finalize/P699b/treasure-pickup/death-respawn, 327 tests) | 327 / 0 / 0 |
| existing neighbours (ClientCore: lifepod/death/P648/P996/loadout/eject/P638, 265 tests) | 265 / 0 / 0 |
| existing neighbours (MonoGame: lifepod/hangar/loadout/shop/death, 187 tests) | 186 / 0 / 1 (the private Meshy pack skip) |

---

## 5. Screenshots (Classic **and** Modern, real window, real server)

`docs/reports/P998_assets/` — driven by `tools/p998-ui-proof` at 1920×1080 (see
the note below), PNG, quantised to 256 colours where needed. I opened every file
and describe what is actually visible:

| file | size | what is visible |
|---|---|---|
| `loadout-after-pod-death-classic.png` | 186 099 B | The docked loadout screen after the pod death, Classic HUD: `CURRENT SHIP / hull: #33 / (no parts mounted)` in the left column — **the bare pod** (hull 33 = the Iron-Coalition-class pod the server docked, and P998's zero parts) — then `PENDING LOADOUT / hull: Adv Fighter / parts: Hvy Booster, Counter 3, Mine Pack 2, PW Gat Gun 3 ×3, Sm Shield 3, SRM Dumbfire 3` (P996's restored real ship), `PRICE … cost: $0`, `money: $1000000 AFFORDABLE`. The right-hand part-info panel shows the highlighted hull's native stats. |
| `loadout-after-pod-death-modern.png` | 186 099 B | **Pixel-identical** to the Classic capture (same md5): the loadout pane is the shared model/layout, so Modern's overrides (hangar artwork/buttons, flight HUD reticles/typography) change nothing here. Stated rather than staged — the *style* difference is shown by the flight-HUD pair below. |
| `pod-hud-classic.png` | 166 585 B | The **flying pod** HUD in Classic, right after the eject: `You ejected`, the pod callout `YOU ARE IN AN EJECT POD — G to AUTO PILOT TO BASE`, `AMMO 0%` / empty ammo+energy bars, `Ammo depleted.`, `WEAPONS: GROUP  BOOST: OFF`, the classic radar/armor/shield cluster and `SIG: 50%`. No weapons and no cargo anywhere: the pod flies bare. |
| `pod-hud-modern.png` | 212 571 B | The same moment in the **Modern** HUD: `SKIN: Modern`, ring gauges `AMMO 0%`, `ENERGY 0%`, `SHIELD 0%`, `ARMOR 100%`, `OXYGEN 04:57`, `YOU ARE IN AN EJECT POD`, `HUD Tactical / RADAR Default`. Clearly different composition from Classic, same bare-pod state. |

`proof-classic.json` / `proof-modern.json` (11 437 / 11 436 B) record
`{"launchedHull":15,"launchedParts":8,"podHull":33,"serverPodParts":0,
"clientPodHull":33,"clientPodParts":0,"clientPodCargo":0,"currentHull":33,
"currentParts":0,"hangarSelection":15,"hangarParts":8,"podDeadline":null}` plus the
full `ShopLayout` text rows of the captured screen (so the PNG content is also
machine-readable).

Total asset size **774 KB** (limit 1 MB).

**Layout note (honest, pre-existing):** at 1280×720 the port's docked loadout
screen draws its part-info panel over the CURRENT SHIP rows (the panel is sized to
the highlighted hull's long native description), so the proof drives a 1920×1080
window where the panel sits to the right and the left column is readable. No code
change; the driver also parks the proof pointer above the list so no hover state
is staged. The 1920×1080 captures were quantised (`pod-hud-*`, 804/747 KB →
167/213 KB).

The PNGs are **new media**: they need Domingo's review before the push
(`ALLOW_MEDIA=1` for `scripts/publish-guard.sh`).

---

## 6. Gate

All runs in this worktree, shared host with other lanes active, every command with
the lane's anti-hang flags (`--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`) and every suite/run bounded with `timeout`. Raw
fast-tier logs/TRX of the sweep are outside the repo at
`~/allegiance-evidence/P998/fast-tier-1/`.
`local.env` is present (artwork + native-source paths); the only skips anywhere are
the private-Meshy-pack tests.

| gate item | command | result |
|---|---|---|
| whole-solution build, 0 warnings | `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** (final tree) |
| every tools project | `tools/build-tools.sh` (with a `DOTNET` wrapper adding the anti-hang flags; the script's plain `dotnet build` is the documented shared-host hang) | **all 134 tool projects built**, 0 failures (includes the new `tools/p998-ui-proof`) |
| AOT publish (Server/Core changed) | `dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true` | **0 errors / 0 warnings / no IL2026 / no IL3050**, 17.5 MB native binary |
| new tests, 3× alone | Core `P998DeadShipPartsTests` + `P998DeadShipPartsLiveTests`, ClientCore `P998DeadShipPartsClientTests` | **3 × 11 passed / 0 failed / 0 skipped** (Core), **3 × 4 passed / 0 failed / 0 skipped** (ClientCore) |
| P996 regression kept green | `P996LifepodSavedLoadoutTests` (ClientCore) 3×, `P996LifepodRespawnLiveTests` (Core) 3×, `P996LifepodHangarRestore*` (MonoGame) | **3 × 6 pass**, **3 × 1 pass**, **5 pass** |
| existing neighbours | Core P411/P412/P663/P746/lifepod/pod/eject/P955/P977/P323/P699b/treasure/death-respawn (327 tests), ClientCore lifepod/death/P648/P996/loadout/eject/P638 (265), MonoGame lifepod/hangar/loadout/shop/death (187) | **327 / 0 / 0**, **265 / 0 / 0**, **186 / 0 / 1** (private Meshy pack skip) |
| one full fast tier | `tools/test-tiers/runner.py --tier fast --no-build` (waited on the shared runner lock, bounded) | **14 072 tests, 571.3 s, FAIL**: 14 066 passed, **4 failed**, 3 `NotExecuted` (Meshy), 2 `SLOW FAST` (38.178 s `MissileLockLiveTests…`, 10.566 s `P777WarpExitWobbleTests…`). All four are **non-task** and already ledgered with these exact signatures; each re-run alone on this tree: `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again…` **3/3 pass**, `P739TimerLiveTests.Warp_bomb_notice(delay:5)` **2 runs 3/3 + 1 run 2/3** (`5.0000000000000009` vs `(4.9,5]`), `LiveSideSelectTests.Flag_on_auto_seats…` **1 pass / 2 fail** at 32.15/32.20 s against `[28,31]`, `MissileLock…` **0/3** at 38 s (the ledgered persistent family, fails alone on the clean base too). Ledgered in `docs/TEST_FLAKES.md` under "P998 dead-ship part strip". No P998 test failed. |
| showcase never-parks 5× vs base rate | `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` ×5 (+2 instrumented) | **7/7 pass** (278 s, 176 s, 165 s, 188 s, 360 s, 171.3 s, 261.4 s); reached **24/30** each run, **longest "speed < 1 with a target" 1.907 s / 0.407 s** (limit 5 s), `idleWithTarget.currentCount == botsLastMinute == 0` asserted every run — at or above the ledgered base rate |
| bots path (extra, Live) | `P996BotsLifepodNoOxygenLoopLiveTests` | **pass**, seed 996: pods drifting 13, pod deaths 53, oxygen deaths 19, **minimum launch→oxygen-death gap 14.55 s** (rule > 1 s) — bots still relaunch real hulls after a pod death |
| docs/tooling | `scripts/parity-scorecard.py` (860 sim rows, 7 eject rows), `tools/p832a-mission-rules-audit.py --check` (351 bodies), `tools/check-glossary-links.py` (771 paths), `grep -c $'\r'` on the edited CSVs | all pass, 0 CR bytes |
| publish guard (self-check) | added lines grepped with the guard's patterns (no `/home/<user>/`, no personal email, no secret pattern in `src tests tools`); the 4 PNGs are new media needing `ALLOW_MEDIA=1` | clean apart from the intended media review note |

---

## Native intent

A ship's hardware is *consumed* by its destruction. Native's `KillShipEvent`
loots and then terminates every part of a dead root ship — cargo included, since
the cargo bay is just the negative-mount half of the same part list
(`Igc/shipIGC.cpp:1516-1549`) — zeroes ammo, fuel and the carried flag
(`FedSrv/FedSrv.CPP:11686-11728`), and only then swaps the hull to the eject pod or
the berthed lifepod (`:11942`, `:12052`), which is why `SetBaseHullType` can assert
`m_parts.n() == 0` (`Igc/shipIGC.cpp:1377`) and resets the remaining pools on the
same call (`:1428-1437`). The port replicates that order exactly and lets the
existing whole-loadout `LOADOUT_CHANGE` (plus the client's own EJECT /
ENTER_LIFEPOD part termination) carry the resulting bare pod to the server, the
owner and the teammates.

## Modern approach

The port keeps the strip as one documented ship-level operation
(`Ship.TerminatePartsOnDeath`) instead of scattering `Terminate` calls through the
death paths: it is the same mechanics `ClearLoadout` already used for a purchase
rebuild (parts, realized weapons, cargo, shield charge) plus native's scalar resets,
and it is deliberately **wire-silent**, matching native's server-side
`LoadoutChangeEvent` (which only re-targets area-effect weapons) — the loadout
message the clients actually see is the hull swap's, which now carries the pod hull
with zero parts. The clients' renderer model gets the two native handlers it was
missing (ENTER_LIFEPOD's part termination, and EJECT's cargo half), and P996's
saved-loadout cache is hardened so *no* parts-less refresh can replace it. The
result is native's outcome on every death path — a pod that is a pod, a hangar
whose CURRENT SHIP pane is the bare pod while PENDING is the ship the player last
flew — with the loot rolls untouched, bots relaunching exactly as before, and both
HUD styles showing the empty pod.
