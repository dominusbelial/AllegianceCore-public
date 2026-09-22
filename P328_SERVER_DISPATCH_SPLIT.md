# P328 — Server.cs partial-class split, stage 2: wire dispatch

Structural refactor only. **Zero behavior change.** Moves the inbound wire
dispatcher `Server.ProcessDatagram` out of the 11,021-line `Server.cs` god
class into its own `src/Allegiance.Server/Server.Dispatch.cs` as
`public sealed partial class Server`.

## Relationship to the 4-stage plan
- Stage 1 (P327, done): lifecycle + per-tick broadcast → `Server.Lifecycle.cs`,
  `Server.Broadcast.cs`.
- **Stage 2 (this task): the wire dispatcher `ProcessDatagram` → `Server.Dispatch.cs`.**
- Stage 3 (P329): lobby / ballots / economy handlers → separate files.
- Stage 4 (P330): ship-command / docking / combat handlers → separate files.

`ProcessDatagram` is the largest and most safety-critical single member: it is
the message-dispatch switch that routes every inbound wire message to its
handler, so a single dropped/reordered case would affect every inbound message.
It is therefore treated as its own file and moved with the highest care.

## Scope boundary
- **Moved:** `ProcessDatagram` (1,782 lines including its XML doc comment) —
  same body, same case ordering, same per-branch try/catch structure, its
  nested local function `static string sideStr(...)` included verbatim.
- **Kept in Server.cs:** every `Handle*` method it calls (`HandleViewCluster`,
  `HandleBoardShip`, `HandleSwapPart`, ...). Those belong to their own topic
  areas and move in stages 3 (P329) / 4 (P330). Being `partial class Server`,
  cross-file private member access is unaffected, so this is purely cosmetic.
- No `Handle*` moved, no behavior change, no bug fix, no naming cleanup.

## Verification
### Byte-identical dispatch logic (the hard requirement)
The moved method was compared byte-for-byte against the pre-move source:
- The 1,782-line block extracted from `Server.cs` @ HEAD is byte-identical to
  the same block as it now sits in `Server.Dispatch.cs`.
- `git diff HEAD -- Server.cs` reduces to a single contiguous 1,782-line
  deletion whose text exactly equals the moved method (`difflib` opcode check:
  one `delete` op, zero non-delete ops). Nothing else in `Server.cs` changed.
- `Server.Dispatch.cs` is the standard partial-class preamble (23 lines) +
  the verbatim method (1,782 lines) + the class close brace.

### Build
Whole-solution `dotnet build AllegianceCore.slnx -c Debug`: **0 warnings,
0 errors**.

### Full test suite (run 3x + pristine A/B)
All other projects clean: `ClientCore.Tests` 162/162, `MonoGame.Tests` 592/592,
`Lobby.Tests` 12/12.

`Core.Tests` (3,268 total), three consecutive runs:
- Run 1: 3,266 pass / 2 fail — `LiveTurretGunnerBindingTests` (documented
  persistent flake) + `GameOverPlayersWireTests.Conquest_...` (multi-mission
  conquest live-timing flake).
- Run 2: 3,267 pass / 1 fail — `LiveTurretGunnerBindingTests` only.
- Run 3: 3,265 pass / 3 fail — `LiveTurretGunnerBindingTests` +
  `ConquestTerritoryLiveTests.Conquest_win_in_one_mission...` +
  `LoadoutChangeLiveTests.Boarding_...` (both live-network timing flakes).

Every failure is in the documented environmental live-network timing-flake
family; run-to-run variation tracks machine load (runs 1 and 3 overlapped the
CPU-heavy vizfight live-proof harnesses). **Pristine-tree A/B:** stashing the
change (back to HEAD) and running the same four suspect tests in isolation gives
exactly the same result as the changed tree — the three conquest/boarding tests
pass in isolation on both, `LiveTurretGunner` fails on both. Identical pass/fail
⇔ the move introduces no behavioral difference.

### Live proof (vizfight bot harness)
- **Stress harness** (8 real Bot peers, 20 ups, 25 s, self-terminating): 507
  ticks, 0 overruns, exit 0. Exercised and confirmed dispatched: **join/logon**
  (8 logons validated → `MISSIONDEF` + `JOINED_MISSION`), **side-select**
  (`[sideselect] AUTO-assigned to side`), **ship commands** (continuous cruise
  controls → per-tick ship-update broadcasts), **chat** (45 `[chat]` echo
  broadcasts to all peers). Zero `[wire]` errors / unsupported / rate-limited /
  dropped messages.
- **Showcase harness** (bounded ~180 s run of the scripted trailer): full arc
  dispatched cleanly — ready-up countdown, launch via real `BUY_LOADOUT`
  (`[loadout] ... LOADOUT_CHANGE(96)` to same-side peers), formation flight,
  **combat** (`[combat] ship 8101 killed by ship 8096 ... KILL_SHIP(105) -> 6
  peer(s)`, `[combat] ship 8100 killed by ship 8098 ...`, NPC-drone kills,
  player eject→lifepod `EJECT(77)`), flag pickup, dock/secure, and a real
  leadership mutiny **vote** driven over **chat** (`#mutiny` → `SET_TEAM_LEADER`
  transfer). Zero exceptions.

## Deliverable
`src/Allegiance.Server/Server.Dispatch.cs` created; `Server.cs` slimmed
11,021 → 9,239 lines (−1,782). Committed on `master` (not pushed — Domingo
pushes). No side branch. No native reference source touched.
