# CONQUEST_TERRITORY_P191 — conquest/territory game mode (GAME_STATE, station-based win conditions)

Date: 2026-09-12. Branch: `master`. Prior stage: P190.

Stage landing for the `GAME_STATE`(155) / station-based win conditions — the
"confirmed genuinely blocked" bucket from the roadmap, now tractable because
P126's station capture (with an already-mutable `Station.SideId`) and P178's
GameOver path are in place.

---

## What changed

### 1. `DataStationTypeIGC.SabmFlag` (0x200) — the "counts for victory" bit
`src/Allegiance.Core/Data/DataStationTypeIGC.cs`.
`c_sabmFlag = 0x200` ("counts for victory", `src/Igc/igc.h:753`) was **not**
previously decoded in this port (only `Unload`/`Restart`/`Ripcord`/`Land`/
`Pedestal` were — re-confirmed against the live source this session). It was
added the same one-line way the others were exposed, so
`HasCapability(SabmFlag)` identifies the station-ability bit behind the native
`"IsVictoryObjective"` capability (`Igc/missionigc.cpp:280-281`). The conquest
recompute counts only stations carrying it.

### 2. `Side.ConquestPercent` / `Side.TerritoryCount` (byte, default 0)
`src/Allegiance.Core/Model/Side.cs`.
Ports of the native per-side `m_data.conquest` / `m_data.territory`
(`sideigc.h:525-541` Get/SetConquestPercent/Get/SetTerritoryCount). Both start
0 and are written **only** by the mission's recompute — never at mission start
(native likewise only computes inside the capture/kill handler).

### 3. `Mission` win-condition flags + the recompute
`src/Allegiance.Core/Model/Mission.cs`.

- **Stage-1 mode substitutes** (stated plainly, NOT recovered values — this port
  parses no `MissionParams`):
  - `Mission.ConquestEnabled` / `Mission.TerritoryEnabled`, both **default
    false**, so existing missions/tests are unaffected unless explicitly opted in.
    Stand-ins for native `MissionParams::IsConquestGame()` / `IsTerritoryGame()`.
  - `Mission.ConquestWinThreshold = 65` — stand-in for `pmp->iGoalConquestPercentage`.
  - `Mission.TerritoryWinThresholdPercent = 50` — stand-in for
    `pmp->iGoalTerritoryPercentage`, folded into the native threshold formula
    `(50 + nClusters·pct)/100` (so for an n-cluster game the threshold is
    `(50+50n)/100` — just over half the clusters).
