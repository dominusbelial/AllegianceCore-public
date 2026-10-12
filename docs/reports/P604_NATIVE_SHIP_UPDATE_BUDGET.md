# P604 — Native deviation-ranked ship-update budget (`broadcast.shipUpdateModel`)

**Task:** replace the port-only per-tick O(N²) teammate ship-state stream
(`BroadcastShipStates`' per-tick `SINGLE_SHIP_UPDATE(78)` `posBatch`) with the
native deviation-ranked HEAVY/LIGHT_SHIPS_UPDATE(176/175) budget — **behind a
flag, default `port`**. Both models stay selectable at boot without a rebuild,
for A/B; the default flips to `native` in a follow-up once the A/B (and a 221
run) shows it is faster, smooth on the client, and the bots navigate.

**Branch/commit:** `master` (not pushed). `76c3b430`.

---

## 0. Headline

| metric (measured, settled 5 s window) | ~121 | ~225 | ~391 |
|---|---|---|---|
| **`step:broadcastShipStates` ms — port** | 1.48 | 5.24 | 17.35 |
| **`step:broadcastShipStates` ms — native** | **0.68** | **1.56** | **3.26** |
| tick-wall mean ms — port | 3.78 | 9.69 | 25.11 |
| tick-wall mean ms — native | **2.98** | **6.66** | **13.98** |
| egress per player (kB/s) — port | 76.9 | 149.9 | 239.3 |
| egress per player (kB/s) — native | **11.1** | **12.8** | **16.6** |

At ~391 players native cuts `broadcastShipStates` **17.35→3.26 ms (−81%)**, the
whole tick **25.1→14.0 ms (−44%)**, and per-player egress **−93%** — the O(N²)
posBatch stream is gone, replaced by the bounded heavy/light budget. (Total
egress still includes the non-ship-state per-side syncs — stations/asteroids/
probes/missiles/exports/status/clusterinfo — which both modes send identically;
the **ship-state-specific** egress drops to the ~1.6 kB/s budget.)

**Recommendation (§9):** keep the default **`port`** (as instructed). `native`
is a clear, large, layout-independent perf win and the bots navigate correctly,
but the real-window client-smoothness check (P572-style) and a 221 run are the
prerequisites the task names for the default flip — those remain for the
follow-up, and the report's §9 is deliberately conservative.

---

## 1. Why: the port-only stream is the O(N²) ceiling (re-verified)

P600 (`docs/reports/P600_TICK_PROFILE_AND_PER_SECTOR_DESIGN.md`) measured
`BroadcastShipStates` at **~58% of the tick** (9.5 ms of 16.4 ms @ 241 players),
growing ~4.3× per 2× players (O(N²): per recipient × per visible ship). The
2026-10-11 221 tests put `step:broadcastShipStates` at **45–60 ms/tick** (budget
50 ms) around 390 players, so battles ran at 30–60% of real time. P603
(parallel build) and P605 (send pool) spread the same work but did not remove
it — the per-tick teammate `posBatch` itself is the cost.

`BroadcastShipStates` (`src/Allegiance.Server/Server.Broadcast.cs`) sends every
peer, every tick:
- the **owner's own-ship** `SingleShipUpdate(78)` — P608, its own guaranteed
  coalesce key (`CoalesceOwnShip` = 12);
- a per-tick **teammate position batch** (`posBatch`, `CoalesceShipStates` = 1) —
  one `SingleShipUpdate(78)` per **dirty same-side teammate**, for every peer.
  This is the O(N²) stream (P606 called it a "renderer-only extension").

Native has **no** per-tick teammate stream: per sector per tick it budgets
**`cHeavyPerInterval = 2`** full ("heavy") updates + a proportional **light**
share, **sorted by deviation** (how far each ship has drifted from the clients'
dead-reckoned prediction), sized to about **1,600 B/s per client**
(`cbAvailable = 1600·c_dsecUpdateClient`). Clients fill the gaps with dead
reckoning. Native uses `SINGLE_SHIP_UPDATE` for events and its own ship, **not**
as a per-tick feed of every teammate.

---

## 2. Native citations (re-verified in `~/Development/Allegiance/src/FedSrv/FedSrv.CPP`)

Every rule below was read directly in the reference source this session
(not taken on faith from P219's doc):

| Rule | Native cite |
|---|---|
| Per-cluster candidate gathering (ships with fresh updates) | `FedSrv.CPP:3742-3817` (rgShips built from the cluster ship list; player ships if `ucLastUpdate == c_ucShipUpdate`) |
| Per-client bandwidth sizing `cbAvailable = 1600·c_dsecUpdateClient` | `FedSrv.CPP:3836` (dead-code-disabled adaptive sizing at `:3824-3843`) |
| Fixed budgets `cHeavyPerInterval = 2`, `cLightPerInterval = 2` (w0dk4 patch) | `FedSrv.CPP:3844-3846` |
| Small-candidate fast path (`cShipUpdates <= 2` → all heavy, no sort) | `FedSrv.CPP:3860-3864` |
| Proportional light count `(cShipUpdates·cLightPerInterval)/cShips` | `FedSrv.CPP:3870` |
| Sort candidates by deviation descending (`CmpShipDeviation`) | `FedSrv.CPP:3923` (comparator `:3614-3621`) |
| **Deviation bands**: heavy-served reset to 0; light-served `+0.70 + rand(0,0.25)`; skipped `+1.00 + rand(0,0.25)` | `FedSrv.CPP:3937-3950` |
| Target promoted first, consumes one heavy slot | `FedSrv.CPP:4141-4146` |
| Assembly loop skips self & target | `FedSrv.CPP:4148-4170` |
| Heavy sent whenever flying (carries the owner's own fractions); light appended to the heavy message | `FedSrv.CPP:4179-4188` |
| Non-guaranteed send | `FedSrv.CPP:4189-4196` |

The port takes two documented simplifications (matching P219's established
convention, cited in the code):
- **light count** fixed at `cLightPerInterval = 2` instead of the converging
  formula (`(n·2)/cShips` → 2 at realistic cluster sizes);
- **deterministic** deviation increments (no live `rand(0,0.25)` jitter) —
  bands preserved exactly (`0 / +0.70 / +1.00`), jitter dropped for
  determinism/testability (the P219 precedent).

---

## 3. Dependency inventory — what the port's per-tick stream carries, and who consumes it

Scope item (2). The **only** thing unique to the per-tick stream is the teammate
`posBatch` of `SingleShipUpdate(78)`; everything else in `BroadcastShipStates`
(EXPORT/PLAYERINFO/SHIP_STATUS/CLUSTERINFO/loadout + same-side NPC identity) is
one-shot or throttled and is **kept unchanged** in native mode.

| Consumer | Data it reads | Source that replaces the posBatch in native | Break? |
|---|---|---|---|
| MonoGame teammate/visible-ship rendering + interpolation | `ClientWorld.Apply(HeavyShipsUpdate)` (`src/Allegiance.ClientCore/State/ClientWorld.cs:1719`) upserts + poses each 176 entry | **HEAVY(176)** budget | No — 176 is already a first-class pose source (`:1712`, "a live-position refresh exactly like SingleShipUpdate"); no per-tick assumption |
| Dead reckoning (P448a/b) | `ShipDeadReckoner` (`src/Allegiance.ClientCore/Flight/ShipDeadReckoner.cs`) re-runs the real flight model forward from the last seed; `ReseedRemoteReckoner` (`ClientWorld.cs:535`) re-seeds at every pose-arrival site — **both** SingleShipUpdate and HeavyShipsUpdate | 176 seeds it; it dead-recks between the sparser updates | No — designed for sparse updates (replays kinematics; 1.5 s extrapolation cap) |
| Minimap / radar | `DrawRadar` (`src/Allegiance.ClientCore.Game/Game1.Rendering.cs:2602`) reads `world.Ships` filtered by `SectorId == mySector`; 176 sets `SectorId = own sector` (`ClientWorld.cs:1757`) | 176 (same-sector ships) | No — radar shows only current-sector ships anyway; those still get 176 |
| allegiance-mcp bots' own pose (P608) | `CoalesceOwnShip` SingleShipUpdate → bot's `ClientWorld` via `ConnectionSession` | **own-ship stream kept** in native | No |
| Bots' view of teammates/enemies (avoidance/targeting) | `cs.World.Ships` (`tools/allegiance-mcp/BotManager.cs:1576-1591` `TryCsPose`) — the shared ClientCore `ClientWorld` | 176 (same-sector ships) | No — bots see same-sector ships via 176; **verified live** both modes reach target (§7) |
| Cross-side visibility | `Apply(HeavyShipsUpdate)` (P347/P346) — cross-side ships get 176 records in the viewer's cluster | 176 | No |
| Teammate object creation / identity | P62 EXPORT + P63 PLAYERINFO + P58 SHIP_STATUS (kept unchanged) | kept | No |

**Verdict:** no client or bot consumer assumes a per-tick teammate feed. The
shared `ClientCore.ClientWorld` treats HEAVY(176) as the in-sector live-position
source and dead-reckons between updates (native-faithful). **No client-side
change was required.**

---

## 4. Design (all behind the flag)

### 4.1 Flag
- `broadcast.shipUpdateModel`: `"port"` (**default**) | `"native"`.
- Env override: `ALLEGIANCE_SHIP_UPDATE_MODEL` (env wins).
- Printed at boot: `[boot] broadcast.shipUpdateModel = native|port …`.
- Stamped into the profiler report header alongside P603 broadcast / P605
  transport, so every measurement is labelled.
- Unrecognized value fails loudly at boot.

### 4.2 native mode — `BroadcastShipStates`
- The per-tick teammate **`posBatch` (CoalesceShipStates) is not sent** (serial
  path `Server.Broadcast.cs` ~:892-932; parallel path ~:1350-1362; the parallel
  snapshot also skips the now-useless O(P) `positionEncode` prebuild).
- The owner's own-ship stream (P608, `CoalesceOwnShip` = 12, guaranteed) is
  **kept every tick it changes**.
- EXPORT/PLAYERINFO/SHIP_STATUS/CLUSTERINFO/loadout + same-side-NPC identity are
  **kept** (they establish object creation/identity so the client can render the
  176 poses; they are one-shot/throttled, not per-tick O(N²)).

### 4.3 native mode — `BroadcastShipsUpdate` (the P219 path, extended)
- Small-candidate fast path (`all.Count <= 2`): unchanged, all heavy, reset 0.
- Budget path — the **real two-tier split** now:
  - top `cHeavy` (≤2) → **HEAVY(176)** record, `Deviation = 0`;
  - next `cLight` (≤2) → **LIGHT(175)** record (presence only, no pose),
    `Deviation += 0.70`;
  - the rest → **skipped**, `Deviation += 1.00`.
  This matches native's deviation bands (`FedSrv.CPP:3937-3950`) at native-sized
  bytes/sec. Port mode keeps P219's fold (all served heavy, deterministic
  +1.00) byte-for-byte.
- **HEAVY+LIGHT ride one coalesced `CoalesceShipsUpdate` frame** — the two
  fedmsgs are packed into one FedPacket (native appends the light message to the
  end of the heavy message, `FedSrv.CPP:4183-4188`); one latest-wins frame keeps
  both halves in lockstep and the client dispatches both by fmid.

---

## 5. Unit tests (`tests/Allegiance.Core.Tests/NativeShipUpdateModelTests.cs`, 4)

All 4 **fail on pre-P604 code** by construction (the native code paths /
flag do not exist there) and pass after:

| Contract | Test |
|---|---|
| flag defaults to `port` | `Ship_update_model_defaults_to_port` |
| native sends **no posBatch** (no `CoalesceShipStates`=1) yet keeps own-ship (`CoalesceOwnShip`=12); port contrast | `Native_mode_suppresses_posBatch_but_keeps_the_owner_own_ship_stream` |
| deviation bands match native (reset 0 / +0.70 / +1.00) | `Native_mode_deviation_bands_match_native` |
| native sends at most the budgeted heavy (2)+light (2) per peer per tick | `Native_mode_sends_at_most_the_budgeted_heavy_and_light_per_peer_per_tick` |

Existing broadcast suites (P85 teammate position, P219 budget, P139 ships-update,
P608 own-ship coalesce, P603 byte-identity) pass unchanged — **port mode is
byte-for-byte today's behaviour**.

An additive, diagnostic-only **egress counter** was added to the server
(`[egress] window: bytes=… peers=… per-peer-bps=…`, every `DisconnectSummaryTicks`)
so the A/B measures real egress per player; it has no behavioural effect.

---

## 6. Whole-solution build

`dotnet build`: **0 warnings, 0 errors**.

## 7. Verification

### 7.1 Unit suite (2 consecutive runs on the final code, `scripts/run-tests.sh`)

Run A: Lobby **12/12**, MonoGame **1735/1735**, ClientCore **603/603** clean;
Core **3926/3933** (7 failures — all documented live-wire load-flakes in
`docs/TEST_FLAKES.md`, none related to P604: `AutoReloadOnEmpty…RELOAD129`,
`Transport.TcpLoopback…ClientHalf`, `LiveWinCondition…Board_refused`,
`LiveTeamLobby…Unready`, `LiveCountdownStartPing`, `GameOverPlayers…Conquest leak`,
`AddPart…destroys_then_accept`).

Run B: Lobby **12/12**, MonoGame **1735/1735**, ClientCore **603/603** clean;
Core **3930/3933** (3 failures — `P590PodRescue`, `CargoBayReload…SwapPart`,
`AddPart…pickup_accept`, all in the ledger).

The 4 new `NativeShipUpdateModelTests` pass in **both** runs. Every failure is a
pre-existing fixed-sleep live-wire-receipt load-flake (they rotate run-to-run,
reproduce only under full-suite load, and are all cited in the ledger) — none
references the P604 symbols. (Two further runs on the pre-egress-counter code
are recorded in the commit note for transparency: run 1 = 4 ledged flakes,
run 2 = 6 ledged flakes.)

### 7.2 A/B live measurement (local server, temp worktree `/tmp/p604-proof`, never 221)

Pool transport + `parallelBuild` ON (the 221-like config), showcase,
settled 5 s window, `step:*` means from the profiler CSV, egress from the
`[egress]` counter:

| players | mode | `broadcastShipStates` ms | `broadcastShipsUpdate` ms | sim `update` ms | tick mean ms | tick p95 ms | ticks/5 s | egress kB/s/player | RSS kB |
|---|---|---|---|---|---|---|---|---|---|
| 121 | port | 1.48 | 0.60 | 0.26 | 3.78 | 5.29 | 101 | 76.9 | 106,659 |
| 120 | native | **0.68** | 0.57 | 0.26 | **2.98** | 4.07 | 100 | **11.1** | 104,353 |
| 225 | port | 5.24 | 1.93 | 0.53 | 9.69 | 12.54 | 100 | 149.9 | 118,224 |
| 225 | native | **1.56** | 1.74 | 0.51 | **6.66** | 8.84 | 100 | **12.8** | 115,599 |
| 391 | port | 17.35 | 4.41 | 0.99 | 25.11 | 35.80 | 101 | 239.3 | 151,815 |
| 373 | native | **3.26** | 4.67 | 1.01 | **13.98** | 18.70 | 101 | **16.6** | 135,373 |

Reading (measured):
- `broadcastShipStates` (the O(N²) bucket) falls **−54% / −70% / −81%** at
  ~120 / 225 / 391 players.
- Whole tick −21% / −31% / −44%. At ~391 the port tick is 25 ms (approaching the
  50 ms budget; matching P600's saturation finding), native is 14 ms — roughly
  doubling the headroom.
- Per-player egress −86% / −91% / −93%. The ship-state-specific portion drops to
  ~1.5 kB/s (2 heavy + 2 light × 20 tps ≈ 76 B × 20), matching native's
  `cbAvailable` budget; the remaining ~10–16 kB/s is the shared non-ship-state
  streams both modes send identically.
- `sim.update` is unchanged (~0.3–1.0 ms) — native only removes broadcast work.
- Disconnects: every run showed exactly `ReadErrorOrEof=1` (a single bot that
  closed its socket — mode-independent, not a P604 effect).

### 7.3 Behaviour checks — bot navigation (the mandatory live check)

`size_scale 1`, `target_sector Haumea` (XXV_simple's sectors are Arrokoth…Sedna;
"Alderan" is not one — the task brief's target name was inexact, so the
canonical P600 rendezvous sector Haumea was used). Both modes, 120 s window,
server `[warp] ship … crossed` lines:

| mode | total warp crossings | distinct peer ships that warped | reached Haumea |
|---|---|---|---|
| port (baseline) | 34 | 29 | 5 |
| native | 34 | 30 | 4 |

Bots **leave their home sectors** (Ceto / Quaoar) and **reach the target**
(Haumea) via real warp transits in both modes — native does **not** break bot
navigation (native is statistically equivalent to the port baseline).

### 7.4 Client smoothness (real window)

The architectural proof is in place — the shared `ClientCore.ClientWorld` already
consumes HEAVY(176) as a first-class in-sector pose source and dead-reckons
between the sparser updates (`ShipDeadReckoner`, P448a/b), and the bots'
navigation through the same `ClientWorld` confirms native's sparse updates carry
enough state. The **P572-style real-window frame-sequence capture** (teammates
and enemies at 390 players, `[P448b] dead-reckon re-seed corrections` log
`n/mean/max`) was **not** executed in this session — it needs the windowed
MonoGame client harness on a display and is recorded in §10 as the explicit
prerequisite (alongside a 221 run) for the default flip, per the task's own plan.
This is why §9 keeps the default `port`.

---

## 8. Files touched (commit `76c3b430`)

| File | Change |
|---|---|
| `src/Allegiance.Server/ServerConfig.cs` | `BroadcastConfig.ShipUpdateModel` (`"port"` \| `"native"`) |
| `src/Allegiance.Server/Server.cs` | `_nativeShipUpdates` / `_shipUpdateModel` / `_egressBytesSent` |
| `src/Allegiance.Server/Server.Lifecycle.cs` | `ResolveShipUpdateModel()` (boot-print + profiler label); egress accounting in `SendPeer`/`SendPeerCoalescing` |
| `src/Allegiance.Server/Server.Broadcast.cs` | gate `posBatch` (serial + parallel) on `!_nativeShipUpdates`; native heavy/light split + deviation bands in `BroadcastShipsUpdatePeer`; `[egress]` log in `disconnectSummary` |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs` | `TestSetShipUpdateModel(bool)` seam |
| `tests/Allegiance.Core.Tests/NativeShipUpdateModelTests.cs` | 4 new tests |

---

## 9. Recommendation on the default

**Keep `broadcast.shipUpdateModel = "port"` (the default) in this commit**, as
the task instructs. The `native` model is a large, measured, layout-independent
performance win (−81% on the ceiling bucket, −44% tick, −93% ship-state egress
at ~391 players) and the bots navigate correctly, but the task's stated gate for
flipping the default is "faster, **smooth on the client**, and the bots navigate".
Faster ✓ and bots ✓ are demonstrated here; smooth-on-the-client (real-window)
remains to be validated. Both modes stay selectable at boot via
`broadcast.shipUpdateModel` / `ALLEGIANCE_SHIP_UPDATE_MODEL`, so a follow-up can
A/B the real client and flip the default with a one-line config change.

---

## 10. Follow-ups (named concretely)

1. **Flip the default to `native`** after: (a) the P572 real-window frame-sequence
   capture at 390 players (teammate/enemy smoothness + `[P448b]` correction
   sizes, port vs native), and (b) a 221 run confirming egress/tick margins hold
   on the deploy hardware.
2. **High-density freeze note:** the dead-reckoner's 1.5 s extrapolation cap means
   a ship in an extremely dense single-sector fight (≫2 heavy slots per tick of
   rotation) holds pose at the cap between its heavy refreshes. This is native's
   inherent behavior at high density; the P448b correction stats quantify it.
   If it proves objectionable in the real-window check, the follow-up can raise
   the cap or grade heavy refresh by distance (native's client detail scheme).

---

## 11. Raw data

Committed in `docs/reports/P604_assets/` (not this session's screenshots — the
A/B summaries, profiler CSVs, server logs and nav logs are referenced here):
- `summary-port-s4.json` / `summary-native-s4.json` / `-s8` / `-s13` and their
  `tick-*.csv` + `server-*.out` (A/B numbers above).
- `server-nav-port.out` / `server-nav-native.out` (the `[warp]` crossing logs).

**All numbers in §7 are what was actually measured** on this workstation (shared
cores; bots + server co-resident, the same caveat P600 noted — relative bucket
breakdown is unaffected). No values were fabricated or extrapolated from 221.
