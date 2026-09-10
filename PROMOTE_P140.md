# P140 — PROMOTE (189/190): swap a boarded gunner into the pilot's seat

Repo: `AllegianceCore`, branch `master`, atop `d2e9e7e` (P139).

An in-game (not lobby) role-swap: a commanding ship promotes one of its own
boarded turret gunners into the command seat *in place* — the gunner takes the
carrier's hull, loadout, ammo/energy, position/orientation and hull/shield
fractions, and every remaining boarded gunner transfers to it; the old carrier
is demoted into the exact turret mount the gunner vacated. **Nothing is
destroyed** — it is the port of `CshipIGC::Promote`
(`Allegiance/src/Igc/shipIGC.cpp:3509-3591`) and the server handler
`case FM_C_PROMOTE` (`Allegiance/src/FedSrv/FedSrv.CPP:6353-6382`).

## What was added

| File | Change |
|---|---|
| `src/Allegiance.Core/Wire/Promote.cs` | Both directions' codecs: `Promote` (FM_S_PROMOTE, 189) and `PromoteRequest` (FM_C_PROMOTE, 190). |
| `src/Allegiance.Core/Sim/Ship.cs:582` | `Ship.Promote()` — the real role-swap. |
| `src/Allegiance.Server/Server.cs:2166` | `FM_C_PROMOTE(190)` dispatch branch. |
| `src/Allegiance.Server/Server.cs:2710` | `HandlePromote(...)` — the handler. |
| `src/Allegiance.Core/Wire/FedMessageDumper.cs` | Formatters for fmid 189/190. |
| `tests/.../Wire/PromoteWireTests.cs` | Codec round-trip + byte-exact tests. |
| `tests/.../LivePromoteTests.cs` | Pure-unit role-swap + live loopback proofs (broadcast, no-op, stability). |

## Wire layout (Messages.h:975-981)

```
DEFINE_FEDMSG(S, PROMOTE, 189) ShipID shipidPromoted; END_FEDMSG   // 6 B: hdr(4) + short
DEFINE_FEDMSG(C, PROMOTE, 190) Mount mountidPromoted; END_FEDMSG   // 6 B: hdr(4) + char@4 + pad@5
```

- `Mount` is a C++ `char` (`Allegiance/src/Igc/igc.h:841`) — the same
  turret-slot concept P108/P109 already track on `Ship.TurretId` (a `short?`).
  The codec stores it as a single byte and **sign-extends** on decode (the
  signed-char widening: NA = -1 → `0xFF` → `(short)-1`), so the handler compares
  it directly against `Ship.TurretId`.
