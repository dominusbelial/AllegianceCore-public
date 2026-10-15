# P672 — scoreboard traffic

Base: `integ3`, `6c3e54ad` (clash43 source). Changes remain in the working tree;
no commit, push, deployment, rig/221 run, or 200v200 run was performed.

## Native comparison

Native references below are relative to `~/Development/Allegiance/src/` and were
checked against the local source checkout:

- `FedSrv/FedSrv.CPP:2080-2088`: PLAYERINFO seeds experience, kills, deaths and
  ejections from the current ship. The port's PLAYERINFO sender leaves these
  counts zero; that is a port limitation, not a native rule.
- `FedSrv/FedSrv.CPP:11793-11798,11811-11821`: KILL_SHIP carries death/kill credit
  flags; credited cross-side non-pod kills update the server's kill count and
  combat rating.
- `clintlib/appmsg.cpp:2747-2775,2803-2815`: the native client consumes KILL_SHIP,
  updates the victim's deaths/ejections and the credited player's kills, and
  notifies the team member view.
- `clintlib/clintlib.h:346-357`: MissionKills/MissionDeaths read the local ship's
  counters, and AddKill/AddDeath mutate those counters.
- `WinTrek/teampane.cpp:233-244`: the team pane draws these local mission counts.

Thus native keeps its live scoreboard through event-driven local counters,
seeded by PLAYERINFO. It does not require this port's periodic whole-roster
message. P672 keeps the renderer-only client's authoritative score feed, including
rank, score, combat rating and all six secondary counters.

## Design

Full snapshots retain fmid **209** and its existing `8 + 56*N` layout. The new
port-only **ScoreRosterDelta (219)** has this little-endian layout:

| Offset | Field |
| --- | --- |
| 0, 2 | ushort message length, fmid |
| 4, 6 | ushort baseline row count, resulting row count |
| 8, 10 | ushort changed-row count, reserved zero |
| 12 onward | changed-row count × (ushort row index + 56-byte ScoreRosterEntry) |

Patches overwrite only listed rows, preserve the unchanged prefix and truncate
to the resulting count. Every appended row must be listed. Interior departures
shift subsequent rows; dense churn uses a full snapshot if it costs less. Row
positions preserve collection order and duplicate character names without
inventing a name-based identity constraint.

Both decoders reject inconsistent headers, lengths and bounds. Delta indices
must increase strictly, stay inside the resulting count and cover every appended
row; unknown reserved bits are rejected. Counts are bounded by the full message's
16-bit length (1,170 rows); encoding rejects overflow. Full snapshots now also
reject fixed-header overlap, invalid stride and unclaimed/trailing payload bytes.

The server retains one baseline per mission. Both message forms use `SendPeer`'s
guaranteed **ordered** TCP lane, with no coalescing or shedding. This implements
the brief's “since the last broadcast” option: the transport's ordering establishes
the baseline, with no new ACK protocol or per-recipient score copies. A client
with a mismatched baseline count ignores the patch until a full snapshot arrives.
A same-count artificial gap also heals at the next full snapshot.

- Changes are checked at the existing 40-tick cadence (two seconds at 20 Hz).
  No message is emitted for unchanged scores between full refreshes.
- Full snapshots go immediately to new recipients on join/rejoin, to everyone
  on mission-stage transitions (including match start), and every **30 simulation
  seconds** even with no score changes. New recipients get the current full state;
  existing recipients get the corresponding patch in the same pass.
- Empty/retired missions and missions with no eligible audience release their
  retained state. Recipients are intersected with the current connected set.
- `ClientWorld` applies patches to the same `ScoreRoster` consumed by the
  scoreboard, team pane and pilot info. The test Bot reconstructs snapshots for
  its existing score-history assertions; clearing that history keeps its baseline.

The new client decoder and server should be deployed together. An older client
that ignores fmid 219 would refresh only on 209 snapshots.

## Harness audience audit

The server uses its existing `HarnessPrefix` rule (configured test-harness prefix,
otherwise `mcp-`, ordinal matching) to exclude harness recipients. Humans still
see harness players as rows in their mission roster.

MCP consumers do not need a reduced roster:

- `tools/allegiance-mcp/BotManager.cs:3147-3154` increments each bot's metrics from
  `World.Events.KillsAfter`, checking the credited ship id and type.
- `BotManager.Finale.cs:224-228` builds the rolling kill window and per-side
  maturity from those bot counters.
- `ShowcaseFinale.cs:35-46` selects the “leader” side by these maturity kill counts.

There are no ScoreRoster references in `tools/allegiance-mcp`. Its scenarios,
metrics and finale retain their existing kill-event feed. The non-harness test
Bot remains a score consumer and understands both forms.

## Loopback measurement

