# P571 — clear the eject autopilot on dock + seed a new hull's default loadout

Date: 2026-10-07. Branch: `master` (not pushed). Client `99df9de6` reproduction.

## Problem

Domingo, live on 221 (2026-10-06): *"some of the ships as soon as I undock are
trying to dock again … that's right when I come back from dead … back in
station."* Two compounding bugs on the relaunch-after-death path:

1. The P540 eject/pod autopilot was never cleared when the own ship docked, so
   a freshly launched hull resumed steering straight back to the Garrison.
2. The docked shop launched a newly chosen hull with **zero parts**
   (`BUY_LOADOUT(fLaunch) hull 85 + 0 part(s)`, server `BUY_LOADOUT_ACK
   hull 13 nParts 0`), because the shop's pending loadout was seeded only from
   the docked ship's current (empty, respawned-pod) parts.

## Fixes

### 1. Autopilot cleared on dock (Game1.Panes.cs / Game1.Eject.cs)

`HandleDockedScreen` now tears down the P540 pod autopilot (and any autopilot
goto/target) the moment the own ship enters the docked hangar:

```csharp
if (!EjectAutopilotStaysEngaged(myDocked))
    DisengageAutopilot("docked");
```

`EjectAutopilotStaysEngaged(bool ownShipDocked) => !ownShipDocked` is a pure,
static rule so the dock-transition invariant is headlessly testable.

Native fidelity — the eject autopilot is a flight-time goto that native leaves
behind on docking:
- engage: `pShip->SetCommand(c_cmdCurrent, pNearestBase, c_cidGoto);
  SetAutoPilot(true)` — `clintlib/appmsg.cpp:234-235` (FM_S_EJECT, own ship).
- dock: `DockWithStationEvent` → `ss.SetState(c_ssDocked)` —
  `WinTrek/trekigc.cpp:4123, 4146` (the ship enters the docked state and the
  hangar screen replaces the cockpit/flight interface).

The pod autopilot stays engaged only while the own ship is an **undocked** pod
in flight (P540 preserved).

### 2. New-hull selection seeds the native default loadout (ShopModel.cs)

