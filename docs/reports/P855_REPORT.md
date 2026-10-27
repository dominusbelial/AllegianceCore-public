# P855 — squad game results to the lobby store, and trap-hack reporting to the operator log (SIM-0765, SIM-0809)

Status: **complete** — both duties implemented, tested, matrix rows moved to
`implemented`. No commit/push; changes left in the working tree.

Evidence: `~/allegiance-evidence/P855/` (fast-tier run logs + summary; the
worktree itself carries the tests).

---

## 1. Squad game results (SIM-0765)

### Native behaviour (re-read, all line numbers re-verified in this session)

`RecordSquadGame` (`FedSrv/FedSrv.CPP:1198-1300`, declared `FedSrv.H:419`)
builds a `CQReportSquadGame` query (`FedSrv/srvqueries.h:173-194`) whose payload
is exactly six ints: `squadIDWon` and `squadIDLost1..5`, posted through the
club-DB stored procedure `SquadReportGame(?,?,?,?,?,?)`. The loser walk
(FedSrv.CPP:1206-1290) iterates the side list five times, skipping the winning
side and every side whose `GetSquadID() == NA`; a side with no squad never
appears in the payload (the `NA` slots stay unset). It is called from
`CFSMission::OnEndGame`'s finalize block (`FedSrv/fsmission.cpp:3425`) behind
the qualification gate **fsmission.cpp:3421-3424**:

```c
if (m_misdef.misparms.bSquadGame && m_misdef.misparms.bScoresCount && m_psideWon
    && m_misdef.misparms.nTeams == 2
    && m_pMission->GetSide(0)->GetSquadID() != m_pMission->GetSide(1)->GetSquadID())
```

