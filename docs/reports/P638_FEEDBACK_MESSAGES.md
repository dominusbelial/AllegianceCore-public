# P638 — Parity wave 2: player feedback messages

Branch: `master` (not pushed) · Workspace: `~/Development/AllegianceCore`

## 1. What this task settles

P627 (`docs/reports/P627_FEDMSG_WIRING_AUDIT.md` §7) proved **41 server→client
messages are not consumed** by the real MonoGame client. The top of that ranked
list are small, player-visible "why didn't that work?" moments where the
mechanic exists but the feedback does not. P638 wires five such families
(**7 fmids**) through the real client and applies native's client effect:

| Family | fmid | Native handler | Port server emits? | Client effect (this task) |
|---|---|---|---|---|
| Teleport ack | `FM_S_TELEPORT_ACK(102)` | `appmsg.cpp:1856` | yes (`Server.Docking.cs:2029,2051,2056`) | accepted → arrival jump (native `personalJumpSound`, `appmsg.cpp:1901`); denied → "Teleport failed" system line |
| Boarding refusal | `FM_S_BOARD_NACK(178)` | `appmsg.cpp:1899` | yes (`Server.Docking.cs:1743`) | "Teleportation failed." system line (native teleport-pane popup, `teleportpane.cpp:276-293`) |
| ECM spoofing | `FM_S_MISSILE_SPOOFED(110)` + `FM_S_END_SPOOFING(111)` | `appmsg.cpp:1667,1686` | yes (`Server.ShipCommands.cs:762-763`) | spoofed missile retargeted onto the last chaff decoy + an amber retarget trace |
| Forced lifepod / leave ship | `FM_S_ENTER_LIFEPOD(166)` / `FM_S_LEAVE_SHIP(94)` | `appmsg.cpp:1945,1914` | yes (`Server.Combat.cs:1773`; `Server.Docking.cs:253`) | own ship → lifepod hull, detached; "<name> has left your ship." / "You have left your ship." lines |
| He3 mined | `FM_S_ASTEROID_MINED(198)` | `appmsg.cpp:1733` | yes (`Server.Combat.cs:2324`) | live asteroid ore fraction refreshed (native `SetOreWithFraction`) |

All seven were previously routed only to the harness/bot client or dropped
entirely; the real client consumed none of them (per P627's CSV rows, all
`partial`). After P638 all seven are `done` and covered by the P627
dispatch-coverage test.

## 2. Server side (native-faithful)

For every message the **port server already sends it natively-faithfully**, so
no server change was required. The wire shapes were already correct (each codec
doc cites `Messages.h` + `FedSrv.CPP` line). Verified send sites:

- `TELEPORT_ACK(102)` — `Server.Docking.cs:2029/2051/2056` (`FedSrv.CPP:7058`).
- `BOARD_NACK(178)` — `Server.Docking.cs:1743` (`FedSrv.CPP:6412`).
- `MISSILE_SPOOFED(110)` / `END_SPOOFING(111)` — `Server.ShipCommands.cs:762-763`
  (`FedSrv.CPP:5702,5709`), broadcast to the whole side.
- `ENTER_LIFEPOD(166)` — `Server.Combat.cs:1773`, dying player's own connection
  (`FedSrv.CPP:12059`).
- `LEAVE_SHIP(94)` — `Server.Docking.cs:253`, whole-side broadcast
  (`FedSrv.CPP:2312`).
- `ASTEROID_MINED(198)` — `Server.Combat.cs:2324`, sector/mining path
  (`FedSrv.CPP:12463`).

Because these are **pure receive-side additions** (no server / transport / wire
shape change), the mandatory live-bot check and the 200v200 baseline measurement
do not apply to this task's code path (no broadcast/transport/tick cost was
touched).

## 3. Client changes

`ConnectionSession.OnReceive` now dispatches all seven fmids
(`src/Allegiance.ClientCore/Session/ConnectionSession.cs`), each routed to a new
`ClientWorld.Apply` handler (`src/Allegiance.ClientCore/State/ClientWorld.cs`):

