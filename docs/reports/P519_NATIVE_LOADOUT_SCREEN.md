# P519 — Native loadout screen: slot-driven parts + hull cycling, no auto-launch

Landing date: 2026-10-03. Branch: `master` (not pushed).

## Summary

Reworked the docked/hangar shop into native's slot-driven loadout screen with
two tabs (HULLS / PARTS), hull cycling, a live 3D hull preview, and — the
highest-priority Bug 3 — fixed the launch that stripped every ship to a bare
hull. Also removed the POC 30 s auto-launch.

### Bug 1 — removed the port's auto-launch (players stay docked until they act)
`Game1.cs`/`Game1.Panes.cs`: deleted `DockFallbackMs = 30_000` and the fallback
timer block that auto-launched docked players after 30 s. Native docked players
stay docked until they launch; the server's own auto-launch is NPC-only
(`Server.Docking.cs` IsNpc gate). A player now undocks only on their own action
(L / pad Y / touch Launch). No test relied on the fallback (verified by grep);
nothing needed to be gated to `_captureSpec`.

### Bug 3 (highest priority) — launches always carry the full intended loadout
Root cause: the client only ever learned its own ship's loadout from
`BUY_LOADOUT_ACK` (own-ship EXPORT is the NA dummy), so on a FRESH dock the
shop seeded an empty pending list and `L` sent **0 parts**. `ApplyLoadout`
clears then remounts only the requested parts, so the ship undocked as a bare
hull (weapons gone; the dock-refilled shield was removed → shield fraction 0 →
`DamageFlashTracker` red flash).

Fixes:
- **Server (`Server.Combat.cs`)**: on a ship's dock, send the docked peer a
  `BUY_LOADOUT_ACK` (FLaunch=false) carrying its REAL mounted parts — the port's
  analogue of native `RestoreLoadout` opening the hangar with the current
  loadout (`load.cpp:537`). The client's existing `Apply(BuyLoadoutAck)` sets
  HullId + Loadout, so the shop always opens seeded with the real loadout.
- **Client (`ShopModel`)**: the pending loadout is now the *full intended
  loadout* — seeded verbatim from the ship's real parts (preserving each part's
  Amount/FractionMounted/mount), and every edit (fill a slot / make empty /
  swap hull) mutates that same list, so `L` always sends N > 0 parts equal to
  the ship's intended loadout. On a hull change, parts that don't fit the new
  hull are dropped eagerly (native `ReplaceLoadout`, `load.cpp:1453-1474`), so
  launching after a hull change sends the new hull's mountable loadout.

### Scope 2 — slot-driven loadout (the native LoadoutImpl design)
`ShopModel` was redesigned around native `LoadoutImpl::Slot` (`load.cpp:58-83`)
and `BuyPartMenu` (`load.cpp:1680-1751`):
- **Slots** = the effective hull's real slots: one weapon slot per hardpoint
  (turrets labelled) plus a single slot for each equipment type the hull has a
  socket for (`PmEquipment[et] != 0`, native `SlotValidForHull` else-branch,
  `load.cpp:112-122`).
