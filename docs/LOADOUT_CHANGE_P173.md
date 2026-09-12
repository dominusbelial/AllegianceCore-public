# LOADOUT_CHANGE (P173) — broadcast a ship's loadout to visible peers

Branch `master`, on top of `be549be` (P172, NPC ripcord-as-travel). Baseline:
2814 tests; this task adds 2 (`LoadoutChangeLiveTests`) → 2816 total.

This wires up `FM_S_LOADOUT_CHANGE(96)` — one of the 57 still-unported fmids,
but unlike the lobby/team/voting/side-config bulk (architecturally N/A for this
direct-connect port), this one is a real, live, wired-for-but-never-raised gap:
every primitive already existed in the port — the `IIgcSite` hook, the C# enum,
the `ShipLoadout`-blob codec pattern (used 3x already) — but nothing ever called
the hook and nothing ever broadcast the message. It is the same shape of finding
as P171's dead `ComputeRelativeSpeed` helper, at larger scope (a whole
never-wired feature, not one duplicated helper). Landing it moves the ported
fmid count 79 → **80**.

## What changed

- **`src/Allegiance.Core/Wire/PassengerData.cs`** (new) — the per-boarded-child
  wire record, native `PassengerData` (`src/Inc/Messages.h:30-34`):
  `ShipID shipID` (short, `igc.h:480`) + `Mount turretID` (char, `igc.h:841`).
  **Width is 4 bytes, not 3** — see the Divergence note; this is the one place
  the task's "don't guess the width" warning bit: `PassengerData` is declared
  *outside* every `#pragma pack(push,1)` region (the only ones are
  `Messages.h:636-642` & `917-925`, and `igc.h:1673-1691` & `2120-2486`), so
  default MSVC alignment pads the 2+1 struct to 4 (short @0, char @2, 1 pad @3).
  Encode writes the pad byte as 0 (determinism); Decode consumes-and-discards it
  so the 4-byte stride stays aligned across a multi-passenger blob.
- **`src/Allegiance.Core/Wire/LoadoutChange.cs`** (new) — the `FM_S_LOADOUT_CHANGE`
  codec, fmid 96, `Messages.h:666-671`. **Two** `FM_VAR_ITEM`s (`loadout` +
  `rgPassengers`) plus one scalar — the exact two-var-item envelope
  `LightShipsUpdate` documents:
  `offset 0 cbmsg, 2 fmid=96, 4 ibLoadout, 6 cbLoadout, 8 ibRgPassengers,
  10 cbRgPassengers, 12 sidShip, 14 loadout blob, then passengers blob`;
  `FixedSize = 14`. The `loadout` blob is the same `ShipLoadout`
  (short hullID + `ExpandedPartData[]`) shape `BuyLoadoutAck`/`RelaunchShip`
  encode — reused verbatim, not re-derived.