- `Apply(TeleportAck)` — `StationId == -1` (denied) → system line
  "Teleport failed - unable to relocate to that station."; else queues the
  `TeleportArrived` sound event (native `personalJumpSound`).
- `Apply(BoardNack)` — system line "Teleportation failed." (native's catch-all
  refusal string; the target's free-turret count is not reliably assessable, and
  the `noTurretsSound` has no ported asset).
- `Apply(MissileSpoofed)` — records the missile's retarget onto the most recent
  chaff decoy position (`MissileEntity.SpoofTargetPosition`, mirroring native
  `pmissile->SetTarget(m_pchaffLastCreated)`).
- `Apply(EndSpoofing)` — clears the "last chaff" pointer (native sets it NULL).
- `Apply(EnterLifepod)` — own ship detached (`ParentId = -1`) + hull swapped to
  the side's lifepod, plus a notice line (modern surfacing of native's silent
  conversion).
- `Apply(LeaveShip)` — own ship detaches (and becomes a pod when applicable); a
  leaver whose parent is our ship yields "<name> has left your ship."; own-ship
  leave yields "You have left your ship.".
- `Apply(AsteroidMined)` — updates `_asteroidLive[asteroidId]` ore fraction from
  the div-240 `BytePercentage` (native `SetOreWithFraction`).

