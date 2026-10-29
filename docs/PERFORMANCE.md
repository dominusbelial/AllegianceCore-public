# Server performance: the 200v200 baseline

This page explains how the server got from struggling at about 100 players to running 200-vs-200 battles at full speed. It
covers the target, how we measure, each change that moved the numbers, and the settings that came out of it. Those settings
are now **the baseline**: every later change is measured against them.

## The target

We picked a deliberately hard target: **two full teams of about 195 players each (≈ 394 connections) fighting a
complete match unattended**. That includes economy, research, construction, scouting, strike waves, a bomber finale, game over and the next match.
We adopted this "200v200" run as the project's standard test on 2026-10-12.

The server ticks at **20 Hz**, so each tick has a **50 ms budget** (100 ticks per 5 s window means full speed). The
acceptance bar for a busy window is p95 ≤ 40 ms, p99 ≤ 50 ms and time dilation ≥ 0.95. Above that, the game slows down
(see time dilation below) instead of stuttering.

## How we measure

**The rig.** One mini PC runs the server: AMD Ryzen 9 6900HX (8 cores / 16 threads), 32 GB, Docker, Native AOT build. A
separate Apple M4 Max runs the bots over 2.5 Gb wired Ethernet. The bots are the project's `allegiance-mcp` driver: 390
bot pilots in two worker processes, plus probes. Running the bots on their own machine matters: when bots shared a
machine with other work, the battles died in mass "no write progress" disconnects that had nothing to do with the server.

**The match.** The showcase scenario at size scale 13 on `m2xspiderw.igc`. Fleets "march" from home through the alephs and
converge on Alderan. The economy runs, the research commander unlocks tech, and a bounded finale ends each match. Civilisations are picked
at random per match.

**One A/B batch** = deploy one build or setting to the rig, let **2–3 full matches** finish unattended (game over → next
match → game over), and compare with the previous batch. We never cut a match in the middle. Batches are numbered (b39,
b50, b55, …).

**What the server logs** (every 5 s, `ALLEGIANCE_TICK_PROFILER=1`):
- `[tickprof]` — tick count, tick wall time mean / p95 / max, and per-subsystem timings;
- `[gcprof]` — GC pause total and worst, gen0/1/2 counts, allocation, heap after GC;
- `[tidi]` — time dilation, the factor the game slowed to;
- `[overrun]` — ticks that ran over budget;
- egress by message type, update age, and send-queue health.

**What we compare:** median tick, window p95 (median and p90 across windows), worst tick, windows that fell behind (< 95
ticks in 5 s), windows with time dilation, GC pauses > 20 / > 40 ms, allocation rate and heap per match. We also check that
the battle stayed real: kills per match, station kills, research, strikes, bot stall reports, and disconnects. A setting
that makes ticks cheaper by making bots stop fighting is a regression.

**Rules we kept:**
- A new idea goes behind a flag that is off by default.
- It is kept only if the 200v200 A/B improves.
- Behaviour must match the original game. A speed-up that changes what players see or what the rules do does not count.

## How it started vs. now

The workload grew along the way: early runs had no economy, research or finale. So early and late numbers are not the same
load, and the late numbers carry much more game.

| When (2026) | State | Result |
|---|---|---|
| Oct 9 | Original port design: one thread builds every player's update, a writer thread per connection, workstation GC | ~94 players already used one core at 93 % |
| Oct 10 | Tick profiler (P600) | Simulation was only 3–4 % of the tick; **broadcast fan-out was ~92 %**, and O(N²) in players |
| Oct 11 | Parallel per-player build (P603) + send-worker pool transport (P605) | 301 players: **60 → 96 ticks per 5 s**, tick 83 → 46 ms, threads 614 → 342 |
| Oct 11 | Native ship-update model (P604): the original's ranked HEAVY/LIGHT update budget | about **1/5 of the bandwidth** of the port model at 400 players |
| Oct 12 | Server GC (P619) | 392 players: tick **p95 1.2–1.8 s → 65–80 ms** (the port model had a gen2 GC storm every window) |
| Oct 14 | Automatic time dilation (P618) on by default | a 98-minute 200v200 match: mean 31 ms, p95 44 ms, worst 242 ms |
| Oct 27 | Immutable broadcast snapshot (P668) + encode once (P669), rig b42 vs b39 | big-fight peak tick **71–80 → 44 ms**; overruns **22–40 k → ~1 k**; worst time dilation 0.48 → 0.87 |
| Oct 28 | Big-fight tick budget (P863), rig b50 vs b49 | median tick **35.8 → 16.3 ms**; windows over 40 ms 55.6 % → 4.4 %; time-dilated windows 5.6 % → 0.6 % |
| Oct 28–29 | Native NPC travel (`sim.nativeGotoPlan`), rig b52 / b55 | performance neutral (16.2 ms); more station kills; a match ended by conquest before the deadline |
| **Now (b55)** | The baseline below, 3 full matches, 394 connections | **median tick 16.2 ms, window p95 ≈ 20 ms, 0 windows behind, worst GC pause 36 ms**, ~1,500–1,900 kills per match |

At the start, the server could not keep a 300-player game at full speed. Today a 394-connection match uses about a
third of the tick budget at the median.

## The baseline settings

| Setting | Value | Why |
|---|---|---|
| `broadcast.immutableSnapshot` + `broadcast.encodeOnce` | on | Build one read-only world snapshot per tick and encode each message once for everyone who gets it (b42) |
| Big-fight tick budget (P863) | built in | Profiled and cut the cost of crowded-sector ticks (b50); P863b trimmed surge allocation |
| `broadcast.parallelBuild` | true | Builds each player's updates in parallel; output is byte-identical to serial |
| `transport.mode` | `"pool"` | A small pool of send workers instead of a thread per connection; slow peers are skipped, not waited on |
| `broadcast.shipUpdateModel` | `"native"` | The original game's ranked ship-update budget; native behaviour and far less bandwidth |
| `sim.nativeGotoPlan` | true | The original game's NPC travel planning; native behaviour, no cost |
| Server GC (concurrent) | on | Built into the server project |
| `liveSettings.timeDilation` | `"auto"` | Under overload the game slows slightly instead of stuttering |

Not every row is a code default yet: see the README's "Recommended settings" for what a server needs to set today.

## Options that stay off

| Option | Why it is off |
|---|---|
| `broadcast.interestTiers` | Halves update age (0.22 s) but costs +40 % bandwidth and +6–18 ms peak tick |
| `sim.nativeBroadcastOrder` (native parity) | +7 ms median tick on b51; to be re-tested after P863b, adopted if it costs < ~2 ms |
| `receive.decouple` | Only a local A/B so far (P673), never tested on the rig |
| Spotter cache, parallel sector cells | No gain on the rig; removed (P858) |

## What is still open

- **Heap per match:** the heap floor still rises from match to match without tick cost so far (investigation P884/P884b).
- **Time dilation ≥ 0.95 in every window:** the rare surge windows (≈ 0.6 %) are the remaining gap.

## Reports

P600, P603, P604, P605, P618, P619, P659, P668, P669, P863 and P863b in `docs/reports/` hold the local measurements and
design for each step. The rig A/B numbers above come from the 200v200 batches.