- **`src/Allegiance.Core/Sim/MountedPart.cs`** — `MountedPart` now implements the
  empty `IPartIgc` marker interface (`IgcMarkers.cs:22`), so the part can be the
  `ppart` argument of `IIgcSite.LoadoutChangeEvent` (native's `IpartIGC*`).
- **`src/Allegiance.Server/ServerHost.cs`** — a `public Action<IShipIgc, IPartIgc,
  LoadoutChange>? LoadoutChange;` hook + the `public void LoadoutChangeEvent(...)`
  site implementation that raises it, the exact `ProjectileCreated`/`MissileDestroyed`
  pattern; null by default (headless host unchanged).
- **`src/Allegiance.Core/Sim/Ship.cs`** — the real mutation sites now raise the
  event through the established `Mission?.Site` idiom (same reachability as the
  `KillShipEvent` raise, Ship.cs:2195):
  - `ChangeHull` → `HullChange` (native shipIGC.cpp:1455, NULL part).
  - `MountPart` → `Mounted` (collapses native's `Added`/`Mounted` — both AddPart
    and SetMountID boil down to one MountPart here; a stated simplification).
  - `TryRemovePart` → `Removed` (native shipIGC.cpp:1510, passing the part).
  - `ClearLoadout` → one `Removed` (native ProcessShipLoadout triggers one
    QueueLoadoutChange, not one event per part).
  - `AddChildShip` → `AddPassenger` / `RemoveChildShip` → `RemovePassenger`, both
    raised **on the parent** (`this`), matching native's
    `LoadoutChangeEvent(pshipParent, NULL, ...)` at shipIGC.cpp:3668/3670.
  `Ship.Promote()` needs **no fifth raise** — it routes entirely through these six
  methods (RemoveChildShip/AddChildShip/ChangeHull/MountPart/ClearLoadout), so it
  inherits every event; confirmed by tracing, not assumed (see Divergence note).
- **`src/Allegiance.Server/Server.cs`**
  - subscription `_host.LoadoutChange = BroadcastLoadoutChange;` in
    `BindHostAndTransport` (beside line 311's `MissileDestroyed`).
  - `BroadcastLoadoutChange` — builds the ship's **current whole** loadout (hull +
    every `Part`, plus one `PassengerData` per `ChildShip`), exactly native
    `CFSShip::QueueLoadoutChange`'s fresh `ExportShipLoadout` per event
    (`FedSrv/fsship.cpp:708-735`), then sends to the changed ship's own peer plus
    same-side teammates holding its `ExportShip` — the identical
    `_teammateExportSent`-gated idiom `BroadcastMissileDestroyed`/
    `BroadcastExportMine` use. `sidShip` = the ship's own real id (non-force path).
- **`src/Allegiance.Bot/Bot.cs`** — `LoadoutChanges` list + `ResetLoadoutChanges`
  + a dispatch `case 96` so live tests can observe decoded LOADOUT_CHANGE packets.

## Scope boundary / exclusions (deliberate)

- **`bForce` / `ForceLoadoutChange` / `sidShip == NA` self-notify path** — no
  trigger exists in this port (no lifepod-at-allied-base mechanic). Not built.
- **Any client-side receipt/dispatch logic** (`appmsg.cpp:1991-2032`) — this port
  has no client; only the server broadcast side matters.
- **Cross-side ship visibility** — this port's ship visibility is same-side only
  (every prior per-ship broadcast P58/P62/P63/P85/P86/P88/P90/P94 is same-side
  only, per `BroadcastExportMine`'s own doc). The enemy not receiving
  LOADOUT_CHANGE is asserted explicitly in the tests, matching that scope.
- **`Promote()`'s hull/shield/kinematics adoption** — not loadout-shaped, untouched.

## Divergence from the prompt's assumptions (verified, not guessed)

- **`PassengerData` wire width is 4, not 3** (the "don't guess the width" trap).
  `Messages.h:33` sits outside all four pack regions (above), so default MSVC
  alignment pads to `sizeof = 4`. Had it been in a `pack(1)` region it would be 3;
  the pack-region census settles it. Documented in the codec's type doc with the
  exact pack line numbers.
- **`Promote()` confirmed to need no explicit raise** — traced: every mutation it
  performs (steps 1-6, Ship.cs:1429+) goes through the six instrumented methods,
  so the events fire automatically. No fifth redundant raise was added.
- Everything else matched the prompt's premises exactly: hook presence, enum
  mirror, `ShipLoadout`-blob reuse, same-side-only recipient set, `sidShip` as a
  routing/self-reference flag (own real id for the non-force broadcast path).

## Verification

- `/home/domingo/.dotnet/dotnet build` — clean (Core / Server / Bot / Tests, 0 W, 0 E).
- New tests green in isolation repeatedly (3× clean; `--filter
  FullyQualifiedName~LoadoutChangeLiveTests`).
- **Must-fail-if-removed proven**: disabling the `_host.LoadoutChange =
  BroadcastLoadoutChange` subscription makes both new tests fail (2 failed / 0
  passed); restored → both pass. The tests genuinely discriminate the wiring.
- Full suite `dotnet test`: run 1 = 1 failure (`LiveCombatWireTests.Moving_
  target_streams_its_falling_hull_over_the_wire`), confirmed green in isolation —
  one of the five documented pre-existing flakes (LiveCombatWireTests is known
  intermittently flaky even alone). Runs 2 and 3 = fully clean, 2816/2816.
- fmid coverage delta: 79 → **80** ported (verified by counting distinct
  `Fmid` constants in `Wire/`; `LoadoutChange.cs` supplies fmid 96).

## What I actually ran

Build + 2 new live tests (isolated ×4) + the must-fail-if-removed revert/restore
probe + 3 full-suite runs (2 clean, 1 clean-after-isolated-flake-confirm).
