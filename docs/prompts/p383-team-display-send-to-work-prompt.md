# P383 — F6 Team display: roster pane + "send docked utility ship back to work"

## Why this task exists

From the native training script (`docs/NATIVE_TRAINING_SCRIPT.md`, transcribed
this session straight from `~/Development/Allegiance/src/training/mission5.cpp`
source comments — no audio asset exists, the script text is the source of
truth): **tm_5_20** — "The Team display is very helpful for keeping track of
utility ships and getting the status of your team members. If a utility ship
is docked, you can use that screen to send them back to work. You can press
the F6 key to bring up the Team display —". This is a player-facing manual
override sitting next to P381 (which auto-undocks a docked NPC on a timer) —
this task is the same undock, triggered by hand from a roster pane instead.

**Native frames this as team-commander/officer content, not a general
player action** — the Mission 1 hangar-tour narration (`mission2.cpp`'s
predecessor, quoted in the training doc) says explicitly: "These areas of
the hangar are primarily for use by the team commander. If you are not in
line for officer duty, don't worry about them... these topics will be
covered in Training Mission 5, Command View." Match this port's existing
leader-only convention (see P366 below) rather than opening it to every
player.

## Confirmed already built — reuse exactly, do not rebuild

- **Server-side undock-on-command already exists and needs zero changes.**
  `Server.ShipCommands.cs`'s `HandleOrderChange`, docked-NPC branch
  (~line 1090-1122): any `ORDER_CHANGE` commandID other than
  `c_cidDoNothing` sent for a docked NPC on the sender's own side sets
  `StayDocked = false` and calls `UndockShipState(target, undockSector)` —
  the exact "send back to work" mechanic. Triggered by `FM_CS_ORDER_CHANGE`
  (fmid 93, `Wire/OrderChange.cs`), dispatched at `Server.Dispatch.cs:1679`.
  Not team-leader-restricted server-side (resolves the target via
  `FindSameSideShip`, no `IsTeamLeader` check) — any leader gating is a
  client-side UI convention only, same as native.
- **The exact client call to reuse**: P366 (`docs/P366_TEAM_LEADER_UI.md`,
  `LeaderBotCommandResolver`, `Allegiance.ClientCore/State/LeaderBotCommand.cs`)
  already sends this precise command shape —
  `_session.SendOrderChange(targetShipId, OrderChange.CidGoto, OrderChange.CmdAccepted)`
  (`Game1.cs:~1881-1894`, the `X` key) — for a leader-selected docked bot.
  This task's "send back to work" button calls the same
  `ConnectionSession.SendOrderChange` with the same arguments, just from a
  different UI entry point (a roster list instead of the current T-select
  target). No new wire message, no new server logic.
- **P366's `world.IsTeamLeader` gate** (`LeaderBotCommand.cs:27`) is the
  existing leader-check to reuse for this pane too, for consistency (see
  "Why" above).

## Confirmed missing — this is genuinely new client work

1. **No roster data source exists.** P366's resolver only ever looks at the
   single currently-T-selected/targeted ship (`Game1.cs:4342`,
   `ResolveLeaderBotCommand`, fed from `_autoTarget`) — there is no
   "list every ship on my own side" query anywhere. This task needs a new
   pure, headless-testable model (matching the P273
   `TeleportPaneModel`/P268 `DockedScreenRenderer` shape already
   established in this codebase) that filters `ClientWorld.Ships` by
   `SideId == MySideId`, regardless of sector/visibility — including ships
   the local player has never rendered/seen.
2. **No F6 pane exists.** Confirmed via `docs/KEYBINDING_AUDIT_P220.md:169`
   — native's `TK_ConModeTeam` family is "Bucket C, no backing feature at
   all." Build a new pane (reuse the existing translucent-overlay
   rendering convention already used for `ScoreboardOverlayRenderer`/
   `SectorInfoPaneLayout`/P273's `TeleportLayout` — same look/feel, new
   content), bound to F6, listing the roster with a cursor/select, same
   input-handling shape P273 already established (up/down to move
   selection, an action key to activate).
3. **No right-click/context-menu system exists** (`Game1.cs` has zero
   `ContextMenu`/`RightClick` hits) — don't build one for this task; drive
   selection and the "send back to work" action from the keyboard, matching
   P366's and P273's existing precedent (a dedicated key, not a mouse menu).

## Scope