- The C-codec's 6-byte size follows native C++ struct packing: `{ ushort cbmsg;
  ushort fmid; char mountidPromoted; }` has alignment 2, so `sizeof` rounds to
  6 with 1 trailing pad byte (messagecore.h `FMD_` structs derive from
  `FEDMESSAGE { CB cbmsg; FEDMSGID fmid; }`, `messagecore.h:76-88`; `cbmsg =
  sizeof(FMD_C_PROMOTE)`, `messagecore.h:714`).

## The handler (FedSrv.CPP:6353-6382 → Server.HandlePromote)

Mirrors the native exactly:

1. **Gate: mission started** — `pfsMission->GetStage() != STAGE_STARTED` →
   ignore (`FedSrv.CPP:6357`). Kept via `Ship.Mission` / `MissionStage.Started`.
   The native's `IsGhost()` gate is skipped (this port has no ghost concept —
   the same omission P108 documents for `BOARD_SHIP`).
2. **Eligibility: the requesting ship's OWN boarded children only** — walk
   `GetIGCShip()->GetChildShips()` and match `pship->GetTurretID() ==
   mountidPromoted` (`FedSrv.CPP:6359-6362`). A mount matching no real child
   falls through the loop = **silent no-op** (no crash, no ack, no broadcast).
3. **Call `pship->Promote()`** (`FedSrv.CPP:6363`).
4. **`ShipStatusRecalculate` is a no-op here** — the native calls
   `((CFSShip*)pship->GetPrivateData())->ShipStatusRecalculate()` after the
   hull change (`FedSrv.CPP:6364`). This port's ship status is **not cached**:
   `MakeOwnShipStatus` (`Server.cs:865`) derives it fresh from
   `HullType.HullId`/`ParentShip`/`StationId` on every per-tick refresh
   (`BroadcastShipStates`, `Server.cs:583`) — the same post-hull-change status
   mechanism P105/P107/P108/P110 established — so the promoted state is
   reflected automatically with no explicit recalc.
5. **Send target: sector-wide** — native routes `GetGroupSectorFlying(pcluster)`
   when in a cluster, else the requesting side's group
   (`FedSrv.CPP:6374-6377`). Routed through `SendSectorWide` (`Server.cs:3150`,
   this port's `GetGroupSectorFlying` analogue). **Finding:** during active
   gameplay this port's ships are always placed in a `Ship.Sector` (there is no
   reachable in-game no-cluster state), so the side-group fallback is
   **unreachable here** — a real simplification, not a silent drop.

## Ship.Promote() (shipIGC.cpp:3509-3591 → Ship.cs:582)

Faithful, each step citing the native line:

1. **Gunner leaves the parent** — `SetParentShip(NULL)` (`:3513`): remove from
   the parent's child list, clear back-pointers, release the parent's turret
   weapon-gunner binding at the vacated slot (native `SetParentShip(NULL)` →
   `SetGunner(NULL)`, `shipIGC.cpp:3652`; reuses P109's `Weapon.SetGunner`).
2. **Take the parent's hull** — `SetBaseHullType(parent->GetBaseHullType())`
   (`:3515`) via `Ship.ChangeHull` (P105's helper).
3. **Copy the parent's inventory** — `CreateAndAddPart` per part preserving
   mount/amount and `SetMountedFraction` (`:3519-3527`), then
   `SetAmmo`/`SetFuel`/`SetEnergy` (`:3530-3532`). Reuses `Ship.MountPart`
   (preserves `MountId`/`Amount`/`FractionMounted` and realizes weapon
   subtypes). The gunner's own loadout is empty by construction (boarding
   `ClearLoadout`-ed it, P108). **Fuel is a documented no-op:** this port has
   no runtime fuel pool on `Ship` (only `Ammo`/`Energy`; `DataHullTypeIGC`
   carries only the static `MaxFuel` constant) — native `SetFuel` has no
   counterpart, consistent with the P61-era decision to seed only ammo+energy
   in the constructor.
4. **Transfer every sibling gunner** — `pship->SetParentShip(this);
   pship->SetTurretID(turretID)` (`:3534-3546`), each removed from the old
   parent's child list (native `DeleteChildShip`, `shipIGC.cpp:3677-3689`) and
   re-bound as the gunner of this ship's freshly-mounted weapon at its own
   mount (native `SetTurretID`→`SetGunner`, `shipIGC.h:1405-1437`). Observers
   (`TurretId` null) transfer with no binding.
5. **Adopt the parent's kinematics + state** — position/velocity/orientation
   (`:3550-3552`), hull fraction (`SetFraction`, `:3554`), mounted-shield
   fraction (`:3555-3563`).
6. **Demote the old parent** — `pshipParent->SetParentShip(this);
   pshipParent->SetTurretID(turretid)` (`:3567-3568`). Its loadout is cleared
   per the port's "no parts allowed on a turret" convention (native terminates
   them in `SetParentShip` with a NULL old parent, `shipIGC.cpp:3619-3624`;
   `Ship.ClearLoadout` — the same mechanism P108's `PerformBoard` uses). Its
   hull type is left in place, matching the port's boarding convention that
   gunners retain their hull (P108 `PerformBoard` never nulls the boarder's).

## Model-list position swap: NOT needed (honest simplification)

Native `Promote`'s final step swaps the two ships' **link payloads in the
cluster's `ModelListIGC`** (`shipIGC.cpp:3570-3580`, `pmlParent->data(this);
pmlThis->data(pshipParent)`). That exists only to preserve
**identity-by-enumeration-slot** in native's doubly-linked model list — the
same physical list node keeps its position while its payload changes, so any
iterator already holding a link continues to point at "the same slot".

This port's sector model collection is a **`List<Model>`** in
`Model.Sector` (`Sector.cs:113` `_models`, exposed at `Sector.cs:210`), keyed
by each model's object **`Model.Id`** (insertion order, no slot-identity
coupling — `Model.cs:55`). Swapping the two ships' *positions* in that list
would be meaningless: nothing addresses a sector model by list slot; every
consumer identifies a model by its `Id` and re-enumerates `_models` to find
the same object set. Therefore **simply updating each ship's own
`ParentShip`/`TurretId`/hull fields suffices** — both ships remain in the
sector and enumerate identically either way. This is a real simplification
conceded by the port's collection type, not a dropped behaviour.

## Tests

- **Codec** (`Wire/PromoteWireTests.cs`): round-trip and byte-exact layout for
  189 and 190, wrong-fmid/short-buffer rejection, signed-char widening of NA.
- **Pure-unit role-swap** (`LivePromoteTests.cs`): drives `Ship.Promote()`
  directly on unplaced ships (deterministic `DamageConstant` = 1.0, so the
  hull/shield-fraction copy is asserted to real sub-1.0 values). Proves hull,
  all 3 parts, ammo/energy, position/orientation/velocity, hull fraction,
  shield fraction, sibling-gunner transfer, and parent demotion to the exact
  vacated slot. Also: promoting an observer throws.
- **Live loopback** (`LivePromoteTests.cs`, `[Collection("LiveNetwork")]`):
  1. A real commanding ship with a real boarded gunner at a known mount —
     promote over the wire — assert hull swap, parent↔child inversion at the
     exact mount, and `PROMOTE(189)` with the correct `shipidPromoted` on both
     sector peers.
  2. A non-matching mount → silent no-op (no state change, no crash, no
     broadcast).
  3. Post-promote ticks → structure stable (no runaway state).

Verified green: **3 consecutive full-suite runs** (see the run log below), with
the three documented flakes rerun to clean if they fired.

## Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py`.

## Suggested next task

None surfaced by this task itself — the promote slice reused existing
primitives end-to-end with no new subproblem. Natural candidates for the next
arc, in priority order: (a) **observer promote semantics** (native allows
`CanCheat()` observers; this port folds that gate to always-allow, so an
observer promote is currently guarded by the `TurretId != null` throw — a
future "promote-from-any-seat" admin path would relax it); (b) the
**launcher time-fired** preservation native does during the part copy
(`IlauncherIGC::SetTimeFired`, `shipIGC.cpp:3525-3527`) — the port has no
launcher mount representation, so it is silently skipped today.