- **Parts per slot** are compatible-only: equipment-type equality +
  `CanMount(part, slotMount)` (`PartFitsSlot`) — a magazine slot lists only
  missile launchers, a dispenser slot only mine/probe dispensers, etc.
  `- Make Slot Empty -` clears a slot (native's menu item).
- Choosing a part fills the slot at its real mount (weapon → hardpoint index,
  equipment → mount 0), preserving P437's per-equipment-type mount numbering.
- P468 pricing/affordability/grey-out, P488 successor/obsolete filtering and
  P470 drone-group filtering are preserved (they live in `ShopCatalog.FilteredFor`
  and the model's `ValueOf`/`TryLaunch`).
- **Cargo slots omitted** (documented divergence): native's NA-type slots serve
  the "equip another ship" fleet menu, which this single-ship port has no model
  for (and no carrier/fleet).

### Scope 3 — hull cycling
`CycleHull(bool)` walks the buyable hulls exactly as native
`GetNextHullType` (`load.cpp:1568-1616`): `GroupID >= 0 && CanBuy && !IsObsolete`
(the already-filtered catalogue), wrapping, with the current-hull full-circle
escape. The HULLS tab also lists all buyable hulls flat (native's `HullMenu`,
`load.cpp:1790-1847`) as a quick way to reach any class — matching native,
which offers both a cycle AND a flat hull menu. No class grouping exists in
native, so none was invented.

### Scope 4 — hull preview: live 3D orbit behind the picker
`Game1.Rendering.DrawDockedHullPreview` renders the selected (effective) hull's
REAL in-game model — resolved via `HullModelResolver` exactly as in flight
(incl. any per-model orientation fix) — orbiting at native `cmExternalOrbit`
rates (WinTrek.cpp:7186-7191: Pitch 0.35, Yaw 0.2, Roll 0.5 rad/s), behind the
translucent HULLS/PARTS panes. A hull with no model asset uses the same in-flight
fallback. The orbit-rate accumulation is pure (`Rendering/DockedHullOrbit.cs`)
so it is unit-testable. `Rendering/DockedHullOrbitTests.cs` verifies the exact
native rates and frame-rate independence; `Hull_selection_drives_the_preview_hull_id`
verifies hull selection drives the preview's hull (hence its model key).
Cycling swaps the model instantly (models are already cached in `_shipModels`).
No space scene — one model + one light.

### Input (keyboard / gamepad / touch)
- **Keyboard**: TAB (or Q/E) switch the HULLS/PARTS tab; Q/E cycle the hull;
  UP/DOWN navigate; ENTER/Space select; B back (close a slot / go to HULLS);
  L launch; U undock.
- **Gamepad (P499)**: shoulder buttons switch tabs; DPAD Left/Right cycle hull;
  DPAD Up/Down navigate; A select; B back; Y launch.
- **Touch (P501)**: tap the HULLS/PARTS tab headers to switch; row-taps move the
  cursor and act (pick hull / open slot / mount part) via `DockedTouchZones`
  (added `HullsTabRect`/`PartsTabRect`).

## Tests (failing first, then green)

Pure (no graphics device), all on the real PCore001.igc:
- `ShopNoAutoLaunchTests` — `Game1` no longer exposes `DockFallbackMs`/`_dockSinceMs`;
  the shop model is inert over hundreds of idle "frames" (no spontaneous launch).
- `ShopModelTests` — the slot list matches a real hull's real slots; the magazine
  slot lists ONLY missile launchers; the hull cycle visits exactly the buyable,
  non-obsolete, GroupID≥0 hulls in stable order; launching without touching the
  shop sends the full current loadout (N > 0); changing hull drops non-fitting
  parts; preserved pricing/affordability/preseeding/mount-numbering.
- `ShopLayoutTests`, `PartInfoLayoutTests`, `ShopLauncherCatalogueTests`,
  `DockedHullOrbitTests` — tab/slot layout, part-info detail (part-focused),
  compatible-only dispenser listing, orbit rates.

Live (real in-process server + `ConnectionSession` wire): `DockLoadoutLiveTests`,
`P394ShopPreSeedLiveTests`, `ShopLauncherBuyLiveTests`,
`ShopLauncherMountNumberingLiveTests`, `DispenserEquipFireLiveTests` — dock, use
the slot PARTS tab to find and mount a missile launcher (≤ a handful of actions
from the hangar: Tab→PARTS, pick the Magazine slot, pick a launcher), launch,
and the server ACKs nParts > 0 with the launcher at mount 0 that locks and fires.

## Verification

- Whole-solution build: **0 warnings, 0 errors** (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`).
- Full suite via `scripts/run-tests.sh`, three consecutive runs — see the
  measured numbers below. Any remaining failures are pre-existing live-socket
  flakes documented in `docs/TEST_FLAKES.md` (ConquestTerritory, WarpBomb,
  NpcShipFoundation, SessionLoss, FullSessionKill — different tests across runs,
  unrelated to P519; none fail in isolation for P519 reasons).
- Live check vs a testUnlock-equivalent server (all tech developed + funded):
  dock, mount a missile launcher via the slot PARTS tab, launch; server ACK
  shows nParts > 0, the launcher at mount 0, and it locks + fires.

## Measured numbers (this session)

Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0 errors**.

Full suite via `scripts/run-tests.sh`, three consecutive runs after all P519
changes, measured per run (Lobby / MonoGame / ClientCore / Core):

| Run | Lobby | MonoGame (P519) | ClientCore | Core |
|-----|-------|-----------------|-----------|------|
| 1   | 12 ✓  | **1192 ✓ (0 fail)** | 429 ✓ / 1 ✗ | 3704 ✓ / 4 ✗ |
| 2   | 12 ✓  | **1192 ✓ (0 fail)** | 429 ✓ / 1 ✗ | 3704 ✓ / 4 ✗ |
| 3   | 12 ✓  | **1192 ✓ (0 fail)** | 428 ✓ / 2 ✗ | 3707 ✓ / 1 ✗ |

**The P519 MonoGame assembly is fully green (1192/1192) in all three runs.**
The residual failures are all pre-existing live-network load-flakes in the
ClientCore/Core assemblies that change from run to run (run 1: FullSessionKill,
AutoReload, WarpBomb, MultiCountdown, Conquest; run 2: FullSessionKill, Carrier,
IndividualRelay, ConquestGameOverPlayers, SecondPartTouch; run 3: SeedThreePeers,
RepeatedConnects, WarpBomb) — the documented live-socket class in
`docs/TEST_FLAKES.md` (marked `load-flake` / `suspect (P472)`; e.g.
`AutoReloadOnEmpty…`, `WarpBomb…`, `LiveCountdownStartPing…`,
`FullSessionKill…`, `ConquestTerritoryLiveTests…`). None of them exercise P519
code, and none fail in isolation for a P519 reason; their varying identity across
runs is the signature of load-induced timing, not a deterministic regression.

Live check (testUnlock-equivalent: all tech developed + wallet funded, over a
real in-process server + `ConnectionSession`): dock, use the slot PARTS tab to
find and mount a missile launcher (Tab→PARTS → Magazine slot → pick a launcher),
launch — server ACK carries nParts > 0, the launcher granted at mount 0, and it
locks + fires (ShopLauncherBuyLiveTests, ShopLauncherMountNumberingLiveTests).

Focused P519 tests measured green (this session): ShopModelTests +
ShopLayoutTests + ShopLauncherCatalogueTests = 35; ShopNoAutoLaunchTests +
DockedHullOrbitTests = 5; dock live (DockLoadout, P394×2, ShopLauncherBuy,
DispenserEquipFire×2, ShopLauncherMountNumbering) green; dock-related Core = 70.

## Divergences / scoped omissions (honest)

- **Cargo slots** omitted (native NA-type "parts for other ships" menu has no
  single-ship port analogue).
- **Chaff dispensers unbuyable**: PCore001 has NO hull with a chaff socket, so
  the compatible-only shop never offers chaff (correct per P519); the
  `DispenserEquipFireLiveTests` chaff case was dropped accordingly (mine/probe
  remain).
- **Hull-change default loadout**: native loads the player's custom loadout #0
  (which has no port storage); the port uses the fitting subset of the current
  loadout after a swap — the same reduction for a player who hasn't customized.
- **Hull-preview live screenshot + frame-time**: the client requires a display;
  this headless box cannot produce the on-screen capture. Verified instead by the
  pure orbit-rate test, the model-key test, and code (native constants).
