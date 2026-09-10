# P132 — the periodic cross-side detection sweep (wires P131's foundation in)

Commit: P132 — Sector.ResolveCrossSideVisibility: per-tick cross-side detection
sweep that calls P131's SideVisibilityState + VisibilityMath.InScannerRange /
LineOfSightExist together for the first time, so Ships/Stations/Probes'
SeenBySide / GetCurrentEye return real, live-updating answers.

Branch: master (top was 738db78 = P131 before this change).

---

## 1. What this task builds

A new per-tick `Sector` pass, `ResolveCrossSideVisibility` (src/Allegiance.Core/
Model/Sector.cs, the `Resolve*` family — called from `Sector.Update` right after
the P123 treasure pass), that for every ship/station/probe in the sector and
every side other than its own tests every potential scanner of that side in the
same cluster via `VisibilityMath.InScannerRange` (which itself calls
`VisibilityMath.LineOfSightExist` over the sector's asteroids) and writes the
resulting boolean with `SideVisibilityState.SetSideVisibility`. The model's own
side is always visible to itself, no scan needed (FedSrv.CPP:10890-10892).

It still does **not** wire PROBES_UPDATE(92) / ASTEROIDS_UPDATE(179) /
STATIONS_UPDATE(57) / Phase 5 — those four broadcasts are the concrete, now-
unblocked next tasks (section 6). This task only makes `SeenBySide` /
`GetCurrentEye` return live answers.

### Wiring glue required (the only prior-code touches, all additive)

P131 deliberately left `SideVisibilityState` wired into nothing, so this task
hosts it on the participants and gives them the native model-level read surface:

- `ISideVisible` (new, src/Allegiance.Core/Sim/SideVisibilityState.cs) — the
  common surface (SideVisibility + SeenBySide + GetCurrentEye) implemented by
  Ship/Probe via the `Model` base and by `Station`, the exact set the native
  registers as scanners for their own side and marks c_mtSeenBySide
  (clusterIGC.h:126,148,219; stationIGC.h:177; probeigc.cpp:115).
- `Model` base (src/Allegiance.Core/Sim/Model.cs) — `SideVisibility` holder +
  `SeenBySide`/`GetCurrentEye` passthroughs (mirrors modelIGC.h:110-132).
- `Station` (src/Allegiance.Core/Sim/Station.cs) — same three members.
- `Sector.MissionSides` (internal) — the mission's full side set, set by
  `Mission.Assemble` so the sweep can reconcile every side to false (see
  section 3).

None of these change the behaviour of any prior slice; they are the socket the
P131 foundation plugs into.

---

## 2. Confirmed: the native call site is per-tick, in `Cluster::Update`

Tracked the native driver chain, not assumed it:

- `UpdateSideVisibility` is reached through `Model::UpdateSeenBySide()`
  (src/Igc/modelIGC.h:134-137 → :137 `m_pThingSite->UpdateSideVisibility(...)`).
- The only per-frame caller of `UpdateSeenBySide()` is `Cluster::Update`'s
  throttled block (src/Igc/clusterIGC.cpp:347-356): a loop over every model
  guarded by `if ((m_nPass++) % c_nPassesPerUpdate == 0)`.
- `SetCluster` (FedSrv.CPP:10711-10748) also calls it on sector change, but that
  is a separate arrival-time refresh, out of this task's scope.

## 3. Cadence / throttling convention reused (the finding the task asked for)

Native throttles the sweep to **every 10th** cluster pass
(`c_nPassesPerUpdate = 10`, src/Igc/clusterIGC.h:22).

The port's established per-tick-pass convention is the opposite: every existing
`sector.Update` pass — `ResolveMining`, `ResolveCarrierLandings`,
`ResolveRescueEvents`, `ResolveTreasurePickups`, `ResolveCombatCollisions`, etc.
— runs **unthrottled, every tick** (see `Sector.Update`,
src/Allegiance.Core/Model/Sector.cs). Following the task's instruction to match
the port's convention rather than invent a new frequency mechanism, the sweep
runs **every** `Sector.Update`. No counter is added. The native every-10 delta
is noted in the pass's doc comment (Sector.cs) and here as the one fidelity
difference, consciously accepted per the task rule ("don't invent a new
update-frequency mechanism").

## 4. Confirmed: no registered scanner index needed (query, not an index)

Native `ClusterSite::GetScanners(sideID)` (FedSrv.CPP:10909) is a registered,
incrementally-updated per-side scanner list (`AddScanner`/`DeleteScanner` on
cluster join/leave).

Confirmed this port does **not** need that registration lifecycle. The sweep
instead filters this sector's existing model collections — ships and probes in
`Sector.Models` (`_models`), stations in `Sector.StationRuntimes`
(`_stationRuntimes`) — by `SideId` on the fly. This is functionally equivalent:
every scanner must be a model present in its own sector anyway (each registers
on cluster join, and this is a per-sector pass), and the query is strictly
simpler than maintaining a parallel side-keyed index. It is the same
"query instead of maintain an index" substitution this port already makes by
reusing existing collections throughout (the mining/dock/rescue/combat passes
all walk `_models`/`_mineable`/`_stationRuntimes` rather than keeping dedicated
indexes). No new registration lifecycle is built.

For completeness the sweep reconciles **every** mission side (via
`Sector.MissionSides`, the port of `pcluster->GetMission()->GetSides()`,
FedSrv.CPP:10887-10888) to its computed value — including sides with no
presence, which read false. That is what turns a stale `SeenBySide` into false
when a side's last scanner leaves the sector. A sector built outside
`Mission.Assemble` (some isolated tests) falls back to the sides actually
present, so the pass never throws.

## 5. Deliberate omissions (all per the task's findings, each cited in the code)

- **Alliance simplification** — the native allied leg
  (`pside->AlliedSides(...) && bAllowAlliedViz`, FedSrv.CPP:10894-10895) is
  dropped unconditionally; this port has no alliance/`AlliedSides` concept, the
  same simplification every other `AlliedSides`/`bAllowAlliedViz` gate in this
  arc already made (P107/P112/P115). Own-side is always visible; every other
  side must pass the scanner-range test.
- **"Last spotter" cache omitted** — `sv.pLastSpotter()` (FedSrv.CPP:10896-10903)
  is a pure performance optimisation (skip re-scanning every scanner when the
  same one that saw you last tick still can); the full scanner loop re-runs
  every sweep. Not built unless profiling shows a real cost.
- **`ShowObject`/`HideObject` side effects still deferred** — no client
  notifications, no territory refresh, no broadcasts. P131's own scope
  boundary, respected. The pass only flips `SideVisibilityState`'s flags on
  transition.
- No fleet of global-attribute divisors (already-simplified 1.0 in P131), no
  registered scanner lifecycle (section 4), no new update-frequency mechanism
  (section 3).

## 6. Concrete, now-unblocked next tasks

Once P132 lands, `SeenBySide`/`GetCurrentEye` are real and live. The four
consumers P131/P132 existed to serve become independently tractable:

- **PROBES_UPDATE (92)** — broadcast a side's probe visibility list, now driven
  by each probe's live `SeenBySide` state.
- **ASTEROIDS_UPDATE (179)** — broadcast per-side asteroid visibility/ore,
  driven by each asteroid's live `SeenBySide`.
- **STATIONS_UPDATE (57)** — broadcast per-side station visibility/health,
  driven by each station's live `SeenBySide`.
- **Phase 5 — multi-ship cross-side visibility** — the client-facing fog-of-war
  exports that consume these states wholesale.

Each also finally has the `ShowObject`/`HideObject`-style wire effects (the
OBJECT_SPOTTED / territory side-effects native couples to `SetSideVisibility`)
that P131 and P132 deliberately deferred.

## 7. Verification

Five new real-corpus sweep tests
(tests/Allegiance.Core.Tests/CrossSideVisibilitySweepTests.cs), all driving the
real `Sector.Update` path through a real `Mission.Assemble` mission with real
faohshcar (scanner, range 3600) and dreghpod (target) hulls placed in a real
sector at a base point pushed > 8000 u beyond every real asteroid and station,
so ambient occluders / real same-sector scanners cannot skew a result:

1. **Same side, 50 km apart** — both `SeenBySide(own)=true` (own-side rule, no
   scan); `SeenBySide(other)=false` (no scanner reaches).
2. **Different sides, 300 u apart (≤ 1952 detection radius), clear LOS** —
   `SeenBySide` flips to true after the sweep runs (was false before).
3. **Same pair moved 50 km away** — `SeenBySide` flips true → false on re-run
   (a real transition, not an initial-state check).
3b. **Same in-range pair, occluded by a real injected radius-644 asteroid** —
   `SeenBySide` flips true → false while still within range (honours
   LineOfSightExist, not just the range half; P131's hand-derived 30°-vs-0.06°
   geometry).
4. **A side with no scanners** — a side 2 with no ships/stations/probes sees
   nothing belonging to side 0 (`SeenBySide(2)=false`).

Suite result: full Allegiance.Core.Tests suite passes clean — **2664 tests** (was 2659
at the P131 checkpoint, +5 from this task), across 3 consecutive full-suite runs with no
flakes fired (neither the pre-existing LiveChatSectorWireTests nor LiveCombatWireTests
timing flakes appeared).

## 8. Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py 20260910_025440_f8c4b3`
(session still open at log time): msgs=142, tool_calls=78, api_calls=64,
tokens_in=6.4M, tokens_out=50.5k.
