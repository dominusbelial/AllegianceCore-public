# P872 — server-side native behaviour from the n/a audit + empty-mission reap + Steam retirement + reachability tags

Scope: the SIM rows named in the brief, implemented/certified branch for branch
against the native cites (re-grepped in the read-only tree), with the Domingo
2026-10-28 rulings applied. Base: Claude's local master at dispatch
(`c7e91ed2`, unpushed P865/P864b/P863b/P864d/P868 merges included, as expected).

## Implemented behaviour

- **SIM-0068 — native explosion target cap (w1, implemented).**
  `Sector.ResolveExplosion` now captures the recipient snapshot **once per
  explosion batch** — the first 500 eligible damageable models (ships/missiles/
  probes in `_models` insertion order, the same order native's `m_modelsPickable`
  has for those types, plus the port's separate station/asteroid runtimes under
  the shared 500 budget) and the first 500 mines (collected independently, like
  native's separate `m_mines` scan) — and **reuses it for every chained blast**,
  instead of the previous dynamically sized per-blast scan. The 500-element
  cutoff therefore selects who receives damage exactly like native
  (`Igc/clusterIGC.cpp:365-400,402-481`); a target destroyed by an earlier blast
  in the batch is skipped by the per-blast membership check (`:416`). Test:
  505 mines → exactly the first 500 reduced in insertion order, five beyond the
  cutoff untouched.
- **SIM-0146 — empty session reap (w1, implemented; decision applied).**
  Per Domingo's ruling: **configured/admin worlds persist; player-created
  sessions are reaped after 10 s empty.** `Mission.PlayerCreated` +
  `Mission.CreatedAt` (the `GetStartTime()` analogue) and
  `Server.Periodic.CheckForNoShows` (FedSrv.CPP:7871-7910, called from the
  per-tick periodic like native's `:8088`): reap only `PlayerCreated` missions
  (the port's single-server-config core satisfies native's default-core
  condition, `:7883`), empty (`:7885`), older than ten seconds (`:7902`),
  one per check (`:7909`). Live-server tests cover reap, configured persist,
  young persist and occupied persist.
- **SIM-0258/0259/0261 — defend chatter text (w3 each, implemented).**
  The three wingman-defend producers now emit native's exact captions using
  **model names, not ids**: a new `Ship.Name` (native `m_data.name`,
  `shipIGC.h:52`) — players get their logon callsign (FedSrv.CPP:968/542),
  drones `<drone type name><object id>` (FedSrv.CPP:2253-2262) — substituted
  through a `GetModelName` analogue (`common.cpp:1096-1107`):
  `Attacking %s (%f) to defend %s*` for the acquire (with the new
  `HasDamagingPrimaryWeapon` gate, `shipIGC.cpp:2683`), `Attacking %s (%fm) to
  defend %s` for the >2200/<dist−1000 retarget, `Returning to defend %s` for
  the disengage; six-decimal native `%f` distance formatting; sound 1337
  (`droneInTransitSound`) on every transition, once per transition.
- **SIM-0269 — builder build chatter (w3, implemented).**
  `Building <station type name> at <asteroid model name>` emitted only for an
  accepted build (`shipIGC.h:2404-2410`); bucket constructors (the native
  `c_cidGoto` rewrite, FedSrv.CPP:11358-11366) and the no-config/no-target/
  occupied-rock guards emit nothing.
- **SIM-0270 — wingman default attack chatter (w3, implemented).**
  `Attacking <model name>` after the accepted default attack, only when the
  host `HandlePickDefaultOrder` override returns false and a target exists
  (`shipIGC.h:2436-2451`); the override suppresses both acquisition and chat.
- **SIM-0750 — replay count (w2, implemented).**
  `Mission.ReplayCount`/`HasGenerated`/`BeginReplay` (missionigc.cpp:3849-3857:
  increments only after the first generation, clears the context-name cache).
  Because the port rebuilds a fresh `Mission` on rotation (native resets the
  SAME object), `RotateMissionAfterGameOver` carries the finished world's
  generation state into the rebuilt world before `BeginReplay()`.
- **SIM-0751 — context name as persisted game ID (w2, implemented).**
  `Mission.GetContextName` (missionigc.cpp:4217-4238): cached
  `%c%02d%02d%02d%02d%02d%04d%02d` — U/Z, creation wall-time MMDDHHmmss,
  `MissionId % 10000` (assigned per game at assembly), replay % 100 —
  invalidated on replay, and bound as `LobbyGameResult.GameId` in the lobby
  game-results report (fsmission.cpp:2766 `szGameID`). Only the FILETIME/COM
  representation is excluded.
- **SIM-0148 / SIM-0472 — Steam (both n/a).**
  `retired external service — Domingo 2026-10-28`: Steam authentication and
  external achievement delivery are not a supported identity provider here; the
  port's identity is its own account/JWT join flow (P808a) and no Steam
  integration is implied. Gameplay score ingestion (repair score, P583) remains
  separate behavior. Native cites retained: FedSrv.CPP:9388, :3704.

## Reachability re-tags (evidence re-grepped)

- **SIM-0508, SIM-0625, SIM-0628 → `production`:** P853b's production client UI
  sends `FM_CS_SET_TEAM_INFO` (`Game1.Panes.cs:1994`) and
  `FM_CS_MISSIONPARAMS` (`Game1.Panes.cs:2065-2076`); real clients reach the
  implemented server handlers over the live wire.
- **SIM-0765 RecordSquadGame → `production`:** `mission.SquadGame` is set from
  the MISSIONPARAMS SQUAD bit (`Server.MissionParams.cs:300`) flipped by the
  P853b UI, and every completed mission reports results at game over — a real
  squad-vs-squad game reaches the walk (FedSrv.CPP:1198-1300).
- **SIM-0809 LogTrapHack → `production`:** `ReportTrapHack` is wired into the
  live dispatch validation paths (`Server.Dispatch.cs:1572,1658` — the
  FedSrv.CPP:6926/7362 analogues); any real client sending those malformed FMs
  triggers it.
- **SIM-0821 Ballot::Cancel — finding, stays `implemented`/`test-only`:**
  whole-tree native search finds `Ballot::Cancel` **only** at its declaration
  (`fsmission.h:95`) and definition (`fsmission.cpp:5836-5844`) — **no caller**.
  The port's `PendingBallot.Canceled` mirrors that (un)reachability. Re-classing
  to n/a is blocked by the audit tool's fixed original-exclusion inventory
  (`tools/p868-na-audit.py` requires exactly the original 164+35 rows), so the
  finding is recorded in the matrix row instead.

## Matrix deltas

`docs/parity/sim-rules-matrix.csv` and `docs/parity/p868-na-audit.csv` updated
(LF, validated); `tools/p868-na-audit.py`: **199 rows; A=78 B=119 C=2; B
implemented=20, partial=99** (was A=76 B=118 C=5, implemented=11). Scorecard
(`python3 scripts/parity-scorecard.py`):

| Matrix | Before | After |
|---|---|---|
| parity-matrix.csv TOTAL | 98.69% (1861.832/1886.500), 651/660 | unchanged (no general rows touched) |
| sim-rules-matrix.csv TOTAL | 89.31% (1905/2133), 683/803 | **90.84% (1934/2129), 695/800** |
| sounds area | 95.62% (567/593) | 98.15% (582/593) |
| FedSrv mission rules | 78.20% (599/766) | 79.97% (611/764) |
| periodic | 82.35% (42/51) | 87.76% (43/49) |
| updates | 94.75% (343/362) | 95.03% (344/362) |

test-only: 48 rows → 42 (the six re-tags above); the two Steam C rows became
retired-service A exclusions and SIM-0146's decision was applied.

## Left partial (honest, not deferred silently)

- **SIM-0062 / SIM-0063 (w4/w5)** — global preplot→plot→execute and the single
  sorted swept-collision queue require re-casting the port's per-ship movement/
  collision architecture (`Sector` update passes) around native's phase order;
  nothing in this task's budget could do that without destabilising the sim.
  Rows remain `partial` with the audit action unchanged.
- **SIM-0147 (w2)** — roll-call cadence/absence/queue-growth boot semantics
  remain `partial` (TCP lifecycle mapping not started).
- **SIM-0156 / SIM-0157 (w1)** — clock-conversion certification needs the
  production session clock bound at every export/import call site; the port's
  authoritative-server design keeps most of those timestamps server-side, but
  the per-site certification was not completed.
- **SIM-0171 / SIM-0635 (w1/w2)** — cluster-factory/scanner-init/teardown
  certification against the native constructors not completed.
- **SIM-0469 (w1)** — fixed two heavy/light/active update quota with deviation
  carry-over not re-derived (P604's distance-tiered budget is a different
  selection policy).
- **SIM-0517 / SIM-0519 / SIM-0523 / SIM-0571 (w2 each)** — whole-body
  certification tests for DoPayday/DoTick/MakeOverrideTechBits/UpdateLobby not
  written; the implementations exist but narrow-row coverage is not whole-body
  proof, so they stay `partial`.
- **SIM-0699 / SIM-0700 / SIM-0790 / SIM-0702 / SIM-0703 (w2 each)** — masked
  mission export/import and the generic model/base resolvers remain `partial`;
  the port's wire codecs are a different (documented) surface than
  `CmissionIGC::Export/Import`.

## Verification (light gate)

- Build: **0 warnings / 0 errors** (`dotnet build AllegianceCore.slnx`).
- `tools/build-tools.sh`: **all 92 tool projects, 0/0**.
- AOT publish (`-p:PublishAot=true`, Core+Server changed): clean, no
  IL2026/IL3050.
- New tests `P872NativeBehaviourTests` (11) + `P872SessionReapTests` (2):
  **3/3 alone each**, zero skips.
- ONE fast tier (`tools/run-tests.sh --tier fast`): 11,475 tests; the two
  failures are ledgered pre-existing entries
  (`NpcWingmanDefendLiveTests.Defender_disengages…` — additionally proven
  failing 3/3 with P872's edits reverted to base `c7e91ed2`;
  `RadarTargetHighlightLiveTests.LiveOffScreenEnemy…`) plus one new load flake,
  `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected`, which passed
  3/3 alone immediately after — both entries added to `docs/TEST_FLAKES.md`.
- Showcase never-parks 5×: **5/5 pass** (3m03s–4m23s each), no parking.
- `git diff --check`, `tools/check-glossary-links.py` (274 paths),
  `python3 tools/p868-na-audit.py`, `python3 scripts/parity-scorecard.py`: all
  pass; CSVs kept LF (`grep -c $'\r'` = 0). No new service, port, store or env
  var; docker-compose untouched. Scratch removed from `.scratch/`.

## Native-intent classification

All rows closed this task close as **B → implemented** with native cites: the
500-cutoff, the reap decision, the five chatter producers, and the replay/
context pair are live native gameplay/identity behavior; the two Steam rows are
**A exclusions as a retired external service** (explicit product retirement, not
a physical impossibility), and the reachability corrections convert
mis-tagged test-only rows to production on wire evidence. Nothing was closed as
"the port chose differently" or "same observable outcome".

## Modern/better-approach note

The port keeps native's *semantics* while choosing structurally better
mechanisms where the native one is an artifact of 1999 constraints: the
explosion recipient snapshot is a bounded `List<Model>` built once per batch
(no fixed 500-element C arrays, no AddRef/Release bookkeeping), the reap runs
on a deterministic sim clock instead of FILETIME wall-clock ticks, and the
context name is a cached `string` bound into a JSON lobby report rather than a
COM/OLEDB `szGameID`. `Ship.Name` is a plain settable property rather than a
fixed 25-byte char buffer; the 500 cap itself is kept *exactly* because the
audit showed it changes gameplay, not because the memory saved anything.
