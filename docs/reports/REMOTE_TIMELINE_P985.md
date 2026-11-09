# P985 — Remote-ship server-timestamp timeline (`visuals.remoteTimeline`, default OFF)

Base: master `aa979ab4242c` at dispatch (P975 merge), branch `pi/p985`. Changes are
uncommitted in the task worktree; no commit, push, stash or remote-host contact. Native
and FS2 Open checkouts were read-only. Scratch and raw logs are under `.scratch/p985/`.
`local.env` was present, so artwork/native-source tests ran.

## Recommendation: **keep OFF**

P985 adds an independent, opt-in remote-ship DRAW timeline. It fixes the measured
reordering problem and substantially improves mean/p95 error and correction counts,
but it is **not a candidate to turn on yet**:

- in the combined impairment its worst frame is **86.727 m**, identical to the
  default reckoner (P813b is 86.271 m), at the one-second stall recovery boundary;
- its mean added visual latency reaches **339 ms** in that replay and **361 ms** at
  points in the inspected clip (up to the bounded 500 ms setting);
- the clips are deterministic synthetic-flight/native-renderer evidence, not a live
  human dogfight A/B. The earlier predictor was removed because raw poses looked
  better, so an offline win alone is not enough to change the default.

The default P448a/P448b path and the independent P813b experiment remain available
and unchanged. P985 is useful for a future measured WAN/client A/B, not for default
enablement.

## 1. Design and native boundary

### Existing behaviour retained

Native maps an incoming heavy update's server time to client time
(`Igc/shipIGC.cpp:3110-3112`) and forward-replays the ship in `CalculateShip`
(`Igc/shipIGC.cpp:3244-3297`). The port's `ShipDeadReckoner` is that path and stays
the default. Its existing 1.5 s extrapolation cap also remains the P985 fallback;
native's update acceptance bound is at `Igc/shipIGC.cpp:3114-3115`.
`OwnShipServerClock` retains the native session offset conversion
(`clintlib/clintlib.h:860-869`). No server, wire, send-rate or content file changed.

P448a's history is explicit: the removed `RemoteShipPredictor` blended toward stale
positions and stacked extrapolation on top. P985 never feeds a delayed pose into
simulation and never replaces the default reckoner.

FS2 Open was used for the **idea** of a small sender-timestamp sorted buffer with
duplicate rejection (`fs2open:code/network/multi_interpolate.cpp:187-247`) and an
explicit mission-clock mapping (`fs2open:code/network/multi_time_manager.cpp:58-77`).
No FS2 code or close paraphrase was copied; the implementation below uses P813b's
existing Allegiance Hermite maths and Allegiance's own wire/clock conventions.

### New path

| File:line | Responsibility |
|---|---|
| `src/Allegiance.ClientCore/Flight/RemoteShipTimeline.cs:22-220` | Fixed eight-slot sorted ring; timestamp dedup/expiry; late insertion; adaptive delay; Hermite evaluation; stall/teleport cuts; reusable output orientation. |
| `RemoteShipHermiteInterpolator.cs:212-220,231-273` | P813b's existing cubic Hermite basis is shared, plus an allocation-free caller-owned-orientation overload of the same rotation blend. No second interpolator formula was written. |
| `src/Allegiance.ClientCore/State/ClientWorld.cs:385-442` | `ShipCaches.Timelines`: one bounded timeline per ship, retired with all other per-world ship state. |
| `ClientWorld.cs:1370-1420` | Flag, loop-tick feed through `RemoteTimelineClock` (review fix), feed/query and bounded delay telemetry. Own ships are explicitly excluded. |
| `RemoteShipTimeline.cs` `RemoteTimelineClock` | Loop-tick → local playout clock (review fix; the PING clock is not used). |
| `ClientWorld.cs:3118,4510,4878,5180-5204` | Ripcord/teleport, relaunch, death, sector departure and visibility cuts drop history. Mission rotation/reset/team-world retirement replaces the whole cache container. |
| `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:3127-3155` | A dedicated `RemoteShipDrawPose` wrapper is the only P985 consumer. Non-render users of `RemoteShipPose` (including training proofs) cannot receive a delayed pose; otherwise P813b/dead-reckoner/raw fallback is unchanged. |
| `src/Allegiance.ClientCore.Game/Rendering/VisualFlags.cs:63-90,128-129` | P979 table row and live property: `remoteTimeline`, default OFF. Profile parsing/persistence, `--visuals`, Options → Visuals and startup description are table-driven. |
| `Game1.cs:1763-1770`, `Game1.BenchmarkSharing.cs:254-265` | Startup and live Options toggles update the world's visual-only switch. |

