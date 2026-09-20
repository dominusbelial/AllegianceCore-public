# P285 — Let a player choose a Builder ship's target station type at purchase

Feature: the hangar/loadout shop (P268) and its tech-gating (P284) had no way
to set a purchased Builder/Constructor hull's target station type — the port's
`Ship.BuilderStationType` (`Sim/Ship.cs:1040`) was only ever set in test/config
code. P285 adds the missing purchase-time lever: a player highlighting/buying a
Builder-class hull is shown a small cycle-through chooser of the techbase types
that Builder can construct, and on launch the choice travels over the wire and
is set server-side on the ship. The chosen type is what the existing P163-165
construction flow actually builds.

Dependencies checked before starting: both P268 (hangar shop) and P284 (shop
tech-gating) are landed (`git log` top commit `9e13955` = P284); this task
proceeded with both present.

## The real finding, verified in-source (not re-derived)

A Builder's target station type is genuinely a per-ship-instance setting in
both native and this port:
- Native: `IbuildingEffectIGC`'s station type comes from `m_pshipBuilder->GetBaseData()`
  (`Igc/buildingeffectigc.h:69`) → `GetBaseData()` (`Igc/shipIGC.h:2066`) returns
  `m_pbaseData`, set once at ship creation from the ship's own wire data:
  `m_pbaseData = pmission->GetStationType(dataShip->baseObjectID)`
  (`Igc/shipIGC.cpp:109`, only for `c_ptBuilder`).
- This port: `Ship.BuilderStationType` (`Sim/Ship.cs:1040`, `public DataStationTypeIGC?`)
  is exactly the port's equivalent — already a plain settable property. Confirmed
  via grep: zero references anywhere in the P268/P284 hangar shop; no player-facing
  way to set it existed. (Verified at task start, per the brief.)

**Builder hull identification (the brief left this open):** the shop needed a
principled way to know a hull is a Builder. Native carries the marker on the
hull itself: `c_habmBuilder = (short)0x8000` (`Igc/igc.h:741`), which the port
already decodes via `DataHullTypeIGC.HabmCapabilities` / `HasCapability`
(`Data/DataHullTypeIGC.cs:51,163`). P285 adds the `HabmBuilder = 0x8000`
constant (mirroring the sibling `HabmBoard`/`HabmLifepod` constants) and reads
it in `ShopCatalog.IsBuilderHull`. The real core's Constructor hulls
("Constructor", various Lg/Sm/Std/Enh/Adv) carry this bit.

## Exact DataStationTypeIGC names offered

