# P415 — Friend / Foe / Hull-Role Filtered Nearest-Targeting Keys

Port of native's Targeting key family (`trekinput.cpp:1531-1600`) into the
MonoGame client, built as ONE generalized filtered-nearest command instead of
native's ~30 near-identical clones.

Branch: `master` (not pushed — Domingo pushes). Companion tests:
`tests/Allegiance.ClientCore.MonoGame.Tests/FilteredTargetingTests.cs`.

---

## 1. The generalized filter's shape

Native expresses every targeting command as the combinatorial product of a
target-TYPE mask, a SIDE relation and an optional ability-bit role filter, all
fed to a single `FindTarget` (`src/Igc/common.cpp:470-653`) whose per-object
gate is `FindableModel` (`common.cpp:254-353`). This port mirrors that with one
parameterized filter instead of ~30 cloned methods (the port's shared-
implementation convention):

```csharp
internal enum TargetRelation { Enemy, Friendly, AlliedBase, Self }

internal readonly record struct TargetFilterSpec(
    TargetRelation Relation,
    short          RoleBitmask   = 0,   // native abmAbilities bit test
    bool           StationOnly   = false,
    bool           MajorBaseOnly = false);
```

The pure gate `Game1.MatchesFilter` (native `FindableModel`) and the pure
selection `Game1.FilterCandidates` (native `c_ttNearest` =
`dp.LengthSquared()`, `common.cpp:575-578`) are static/headless. `FilterCandidates`
returns the matching candidates sorted NEAREST-first by true 3D distance; the
wrap-around cycle step `Game1.StepCycle` (nearest-first list, matching-only)
collapses native's separate `*Nearest` / `*Prev` pair into one cycling key
(first press = nearest, repeats step through matches).

### Classification data — no new wire source (scope req. 1)

`Game1.CandidateObject` gained `SideId` + `RoleBits`, both drawn from the SAME
data the existing `CollectTargetCandidates` gather (`Game1.GatherSectorCandidates`)
already reads:

- **Ships**: `SideId` from `ShipEntity.SideId` (gated on `HasRealIdentity`,
  the P373 rule — a ship's side is only trusted once a real EXPORT(26)
  established identity); `RoleBits` = the hull's `HabmCapabilities` resolved
  from the client's `ShopCatalog` by `HullId`.
- **Stations**: `SideId` from `DataStationIGC.SideId`; `RoleBits` = the
  station TYPE's `SabmCapabilities` resolved from `ShopCatalog.StationTypes`
  by `StationTypeId`.

The `ShopCatalog` is the same in-client parse of `PCore001.igc` the docked shop
already loads (`Game1.Lifecycle.cs:203`), so role classification is reachable at
runtime with no new data plane.

### Decoded ability bits used (confirmed present)

- Hull roles: `DataHullTypeIGC.HabmFighter (0x80)`, `HabmMiner (0x4000)`,
  `HabmBuilder (0x8000)` (the native "bomber" role has NO dedicated bit —
  native classifies bombers as `c_habmThreatToStation`, `igc.h:735` —
  `HabmThreatToStation` is decoded too). All map 1:1 to native `igc.h:726-741`.
- Stations: `DataStationTypeIGC.SabmFlag (0x200)` (major/victory base,
  `igc.h:753` — decoded by P191) and `SabmPedestal (0x400)`. Native's generic
  enemy-base filter is `~c_sabmPedestal`; this port's base filter matches any
  station of the right side (pedestal stations are rare/no equivalents in the
  test map), the major-base filter matches `SabmFlag`.

---

## 2. Which relation/role combinations got real keys

Bound (plain = enemy family, Shift+ = friendly family — the brief's suggested
layering, matching the flight-sim convention):

| Key | Command | Native TK_ |
|-----|---------|-----------|
| `U` | Nearest ENEMY ship (repeat = next enemy) | `TK_TargetEnemyNearest` / `TK_TargetEnemyPrev` |
| `Shift + U` | Nearest FRIENDLY ship (repeat = next friendly) | `TK_TargetFriendlyNearest` / `TK_TargetFriendlyPrev` |
| `Z` | Nearest ENEMY base (station) (repeat = next) | `TK_TargetEnemyBaseNearest` / `TK_TargetEnemyBasePrev` |
| `Shift + Z` | Nearest FRIENDLY / ALLIED base (repeat = next) | `TK_TargetFriendlyBaseNearest` / `TK_TargetFriendlyBasePrev` |