The only production consumer is the remote hull/effect transform gather. Target/lead,
lock, collision, targeting, commands and bot decisions continue to read authoritative
`Latest`; own-ship prediction and outbound messages are untouched. No aim/lead pose is
served by this API.

### Constants and storage

- **8 samples**, each with preallocated orientation storage plus one reusable output
  orientation. Insert/search is O(8); memory is bounded per live ship. A pose query
  allocates **0 managed bytes** in the 10,000-query test. Shifting copies orientation
  values, never aliases a mutable orientation between slots.
- Samples are keyed by mapped **server tick**, not arrival. Equal timestamps are
  discarded; samples more than **1.0 s** behind the newest are expired. Late samples
  inside that window are inserted in order. The ring also evicts its oldest slot at
  capacity.
- Delay starts at **100 ms**. Source cadence and absolute arrival/source spacing
  mismatch each use EMA weight **0.20**. Target delay is
  `clamp(cadence + 3 × jitterEMA, 50 ms, 500 ms)` and is itself EMA-smoothed by 0.20.
  Cadence inputs above 500 ms and pathological arrival gaps above 750 ms do not tune
  the estimator, so the one-second stall cannot make delay oscillate.
- Past the newest sample the query returns false and the existing capped reckoner is
  used, then holds at its cap. At a **1.0 s** sender/arrival stall the ring resets.
- A same-sector discontinuity larger than `max(250 m, 4 × max(speed) × dt + 50 m)`
  starts a new history for a newest sample; a late pre-cut sample is rejected rather
  than bridging the cut. Sector/visibility/life/mission events are explicit cuts.

The server pose slot is the port's 20 Hz **loop** `TickIndex` (`Server.Broadcast.cs`
`BroadcastShipStates((uint)TickIndex)`), not sim time. **Review correction (Claude,
2026-11-09):** the delegate's first version converted `tick / 20` through the PING-anchored
`ClientTimeFromServerTime`, but PING carries `_simNow`, which advances by period ×
`timeScale` (`Server.Broadcast.cs:132`, P612/P618). Under auto time dilation, slow
motion or a single-player pause the two clocks drift apart without bound; ten seconds
at TiDi 0.5 put every sample 5 s in the "future" so the ring would draw the OLDEST
sample (frozen ~0.7 s behind) indefinitely. The ring is now fed by
`RemoteTimelineClock` (`RemoteShipTimeline.cs`, shared per world in `ShipCaches`): a
jitter-buffer playout clock, offset = windowed minimum of `arrival − tick/20` over two
2 s windows, a backwards tick (new server epoch) or a >0.25 s offset step clears all
histories. It never reads the PING clock. Pinned by
`Loop_ticks_map_on_the_local_clock_independent_of_time_dilation` and
`Timeline_clock_tracks_the_least_delayed_arrival_and_resets_on_a_step`. A second review
fix: a full ring now rejects a late packet older than every retained sample instead of
evicting a newer one (`Full_ring_rejects_a_late_packet_older_than_every_retained_sample`).
The replay harness drives the ring directly with source time, so its table is unchanged
(re-run by the reviewer: byte-identical). Arrival time never orders the ring.

## 2. Deterministic replay — all impairments and all three methods

`tools/p985-remote-timeline-proof/Program.cs` generates a constant-control
turning/climbing trajectory with the real `ShipKinematics` at 60 Hz, emits 10 Hz
quantized `ServerShipUpdate` poses, and drives the **actual** P448
`ShipDeadReckoner`, P813b interpolator and P985 timeline with one identical seeded
arrival transcript per scenario. Seeds are 985000 + scenario index. Impairments are
uniform 0–150 ms jitter, explicit adjacent-arrival reordering, 20% duplicates, seeded
5% loss, and a 7–8 s stall; `combined` applies all five. Metrics cover 2–14 s at
60 Hz (720 frames/method/scenario).

**Error convention (same for every method):** while interpolating, compare with
`truth(now − currentDelay)`; while extrapolating, compare with `truth(now)`.
Intentional buffering is therefore not double-counted as curve error and is reported
separately as added visual latency. Latency averages zero during fallback. A
correction spike is `abs(framePoseDelta − sameConventionTruthDelta) > 1 m`.
`Extrap %` is the fraction falling back to the reckoner (the default reckoner is
necessarily 100%).