- **`Mission.RecomputeConquestAndTerritory(Station? excluded)`** — the port of
  `CFSMission::CheckForVictoryByStationKill` (`fsmission.cpp:3568-3710`), reduced
  to its two win-condition branches (the restart-station-elimination /
  `DeactivateSide` block at `:3695-3710` is NYI in native and out of scope).
  - Conquest: every `Flag`-capable station (except `excluded`) is tallied per
    side; each side's `ConquestPercent = 100·owned/total`; a side crossing
    `ConquestWinThreshold` is the winner.
  - Territory: runs only if `TerritoryEnabled` and the conquest branch produced
    no winner; a cluster counts for a side iff **every** station in it (except
    `excluded`) belongs to that same single side — the **strict unanimous-
    ownership** rule (mixed-ownership and empty clusters count for nobody);
    a side reaching the territory threshold is the winner.
  - Returns `(Winner, Changed)` where `Changed` is the native `bChange` gate
    (true iff at least one side's value actually changed).
  - Gated on `Stage` being `Started`/`Starting` (native gate `fsmission.cpp:3575-3578`);
    the host flips Stage to `Over` on a win, so this is also the idempotency guard.

### 4. Recompute wiring (capture + kill) in the sector
`src/Allegiance.Core/Model/Sector.cs`.

- **Capture** (in the hostile-capture branch of `ResolveDockCollisions`, the
  P126 hook point — see below): after `rt.Capture(side)` commits the re-side,
  calls `ship.Mission.RecomputeConquestAndTerritory(null)` and surfaces the
  result through the new `VictoryByStationsComputed` site seam.
- **Kill** (in `ResolveCombatCollisions`, alongside the existing
  `KillStationEvent` raise at `:1461-1465`): after `RemoveStation`, calls the
  recompute with `excluded = null` (the dead station is already out of the
  sector's runtime list, so native's `pstationKilled` exclusion is satisfied
  naturally) and surfaces the result.

### 5. `GameState`(155) wire codec
`src/Allegiance.Core/Wire/GameState.cs`.
Layout `header(4) + conquest[6] + territory[6] + nFlags[6] + nArtifacts[6] = 28`
bytes (all byte arrays, `c_cSidesMax = 6`, no var items, no padding).
`nFlags`/`nArtifacts` are **always all-zero** per the native finding (native's
`IsideIGC::SetFlags`/`SetArtifacts` are never called anywhere in `FedSrv`).
`GameState.ForMission(mission)` fills conquest/territory from each real side in
side-id order and leaves the flag arrays zero.

### 6. Server: GAME_STATE broadcast + shared GameOver
`src/Allegiance.Server/ServerHost.cs`, `src/Allegiance.Server/Server.cs`.

- `ServerHost` gained the `VictoryByStationsComputed` IIgcSite override → public
  `VictoryByStationsComputedHook`.
- `Server` wires it to `OnVictoryByStationsComputed`, which:
  1. broadcasts `GameState` whole-mission (scoped to THIS mission's peers) only
     when `changed` (the `bChange` gate);
  2. when a winner emerged and the mission isn't already `Over`, declares it via
     the **shared P178 path**.
- Extracted `Server.DeclareGameOver(mission, winnerId, reason)` from P178's
  `CheckForVictoryByInactiveSides` (identical Stage→Over + `GameOver.ForMission`
  + whole-mission broadcast) and reused it for the conquest/territory wins — the
  explicit "do not build a second game-over mechanism" requirement.

### 7. Bot observation accumulators (test-only)
`src/Allegiance.Bot/Bot.cs`: added `GameStates` (fmid 155) and `GameOvers`
(fmid 62) accumulators so live tests observe the whole chain.

### Tests
`tests/Allegiance.Core.Tests/ConquestTerritoryTests.cs` (10 pure-domain:
conquest %, winner-at-threshold, `excluded` semantics, territory unanimous /
mixed-counts-for-nobody / empty-counts-for-nobody / single-station unanimous,
both-flags-off no-op, non-started gate, GameState round-trip + layout +
ForMission) and `tests/Allegiance.Core.Tests/ConquestTerritoryLiveTests.cs`
(3 live: capture→GameState→GameOver full chain; both-flags-off capture
broadcasts nothing and doesn't end; multi-mission scoping).

---

## Was the `Flag`/`IsVictoryObjective` bit already decoded?

**No.** Before this stage the port decoded only `Unload`(0x01), `Restart`(0x04),
`Ripcord`(0x08), `Land`(0x20) and `Pedestal`(0x400). The `Flag`/victory bit
(`0x200`) was absent and had to be added (small, in-scope addition). The
`SabmCapabilities` field itself was already decoded (`DataStationTypeIGC.cs:104`),
so this was a one-constant addition — no decode change needed.

## Exact station-capture hook point reused from P126

P126's capture path lives in `Model.Sector.ResolveDockCollisions`
(`Sector.cs:2124-2144`): it raises the `CaptureStationEvent` site seam
**before** committing the re-side (so the host reads `sidOld`), then re-sides
the static record and calls `rt.Capture(side)`. The recompute is hooked
immediately **after** `rt.Capture(side)` on the same sim thread, so the
captured station's `SideId` already reflects its new owner — matching native,
which re-sides (`fsship.cpp:631`) then recomputes (`fsship.cpp:686`). The kill
path reuses the existing `KillStationEvent` raise site in
`ResolveCombatCollisions` (`Sector.cs:1461-1465`), recomputing right after
`RemoveStation`.

## Divergences from the prompt's assumptions (verified against live native source)

1. **"Excluding the just-captured station from the total, matching native" is
   WRONG for capture.** The brief told us to exclude the captured station. Live
   native does the opposite on capture: `CFSShip::CaptureStation` re-sides the
   station then calls `CheckForVictoryByStationCapture(pside, psideOld)` →
   `CheckForVictoryByStationKill(**NULL**, psideOld)` (`fsship.cpp:686`,
   `fsmission.cpp:3563-3565`) — `pstationKilled` is NULL on capture, so the
   freshly-captured station counts toward its **new** owner. Only the **kill**
   path passes the killed station (`FedSrv.CPP:12344`), which this port satisfies
   naturally because the dead station is removed from the sector's runtime list
   before the recompute. Implemented to match native; documented in the code.
2. **`nFlags`/`nArtifacts` always zero** — confirmed, implemented (matches the
   brief's own finding).
3. **Stage-1 mode flags / threshold defaults** — plainly substituted (not
   recovered values), per the brief.
4. **The real map's stations ARE Flag-capable** — the shipped `XXV_simple.igc`
   stations both resolve with `SabmFlag`, which shaped the live-test arithmetic
   (they are part of the conquest total; the harness accounts for them).
5. **`ChangeGameState` (IIgcSite / igc.h:4899) is not the recompute seam** —
   verified it has no callers anywhere in the native tree; the GAME_STATE
   broadcast is done inline in `CheckForVictoryByStationKill`
   (`fsmission.cpp:3685-3697`). This port surfaces the recompute result through a
   new, dedicated `VictoryByStationsComputed` seam instead.

## Verification

- `/home/domingo/.dotnet/dotnet build` clean (0 warnings / 0 errors) on the
  whole solution.
- New tests: 13/13 pass (10 pure + 3 live).
- Discrimination (must-fail-if-removed):
  - Removing the capture-path recompute → `Flag_capture_tips_conquest_...`
    fails ("conquest win must flip the mission to Over"). ✅
  - Replacing the unanimous-ownership rule with majority behavior →
    `Territory_mixed_ownership_cluster_counts_for_nobody` fails (mixed cluster
    wrongly counted). ✅
- Full-suite runs below.

Out of scope (unchanged): `GAIN_FLAG`(156), `nFlags`/`nArtifacts` population, any
client/UI presentation, `DeactivateSide`/restart-station-elimination.