Offered = Builder-constructible techbase station types, decoded from
`PCore001.igc`, selected data-drivenly by the techbase station class
(`DataStationTypeIGC.ClassId`, the native `StationClassID` grouping,
`Igc/igc.h:626-633`; the port's real decoded `ClassId` values): Garrison,
Shipyard, Expansion, Supremacy, Tactical (the five the brief names). The real
decoded `Name`s (not the AFS wiki's English aliases) are used in the UI:
`Garrison`, `Shipyard`, `Expansion`, `Supremacy`, `Tactical`. The utility
Outpost/Teleport/Refinery/Platform classes are deliberately excluded (per the
brief's five-techbase scope).

The exact `ClassId → name` mapping measured from the core:
`Garrison`=0, `Shipyard`=2, `Expansion`=5, `Supremacy`=6, `Tactical`=7.

## Whether P284's tech-gating was already there to reuse

Yes. The Builder-target offer is tech-gated by the **exact P283/P284
containment** — `TtbmRequired ⊆ (stationLocal | sideDev)` via
`TechTreeBitMask.RequiredTechsBuyable` — in the new
`ShopCatalog.BuilderTargetsFor(stationLocal, sideDev)`. The same `stationLocal`
(the docked station's `TtbmLocal`) and `sideDev` (the side's live
`DevelopmentTechs` from CREATE_BUCKETS / SIDE_TECH_CHANGE) terms P284 already
computes in `Game1.HandleDockedScreen` are reused directly; no third bitmask
algorithm. When the terms are unavailable it degrades to the constructible set
(P284's degrade-on-unknown precedent).

## What was built

- **Wire (new fmid 201, port extension):** `Wire/SetBuilderTarget.cs` — a small
  dedicated client→server message carrying `hullId` + `stationTypeId` (8-byte
  fixed, mirroring `JoinSide`). Chosen over folding into the native-shaped
  BUY_LOADOUT codec so the existing wire shape stays byte-compatible. 201 is in
  the port's own custom-extension range (beside ChangeAlliance 196 /
  AsteroidMined 198 / ClusterInfo 200), above native's 195-max allocation.
- **Server:** `Server.SetBuilderTargetOnShip` (dispatch in ProcessDatagram +
  handler near ApplyLoadout). Validates (soft-fail log-and-ignore per port
  convention): the named hull resolves in the core AND carries `c_habmBuilder`;
  the named station type resolves in the core. Then sets
  `ship.BuilderStationType = stationType`. This is the purchase-time wiring of
  native's creation-time `baseObjectID` (`shipIGC.cpp:109`).
- **Client (core):** `ConnectionSession.SendSetBuilderTarget` + `Bot.SendSetBuilderTarget`.
- **MonoGame:** `ShopCatalog.IsBuilderHull` / `BuilderTargetsFor` /
  `IsBuilderConstructible`; `ShopModel` builder-target state
  (`IsBuilderActive`, `CycleBuilderTarget`, `BuilderStationTypeId`,
  `CurrentBuilderTarget` — null by default so a launched Builder's target is
  the player's explicit choice, never a silently-imposed default);
  `ShopLayout` renders a `BUILDER TARGET` chooser block only when the effective
  hull is a Builder; `Game1` passes the offered targets in, wires `,/`. cycling,
  and sends `SET_BUILDER_TARGET` (via `SendBuilderTargetIfApplicable`) just
  before the BUY_LOADOUT launch (both L-key and the 30s auto-launch fallback).

## Scope boundary / exclusions (confirmed honoured)

- **Native successor-upgrade behavior** (`GetSuccessorStationType`,
  `FedSrv.CPP:11380`) — out of scope, named not built.
- **`OT_stationType` bucket "detection only" gap** (P119 follow-up) — genuinely
  separate mechanic, not unified.
- **No change to `Ship.BuilderStationType`'s type or the builder-construction /
  `StartBuildingEffect` logic (P163-165)** — untouched; this task only adds the
  purchase-time UI/wire to set the existing correct field. Consequently a
  player-owned Builder still does not autonomously construct (that decision is
  `IsNpc && PilotType==Builder`-gated in `Ship.OnUpdate`, deliberately out of
  scope); the live construction proof reuses the EXISTING P163-165
  `StartBuildingEffect` flow directly (exactly what the brief's "reusing
  P163-165's existing construction flow" specifies).
- **Native `~/Development/Allegiance` untouched** (read-only).
- Physical-hull hot-swap on BUY_LOADOUT remains P268's documented Phase-2 gap
  (not this task); the builder target is set on the live ship regardless.

## Verification — real live proof, not just build/test

- **Whole-solution build: 0 warnings, 0 errors** (`AllegianceCore.slnx`, Debug).
- **Live proof** (`P285PurchaseBuilderTargetLiveTests`, real in-process server +
  real networked bot as the "player" peer):
  1. **Wire → field:** the peer sends `SET_BUILDER_TARGET` over the real network
     naming a real Constructor hull (`c_habmBuilder`) + a chosen Garrison-class
     station type; the server sets the peer ship's `BuilderStationType` to that
     exact choice. `Assert.Null` BEFORE (it is a null-by-default field — the
     value that arrives is the player's CHOICE, not a preset), `Assert.Equal`
     after (choice id + name match). This is the discriminating assertion.
  2. **Field → construction:** reusing the existing `Sector.StartBuildingEffect`
     with the configured field on the peer's ship, the mission ticks the full
     40 s build through the P163 transformation (asteroid→station, builder
     consumed) and the placed station's `StationTypeId` and `Name` equal the
     chosen target — the built station matches what was chosen, evidenced by
     real state.
  3. **Negatives:** a target naming a NON-Builder hull is soft-refused (field
     stays null); an unknown station type is soft-refused (field stays null).
- **Must-fail proven:** temporarily neutering the server handler (removing
  `ship.BuilderStationType = stationType`) turned the primary live test RED
  (`Assert.NotNull` on the still-null field), then restored to green.
- **Tests added:** Core `SetBuilderTargetCodecTests` (+3: roundtrip, NA ids,
  wrong-length reject) and `P285PurchaseBuilderTargetLiveTests` (+3 live);
  MonoGame `ShopModelTests` (+4: builder activates chooser/cycles real types,
  non-builder no-op, null-until-player-cycles, constructible-techbases) and
  `ShopLayoutTests` (+2: section only-for-builder, chooser reflects selection).
  Core +6, MonoGame +6.

## Full-suite runs (baseline compared)

Core suite run **three** times (required ≥ 2). Run totals 3163 (incl. the +6
new P285 tests). Failures across the three runs (7 → 5 → 3, decaying as live
socket pressure dissipates) were exclusively pre-existing/environmental, none
referencing P285 symbols:

| Run | Failed | Failing tests |
|-----|--------|---------------|
| 1 | 7 | mix of StationsUpdateLiveTests / LiveTurretGunnerBindingTests (documented) / LiveMoneyTests.Board_refund / BotFlightTests.Abrupt_disconnect |
| 2 | 5 | StationsUpdateLiveTests ×2 / LiveTurretGunnerBindingTests (documented) / LiveMoneyTests.Board_refund / BotFlightTests.Abrupt_disconnect |
| 3 | 3 | StationsUpdateLiveTests ×2 / LiveTurretGunnerBindingTests (documented) |

- **`LiveTurretGunnerBindingTests.Board_binds_gunner...`** — the documented
  pre-existing machine-state flake (the only one the brief expected).
- **`StationsUpdateLiveTests.*` ×2** — NOT in the documented list, so proven
  environmental rigorously: with my changes stashed and the tree pristine, the
  same `SeatTwoBases` failure reproduces in isolation → pre-existing, not a
  P285 regression.
- **`LiveMoneyTests.Board_refund...` / `BotFlightTests.Abrupt_disconnect...`**
  — full-suite socket-contention flakes; both pass in isolation on the pristine
  tree.
- Every failing test file has **zero references** to `SetBuilderTarget` /
  `BuilderStationType` / `HabmBuilder` / `P285`.

**MonoGame suite 449/449 clean; ClientCore suite 127/127 clean; whole-solution
build 0 warnings, 0 errors.**

## Files

- `src/Allegiance.Core/Wire/SetBuilderTarget.cs` (new, fmid 201).
- `src/Allegiance.Core/Data/DataHullTypeIGC.cs` — `HabmBuilder` constant.
- `src/Allegiance.Server/Server.cs` — dispatch + `SetBuilderTargetOnShip`.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `SendSetBuilderTarget`.
- `src/Allegiance.Bot/Bot.cs` — `SendSetBuilderTarget`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/ShopCatalog.cs`,
  `ShopModel.cs`, `ShopLayout.cs`, `Game1.cs` — client chooser.
- Tests: `tests/Allegiance.Core.Tests/{SetBuilderTargetCodecTests,P285PurchaseBuilderTargetLiveTests}.cs`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/{ShopModelTests,ShopLayoutTests}.cs`.

Committed on `master` (not pushed — Domingo pushes), per the P-series landing
procedure.
