# P416 — turret-seat keys (swap/promote/eject) — investigate what already exists first

## Why this task exists

Same key-binding survey as P413-P415. Native's "Inventory"/"Other"
command groups (`trekinput.cpp:1505-1521,1597-1601`) include a real
multi-seat turret-gunner scheme: `TK_SwapTurret1-4` (take a specific
turret seat), `TK_PromoteTurret1-4` (promote that seat's occupant —
native's captain-hands-control-to-gunner mechanic), and
`TK_Suicide` doubling as "Eject From Turret (Press 3 times)" when
seated in one. This port has real turret/passenger machinery
(`Sim/IgcEnums.cs`: `AddPassenger`/`RemovePassenger`; wire messages
`ActiveTurretUpdate.cs`/`InactiveTurretUpdate.cs`/`PassengerData.cs`;
server-side references across `Server.Broadcast.cs`,
`Server.Dispatch.cs`, `Server.Docking.cs`, `Server.ShipCommands.cs`,
`Server.Combat.cs`) but this session's grep found no
`SwapTurret`/`EnterTurret`/`BoardTurret`-named method anywhere — meaning
either a real command already exists under different naming, or seat
entry/exit is currently only reachable some other way (a UI click, an
NPC-only path, or not reachable by a real player client at all).

**This task starts with investigation, not a known gap** — unlike
P413/P414 (fully traced, confirmed orphaned) and P415 (confirmed gap,
known fix), the actual state of turret-seat control in this port is
genuinely unknown going in. Do not assume a client-side gap exists
until you've traced the real command path.

## Scope

1. Trace the full real turret/passenger flow in this port: how does a
   player currently enter a turret seat at all (if at all)? What wire
   message(s) does the client send/receive for it? Cite exact files/
   line numbers once found.
2. **If a real seat-entry/seat-switch command already exists but has no
   client key bound to it**: add the missing key(s), following native's
   real scheme (`TK_SwapTurret1-4` = take seat N; adapt to however many
   real turret slots this port's ship/hull data actually supports —
   don't assume exactly 4 if the real data says otherwise) and add
   "Eject From Turret" behavior to whatever this port's existing
   `K`/suicide key already does when seated (native reuses the same key
   with a 3-press requirement while turret-seated — check whether
   that "press 3 times while seated" nuance is worth porting exactly or
   is over-specified for this port's simpler suicide-key shape; decide
   and document).
3. **If no real player-facing seat command exists at all** (turret
   machinery is currently server/NPC-internal only): say so plainly,
   scope what the minimal real feature would need (a request message +
   server handler, mirroring however `AddPassenger`/`RemovePassenger`
   already work internally), and either build it if it's a clean,
   contained addition, or stop and report back with a clear "this is a
   bigger feature than a key binding, here's what it would take" rather
   than half-building it.
4. `TK_PromoteTurret1-4`'s "promote" semantics specifically — trace
   native's real meaning (`WinTrek.cpp`/`FedSrv.CPP` — search
   "PromoteTurret" or the captain/gunner hand-off logic) before deciding
   whether this port's current model even has a captain/gunner
   distinction to promote between.

## Explicitly out of scope

- Any change to combat/damage/turret-firing logic itself — seat
  entry/control only.
- P368-P415 — unrelated, don't touch.

## Verification — real proof, not just build/test

- Whatever you build, verify it live: a real player enters a turret
  seat via the new/newly-bound key, confirm server state
  (`ActiveTurretUpdate`/occupancy) reflects it correctly, and ejecting
  returns control to the main ship correctly.
- If you conclude no real feature exists to attach a key to, no live
  proof is required — the report's honest investigation is the
  deliverable instead.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. Either the real fix (committed on `master`, not pushed) or a
   documented investigation verdict if no player-facing gap turned out
   to exist (or if it's a bigger feature than this task's scope).
2. Report doc `P416_TURRET_SEAT_KEYS.md` in the repo root: what you
   found, what (if anything) you built, and the verification performed.