Units: errors **m**, added visual latency **ms**. Values are from
[`P985_assets/replay-summary.tsv`](P985_assets/replay-summary.tsv).

| Impairment | Method | Mean | p95 | Max | Spikes | Extrap % | Added latency |
|---|---|---:|---:|---:|---:|---:|---:|
| clean | default reckoner | 0.0837 | 0.2483 | 0.2487 | 0 | 100.00 | 0.00 |
| clean | P813b Hermite | 0.0051 | 0.0091 | 0.0103 | 0 | 0.00 | 100.00 |
| clean | **P985 timeline** | **0.0051** | **0.0091** | **0.0103** | **0** | **0.00** | 100.00 |
| jitter 0–150 ms | default reckoner | 6.5174 | 13.8391 | 14.3697 | 87 | 100.00 | 0.00 |
| jitter 0–150 ms | P813b Hermite | 6.9442 | 13.5858 | 14.3682 | 171 | 24.44 | 74.58 |
| jitter 0–150 ms | **P985 timeline** | **0.0826** | **0.0105** | **14.3328** | **10** | **0.83** | 246.51 |
| reorder | default reckoner | 4.9888 | 9.9091 | 9.9139 | 90 | 100.00 | 0.00 |
| reorder | P813b Hermite | 3.9975 | 9.8968 | 9.8968 | 359 | 0.00 | 100.00 |
| reorder | **P985 timeline** | **0.0055** | **0.0097** | **0.0129** | **0** | **0.00** | 330.65 |
| duplicate | default reckoner | 0.1227 | 0.3102 | 0.3556 | 0 | 100.00 | 0.00 |
| duplicate | P813b Hermite | 0.0594 | 0.3102 | 0.3556 | 0 | 11.39 | 74.03 |
| duplicate | **P985 timeline** | **0.0051** | **0.0091** | **0.0103** | **0** | **0.00** | 100.00 |
| loss 5% | default reckoner | 0.1400 | 0.5200 | 2.2450 | 0 | 100.00 | 0.00 |
| loss 5% | P813b Hermite | 0.0637 | 0.5200 | 2.2450 | 0 | 5.69 | 100.29 |
| loss 5% | **P985 timeline** | **0.0630** | **0.5199** | **2.2450** | **0** | **5.56** | 99.64 |
| stall 1 s | default reckoner | 0.5644 | 4.5868 | 10.7854 | 3 | 100.00 | 0.00 |
| stall 1 s | P813b Hermite | 1.5900 | 6.7150 | 74.1771 | 19 | 8.19 | 100.06 |
| stall 1 s | **P985 timeline** | **0.4178** | **3.8413** | **9.7779** | **1** | **8.33** | 91.67 |
| combined | default reckoner | 8.4631 | 21.4519 | 86.7270 | 94 | 100.00 | 0.00 |
| combined | P813b Hermite | 9.3935 | 19.8143 | 86.2710 | 252 | 34.72 | 64.08 |
| combined | **P985 timeline** | **0.9460** | **12.9336** | **86.7270** | **3** | **6.25** | 339.38 |

Read honestly: P985 wins mean/p95 and spikes in every impaired case, but rare fallback
frames dominate its tail (hence jitter mean > p95), its combined maximum does not
beat the default, and the price is substantial visual delay. A second complete run
produced byte-identical TSV and JSON. Machine-readable details:
[`P985_assets/replay-summary.json`](P985_assets/replay-summary.json).

## 3. Visual evidence

All retained media is in `docs/reports/P985_assets/` and totals **<0.2 MB**, well below
both the 1 MB/clip and 3 MB total limits (and the lane's 2 MB evidence limit).

- [`remote-timeline-off.webm`](P985_assets/remote-timeline-off.webm) — VP9, 640×360,
  15 fps, 120 frames / 8 s, **57,376 bytes**.
- [`remote-timeline-on.webm`](P985_assets/remote-timeline-on.webm) — same shape,
  **55,991 bytes**.
- [`remote-timeline-classic.png`](P985_assets/remote-timeline-classic.png) and
  [`remote-timeline-modern.png`](P985_assets/remote-timeline-modern.png) — real
  production intro → Options → Visuals backbuffers at 800×480, both showing
  **“Remote timeline: on - server-time remote ship smoothing (M)”** (hotkey moved from T at merge: P982 Sim stars took T) beside the
  existing radial/range rows. The shared options model is identical in both skins.

The clips use the combined replay CSV and the real `NativeShipRenderer` with native
`fig30` geometry/texture. The only inspection changes are a 3× hull zoom, brightness,
caption, and a green cross at the same-convention truth position; poses are not
edited. OFF shows the default's larger correction displacement around that reference;
ON stays closer and smoother through reorder/duplication/loss. Both visibly fall back
at the stall, and ON's caption exposes its delay (about 283–361 ms in inspected
frames). I opened source and VP9-decoded contact frames and both stills; the content
is as described. `ffprobe` decodes both clips as 8.000 s VP9.

