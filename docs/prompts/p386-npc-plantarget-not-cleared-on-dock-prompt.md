# P386 — fix: an NPC's return-to-sell `PlanTarget` is never cleared on dock, permanently stalling the miner decision cycle after its first sell trip

## Why this task exists

Live-tested this session (real server, real debug-mode boot, ship 8096 —
a P382-spawned initial miner). The **first** cycle worked perfectly end to
end: spawned docked → P381 auto-undocked it after 10s → warped to Alderan
→ mined a rock to full (150/150 ore) → warped home → docked → sold 150 ore
for 750 credits. Every cycle **since** has been broken: the ship now just
oscillates `undock → (instant, no travel) → dock` every 10 seconds forever,
never warping anywhere, never mining, never selling again. Confirmed via
raw server log — zero `[warp]`/`[mine]`/`[mine-sell]` lines appear after
the very first cycle, only repeated `[npc-autolaunch]`/`[dock]` pairs.

## Confirmed root cause — exact citation

`Ship.cs:5999-6030`, the NPC default-order dispatch:
```csharp
if (PlanTarget is null)
{
    if (AcceptedTarget is { } accepted) { ... }
    else if (IsNpc)
    {
        if (PilotType == PilotTypeMiner)
        {
            if (Ore >= MineCapacity && MiningAsteroidTarget is not null)
                MiningAsteroidTarget = null;
            if (MiningAsteroidTarget is null && StationId is null)
                DecideMiningOrSelling();   // ← the real mine-or-sell re-decision
        }
        ...
```
The **entire** re-decision block — including the mining-target search — is
gated on `PlanTarget is null`. `DecideMiningOrSelling()` (`Ship.cs:5115`)
sets `PlanTarget = station` for the sell leg (`Ship.cs:5137`) once the bag
is full. **Nothing anywhere in the codebase ever sets `PlanTarget` back to
`null`** — confirmed by grepping the whole solution for `PlanTarget = null`
/`PlanTarget = <null>` outside `Ship.cs` itself: zero hits in
`Sector.cs`/any `Server*.cs` file. So once a miner completes one sell
trip, `PlanTarget` permanently holds "go to the station I'm now sitting
at." Every later tick's dispatch sees `PlanTarget is not null`, skips the
*entire* block above (never re-checks `AcceptedTarget`, never re-enters
the miner cycle), and whatever movement/arrival code drives the ship
toward `PlanTarget` sees it has already arrived (position unchanged since
last dock) and immediately signals dock again. P381's new auto-launch
pass is what exposed this — nothing previously relaunched a docked NPC
more than once, so this staleness never mattered before.

**Exact fix site**: `ServerHost.cs`, the dock-arrival handler around lines
656-696 (`DockWithStationEvent`'s docking branch — `sh.StationId =
ds.Station.StationId;` at line 663, through the ore-sell block, to the
`[dock]` log at line 698). This is the one real "a ship has just arrived
and docked" transition point for every docking ship (player or NPC),
confirmed to already do the analogous "arrival" reset for ore
(`sh.Ore = 0f`, line 694) — `PlanTarget`/`PlanCommandId` need the same
treatment here.

## Scope

1. In `ServerHost.cs`'s dock-arrival handler, once `sh.StationId` is set
   (the "just arrived" transition, not the defensive already-docked early
   return above it), clear `sh.PlanTarget = null;` and
   `sh.PlanCommandId = OrderChange.CidNone;` (matching the existing
   `AcceptedCommandId`/`QueuedCommandId` "unset slot" convention already
   used elsewhere in `Ship.cs`, e.g. `OrderChange.CidNone` default at
   `Ship.cs:971`).
2. **No `IsNpc` guard needed** — confirmed `PlanTarget` is exclusively
   NPC-produced (`Ship.cs:961-963`'s own doc comment: "an NPC's
   return-to-base station... the slot's only producer in this branch is
   `FindNearestFriendlyStation`"; nothing sets it for a real player ship),
   so clearing it unconditionally on every dock is a safe no-op for player
   ships and the correct fix for NPCs. Don't add a redundant guard.
3. Log plainly, matching the existing `[dock]`/`[npc]` convention, e.g.
   append to or place near the existing `dockNote` line:
   `[npc] ship {id} arrived — PlanTarget cleared, ready to re-decide next tick` (NPC-only log line is fine even though the field-clear itself isn't
   gated — just don't log it for a real player dock).
4. Double-check `AcceptedTarget`/`AcceptedCommandId` (the OTHER
   return-to-base slot, promoted into `PlanTarget` at `Ship.cs:6001-6004`)
   for the same staleness risk — if anything can set `AcceptedTarget` to a
   station that later gets arrived-at without ever being cleared, fix that
   too in the same dock-arrival handler for consistency. Investigate and
   fix if real; don't invent a scenario that can't actually occur.

## Explicitly out of scope

- `ResolveMining`'s existing documented simplifications (no crowding
  arbitration) — unrelated, don't touch.
- Any change to P381's auto-launch gating itself — it's correct; this bug
  is entirely in the decision-cycle side, not the launch-trigger side.
- P368-P385 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: reproduce the bug first against the pristine
  (pre-fix) tree — real server, a spawned/auto-launched miner, confirm it
  degenerates into the undock/instant-redock loop after its first sell
  (matching what was observed live this session). Then apply the fix and
  confirm the SAME ship now genuinely repeats the full cycle at least 3
  times in a row: undock → warp → mine → warp → dock → sell → undock →
  warp → mine → ... — real `[warp]`/`[mine]`/`[mine-sell]` log lines
  appearing on every cycle, not just the first.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least 2x:
  only the pre-existing documented live-wire timing flakes expected. Pay
  particular attention to `MinerDecisionCycleLiveTests`/
  `NpcAutoLaunchLiveTests`/`InitialMinerSpawnLiveTests` (P156/P381/P382) —
  if any of them only ever exercised a single cycle and would have masked
  this bug, extend one of them to run multiple cycles so this regression
  class is actually covered going forward.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P386_NPC_PLANTARGET_DOCK_RESET.md` in the repo root: the
   root-cause explanation, the fix, and the live multi-cycle proof
   (before: degenerates after cycle 1; after: repeats indefinitely).
