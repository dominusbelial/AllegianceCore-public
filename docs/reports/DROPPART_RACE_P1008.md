# P1008 — DropPart receive/reset race in `DropPartTests`

The P1002 "subject-only residual" diagnosis is **confirmed**: the failure was a
fixture race, not a production bug. Phase one waited for the observer's and the
enemy's `TREASURE(28)` copy but never for the **subject's own** copy, then cleared
every receive list; a late subject echo landed in the cleared list and broke
`Assert.Empty(subBot.TreasureSpawns)` in the empty-pack phase. The subject *is*
supposed to receive its own echo in native (FedSrv.CPP:6318-6319,
FedSrv.CPP:10750-10778), so the fix is a phase barrier that waits for it (plus a
phase-two completion marker), with every behavioural assertion kept and two
strengthened. No production behaviour, wire format, default or HUD changed.

Base `bcdcd8a0` (Claude's master: v0.1.6 + P1002–P1007). Changes stay in the
working tree; nothing committed or pushed. Runtime .NET SDK 10.0.404, x86-64
Debian container, artwork `/srv/corpus/Artwork`, native
`/srv/corpus/Allegiance/src`.

## 1. The drop path the fixture drives (confirmed from the code)

Server side, one `TickSim` handling of `FM_CS_DROP_PART(33)`:

1. `Server.ShipCommands.cs:620` `HandleDropPart` removes the mounted part
   (`:655-656`) and sends `DROP_PART(33)` sector-wide
   (`:676 SendSectorWide`), then conditionally creates the treasure (`:686-702`,
   the native `(type != OT_pack) || amount != 0` guard) and sends `TREASURE(28)`
   sector-wide (`:703 SendTreasureSpawnSectorWide`).
2. `Server.Docking.cs:481-487` (`SendSectorWide`) and `:497-506`
   (`SendTreasureSpawnSectorWide`) build **one packet per peer** and queue it per
   connection; `Server.BroadcastSnapshot.cs:295-313` publishes to each peer
   independently (`_host.Send` per peer).
3. Each bot decodes on its own transport thread (`src/Allegiance.Bot/Bot.cs`
   `OnReceive`, one frame per read, `TcpTransport.cs:1387-1389`).

So the subject and the observer receive the same two frames in the same tick,
but publication and TCP reads are per peer: **there is no cross-peer ordering**.
Waiting for the observer's/enemy's copies proves nothing about the subject's.

## 2. Native: the subject does receive its own echo

- The drop relay audience is the sector's *flying-player* group,
  `GetGroupSectorFlying(pcluster)` (FedSrv.CPP:6319), and that group contains the
  requesting player itself — `CFSPlayer::SetDPGroup` adds the flying player to
  `pgrpClusterFlying` (FedSrv/fsship.cpp:1246-1249; group created at
  FedSrv/fsmission.cpp:3920).
- The created part treasure is exported to the dropping **side's** group:
  `ThingSiteImpl::ShowObject` sets `g.fm.SetDefaultRecipient(CFSSide::FromIGC(pside)->GetGroup(), ...)`
  and calls `ExportObj(pmodel, type, NULL)` (FedSrv.CPP:10774,10778), gated only
  on active side + `STAGE_STARTED/STARTING` (FedSrv.CPP:10765-10767). Treasures
  carry `c_mtSeenBySide | c_mtPredictable` (Igc/treasureIGC.cpp:167-177), i.e.
  they are exported to the side that owns them, which is the subject's side.
- Treasure contents: `CreateTreasure` sets objectID/p0/v0/clusterID/time0
  (FedSrv.CPP:4329-4343) and the part overload sets `c_tcPart`, `treasureID =
  ppt->GetObjectID()`, `lifespan`, `amount` (`default: amount = 0`,
  FedSrv.CPP:4374-4396); the DROP_PART call passes `dv=25, lifespan=120`
  (FedSrv.CPP:6325-6326).

The port matches this: the subject is a `_peerShips` entry in the same sector and
is served both frames. The test already asserted the requester's `DROP_PART`
echo (`DropPartTests.cs` phase (b)); it simply never asserted (or waited for) the
requester's `TREASURE` echo. Native sends it, so waiting for it is parity, not a
concession.

## 3. Root cause (fixture)

- Old barrier, `tests/Allegiance.Core.Tests/DropPartTests.cs:254-261`: waited for
  server state, the three `DROP_PART` echoes, and only the **observer's** and
  **enemy's** `TreasureSpawns`.
- Then the fixture cleared every receive list (`:325`) and later asserted
  `Assert.Empty(subBot.TreasureSpawns)` (`:340`).
- Per-peer delivery (§1) lets the subject's `TREASURE(28)` arrive after the
  barrier satisfied on the observer's/enemy's copy. It lands in the cleared list
  and the empty-pack assertion fails with the phase-one weapon treasure.

The recorded P1002 signature was reproduced exactly with the deterministic hold
(§4): `TreasureId = 3, Amount = 0, TreasureCode = Part, ClusterId = 1051` — the
dropped weapon's phase-one treasure, not a wrongful empty-pack treasure. No
duplicate is possible: the P797 creation latch records every per-peer send and
the discovery sweep never re-sends a latched `(peer, Treasure, id)`
(Server.Docking.cs:504, Server.Treasure.cs:21-25). One copy arrived per list in
every run.

**No production defect:** the drop creates one treasure and sends one copy per
sector peer, exactly native's intent. Nothing was changed outside the fixture and
one Bot test seam.

## 4. Reproduction

Deterministic seam (new, test-only): `Bot.InboundFrameDelayHook`
(`src/Allegiance.Bot/Bot.cs:61-70`, invoked at `:1907-1917` before the recording
lock, i.e. on the transport thread). The fixture holds the subject's own
`TREASURE(28)` frame 200 ms, which turns the real per-peer skew into a fixed,
reproducible interleaving (all other traffic is untouched). The settle window of
the empty-pack phase was lengthened from 20 ticks (~30 ms of wall time) to a
400 ms wall-clock window — the *same* "let any wrongful treasure arrive" intent,
strictly a longer window, so the held echo is guaranteed to land inside it when
the barrier is missing.

Measured interleaving (probe run, then removed): frame held at T+0, barrier
exited at T+0 (old condition), phase-two reset at T+8 ms, empty-treasure
assertions at T+40 ms, held frame recorded at T+200 ms — i.e. exactly the
post-clear window.

| State | Runs | Result |
|---|---|---|
| Old barrier + hold + 400 ms settle (before) | 5 × alone | **0 passed / 5 failed**, every run at the old empty-pack assert, `TreasureId = 3, Amount = 0, TreasureCode = Part` |
| Fixed barrier + hold + 400 ms settle (after) | 5 × alone | **5 passed / 0 failed** |
| Old barrier, **no** hold, bounded-parallel | 60 × at `xargs`-style P4 (`~/allegiance-evidence/P1008/driver.py`) | **60 passed / 0 failed** (load average 12.3/15.1/14.7) — the natural window is far narrower than a full Fast sweep's load |
| Fixed barrier, no hold, bounded-parallel | 60 × at P4 | **60 passed / 0 failed** (load average 9.6/9.8/11.7) |

The natural-load cohort explains P1002's single failure in a 14k-case sweep
(one observation) and adds no new load: no busy loops, only the test's own
processes, maximum four at a time.

## 5. Fix (fixture only)

`tests/Allegiance.Core.Tests/DropPartTests.cs`:

- Phase one (`:251-285`): install the 200 ms hold on the subject's `TREASURE(28)`
  frame, and add the subject's own echo to the barrier —
  `subBot.TreasureSpawns.Any(t => TreasureCode == Part && TreasureId == pidW)`
  — with an explicit `Assert.True(pumped < 4000, ...)` so a missing echo fails
  loudly instead of silently leaving the race window open.
- Phase (c) (`:305-311`): assert the requester's copy too —
  `Assert.Single(subBot.TreasureSpawns)` with the same `ObjectId` as the
  observer's/enemy's. This is the assertion that makes the native "side group
  includes the dropping player" rule observable (native cite above). It also
  pins "exactly one, never a duplicate".
- Phase two (`:353-373`): the barrier now also waits for the subject's own
  `DROP_PART` echo with `Et == Pack, Mount == 0` (the phase's completion marker;
  an empty Pack creates no treasure), asserts the barrier completed, and asserts
  the subject received exactly one such echo (mirroring phase (b)).
- Empty-pack phase (`:375-384`): 400 ms wall-clock settle instead of 20 ticks,
  then the unchanged `Assert.Empty` on all three lists.

Every pre-existing assertion, its intent and its native citation is kept; the
only "weakening" is none at all — the new checks are stricter.

## 6. Sibling tests with the same wait-then-clear shape

| Test | Shape | Same hole? | Action |
|---|---|---|---|
| `tests/Allegiance.ClientCore.Tests/TreasureDropLiveTests.cs` | filters by treasure identity (`before` set + `PayloadId`, `:134-154`) | no | none |
| `P626InFlightUtilityLiveTests.DropCargo_wire_drops_cargo_part_as_treasure_and_broadcasts` | clears before the action, waits on the subject's `DROP_PART`, asserts no list emptiness (`:136-160`) | no | none |
| `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` | waits for the same list it then asserts `Single` (`:342-359`) | no | none |
| `P847MissionRulesCertifyTests` drop cases | assert **server-side model counts** (`sector.Models.OfType<Treasure>()`), no wire lists | no | none |

None needed the change; the shape is identical only in `DropPartTests`.

## 7. Tests

| Cohort | Passed / Failed / Skipped |
|---|---|
| Changed test, 20 × alone (deterministic hold installed, fixed barrier) | **20 / 0 / 0**, 972–1000 ms per case |
| `DropPartTests` class (3 cases), 3 × alone | **3 / 0 / 0** each run |
| `TreasureDropLiveTests` (sibling), 3 × alone | **1 / 0 / 0** each run (459/508/506 ms) |
| `P626InFlightUtilityLiveTests` + `P847MissionRulesCertifyTests` (siblings), 3 × alone | **13 / 0 / 0** each run |

## Evidence

Raw run logs and the per-run summaries (pass/fail, durations, load average) are
outside the repo in `~/allegiance-evidence/P1008/` (`repro-*.json`,
`failure-before-fix-run001.log`, `pass-after-fix-run001.log`, `driver.py` — the
bounded-parallel driver: N fresh `dotnet test` processes, P at a time, filter
`FullyQualifiedName~DropPartTests...`). Nothing in that directory is committed.

## 8. Gate

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (Debug; `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) | **0 warnings / 0 errors**, 21.63 s |
| `tools/build-tools.sh --drop-bins` (every `tools/*/*.csproj`, same flags via a scratch `DOTNET` wrapper) | **138/138 projects built**, no failures |
| One `tools/run-tests.sh --tier fast --no-build` | **FAIL: 14,236 passed / 9 failed / 20 skipped** of 14,265 discovered Fast cases, 573.0 s; calibration factor 1.1523, Fast limit 11.523 s. 9/9 infrastructure checks PASS; both quarantine lanes PASS (ClientCore 2, Core 17); no quarantine retry needed |
| Changed test inside that sweep | **Passed**, 0.693 s (below the limit) |
| `scripts/parity-scorecard.py` | all matrices validated (sim-rules 92.79% unchanged, 0 new errors) |
| `tools/check-glossary-links.py` | OK, 810 distinct paths |
| `git diff --check` (whitespace, LF) | clean |

Timing offenders in the sweep (all pre-existing, all pass their assertions):
`SC2NaniteSupportTests...` 11.653 s, `MissileLockLiveTests...` 37.812 s,
`LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire` 31.426 s,
`P999CivStrategyTests...` 15.617 s — every one is a ledgered Fast-limit case; none
touches the drop path.

### Non-task failures — 3× alone (fresh process, one case per invocation)

| Case (Fast outcome) | Three isolated repetitions |
|---|---|
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | 2/3 theory cases passed, 1 failed, in runs 1 and 3; 3/3 in run 2 — the ledgered one-ULP float upper-bound family (1/2/0 and 2/1/0 cohorts) |
| `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire` | **pass / pass / fail** (361 / 387 ms / 31 s) — the ledgered contention signature (0.4 s alone, 31.8 s under sweep load) |
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | **pass / pass / pass** (298–305 ms) — ledgered transit-receipt intermittency |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **pass / pass / fail** (972 / 969 / 998 ms) — ledgered auto-seat boundary family |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | **pass / pass / fail** (~1 s) — ledgered miner-capacity receipt family |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes` | **pass / pass / pass** (6 s) — the ledgered clearance issue stays OPEN |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | **pass / pass / pass** (568–602 ms) — ledgered roster-stint recommendation family |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | **pass / pass / pass**, 10/10 cases each run (33–34 s) — ledgered Fast-limit case |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | **pass / pass / pass** (~1 s) — ledgered load-only death-receipt intermittency |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **fail / fail / pass** (38 / 38 / 1 s) — the OPEN partial-tip receipt issue (P933/P935/P966 family) |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | **pass / pass / pass**, 2/2 cases each run (7 s) — ledgered Fast-limit case |

No failure is in the changed test or any drop/treasure path: the changed case
passed inside the sweep and 20/20 alone. Every non-task failure repeats the
signature its existing `docs/TEST_FLAKES.md` entry records (load-only or a
pre-existing OPEN receipt issue), and the run added no entry of its own.

AOT publish is not required: neither `src/Allegiance.Server` nor
`src/Allegiance.Core` changed (only `src/Allegiance.Bot`'s test seam and the
test). No HUD/UI path is touched, so no Classic/Modern screenshots apply.

## Native intent

One `DROP_PART` relay reaches every player flying in the sector, including the
requester (FedSrv.CPP:6318-6319; FedSrv/fsship.cpp:1246-1249), and the dropped
part's treasure is created once and exported to the dropping side's group
(FedSrv.CPP:4329-4343, 4374-4396, 10750-10778; Igc/treasureIGC.cpp:167-177). An
empty pack creates no treasure at all (FedSrv.CPP:6322-6323). The fixture now
waits for exactly that traffic before it treats a receive list as a new phase,
and asserts the requester's own copies on both phases.

## Modern approach

Keep the wire semantics untouched and make phase boundaries explicit: one
completion marker per phase (each peer's echo of the message that phase sent),
plus a test-only transport hook that can hold a single frame so the real
per-peer delivery skew becomes a deterministic interleaving instead of a rare
suite-load race. Assertions stay byte-for-byte strict (`Assert.Empty`, `Single`,
`Equal`), and the empty-pack settle window is a wall-clock window so a late echo
cannot escape it.