`P672ScoreRosterTrafficTests` boots a real server and 64 actual TCP Bot peers:
**2 human names + 62 harness names**. After join warmup, it measures 640 ticks
(32 simulation seconds, including a periodic full refresh), then 240 ticks
(12 simulation seconds) with **32 lethal ReceiveDamage events**. Those events
execute real server AddKill/AddDeath and combat-rating logic; the test verifies
32 kills and 32 deaths. As in the existing PlayerScoreCombatScoringTests fixture,
a victim side without a lifepod forces death rather than ejection. This stresses
score replication, not weapon aiming or a rig-sized fleet battle.

Ticks are paced at 20 Hz. P661's real socket `[egress-by-type]` counters provide
sent bytes, including FEDMESSAGE headers and excluding separately attributed
transport/CRC overhead. The benchmark checks roster offered bytes equal sent
bytes before recording each window. Wall windows include a 200 ms writer-drain
allowance; simulation-normalized rates make runs directly comparable.

The unchanged base source was extracted with `git archive` into
`/tmp/p672-baseline`, built there, and ran the **identical benchmark source**.
No legacy-sender mode was added to production code.

| Phase | Before roster bytes | After roster bytes | Before B/s (sim) | After B/s (sim) | Reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| Steady, 32 s | 3,678,208 | 7,184 | 114,944 | 224.500 | **99.805%** |
| Combat, 12 s / 32 kills | 1,379,328 | 5,132 | 114,944 | 427.667 | **99.628%** |

First final-tree run: wall-normalized rates were 114,194.791 → 223.023 B/s
(steady) and 113,025.899 → 420.510 B/s (combat). After steady state sends only
one full message per human in the window (two messages total); combat sends
12 delta messages total. Harness roster egress falls from 3,563,264 / 1,336,224
bytes in the two windows to **zero** in both.

The gain is not solely audience filtering. For each human, steady roster bytes
fall from 57,472 to 3,592 in the measured 32 seconds; combat bytes fall from
21,552 to 2,566. A 30-second steady long-run full-refresh cadence is 15× cheaper
per human than the previous two-second snapshots. The measured windows contain
one full refresh versus sixteen old snapshots.

All three final-tree repetitions sent exactly **7,184 steady bytes**. Combat
sent **5,132 / 5,016 / 5,016 bytes** (427.667 / 418.000 / 418.000 B/s of simulation,
99.628–99.636% reduction). The slight difference comes from setup-dependent
batch boundaries combining a killer-row change. All three runs assert exactly
32 real kills and deaths; their counters report zero harness roster bytes.

Raw P661 `[egress-by-type]` lines and exact 209/219 rows are retained in
[P672_EGRESS.txt](P672_EGRESS.txt), including all three final-tree repetitions.
These are 64-peer loopback measurements, not an extrapolated claim about rig
performance at 392 peers. The brief's 35–44% egress share was observed separately
on clash43; this smaller docked fixture's baseline shares were 28.1% steady and
26.5% during combat.

## Verification

`NUGET_HTTP_CACHE_PATH=/tmp/p672-nuget dotnet build AllegianceCore.slnx --nologo -v:q`
passed with **0 warnings and 0 errors**. Redirecting NuGet's HTTP cache avoided
writes to the sandbox's read-only home cache; no project settings were weakened.
Artwork and native-source corpora were enabled through the local environment.

The focused matrix passed **40 methods / 120 separate method-filtered runs /
147 executed cases**, with no failures or skips. Every selected method ran three
times alone. It covers both new codec/model classes, the new live client and
64-peer measurement tests, the existing full codec and Bot score test, dispatch
coverage, world/reconnect cleanup, all scoreboard/team-pane/pilot-info layouts,
and MCP rolling kill metrics and deterministic leader selection.

Complete assemblies run unfiltered, sequentially after the build and isolation
matrix, with `ALLEGIANCE_LIVE_P660_SHOWCASE=1` to include the actual 15v15 MCP
loopback showcase. Final counts and any load-flake reruns follow.

| Assembly | Passed | Failed in full run | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,314 | 1 existing countdown timing case | 0 |
| ClientCore | 937 | 0 | 0 |
| MonoGame | 2,125 | 0 | 3 existing optional Meshy-pack tests |

The opt-in `P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms`
ran and passed (21 seconds). The 64-peer benchmark also passed inside the full
Core run, again sending 7,184 steady bytes and 5,132 combat bytes.

Core's sole failure was
`LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown`:
its line 212 read the second peer's SET_START_TIME collection before that
receipt arrived, after waiting only for the first peer. This exact test already
has an isolation-flake entry in `docs/TEST_FLAKES.md`. After all suites finished,
the first exact-method isolation batch was **fail / pass / pass**; a fresh final
batch was **pass / pass / pass**. Both batches are retained in the results. No
countdown production code, fixture assertions, deadlines or quarantine traits
were changed. See the new P672 entry in [TEST_FLAKES](../TEST_FLAKES.md).

[Durable verification ledger](P672_RESULTS.json) records all 120 focused runs,
the three complete assembly outcomes, and all six countdown reruns.
`git diff --check` passes. No commit or push was made.