These are **not** claims of a live human dogfight win. Such a real-client WAN A/B is
the remaining evidence needed before reconsidering enablement.

## 4. Tests and gate

### Focused coverage

`tests/Allegiance.ClientCore.Tests/P985RemoteTimelineTests.cs` has **12 cases** (9 delegate + 3 review):
late insertion/dedup, bounded ring/expiry, stall/teleport reset, smoothed bounded
delay, zero-allocation queries, server-tick mapping and rotation retirement,
sector/death/respawn cuts, exact flag-off dead-reckoner equality, and own-ship
exclusion. The P979 tests cover launch parsing, JSON/profile persistence, unknown
keys, table default OFF, menu rows/hotkeys and both Classic/Modern styles.

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **PASS — 0 warnings / 0 errors** |
| `tools/build-tools.sh` | **PASS — all 126 tool projects** |
| New/affected ClientCore filter ×3 | **31/31 passed each run**, including all 9 new P985 cases, P813b, P975 cache, P448/P781 and server-clock families |
| Visual flags + Options filter ×3 | **26/26 passed each run**, including both styles |
| Replay proof | **PASS**, combined p95/spikes beat P813b; second run byte-identical |
| One full Fast suite | **COMPLETED, non-green on unrelated known families:** 13,774 passed / 2 failed / 3 not executed of 13,779 in 586.3 s; infrastructure 9/9 PASS |
| Showcase never-parks 5× | Initial **4/5**; failure was arrival-only, not parking. Exact failure retries **3/3 pass**; details below |

Fast's skipped/not-executed count is **3** (optional private Meshy pack). No P985 or
affected task test failed. The two failures were the already-ledgered three-peer
logon/seating race (2/3 isolated passes, same lobby -2 signature) and missile-lock
partial-tip timeout (3/3 isolated passes, ~1 s). The four SLOW FAST entries were
existing NPC-dodge/warp timing families; all passed 3/3 isolated below the limit.
They are recorded in `docs/TEST_FLAKES.md`; no second full Fast suite was run.

Never-parks runs 1/2/3/5 passed in 176.5/151.3/381.6/173.7 s, reached 24/30 with real
combat, and had longest stopped-with-target intervals of 1.906/2.022/2.011/2.006 s.
Run 4 failed the separate 480 s arrival objective at 19/30 but still held the parking
bound at 2.006 s. Its three retries all passed (456.5/153.5/182.9 s; 24/30; longest
0.601/2.006/1.716 s). This is the same arrival-only pattern already ledgered by P967;
the unchanged P975 base rate is 5/5. P985 changes no bot/server/sim/showcase path.

Raw evidence remains in `.scratch/p985/{fast,showcase,tests,proof}/`. The flake ledger
contains the exact counts. No AOT publish was required because `src/Allegiance.Server`
and `src/Allegiance.Core` were not changed.

## 5. Unfinished / future decision evidence

- A real multiplayer WAN/dogfight A/B with human-reviewed motion and aiming feel was
  not run (the rig/mini/Mac and public hosts were explicitly out of bounds). This is
  required before any future enablement proposal.
- Stall recovery still shares the default's worst combined frame. A future experiment
  should improve that boundary without increasing delay, rather than hide it by
  changing the error convention.

## Native intent

Native remote-ship presentation uses server-time-mapped authoritative updates and
forward flight replay. P985 leaves that path and every gameplay consumer unchanged.
The sorted delayed render ring is a default-off visual experiment, not a claimed
native behaviour or parity fix; the parity matrix records it as `n/a` beside P813b.

## Modern approach

A tiny sender-timestamped buffer with explicit cut ownership removes arrival-order
backsteps while preserving the native reckoner as fallback. Bounded adaptive delay,
fixed storage and deterministic impairment replay make the trade measurable: much
better mean/p95 continuity under jitter, at substantial added visual latency and an
unimproved worst stall frame. The evidence therefore says **keep off**.
