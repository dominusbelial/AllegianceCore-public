# P366 — Team leader-only UI: command bot teammates to dock/launch

## Why this task exists

Domingo asked about "director only" UI features. Investigated: the
literal term "director" in this codebase is `TeamDirector`
(`~/Development/Allegiance/src/Wopr/Wopr/TeamDirector.cs`), an AI
bot-strategy framework, not a human player-facing role — a dead end.
The real match is the elected **Team Leader** role, a genuine native
mechanic with real leader-exclusive UI.

**Confirmed real native mechanic**
(`~/Development/Allegiance/src/WinTrek/WinTrek.cpp`):
- `SET_TEAM_LEADER` wire message + `IsTeamLeader()` — a real elected
  leadership state, settable via a right-click "Make Leader" context
  menu item (line 4309, handler `contextMakeLeader()` at line 3722,
  dispatch at 6381-6382).
- **The real leader-exclusive capability**: right-clicking a bot/drone
  teammate shows a "Dock"/"Launch" command (or "Dock/Flee" specifically
  for a carrier) — but **only when the local player `IsTeamLeader()`
  AND the target is a bot** (`GetPilotType() < c_ptPlayer`),
  `WinTrek.cpp:4349-4368`. This lets the team leader remotely command
  AI teammates to dock or launch — a real, distinct, exclusive
  capability normal players don't have.
- A leader-only cross-side "Accept" context action also exists
  (`WinTrek.cpp:4375-4379`), likely alliance/diplomacy-related — **not
  fully traced this session, exact semantics unclear**, investigate
  before building it (see Scope item 1).

**Port status, confirmed**: the underlying leadership mechanic is
ALREADY correctly ported server-side —
`~/Development/AllegianceCore/src/Allegiance.Core/Wire/SetTeamLeader.cs`
exists, `IsTeamLeader` is real, live state used in
`Server.Lobby.cs`/`Server.Economy.cs`/`Mission.cs`/`PlayerInfo.cs`.
**But zero references to `IsTeamLeader`/`TeamLeader` exist anywhere in
`Allegiance.ClientCore.MonoGame/`** (confirmed via direct search) —
the leadership vote/tracking works end-to-end, but no leader-exclusive
UI has ever been built on the client. This task is a pure client
addition; no new wire/sim work needed, the leadership state is already
real and already flowing to the client (confirm this directly — the
state must already be decoded into `ClientWorld` somewhere even if
unused, check before assuming new wire decode work is needed).

## Scope

1. **Investigate the cross-side "Accept" action first**
   (`WinTrek.cpp:4375-4379`) — read enough surrounding context to
   understand what it actually does (alliance request acceptance?
   side-change approval? something else?) before deciding whether it's
   in scope for this task or a separate one. If its semantics don't
   depend on anything this port doesn't already have (an alliance
   system, e.g. — note this project's own precedent that "no alliance
   concept" is an accepted, documented simplification elsewhere),
   consider it explicitly out of scope and say so plainly rather than
   building something half-right.
2. **Confirm exactly how `IsTeamLeader` state already reaches (or
   would need to reach) the client** — check `ClientWorld`/`ShipEntity`
   or wherever side/player state already gets decoded, and the real
   `SetTeamLeader` wire message's actual field shape.
3. **Build the leader-only bot dock/launch command**: when the local
   player is the team leader, right-clicking (or whatever this port's
   existing UI convention is for a target-context-action — check how
   other target-interaction UI already works, e.g. the target
   selection/autopilot system from P212/P345/P359) a bot teammate shows
   a real "Dock"/"Launch" command that actually issues the
   corresponding order to that bot (reuse the existing order/command
   wire machinery already used elsewhere for ship commands —
   `SendOrderChange` or equivalent, check what's already wired for
   commanding another ship).
4. Gate this UI so it's ONLY visible/usable when the local player
   genuinely `IsTeamLeader` — a non-leader should see no such option at
   all (matching native's real gate, not just a disabled-but-visible
   button).

## Explicitly out of scope

- The `TeamDirector` AI bot-strategy system itself — that's an
  internal AI framework (`Wopr/TeamDirector.cs`), not a player-facing
  UI feature, not what this task is about.
- Rebuilding the leadership ELECTION/voting mechanic itself — already
  correctly ported server-side (`SetTeamLeader.cs` etc.), this task
  only adds the missing client-side UI that USES that existing state.
- Any alliance/diplomacy system, unless investigation (Scope item 1)
  finds the cross-side "Accept" action doesn't actually need one —
  don't build a new alliance concept for this task.
- P358-P365, P354-P357 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: a real multiplayer session (2+ real peers,
  at least one bot teammate) where the local player becomes team
  leader (via the existing, already-working leadership mechanic),
  confirm the leader-only bot dock/launch UI appears and actually
  issues a real, effective command (the bot visibly docks or launches
  in response). Confirm a NON-leader player does NOT see this option
  at all for the same bot.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The leader-only bot dock/launch UI, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P366_TEAM_LEADER_UI.md` in the repo root: what the
   cross-side "Accept" action turned out to be (in scope or correctly
   excluded, with real evidence either way), how leadership state
   reaches the client, what was built, and the live leader-vs-non-
   leader verification performed.
