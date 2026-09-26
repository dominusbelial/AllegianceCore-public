# P384 — split Game1.cs into topic partial-class files (mirror the Server.*.cs pattern)

## Why this task exists

`src/Allegiance.ClientCore.MonoGame/Game1.cs` is 6217 lines, one single
`public sealed class Game1 : Game`, and growing every time a client feature
lands. This codebase already has a real, established precedent for exactly
this problem on the server side: `Server` is
`public sealed partial class Server : IDisposable` in `Server.cs` (806
lines — fields, constructor, main-loop entry points), with the actual
behavior split by topic into sibling files that each declare
`public sealed partial class Server` again: `Server.Lifecycle.cs` (691),
`Server.Economy.cs` (866), `Server.ShipCommands.cs` (1732),
`Server.Dispatch.cs` (1805), `Server.Combat.cs` (1854),
`Server.Docking.cs` (1913), `Server.Broadcast.cs` (2118),
`Server.Lobby.cs` (2656). This task is the identical treatment for `Game1`.

**Pure structural refactor — zero behavior change.** Every method, field,
and property moves verbatim to a new file; nothing is rewritten, renamed,
or "cleaned up" along the way. This is a mechanical extraction, not a
redesign.

## Pattern to mirror exactly

1. `Game1.cs` becomes the **facade**: `public sealed partial class Game1 :
   Game` (the `partial` keyword and the `: Game` base go on this one
   declaration only). It keeps the field/property declarations, the
   constructor, and the core MonoGame lifecycle entry points
   (`Initialize`, `LoadContent`, `Update`, `Draw`, `UnloadContent` and
   whatever else XNA/MonoGame requires to stay on the primary class body
   — check what actually needs to stay vs. what can move). Mirror
   `Server.cs`'s shape: it's almost entirely field declarations plus a
   handful of true entry points, everything else lives in a sibling.
2. Each sibling is `Game1.<Topic>.cs`, `public sealed partial class Game1`
   (no base-class list — only the primary declaration has one). Group
   moved members by real topic, discovered by actually reading the file,
   not guessed in advance. From partial reading this session, at least
   these real topic clusters exist and are a reasonable starting point
   (confirm/adjust against the full file — don't force a bad fit):
   - **Input/flight controls** — keyboard/joystick/mouse sampling,
     `FlightInput`/`JoystickFlightController` integration, the leader
     bot-command `X`-key handling (~`Game1.cs:1252`, `:1721-1894`).
   - **Camera** — chase/cockpit/turret/external view modes, projection
     helpers (`ProjectToScreen`, ~`Game1.cs:5587-5600`), turret-gunner
     camera pose (~`:5602-5626`).
   - **Target-picking / HUD** — `CollectTargetCandidates`,
     `SelectCandidates`, `GatherSectorCandidates`, lead-indicator
     projection (~`:5718-5900`).
   - **Rendering** — ship/effect/particle/warp drawing, minimap/starmap
     (`DrawStarmap`, ~`:4360+`).
   - **Networking/session glue** — `ConnectionSession` event callbacks,
     chat, lobby/team UI panes (scoreboard, ready-check, sector-info,
     teleport pane, docked screen).
   - **Audio** — sound-cue triggering (new-contact cues, sector-threat
     warnings, etc. from P372-P374).
   Adjust this list freely based on what's actually there — the real file
   is the source of truth, this is a starting hypothesis only.
3. Target sibling sizes in the same range `Server.*.cs` landed in
   (roughly 700-2700 lines) — if a topic is much bigger than that, split
   it again by sub-topic rather than leaving one huge sibling.
4. No `#region` blocks, no compatibility shims, no re-exports — a straight
   textual move of each member into its new home file, `namespace` and
   `using` directives duplicated into each sibling as needed (matching how
   the `Server.*.cs` siblings each carry their own `using` list already).

## Explicitly out of scope

- Any behavior change, rename, bug fix, or "while I'm in here" cleanup —
  if something looks wrong while moving it, note it in the report, don't
  fix it in this task.
- Splitting any other file (this is `Game1.cs` only).
- Changing the public API surface of `Game1` visible to other projects (if
  any) — internal/private members can be regrouped freely since they're
  only visible within the partial class itself, but don't change any
  member's accessibility level.

## Verification — real proof, not just build/test

- Whole-solution build: 0 warnings, 0 errors, before AND after — diff the
  two build outputs if helpful to confirm nothing new appeared.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least 2x:
  identical pass/fail set to a pre-refactor baseline run (only the
  pre-existing documented live-wire timing flakes expected) — run the
  suite once BEFORE starting the split to have a real baseline to diff
  against, not just once at the end.
- **Live proof, required** (per this project's standing rule that a
  MonoGame client change needs a real feel/scale check, not just build+
  test): launch a real client against the running server, fly for a
  minute, confirm flight controls, camera switching, target-picking (T
  key), HUD, and any pane you touched (F6/F8/scoreboard/etc.) all still
  behave identically to before the split. This is a pure refactor, so
  "identical" is the actual bar — any observed behavior difference is a
  bug in the extraction, not an acceptable side effect.
- `git diff --stat` on the final result should show `Game1.cs` shrinking
  substantially and several new `Game1.<Topic>.cs` files appearing, with
  the total line count across all of them roughly matching the original
  (a big net line-count change in either direction signals something was
  duplicated or dropped, not just moved).

## Deliverables

1. The split, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P384_GAME1_DECOMPOSITION.md` in the repo root: the final
   topic breakdown (file name → line count → what it holds), confirmation
   of the before/after line-count reconciliation, and the live verification
   performed.
