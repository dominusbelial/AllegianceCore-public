# P480 — mission rotation keeps connected players seated (native Vacate semantics)

## Why this task exists

Native keeps every player **in the same mission** across a game-over. Native
source is at `~/Development/Allegiance/src/`. `CFSMission::GameOver` sends
GAME_OVER and then:
- `Vacate()` (`FedSrv/fsmission.cpp:2604-2622`) deletes every drone and
  calls `pfsShip->Reset(true)` on every **player**;
- `SetStage(STAGE_NOTSTARTED)` (`:3490`) and a per-side ready and money
  reset (`:3493-3521`).

The players stay connected, stay on their sides, and see `MISSION_STAGE`
NotStarted. Their game-over screen's Back button then takes them to the team
lobby (`WinTrek/gameoverscreen.cpp:609-625`, stage != `STAGE_OVER`), ready
for the next game.

**The port diverged.** P439 (`9c445a8c`) swaps a freshly assembled mission
object into the same `_missions` index. Peers still attached to the old
mission are orphaned: their ships freeze, and they must reconnect. P439's
report says this explicitly (`docs/reports/P439_MISSION_OVER_ROTATION.md`,
"Orphaned-peer audit", ~lines 130-150) and names this task as the
follow-up. P455b (`ba41a3f8`) hit the same missing piece.

## Scope

1. **Migrate the old mission's connected peers into the rotated mission**
   at rotation time, as native `Vacate` + `Reset(true)` would leave them:
   - still connected;
   - seated on the **same side id**. Sides map 1:1 because the new mission
     is assembled from the same `GameConfig`. Verify that rather than
     assume it.
   - with a fresh docked, lobby-state ship in the new mission;
   - with leadership and mission ownership re-established, as native keeps
     them;
   - with ready state cleared, and money reset per native `:3493-3521`,
     composing with P443's starting money.

   Old-mission drones are **not** carried over, since native deletes them.
2. **Send each migrated peer what a fresh joiner of a NotStarted mission
   receives**, so its client state is correct for the new world. Reuse the
   existing join-burst path; don't hand-build a parallel one:
   - `MISSION_STAGE` NotStarted;
   - the new world's clusters, stations and EXPORTs;
   - side, leader and ready info.
   Clear any per-peer latches keyed to the old mission (`_teammateExportSent`,
   `_crossSideExportSent`, `_teammatePlayerInfoSent` and the rest P439
   audited), so the new world's identity messages actually get sent.
3. Remove the orphaned-peer handling P439 added where it's now dead code,
   keeping the ship-id allocator safety it established.

## Explicitly out of scope

- Client UI: the game-over screen's Back button and the team-lobby screen.
  That's **P481**, next.
- The side-only game-over for an eliminated side (P455b). The mission isn't
  rotating there.
- Enabling goal-based win modes by config.

## Verification

- A loopback wire test with 2 sides, 2 Bots per side. Drive a real
  game-over (P455 elimination or the existing `#draw` ballot chain), then
  assert that:
  - every Bot is still connected, gets `MISSION_STAGE` NotStarted, and is
    on its original side id in the **new** mission;
  - each Bot receives the new world's EXPORTs;
  - all four can ready up and the new game actually starts (countdown to
    Started) **without reconnecting**.
- **Run it against unfixed code first** and record the failure. Today the
  Bots stay orphaned in the old mission.
- Existing P439, P455 and P455b tests stay green.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full `/home/domingo/.dotnet/dotnet test`, 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P480_ROTATION_KEEPS_PLAYERS.md`. Cover the
   native `Vacate`/`Reset` mapping, what each migrated peer receives, and
   the latch audit.
