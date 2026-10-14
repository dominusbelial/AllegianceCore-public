# P424 — accept/reject/clear keys for a received queued order (real server plumbing already exists)

## Why this task exists

Domingo, from watching footage: *"did you know you could receive
orders and either one player would accept or ignore them?"* Confirmed
real against native, and — same pattern as cloak/ripcord/promote this
session — the server-side plumbing already exists in this port with
zero client-facing way to use it.

## Confirmed real native mechanic

Three real keys (`~/Development/Allegiance/src/WinTrek/
WinTrek.cpp:10389-10406`):
- `TK_AcceptCommand`/`TK_ExecuteCommand` → `AcceptQueuedCommand(bExecute)`
  — promotes the ship's queued order (`c_cmdQueued`) to its accepted
  order (`c_cmdAccepted`).
- `TK_RejectCommand` → `RejectQueuedCommand(true, true)` — clears the
  queued order (with a "Negative." voice-line reply to whoever sent
  it, via `SendChat`).
- `TK_ClearCommand` → `SetAccepted(NULL, c_cidNone)` — cancels whatever
  is already accepted (distinct from rejecting a still-queued one).

**A real 15-second auto-reject** (`c_dtRejectQueuedCommand = 15.0f`,
`WinTrek.cpp:84,7686-7687`): a queued order that's neither accepted nor
explicitly rejected auto-rejects itself after 15s of real inaction —
genuinely modeling "ignore it" as its own real outcome, not just
"accept" vs. "explicit reject."

**A real dual-use nuance**: the exact same two keys also handle a
*pending money-donation request* from another player
(`AcceptQueuedCommand`/`RejectQueuedCommand`'s own bodies,
`WinTrek.cpp:10583-10650`, gated on `GetLastMoneyRequest() != 0`) —
accepting donates the requested amount (clamped to what you actually
have), rejecting sends the same "Negative." reply. Investigate whether
this port's already-existing money/donation wire path (`SendPlayerDonate`,
confirmed real and already used elsewhere this session) is the right
thing to reuse here, or whether it's a genuinely separate flow — don't
assume, trace it.

## Confirmed port state — the real gap

This port already has the underlying state and server-side handling:
- `Ship.QueuedCommandId` (`src/Allegiance.Core/Sim/Ship.cs:1099`) — a
  real, live field.
- `OrderChange.CmdQueued` (`Server.ShipCommands.cs:1275-1277`) — a real
  server handler that sets another ship's queued command.
- `OrderChange.CmdAccepted` (`Server.ShipCommands.cs:1195-1228`) — a
  real handler for the accepted-target slot (`Ship.AcceptedShipTarget`/
  `AcceptedShipCommandId`, P154's own doc comment there already cites
  native's `SetAccepted` menu action precisely).

**Confirmed zero client-side usage anywhere** — grepped
`Allegiance.ClientCore`/`Allegiance.ClientCore.MonoGame`: no key, no
send method, nothing reads or reacts to `QueuedCommandId` at all.

## Scope

1. Add the real Accept/Reject/Clear keys to the MonoGame client (check
   `KeyBindingTable.cs` for free keys), sending the existing real
   `OrderChange` wire messages this port's server already handles
   (`CmdAccepted` to promote/clear, and whatever clears `CmdQueued` for
   reject — trace the exact existing send-side shape from how other
   `OrderChange` sends already work in this codebase, e.g. the P153/P154
   `SetTarget`/`SetAccepted` pattern already cited in
   `Server.ShipCommands.cs`'s own comments).
2. Add the real 15-second auto-reject timer client-side (or confirm/add
   the equivalent server-side if that's the more faithful port location
   — check whether this port's existing per-tick ship update loop is
   the more natural home for it, matching how other timed behaviors
   like ripcord's countdown already live server-side).
3. **Investigate before building**: does anything in this port already
   set another (real player's) ship's `QueuedCommandId` at all — i.e.,
   is there any existing "give an order" path to test acceptance
   against? If yes, wire against it. If genuinely nothing exists yet
   (native's real "give order" UI is a mouse-driven Command-console
   panel this port has no equivalent of — already established this
   session as legitimately out of scope for other reasons), say so
   plainly and scope a minimal, honest way to test the accept/reject
   flow (e.g. a debug/test-only helper that queues a command, clearly
   marked as test-only, not a real player-facing feature) rather than
   leaving the new keys completely unverifiable.
4. Decide and document the money-request dual-use from the native
   nuance above: build it if it cleanly reuses existing donation
   plumbing, or explicitly scope it out as a separate follow-up if it
   doesn't.

## Explicitly out of scope

- The "give an order" issuing side in general (native's mouse-driven
  Command-console squad-management UI) — a much bigger, architecturally
  different feature; only build the minimal test-helper needed to
  verify accept/reject per point 3 above, not the real UI.
- P368-P423 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving Accept promotes queued→accepted correctly, Reject
  clears the queued slot (and, if built, sends the real "Negative."
  reply), and the 15s auto-reject timer fires correctly when nothing
  is pressed.
- **Live proof, required**: using whatever real-or-minimal-test path
  point 3 establishes, queue a real order for a ship, confirm pressing
  Accept promotes it and the ship acts on it, confirm pressing Reject
  clears it, and confirm true inaction auto-rejects after 15s.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P424_ACCEPT_REJECT_QUEUED_ORDERS.md` in the repo root:
   the keys added, the auto-reject timer's real location, the
   investigation result for point 3 (and whatever test path it led
   to), the money-request decision, and the verification performed.
