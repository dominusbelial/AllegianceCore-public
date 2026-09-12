# P186 — leader-gated team-lobby config toggles

## What changed

Four new wire codecs (`LockLobby`(43), `LockSides`(44), `ChangeTeamCiv`(51),
`AutoAccept`(52)) and their server dispatch, following the established
forward-then-apply pattern:

- **`LOCK_LOBBY`(43)** — refused unless sender is the mission owner
  (`Server.IsMissionOwnerPlayer`, new helper: owning-side leader per
  `Mission.MissionOwnerSideId`). On success, relays whole-mission and sets
  `Mission.LockLobby`. No enforcement hook exists in this port (see below) —
  wire-complete only.
- **`LOCK_SIDES`(44)** — same owner gate, sets `Mission.LockSides`. **Has a
  real hook**: the P184 `POSITIONREQ` handler now refuses a side-switch with
  `DELPOSITIONREQ(DPR_SidesLocked)` when `Mission.LockSides` is true, checked
  before the leaderless/auto-accept branches.
- **`AUTO_ACCEPT`(52)** — refused unless sender is the *named* side's current
  leader, with the side re-derived from the sender's own ship (defensive,
  doesn't trust the message's `iSide` for the gate — only requires it match
  the sender's own side). Sets `Side.AutoAccept`. Turning it **on** sweeps
  P184's pending-request map for that side and seats each requester via
  `TryJoinSide` (new `Server.SweepAutoAcceptPending`), removing the pending
  entry regardless of outcome. The `iSide == NA` mission-owner-level variant
  is out of scope (logged and ignored — no such concept in this port).
- **`CHANGE_TEAM_CIV`(51)** — refused unless sender is the named side's
  current leader **and** `Mission.Stage == NotStarted`, and the named
  `civID` resolves in `Mission.Core.Civilizations`. On success, relays
  whole-mission and sets `Side.Civilization`. The `random` field is decoded
  but ignored (no faction-randomization concept in this port).

## `Side.Civilization` mutability investigation

`Civilization` was `{ get; }`, set once at construction. Widened to
`{ get; set; }`. Checked every consumer for a hidden assumption of
immutability: `Side.Buckets` (P119's construction-bucket catalog, via
`BucketCatalog.ForSide`) is seeded once at `Mission.Assemble` from a single
fixed synthetic bucket referencing hull 504 directly (`Bucket.cs:242-258`) —
**it does not key off civilization at all**, so no bucket recomputation is
needed on a civ swap. The only other live readers of `Side.Civilization`
(`Lifepod`, `IncomeMoney`, `CivilizationId`) read it live each time, so they
see the new value automatically. No inconsistency found; no recompute logic
added because none is needed.

## Divergences from the prompt, verified against native

None found beyond what the prompt already flagged as expected:
- `SET_TEAM_INVESTOR`(122) — confirmed dead in native (zero handler/sender
  outside `Inc/Messages.h` and Wopr bindings), built nothing, as scoped.
- `LOCK_LOBBY`'s real gating effect (refusing brand-new lobby joiners) has
  no hook point in this port's architecture — stated in code comments and
  here rather than inventing a join-flow.
- Per-player score-reset on civ change (native `SetSideCiv`,
  `fsmission.cpp:5274-5283`) skipped — no player-score-persistence system
  exists in this port (same gap that excluded `GAME_OVER_PLAYERS`).

## Verification

`dotnet build`: clean, 0 warnings/0 errors. `dotnet test`: 3 consecutive
runs — pass 1 had exactly one failure
(`LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`),
matching one of the six documented pre-existing flakes; passes 2 and 3 were
fully clean (2905/2905, 12/12 Lobby tests). No regressions.