That covers the brief's "at minimum" set: nearest enemy, nearest friendly,
nearest enemy base, nearest friendly/allied base, and cycle-prev for each (the
single key both picks the nearest and steps through matching candidates on
repeat). Every new binding is registered in `KeyBindingTable.cs`'s help overlay
(TARGETING / AUTOPILOT group).

### Deliberately LEFT UNBOUND (and why)

- **Per-role variants** (Enemy/Friendly × Fighter/Bomber/Miner/Constructor/
  Lifepod — ~10 more commands). The brief allows omitting these when no sane
  non-overloaded key scheme fits: `U`/`Z` are already committed to the
  relation-level ship/base pair, and eight-plus extra combos would overload the
  keyboard with no clean mnemonic. The generalized filter fully SUPPORTS the
  role bitmask (proven by `Role_bitmask_*` tests), so these are one parameter
  away from binding if a scheme is ever wanted. Left unbound and documented
  rather than forcing an unusable layout.
- **Major-base-only** variants (`TK_TargetEnemyMajorBase*` /
  `TK_TargetFriendlyMajorBase*`): supported by `TargetFilterSpec.MajorBaseOnly`
  (tested) but not bound — same reason as per-role.
- **`Self` relation** / **`AlliedBase` as its own key**: see §4.

### Divergences from native (deliberate, cited)

1. Native's GENERIC enemy/friendly ship keys pass an extra role filter
   `c_habmRescue | c_habmMiner | c_habmBuilder` to `FindTarget`
   (`WinTrek.cpp:10122`, `:10158`), which would make a pure fighter invisible to
   "nearest enemy". This port deliberately does NOT replicate that quirk: the
   generic `U`/`Shift+U` keys target ANY ship of that relation (RoleBitmask = 0),
   matching a pilot's expectation that "nearest enemy" finds every enemy ship.
   The role filter remains available for the per-role commands.
2. Native base search adds `c_ttAnyCluster` (cross-sector via warp hops). This
   port keeps the P371 same-sector narrowing for consistency with the rest of
   targeting; cross-sector targeting is not set up in the client.
3. Native "friendly" (`c_ttFriendly`) includes ALLIED sides
   (`FindableModel`, common.cpp:271-273). This port's client has no alliance
   side map (§4), so "friendly" = same-side.

---

## 3. Implementation

- `Game1.Targeting.cs`: `TargetRelation`, `TargetFilterSpec`, `MatchesFilter`,
  `FilterCandidates`, `StepCycle` (all pure/static), `SelectFilteredTarget`
  (live wrapper over the P371 gather), `ShipRoleBits`/`StationRoleBits`
  (ShopCatalog resolvers), `SelectFilteredTargetForKey`.
- `Game1.FlightInput.cs`: `U`/`Shift+U` and `Z`/`Shift+Z` edge-triggered keys
  (gated behind the composing check, latched `_uTargetKeyWasDown` /
  `_zTargetKeyWasDown`).
- `Game1.cs`: the two new key latches.
- `KeyBindingTable.cs`: the four new help-overlay entries.
- `tests/.../FilteredTargetingTests.cs`: 11 new tests.

---

## 4. Allied-base note (scope req. 5)

Native's allied-base commands are the Imago 8/1/09 addition
(`trekinput.cpp` "#Imago 8/1/09"; `TrekFindTarget` `iAllies==2`,
`common.cpp:275-278`): a station on a side that is ALLIED-WITH-BUT-NOT-EQUAL to
the player's. Meaningful alliances only form with ≥3 active sides
(`Server.cs` ~`:5453`).

This port's client receives only each object's own `SideId` on the wire — there
is NO alliance-side map reaching the client (the alliance relation is a
server-side ≥3-side concept). Therefore:

- `TargetRelation.AlliedBase` EXISTS in the filter but matches identically to
  `Friendly` (same-side stations); it is documented in the enum.