`RebaseForEffectiveHull()` — run when a real hull swap is pending
(`WillSwapHull`) — now REPLACES the pending loadout with the new hull's NATIVE
default loadout (`DefaultLoadoutFor`) instead of merely dropping non-fitting
parts. Launching an **unchanged** hull keeps its current parts (constructor
still pre-seeds from the docked ship's live loadout).

`DefaultLoadoutFor(hull)` walks `DataHullTypeIGC.PreferredPartsTypes` in order
and mounts each part that fits the hull (P555 masks):
- a weapon is mounted into **every** empty hardpoint that accepts its part mask
  (native `iMountMax = GetMaxWeapons()`),
- every other equipment type into its single socket (`iMountMax = 1`),
- preferred parts that don't mount the hull are skipped (native `CanMount`).

Ammo: launchers carry their real shop reserve (`LauncherOffer.Ammo`); weapons
and one-off modules use the shop's standard toggle amount (`DefaultAmount`).

**AllTech/out-tech correction (found in the real client):** the shop builds its
catalogue via `FilteredFor` (station tech + side developments + `!IsObsolete`).
On the allTech/testUnlock debug maps every entry-tier preferred part (PW Gat
Gun, Sm Shield, …) is superseded and culled from the offered catalogue, so a
new hull's default would be spuriously EMPTY. Native solves this by upgrading
each preferred part to its highest buyable successor —
`TryToBuyParts` calls `pstation->GetSuccessor(ppt)` (`clintlib.h:1357`,
`stationIGC.h:399-435`) — so the port now resolves each preferred part to the
raw part when the shop offers it (the task's expected defaults), else to the
highest OFFERED successor (via `SuccessorPartId` through the core's
part/launcher tables). The default loadout is therefore never empty under any
tech level.

Native fidelity:
- `BuyHull` → *"give it the default loadout for this hull"* →
  `RefreshLoadout(trekClient.m_customLoadouts[0], phullType)` —
  `WinTrek/load.cpp:1476-1481`.
- `TryToBuyParts(pship, pstation, &budget, pht, pht->GetPreferredPartTypes())`
  — fills the empty slots with the preferred part types, `CanMount` gate,
  weapons across `GetMaxWeapons()`, successor upgrade —
  `clintlib/clintlib.h:1341-1368`.
- `ReplaceLoadout` / `RefreshLoadout` — `WinTrek/load.cpp:1453-1474`.
- `Station::GetSuccessor` (part successor walk) — `src/Igc/stationIGC.h:399-435`.

#### Verified default loadouts (real PCore001.igc, P555 masks)

| hull | default loadout |
|---|---|
| Adv Scout 285 (1 hardpoint) | Counter 1, EWS Probe 1, PW Gat Gun 1, MRM Seeker 1, Sm Shield 1 |
| Adv Fighter 288 (3 hardpoints) | PW Gat Gun ×3 (mounts 0,1,2) + Hvy Booster + Counter + Mine Pack + Sm Shield + SRM Dumbfire = 8 parts |
| Bomber 124 | EW Pulse Laser fills every hardpoint + Med Shield + SRM Anti-Base + Counter |

Preferred parts that DON'T mount the hull (e.g. the gunship's Prox Mine / Tower
AC Drone under P555 masks) are never shipped. Under allTech the Lxy Hvy Bomber
(299) resolves to its offered successors (XRM Anti-Base 2, Counter 3, PW Gat
Gun 3 ×2, Med Shield, PW AutoCan 3 ×2) = 7 parts (live-proof verified).

## Proof

### Failing-first tests (all fail on today's code, pass on final)

- `P571DefaultLoadoutTests` (7 tests, `tests/…/P571DefaultLoadoutTests.cs`):
  Adv Scout default from a bare chassis (the "+0 parts" bug), fighter mounts
  the gun in every hardpoint, bomber carries only mountable preferred parts,
  non-mountable preferred parts are skipped, the launch transmits the default
  loadout (launcher with real ammo), unchanged hull keeps its current parts,
  and (new) an allTech filtered-catalogue hull swap resolves a NON-empty
  default via the successor upgrade.
  Demonstrated failing-first: **5/6** of the loadout tests fail when
  `RebaseForEffectiveHull` reverts to the old drop-only body (the unchanged-hull
  test is orthogonal and stays green); the allTech filtered test fails (0 parts)
  when the successor resolution is removed. All pass on the final code.
- `P571AutopilotDockClearTests` (2 tests, `tests/…/P571AutopilotDockClearTests.cs`):
  a docked own ship must not keep the pod autopilot; an undocked pod does keep
  it. (These fail on today's code because `EjectAutopilotStaysEngaged` did not
  exist.)

### Regression adjustments (behavior intentionally changed)

Cycling to a new hull now seeds its default loadout, so three older live tests
that asserted `Assert.Single(shop.ChosenParts)` / a "kit + 1 only" loadout were
tightened to assert the bought part is **present** with its real id/ammo while
coexisting with the hull's default loadout:
- `ShopLauncherBuyLiveTests.ShopLauncherPurchase_GrantsTheRealLauncher_Live`
- `DispenserEquipFireLiveTests.BuyMountAndFireARealDispenser_Live`

### Build + assembly run

- Whole-solution build: **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests` assembly (the one exercising the shop
  + all live shop/eject tests): **1377 / 1377 passed** (4 m 20 s).

### Live real-window proof (relaunch-after-death path)

`scripts/live-proof.sh` against a local server (m2xspiderw map, testUnlock) with
the windowed MonoGame client driving itself via P572 `--auto-*` flags. Sequence
(the exact bug path): client spawns → `--auto-dock-after 8` engages the
autopilot to the home Garrison → docks → `[monogame] autopilot OFF (docked)`
(client.out:76 — the P571 dock-clear) → hangar opens → `--auto-launch 299`
picks a NEW hull (Lxy Hvy Bomber) → `[autoproof] auto-launch BUY_LOADOUT
(fLaunch) hull 299 + 7 part(s)` (client.out:79) → server `swapping base hull 92
('Lxy Scout') -> 299 ('Lxy Hvy Bomber')`, mounts parts 1..7, `BUY_LOADOUT_ACK
hull 299 nParts 7 fbought True fLaunch True` (server.out:186-198) → undock →
fly.

Real-window screenshots (2560×1440 real backbuffer, committed in
`docs/reports/P571_RESPAWN_AUTOPILOT_AND_DEFAULT_LOADOUT_assets/`):
- `proof_t006.0_sOmega_ccockpit.png` — the launched ship flying in space;
  HUD shows `WEAPONS: ALL`, `AMMO 100%`.
- `proof_t026.7_sOmega_ccockpit.png` — the relaunched Lxy Hvy Bomber flying
  with its default loadout mounted (SYSTEMS panel: PW Gat Gun 3 ×2, PW AutoCan
  3 ×2, shields charged) and **no autopilot indicator** (it is NOT steering
  back to the station).

This demonstrates both fixes end to end in the real client window: the new hull
launches with its default loadout (server `nParts 7`) and no autopilot
(`autopilot OFF (docked)` fired; the relaunched ship cruises, not home-bound).

## Full suite (3× `scripts/run-tests.sh`, final code `8a2f1480`)

See `docs/TEST_FLAKES.md` for the documented environmental flakes; only those
interrupted. The P571-affecting assembly (`Allegiance.ClientCore.MonoGame.Tests`)
passed **1378/1378 in all three runs**; every other failure is a documented
pre-existing live-wire flake, each confirmed green in isolation.

| run | Lobby | MonoGame | ClientCore | Core | Core failures (all pass in isolation) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1378/1378 | 542/545 | 3761/3766 | ThreePeerSeededSession, ShipDeleteGone ×2; SideElimination, ShipsUpdate, LiveStationRescue, LiveCountdownStartPing, LiveChatSector |
| 2 | 12/12 | 1378/1378 | 545/545 | 3763/3766 | AutoReloadOnEmpty, LiveTreasurePickup, LiveChatSector |
| 3 | 12/12 | 1378/1378 | 545/545 | 3762/3766 | AutoReloadOnEmpty, StationUnderAttackAlert, CargoBayReload ×2 |