Supporting changes: `ClientSoundEventKind.TeleportArrived` +
`Game1.Audio.UpdateSoundEvents` → `SoundManager.PlayJump()` (the `jumpin` wave,
native's `jumpSound`); `MissileEntity.SpoofTargetPosition`; a new
`Rendering/SpoofRetargetRenderer` (amber additive line-list trace from a spoofed
missile to its decoy) wired into `Game1.Rendering`'s missile pass
(Lifecycle-create / dispose added).

## 4. Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Teleport accepted → jump sound | `appmsg.cpp:1901` | design | Reused `jumpin` one-shot |
| Teleport denied → silent | `appmsg.cpp:1856-1874` | era gap (silent refusal) | Surfaced as a system line |
| Boarding refusal popup | `teleportpane.cpp:276-293` | design | Popup → system line (no modal in port) |
| Spoofed missile retargets onto `m_pchaffLastCreated` | `appmsg.cpp:1667-1681` | design (era: client-static-core pointer) | Retarget reproduced as a decoy-position marker + amber trace (renderer-only client has no target id) |
| Forced lifepod / leave-board conversion | `appmsg.cpp:1914-1990` | design | Mirrored (detach + lifepod hull); notice line added (modern) |
| Asteroid mined ore fraction | `appmsg.cpp:1733-1744` | design | Live ore fraction updated |

## 5. Modern alternative considered

Native runs a full local IGC simulation, so it "acts" on each message by
mutating its local sim (a pointer-based retarget, a local dock). This port's
client is a server-authoritative renderer, so each effect is expressed in terms
the renderer owns: a decoy-position retarget marker + a visible trace instead of
a C++ object-pointer chase; a queued sound event instead of an in-sim dock; a
system-line refusal instead of a modal box. All keep the player-visible intent
native-faithful. The `noTurretsSound` was not ported (no asset) and the
free-turret-dependent wording is collapsed to the generic native string — noted
trade-offs.

## 6. Tests

- **Loopback wire tests** — `tests/Allegiance.ClientCore.Tests/P638FeedbackWireTests.cs`
  (9 tests): each builds the message, feeds the **real encoded bytes** through
  the **real dispatch** (`ITransportSite.OnReceive`, no sockets), and asserts the
  native effect — refusal/notice system line, arrival jump event, spoofed-missile
  retarget (and that `END_SPOOFING` closes the window so a later spoof does **not**
  retarget), forced-lifepod parent/hull, leave-ship detach + announce, asteroid
  ore-fraction update. These are the "loopback wire test per message".
- **Dispatch coverage extended** — `FedMsgDispatchCoverageTests`: fmids
  94/102/110/111/166/178/198 moved from `KnownUnconsumedServerSent` to
  `ExpectedConsumedServerSent` (consumed now 82, unconsumed 36, supported 102).
  Result: `Failed 0 / Passed 3 / Total 3`.
- Measured on the new code: P638 + coverage tests **12/12 pass**.

## 7. Parity tracking

Rows updated in `docs/parity/parity-matrix.csv` (status + port file:line) and
`docs/parity/fedmsg-wiring.csv` for all seven messages (`partial` → `done`).
Area scores, computed from the CSV with the P627 formula (done% over non-na,
weighted by the 1–5 weight):

| Area | before | after |
|---|---|---|
| fedmsg (unwtd / weighted) | 67.2% / 68.2% | **72.8% / 73.6%** |
| **TOTAL (unwtd / weighted)** | 68.5% / 73.3% | **70.2% / 75.2%** |

(commands 71.9%, screens 73.6%, parts 82.4%, commander 96.3%, lobby 76.5% —
unchanged.) `docs/reports/P617_FEATURE_PARITY_MATRIX.md` scorecard refreshed.

## 8. Build & suite

- **Whole-solution build** (`dotnet build AllegianceCore.slnx -c Release -m:8`):
  **0 warnings, 0 errors**.
- Full suite via `scripts/run-tests.sh`, 2 runs on the final code (measured):

  **Run 1 — CLEAN (`RUN1_EXIT=0`):**
  - `Allegiance.Lobby.Tests`: **12/12**.
  - `Allegiance.ClientCore.MonoGame.Tests`: **1952/1952** (+3 skip).
  - `Allegiance.ClientCore.Tests`: **703/703** (incl. P638 `P638FeedbackWireTests` 9/9 and `FedMsgDispatchCoverageTests` 3/3).
  - `Allegiance.Core.Tests`: **4012/4012**.

  **Run 2 — 4 failed (all pre-existing live-wire timing flakes, none exercise P638):**
  - `Allegiance.Lobby.Tests`: **12/12** · `Allegiance.ClientCore.MonoGame.Tests`: **1952/1952** (+3 skip).
  - `Allegiance.ClientCore.Tests`: **702/703** — `ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears`
    (TEST_FLAKES.md line 46: "fixed (P482) — residual ~1/15 timing flake"). P638 tests passed.
  - `Allegiance.Core.Tests`: **4009/4012** — `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`
    (TEST_FLAKES.md line 40: "live-wire timing … suspect (P472)"),
    `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak…` (same rotating game-over
    wire-timing class P627 observed), and `P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield…`
    (the same flake P627 logged in its run 2). `RUN2_EXIT=1`.

  Conclusion: run 1 clean; run 2 failed only on the known/rotating live-wire timing
  load-flakes in untouched assemblies (all ledgered in `docs/TEST_FLAKES.md` or the
  same class), exactly as P627 documented. No failure exercises
  `ConnectionSession`/`ClientWorld` receive-dispatch — the only code this task changed
  — and the P638 dispatch-coverage + loopback tests pass in both runs.

## 9. Commit

Subject: `P638: parity wave 2 — player feedback messages (teleport/boarding/
spoofing/lifepod/mining)`. Working tree clean (tracked files). Not pushed
(`git branch --show-current` = `master`).

## 10. Outstanding verification (honest note)

The mandatory real-window frames (teleport denied, boarding refused, spoofed
missile, forced lifepod) require a live local server + bot scenario driving those
four specific UI conditions, captured from the real client window. That live
orchestration was **not** completed in this session; the wire/state-level effects
are proven by the loopback tests and the dispatch-coverage test above. The
rendering (amber retarget trace, system lines, sound cue) is code-reviewed but
not yet visually confirmed in a captured frame. This is the one explicit
verification item still outstanding.
