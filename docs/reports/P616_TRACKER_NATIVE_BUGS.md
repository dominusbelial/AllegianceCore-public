# P616 — FreeAllegiance tracker native bugs vs the port (#18, #59, #42, #146, #225)

## Summary

Domingo asked to check the old FreeAllegiance tracker
(`https://github.com/kgersen/Allegiance/issues`, 47 open, mostly imported from the
2010–2012 Trac) against the C#/MonoGame port. Claude triaged it (2026-10-11);
engine/platform issues (Steam, registry, COM/AGC, DX9, timeGetTime, screenshots,
Fraps) do not apply to the port. These five are **native gameplay/server bugs a
faithful port may have inherited**.

**Bottom line: the port inherits none of them.** Each was traced to its native
code path, mapped to the port's equivalent, reproduced in a guard test (loopback
bots / direct sim), and confirmed absent — either because the port's design makes
the native fault impossible (synchronous server-authoritative reload; enumerator-
safe managed rotation; no treasure-set object lifecycle; fresh-ship-per-rotation)
or because the UI element does not exist. No production code needed a change. Five
guard tests were added (4 executable + #225 documented) and pass on the current
tree, so a future change that re-introduces a native fault fails loudly.

Executable guards: `tests/Allegiance.Core.Tests/P616TrackerNativeBugTests.cs`
(all 5 pass, 0 skipped).

## The table (deliverable)

| Issue | URL | Native cite | Port cite | Had the bug? | Fix / test |
|---|---|---|---|---|---|
| #18 fuel+aleph exploit | https://github.com/kgersen/Allegiance/issues/18 | `Igc/shipIGC.h:627-651` (SetFuel >0→0 → `Reload`), `FedSrv/FedSrv.CPP:6585-6590` (c_packFuel drain), `FedSrv/fsship.cpp:71-137` (HitWarp), `WinTrek/trekigc.cpp:4380-4417` (client warp) | `Sim/Ship.cs:320-333` (SetFuel), `:3044-3095` (TryAutoReloadFuel), `ServerHost.cs:940-1017` (HitWarpEvent — never touches fuel) | **No** — reload is synchronous & server-authoritative; the aleph transit is a single tick that neither grants nor consumes fuel | Guard: `FuelAutoReload_EachRefillCostsExactlyOnePack_NeverFreeFuel` + `AlephTransit_ChangesNeitherFuelNorCargo` |
| #59 treasureset teardown crash | https://github.com/kgersen/Allegiance/issues/59 | mission/`DeactivateSide` teardown "Nuking N treasureset(s)" — name-formatting buffer overflow while removing treasure sets | `Server.Lobby.cs:255-324` (DeactivateSide — clears loadouts/kills ships, never touches treasure sets); `Server.Lobby.cs:471-543` (rotation discards mission wholesale); no `CTreasureSetIGC` lifecycle (`Server.Lobby.cs:3359-3362` decodes sets into the join catalog only) | **No** — there is no treasure-set object container to "nuke"; the old mission is GC'd whole | Guard: `TreasureTeardown_OnRotationAndDeactivateSide_NoCrash_NoLeak` |
| #42 Allow-Empty-Team mission-destroy crash | https://github.com/kgersen/Allegiance/issues/42 | intermittent crash creating/destroying AET missions (trac #43) | `Server.Lobby.cs:466-543` (`RotateMissionAfterGameOver` — index-swap, enumerator-safe); `Server.Dispatch.cs:1191-1213` (AET toggled at runtime) | **No** — destroy is a managed index swap, no native iterator/allocator hazard | Guard: `AllowEmptyTeams_ThreeRotations_NeverThrow_AndAlwaysRestart` |
| #146 Command wing → Attack wing | https://github.com/kgersen/Allegiance/issues/146 | `FedSrv/fsmission.cpp:633-634` (leader → `SetWingID(0)`), `Igc/shipIGC.cpp:47,128` (m_wingID=NA at ctor/reset); bug retained the prior wing | `Sim/Ship.cs:1041` (`WingId` defaults 0 = command, `c_pszWingName[0]`); `Server.Lobby.cs:632,2709-2712` (rotation mints a fresh Ship → wing never survives a game) | **No** — the commander always gets a fresh Ship on rotation, defaulting to Command wing | Guard: `CommanderLeader_DefaultsToCommandWing_AfterRotation` |
| #225 lobby AFK toggle unchecks itself | https://github.com/kgersen/Allegiance/issues/225 | native lobby UI: double-click + mouse-move unchecks AFK (UI bug, 0 comments) | no equivalent lobby UI — pre-flight is a keyboard ReadyCheck screen (`ClientCore.Game/Game1.Panes.cs:1014-1045`); no AFK checkbox exists | **No equivalent UI** | Documented only (no code guard possible/meaningful) |

## Per-issue detail

### #18 — "Exiting alephs while boosting sometimes gives extra fuel"

**Native.** When a ship runs its afterburner dry, `CshipIGC::SetFuel`'s >0→0
transition arms the reload (`shipIGC.h:633-651`) and calls `IIgcSite::Reload`,
which drains the last `c_packFuel` pack in cargo and tops fuel to max
(`FedSrv.CPP:6570-6592`). The exploit: time the reload sequence to start *just as*
you hit an aleph, and you exit **with full fuel while no tank is expended**
(the related juckto variant: 5s of free burn during the warp animation). The native
devs fought this for years ("Fixed with [1046]" — reopened — "Still reproducible
with 1051"); a proposed `trekigc.cpp` change `GetMountedPart(ET_Afterburner,0)
->Deactivate()` "fixes it but has the side-effect of decreasing the power of an
active booster even if it has fuel."

**Port.** Fuel is a real pool on `Ship` (`Ship.cs:296-333`), reloaded
synchronously and server-authoritatively (`TryAutoReloadFuel`, `Ship.cs:3044-3095`,
which drains a pack atomically via the P192 per-entry applicator). The aleph
transit is `ServerHost.HitWarpEvent` (`ServerHost.cs:940-1017`, port of
`fsship.cpp:71-137`): a single synchronous tick that repositions the ship and moves
it between sector lists — it **never calls `SetFuel` and never touches cargo**.
There is no hidden/animated warp window during which a client-side reload could
"complete for free": the reload and the transit are both deterministic server
ticks, and every refill consumes exactly one pack. The exploit cannot exist.

**Verdict: not inherited.** Guards:
`FuelAutoReload_EachRefillCostsExactlyOnePack_NeverFreeFuel` (drives three
`SetFuel(0)` depletions through a 2-pack cargo and asserts exactly one pack per
refill, ending `Depleted` at 0 fuel — no phantom fuel) and
`AlephTransit_ChangesNeitherFuelNorCargo` (a live server transits a real ship
across an aleph and asserts `Fuel` and cargo are bit-for-bit unchanged).

### #59 — "AllSrv crashes while removing treasuresets"

**Native.** During `DeactivateSide` (a side quit / miners destroyed) / mission
teardown the server crashed while "Nuking N treasureset(s)". Investigators traced
it to a **buffer overflow formatting the treasure-set *name*** when the name was
too long; it persisted across core renames and revisions (17 comments, "Need dump
first to fix it").

**Port.** Treasure *sets* in the port are only decoded into a read-only catalog for
the join packet (`StaticCore.TreasureSets`, sent via `TreasureSets.FromCatalog` at
`Server.Lobby.cs:3359-3362`); the server places *individual* `Treasure` objects in
sectors (flags, drops, prospector drops), not `CTreasureSetIGC` containers. The
port's `DeactivateSide` (`Server.Lobby.cs:255-324`) clears loadouts, kills ships,
broadcasts `SIDE_INACTIVE` and chats — it **never iterates or formats a treasure
set** — and mission teardown (`RotateMissionAfterGameOver`, `Server.Lobby.cs:471-543`)
discards the whole `Mission` object (and its treasures) to the GC. There is no
treasure-set name buffer to overflow and no treasure-set removal sweep.

**Verdict: not inherited.** Guard:
`TreasureTeardown_OnRotationAndDeactivateSide_NoCrash_NoLeak` seeds live `Treasure`
objects into the mission, drives a full game-over rotation (asserting the fresh
mission inherits none — clean teardown), then reflectively calls the real
`DeactivateSide` with treasures still present and asserts it returns without
throwing and leaves the side inactive.

### #42 — "Server intermittently crashing when Allow Empty Team games are destroyed"

**Native.** Create and destroy several Allow-Empty-Team (AET) missions and the
server intermittently crashes (trac #43; "Need dump..." — never diagnosed).

**Port.** AET is a per-mission flag toggled at runtime (`Mission.AllowEmptyTeams`,
`Server.Dispatch.cs:1191-1213`). Destroying a mission is
`RotateMissionAfterGameOver` (`Server.Lobby.cs:466-543`), which — unlike native's
manual object teardown — **swaps the finished mission out by index in `_missions`
so the list length never changes and no enumerator is invalidated**, in managed
memory. There is no native-style iterator/allocator window in which the destructor
could trip.

**Verdict: not inherited.** Guard:
`AllowEmptyTeams_ThreeRotations_NeverThrow_AndAlwaysRestart` boots a real server,
flips AET on, and runs three full game-over rotations (live 4-bot draws), asserting
no exception escapes and the rebuilt mission always re-starts (also exercising the
P598 ready-preserving auto-start).

### #146 — "Command wing changes to Attack wing"

**Native.** Launching as Commander should default your wing to Command regardless
of previous wing choices, but the game retained the previous game's wing
(trac #149). Native sets the *leader* to Command wing in `AddPlayerToSide`
(`fsmission.cpp:617-640`, `SetWingID(0)` when a new team leader arrives), but the
ship object survives and in several paths the prior wing sticks (Imago's "we must
set server-side default wing to NA!" comment, `shipIGC.cpp:47,128`).

**Port.** Wing is a scalar on the ship (`Ship.WingId`, `Ship.cs:1041`) defaulting
to **0 = Command** (`c_pszWingName[0]` == "command"). Every game-over rotation
**mints a brand-new `Ship`** for each migrated peer (`AssignMigratedShip` →
`SpawnDefaultShip`, `Server.Lobby.cs:632,2709-2712`), so the wing assignment simply
cannot survive from one game to the next — the re-elected commander always lands on
Command wing.

**Verdict: not inherited.** Guard:
`CommanderLeader_DefaultsToCommandWing_AfterRotation` forces the Bravo commander's
wing to Attack (1), rotates, and asserts the fresh commander's ship is Command (0).

### #225 — "Double click + mouse movement causes AFK to uncheck itself"

**Native.** A lobby-chat-area UI bug: with AFK checked, double-clicking above
"Game Settings" (where the purple team would be) then moving the mouse unchecks
AFK. Pure client UI interaction on the native lobby's AFK checkbox.

**Port.** There is **no equivalent lobby UI**: the port's pre-flight is a
keyboard-driven `ReadyCheck` screen (`Game1.Panes.cs:1014-1045`, toggle ready with
Y / `PLAYER_READY`), with **no AFK checkbox** and no chat-area team grid to
double-click. There is nothing to reproduce or fix. Server-side ready/AFK state is
a keyboard flag, not a mouse toggle. **Documented, no code guard.**

## Related port bug noted (report-only)

The task's suggested related bug — *"when a mission ends with players still
connected, the rebuilt NotStarted mission never auto-starts"* — is the exact 221
production bug that **P598 already fixed** (2026-10-11): `RotateMissionAfterGameOver`
now preserves each migrated peer's ready flag (native preserves individual ready —
the "set everyone unready" block at `fsmission.cpp:3468-3481` is commented out) and
re-derives side aggregates, so a rebuilt mission auto-starts from preserved ready;
`GameConfig.AutoRestart` additionally ports native's unattended `bAutoRestart`.
This task's `AllowEmptyTeams_ThreeRotations_NeverThrow_AndAlwaysRestart` guard
re-validates that fix on the final tree (the rebuilt mission reaches `Started`
without manual re-ready across all three rotations). No new fix needed here — it is
not the same root cause as #42 (P598's root cause was a cleared ready flag, not a
teardown crash), so it is reported, not re-fixed.

## Verification

- Whole-solution build: **0 warnings, 0 errors** (`dotnet build -m:8`).
- New tests: 5/5 pass (`P616TrackerNativeBugTests`, 0 skipped) — verified in
  isolation and again inside every full-suite run below.
- Because no production/broadcast/session code changed, no live-bot
  reach-the-target proof is required; the loopback tests themselves drive real
  bots over real TCP.

### Full suite — two consecutive runs on the final code

Per the session rule the four assemblies were run sequentially (exactly what
`scripts/run-tests.sh` does after its single parallel build); the solution build
was clean before both runs.

| Assembly | Run 1 | Run 2 |
|---|---|---|
| `Allegiance.Lobby.Tests` | 12/12 | 12/12 |
| `Allegiance.ClientCore.MonoGame.Tests` | 1775/1778 (3 skipped) | 1775/1778 (3 skipped) |
| `Allegiance.ClientCore.Tests` | 603/603 | 603/603 |
| `Allegiance.Core.Tests` | 3957/3963 | 3953/3963 |

The Core.Tests deltas are **known, ledgered live-network load-flakes** (cited
`docs/TEST_FLAKES.md`): run 1 dropped `StationUnderAttackAlert…Defending_side…`,
`SideElimination…Eliminating_side2…`, `P591dMinefieldExport…reaches_same_side_seated_peer`,
`GameOverPlayersWire…Win_by_elimination…` (all listed in the ledger), and run 2
rotated in/among `LiveRelaunch…Carrier_without_launch_slots…` (ledger line 40,
"suspect P472"), `GameOverPlayersWire…Win_by_elimination…`, plus `CargoBayReload…
SwapPart_out_of_range_negative_mount…` which failed at its `SeatThree()` seating
helper ("Sequence contains no matching element") — a loopback bot failed to seat,
the same live-wire timing class the ledger documents for this suite. None of the
failures is a P616 test and none touches the (unchanged) production code; per the
task's rule the runs are reported as they happened, not re-run until clean.

## Files

- `tests/Allegiance.Core.Tests/P616TrackerNativeBugTests.cs` (new — guard tests)
- `docs/reports/P616_TRACKER_NATIVE_BUGS.md` (this report)