- The `Shift + Z` key is labeled "friendly / allied base" and covers both
  practically.
- The distinct native `TK_TargetAlliedBase*` behavior is NOT separately
  representable client-side and is left unbound, exactly as the brief permits
  ("clearly document that it's implemented but only live-testable in a 3-side
  game"). If a future stage adds an alliance-side map to the client, the
  `AlliedBase` relation is the one seam to widen.

---

## 5. Verification performed

### Unit tests (11 new, all green; must-fail proven)

- **Side separation**: enemy keeps only other-side ships + excludes self;
  friendly keeps only same-side + excludes self; self relation returns self;
  unsided strangers are never enemies (`Enemy_filter_*`,
  `Friendly_filter_*`, `Self_filter_*`).
- **Role narrowing**: `Role_bitmask_narrows_to_that_role_even_when_a_wrong_role_
  is_closer` (an enemy fighter closer than the enemy miner is dropped for the
  miner filter); `Role_bitmask_applies_to_friendly_too`.
- **Nearest / cycle**: `Nearest_picks_closest_matching_ignoring_a_nearer_wrong_
  relation`; `Cycle_steps_through_matching_only_and_wraps` (walk confirms the
  cycle never offers the nearer friendly and wraps).
- **Station / major-base**: `Enemy_base_filter_keeps_only_enemy_stations`,
  `Friendly_base_filter_keeps_only_friendly_stations`,
  `Major_base_filter_keeps_only_flag_stations_of_the_right_side`.
- **Real wire-apply integration**: `Gather_and_filter_separate_sides_from_real_
  wire_data` seeds a `ClientWorld` through `Apply(IFedMessage)` (own-ship dummy
  EXPORT sets `MySideId`, ship EXPORTs set side/hull identity, station EXPORTs
  carry side) and proves the gather fills `SideId`/`RoleBits` and the filter
  separates enemy vs friendly ships and bases from that data.
- **Must-fail**: neutralizing the role-bit gate turned both `Role_bitmask_*`
  tests red (2 failed) — confirmed by temporary removal, then reverted.

### Build / suite

- Whole-solution build: **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **793/793 green** (incl. the 11 new +
  the HelpOverlay keybinding-table layout tests after the 4 new entries).
- `Allegiance.Core.Tests` and `Allegiance.ClientCore.Tests`: only the
  documented live-wire timing flakes. Two ClientCore live flakes
  (`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`) were confirmed to
  also fail on the PRISTINE tree (git stash), i.e. environmental, not this
  change (this change touches only the MonoGame project).

### Live proof (required)

`FilteredTargetingLiveTests.LiveNearestEnemyAndFriendly_SelectTheCorrectlySidedRealShip`
boots a REAL in-process server (m2xspiderw, 2 sides), seats a real peer on side 0
in the Ganymede sector, and places two real NPC ships in that same sector — one
on the OTHER side (enemy), one on the peer's OWN side (friendly) — which the
server's real cross-side export (P346/P349) and same-side NPC export (P366)
deliver to the peer's `ClientWorld` over the real wire with real `SideId`/
`HullId` (`HasRealIdentity=true`). Driving the real production gather + filter
against that real world proves:
- nearest-ENEMY selects the real other-side ship and never the friendly;
- nearest-FRIENDLY selects the real same-side ship and never the enemy;
- the base filter selects the real in-sector station by whichever side relation
  matches its real wire `SideId` (adaptive to the map's garrison side).

Passes 3/3 isolated runs (~855 ms each). Together with the real-wire-apply
integration unit test (`Gather_and_filter_separate_sides_from_real_wire_data`),
this satisfies the brief's live-proof requirement on a host that has no
input-injection tool (no xdotool/ydotool — the standing constraint recorded
throughout the port, e.g. P221/P222/P225/P229); the keys' selection path is
byte-identical to the code these tests drive.

---

## 6. Scope boundaries honored

- Out of scope by the brief (not touched): `TK_TargetCommand/Center/Self/
  Nothing`, `TK_ConMode*`, P368-P414.
- No server / `Allegiance.Core` / `Allegiance.ClientCore` change was made.