1. **`Rendering/TeamPaneModel.cs`** (new, pure/headless, mirror
   `TeleportPaneModel`'s shape exactly): given `ClientWorld` + local side id,
   produce the roster (ship id, display name/character name, current
   sector, docked-station-or-null). Own a cursor. Expose which entries are
   "docked NPC" (eligible for send-back-to-work: `IsNpc && StationId != -1`
   — per **tm_5_14 vs tm_5_15**, only drones "always accept orders"; a real
   player-piloted teammate is not a valid send-back-to-work target even if
   docked, so filter it out of that specific action, though it can still
   appear in the roster for visibility).
2. **`Rendering/TeamLayout.cs`** (new, pure ASCII rows, mirror
   `TeleportLayout`): render the roster with the cursor highlighted, one
   row per ship (name, side-relative status, docked/flying, sector).
3. **F6 binding in `Game1.cs`**: open/close the pane (same toggle shape as
   the existing F8 teleport-pane binding — find and mirror it exactly), gate
   opening it on `world.IsTeamLeader` (same as P366's existing convention —
   see "Why" above; if `IsTeamLeader` is false, either don't bind F6 at all
   or show the pane read-only with the send-to-work action disabled — pick
   whichever this codebase's existing leader-gate precedent already does
   for an analogous case, don't invent a third behavior).
4. **The "send back to work" action**: for the currently-selected roster
   row, only when it's an eligible docked NPC (see #1), call
   `_session.SendOrderChange(targetShipId, OrderChange.CidGoto, OrderChange.CmdAccepted)`
   — identical arguments to P366's existing call. Reuse, don't reimplement.
5. Log plainly, matching this port's existing convention (e.g.
   `[team-pane] leader {id} sent docked NPC {targetId} back to work`).

## Explicitly out of scope (separate future task, do not build here)

- **"Take Me To" / jump the live view to another ship's sector without
  moving your own ship.** Confirmed nowhere in this codebase — every
  render/filter call site (`Game1.cs`, 9+ separate `short mySector = ...`
  reads, e.g. lines 2883, 3835, 3934, 4003, 4058, 4454, 4488, 5673) derives
  the rendered sector directly and independently from the *local player's
  own ship's real* `SectorId`. There is no decoupled "sector currently
  being viewed" concept at all. Native's real equivalent
  (`pWindow->SetViewMode(TrekWindow::vmCommand)`,
  `EnableDisplacementCommandView(true)`, `mission5.cpp:103-106`) is a whole
  separate tactical camera MODE (`docs/KEYBINDING_AUDIT_P220.md:157-158`
  confirms the entire native camera-view-mode family is unported, Bucket
  C). Building this properly is a real, separate, larger task — don't
  attempt a partial/hacky version of it here just to make F6 feel complete;
  a roster pane that only selects+commands ships (no view-jump) is a
  legitimate, useful, shippable slice on its own.
- Any command targeting a real player-piloted teammate ship (the tm_5_15
  "give an order to a pilot" scenario) — the generic `ORDER_CHANGE`
  acceptance path already exists server-side (§ above) but wiring a UI for
  it is separate scope; this task's UI only ever sends the docked-NPC
  launch command.
- Any change to P366's existing `X`-key/T-select leader-bot-command flow —
  must keep working byte-for-byte unchanged; this task adds a second,
  independent entry point to the same underlying server command, it does
  not replace or touch the first.
- P368-P382 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: two real clients, one side's player as team
  leader. Get a same-side NPC miner docked (either via the existing P156/
  P160 spawn-test harness pattern, or by waiting for a live miner to dock
  naturally). As the leader, press F6, confirm the roster shows that ship
  (docked, correct sector), select it, trigger "send back to work", and
  confirm it actually undocks and resumes its normal autonomous mining
  loop (same observable behavior P381/P156 already prove) — with zero
  `ORDER_CHANGE` sent through the pre-existing P366 T-select/X-key path.
  Confirm a non-leader client either has no F6 binding or sees a read-only
  pane (per whichever precedent #3 above lands on) and cannot trigger the
  action. Confirm P366's own `X`-key flow still works unchanged on the
  leader client.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least 2x:
  only the pre-existing documented live-wire timing flakes expected.

## Deliverables

1. `Rendering/TeamPaneModel.cs`, `Rendering/TeamLayout.cs`, the F6 binding
   and send-back-to-work wiring in `Game1.cs`, committed on `master` (not
   pushed — Domingo pushes).
2. Report doc `P383_TEAM_DISPLAY_SEND_TO_WORK.md` in the repo root: the
   roster model, the leader-gate decision and precedent followed, and the
   live verification performed.