So the qualifying rules are: a **squad game**, **scores count**, a **decisive
winner** (`m_psideWon != NULL` — a draw never reports), **exactly two teams**,
and **side 0's squad differs from side 1's** (which subsumes "all sides have
squads" only partially: a NA-vs-set pair passes the gate, and then the loser
walk or the NA `squadIDWon` keeps the facts sane — mirrored exactly, see
below). There is **no** stage, minimum-player or minimum-time condition in
native (the brief's hints checked: absent). **Draws**: excluded by
`m_psideWon`. **Resign/forfeit**: any ending funnels through the same shared
finalize (`OnEndGame`), so a resigned/forfeited squad game reports exactly like
an elimination win. What the `SquadReportGame` proc does with the payload is
not in the public tree (it lived in the zone's club DB), but its observable
facts — the squad standings the client screens read — are one **win** for
`squadIDWon` and one **loss** per non-NA `squadIDLost*`.

### Port

The result rides the **same path career results already use**
(`ReportMissionResultsToLobby`, `src/Allegiance.Server/Server.Lobby.cs`):

- Qualification gate verbatim: `mission.SquadGame && winnerId != GameOver.NA &&
  mission.Sides.Count == 2 && Sides[0].SquadId != Sides[1].SquadId`
  (fsmission.cpp:3421-3427). `bScoresCount` has no port equivalent — every port
  mission counts scores (`LobbyGameParams.FromMission`, `ScoresCount: true`),
  documented on the gate. The winner squad may be `null` (native posts `NA`)
  and losers are every side with a squad that is not the winner's, in side
  order — the port keeps the walk general instead of unrolling 5 slots.
- `matchId` is minted for the whole report (not per-player) so a squad game
  with **zero verified players** still reports — native posts the squad query
  independent of `SetCharStats` output.
- Wire: `LobbyMatchReport` gains `SquadGame`
  (`src/Allegiance.Server/LobbyHeartbeat.cs`, `LobbySquadGameResult`), the
  source-generated AOT-safe mirror of the lobby's `CareerContracts.MatchReport.
  SquadGame` (`src/Allegiance.Lobby/Career/CareerContracts.cs`).
- Ingest: `CareerStore.ApplyMatchReport` applies the squad section **inside the
  same transaction** as the player rows, through the P820a squad store:
  `SquadStore.ApplyGameResult` (`src/Allegiance.Lobby/Squad/SquadStore.cs`)
  credits the winner squad `wins + 1` and each loser `losses + 1`, idempotent
  via the new `squad_game_results` table (`IdentityStore.Migrate`; match key is
  server-scoped like `match_results`). SQL shape differs from the era's stored
  proc by design; the stored facts and the qualification rules are the
  behaviour and are exact.
- Endpoint `POST /api/career/results` passes the `SquadStore` through
  (`src/Allegiance.Lobby/Career/CareerEndpoints.cs`); enrollment proof and
  rate-limiting unchanged.

`docker-compose.yml`: **no change** — no new service, store, port or env var;
the new table lives in the existing versioned lobby store (migration is
idempotent `CREATE TABLE IF NOT EXISTS`, applied on lobby start; `docker
compose config` validates).

## 2. Trap-hack operator events (SIM-0809)

### Native behaviour

`LogTrapHack` (`FedSrv/FedSrv.CPP:2749-2798`) turns every message-validation
failure into an operator event: who (`pszSubject` = player name), what
(`pExpr` = the stringized check), where (`nLine` = the native `__LINE__`),
disconnected for the Boot variants (`g.fm.DeleteConnection`,
:2779-2788) — dispatched by the `TrapHackLog` / `TrapHackBoot` /
`TrapHackBootNoPlayer` macros (FedSrv.CPP:4662-4665).

### Port

- **New `src/Allegiance.Server/TrapHack.cs`**: `TrapHackKind`
  (log/boot/boot-no-player), `TrapHackEvents.FormatLine` — one structured
  key=value operator line (`kind`, `check="…"`, `site=FedSrv.CPP:<line>`,
  `player="…"`, `ship=N`, `conn=N`, `action=log-and-ignore`), client-influenced
  strings through `ConsoleLog.Sanitize` (log-forging defence);
  `TrapHackRateLimiter` — per-connection fixed window (8 events/s), suppressed
  burst collapsed into a single summary line, so a spamming client cannot flood
  the operator log; `Server.ReportTrapHack` / `ReportTrapHackNoPlayer`.
- **Design decision (recorded, not an oversight): the port does NOT start
  disconnecting.** The standing convention since P182/P849 softens native's
  harsh trap to log-and-ignore; P855 adds the missing structured operator
  event, and the line records `kind=boot … action=log-and-ignore (native
  boots; this port does not disconnect)` so an operator sees what native would
  have done. The rate limit is new port-side hardening: native never needed it
  because it disconnected on the first bad message.
- **Routed sites** (every soft refusal standing in for a native `TrapHack*`
  check; the refusal behaviour of each site is unchanged — only the operator
  record was added):

| Handler | native check | site |
|---|---|---|
| SWAP_PART boarded gunner | `pfsPlayer->GetIGCShip()->GetParentShip() == NULL` | FedSrv.CPP:6464 |
| RELOAD boarded gunner | same | :6509 |
| RELOAD batch count | `(nReloads > 0) && (nReloads <= 5)` | :6523 |
| RELOAD malformed entry | `!bBoot` | :6647 |
| DROP_PART boarded gunner | parent `== NULL` | :6301 |
| FIRE_EXPENDABLE boarded | `pShip->GetParentShip() == NULL` | :5625 |
| FIRE_EXPENDABLE dispenser | `pdispenser->GetAmount() > 0` | :5630 |
| FIRE_MISSILE boarded | `dm.pLauncher->GetParentShip() == NULL` | :5418 |
| FIRE_MISSILE launcher | `pmagazine->GetAmount() > 0` | :5423 |
| SET_WINGID range | `pfmSW->wingID < c_widMax` | :5324 |
| TREASURE_ACK mount | `…CanMount(ppt, pfmTA->mountID)` | :6115 |
| BUY_LOADOUT funds | `pfsPlayer->GetMoney() >= cost` | :6198 |
| REQUEST_MONEY amount | `pfmRequest->amount > 0` | :5368 |
| PLAYER_DONATE given | `pfmPlayerDonate->moneyGiven > 0` | :7220 |
| PLAYER_DONATE wallet | `pfsPlayer->GetMoney() >= pfmPlayerDonate->moneyGiven` | :7221 |
| AUTO_ACCEPT own side | `pfmAutoAccept->iSide == sid` | :6926 |
| CHANGE_TEAM_CIV direct civ | `pciv` (direct requests only; random has no native trap) | :7362 |
| QUIT_MISSION reason | `pfmQuitMission->reason == (bIsBoot ? QSR_LeaderBooted : QSR_Quit)` | :6799 |
| QUIT_SIDE reason | `pfmQuitSide->reason == (bIsBoot ? QSR_LeaderBooted : QSR_Quit)` | :6870 |

  Deliberately **not** routed: the P654 H-19 PLAYER_READY forged-`shipID`
  seam (native `TrapHackBoot(shipID == GetShipID())`, FedSrv.CPP:7019) — the
  port re-encodes the relay with the sender's own id instead of refusing, so
  there is no refusal to report. The native logon-var `TrapHackBootNoPlayer`
  checks (:5817-5821) have no existing port refusal site; the no-player kind
  and its reporter exist and are exercised by tests.

## 3. Tests (all failing-first — they did not compile/pass before the change)

- `tests/Allegiance.Lobby.Tests/P855SquadResultTests.cs` (5): qualifying game
  updates both squads' standings; report without a squad section leaves them;
  replay idempotent; per-server match-key scoping; squad-only report needs no
  player rows. **3× alone: PASS.**
- `tests/Allegiance.Core.Tests/P855SquadGameLoopbackTests.cs` (1): server
  heartbeat → real in-process lobby over HTTP (AOT-safe source-generated
  serialization), standings updated, replay double-application rejected.
  **3× alone: PASS.**
- `tests/Allegiance.Core.Tests/Transport/P855TrapHackTests.cs` (5): limiter
  window/suppression/per-source isolation; line shape (kind/check/site/
  player/ship/conn, control chars escaped); live-network: 20 out-of-range
  SET_WINGID batches produce ≤ 8 structured `[traphack]` events with the right
  check/site/ship/player. **3× alone: PASS.**

## 4. Verification (light gate)

- `dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 90 tool projects built**.
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`): **clean, no IL2026/IL3050**.
- New tests: each file **3× alone PASS** (above).
- ONE `tools/run-tests.sh --tier fast`: **PASS — 11156 tests, 312.4 s**
  (`~/allegiance-evidence/P855/fast-tier/`, summary.json copied).
- `docker compose config`: validates (no compose change needed).
- `scripts/parity-scorecard.py`: runs clean; rows SIM-0765/SIM-0809 now
  `implemented` with tests. CSV has 0 CR bytes (LF kept).

## 5. Native-intent classification

- **SIM-0765 (RecordSquadGame)** — *design*: the native SQL/stored-proc
  plumbing is era-specific (zone club DB); what native intends is "a finished
  qualifying squad game changes the two squads' standings", which the port
  reproduces exactly through its own lobby persistence. The gate is ported
  rule-for-rule; the loser walk is ported in generalized form (a list instead
  of five named slots; the two-team gate keeps the payload at one loser).
- **SIM-0809 (LogTrapHack)** — *adapted, documented*: the operator event is
  ported (who/what/where per failure), but the disconnect side effect is
  deliberately not ported (standing port convention, recorded on every line
  and in `TrapHack.cs`'s remarks). The rate limiter is a port-side safety the
  native never needed because it booted the spammer.

## 6. Modern/better-approach note

Native strings three ints per message type into fixed slot variables
(`squadIDLost1..5`) and five unrolled loops; the port uses a list, which keeps
the facts identical while removing the artificial 5-loser ceiling (asserted in
native). The operator log moves from native's free-text `retailf` line +
AGC context event to one greppable key=value line per failure with the native
source site embedded — an operator can answer "who failed which check, how
often" without recompiling anything — and the per-connection rate limit closes
the log-flood hole the softening convention would otherwise have opened. If a
future task revisits the boot policy, the event kind is already on the line;
flipping `ReportTrapHack` to actually disconnect is a one-line change per kind.

## 7. Unfinished / out of scope

- `RecordGameResults` (the per-game results query, fsmission.cpp:3432) is a
  separate matrix row, not this task.
- The zone-club stored procedure's exact SQL is unknowable from the public
  tree; the lobby-side effect was reconstructed from the SQUAD_INFO facts the
  client screens consume (noted in the CSV row).
- No `docker-compose.yml` change was required (no new service/store/env var).
