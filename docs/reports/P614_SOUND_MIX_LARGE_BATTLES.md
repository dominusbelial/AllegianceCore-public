# P614 — sound mix for large battles: native voice limiting, priorities & mutex groups

## Summary

The port's `SoundManager` had **no global voice cap, no per-category cap, no
priority and no "only one of these at a time" grouping**. Every shot, hit,
missile, explosion and ambient of ~200 ships started its own overlapping
instance, so hundreds summed into the "constant rattle at the highest volume"
Domingo heard on 221 (2026-10-11).

P614 ports native's voice management wholesale: a **global voice budget** with
**priority-based stealing**, **per-kind limit (mutex) groups**, **distance
culling**, and a hierarchy that lets **explosions cut through gunfire**. The
result is a quiet, articulated mix whose audible-voice count is *scale
invariant* — whether 30, 120 or 200 ships are fighting, at most ~16 voices are
audible and projectile in-flight ambients are capped to the nearest 6, instead
of the ~67,000-start unbounded rattle in Domingo's client log.

The core decision engine is a new pure, headless-testable class
`Allegiance.ClientCore.MonoGame.Audio.VoiceBudget`, with `SoundManager` wiring
every loud world sound through it.

---

## 1. Native model (everything cited)

Native's sound engine already does exactly this; the port just never ported it.

### 1a. Global voice budget with priority stealing
`DS3DSoundEngine::Update()` (soundengine/ds3dengine.cpp:360-466) keeps only the
top `m_nNumBuffersDesired` most-important virtual sounds audible each frame:

```
ds3dengine.cpp:410  // get the m_nNumBuffersDesired most important sounds …
ds3dengine.cpp:411-414  … by popping them off a priority queue …
ds3dengine.cpp:460-466  … and stop the buffers of the least important ones.
```

The budget is initialised per quality tier; software (no HW buffers) mid
quality is **desired 16 / max 24**, max quality **desired 24 / max 32**
(ds3dengine.cpp:513-544). The port uses the conservative mid-quality numbers
(desired 16 / max 24).

The ordering function is native's **dynamic priority**
(soundengine/ds3dvirtualbuffer.cpp:384-386):

```cpp
m_fDynamicPriority = m_fGain + m_fPriority          // loudness(dB) + static priority
    + (m_bBufferPlaying ? 0.1f : 0)                 // continuity boost for playing sounds
    + std::max(0.0f, 1.0f * (2 - m_fAge));          // young-sound boost
```

Higher wins; out-of-range / inaudible sounds get a −100 kick and are skipped
(`!IsAudible()`, ds3dengine.cpp:424; ds3dvirtualbuffer.cpp:804-806). The
audibility floor is `c_fMinAudible = −60` dB (ds3dbuffer.h:12).

### 1b. Priorities and mutex/limit groups
Templates set priority (`CreatePrioritySoundTemplate(…, fPriority)`,
soundengine/soundtemplates.h:40-42) and serialise via a **mutex** — a template
whose sound "will not be played at the same time as any other … with the same
mutex" and where a new low-priority sound **is discarded** once the mutex is
held (`CreateSerializedSoundTemplate`, soundtemplates.h:101-106). Repeating
fire keeps "only the two most recent sounds" (`CreateRepeatingFireSoundTemplate`,
soundtemplates.h:56-60).

The actual priorities are in the artwork `sounddef.mdl` (a port of native's
definitions):
- projectile single-shot & **projectile ambient** (`er_beam`): `PrioritySound(-1.5)` — sounddef.mdl:14,11-17
- weapon **bursts**: `PrioritySound(5)` + `RepeatingFireSound` — sounddef.mdl:18-20,38…
- **large** explosion: default priority (sounddef.mdl:268); **medium**: default (:269, = `explodeShipSound`:276); **small**: `PrioritySound(-1)` (:270)
- my shield/hull hits: default priority (:250,256); **other** shield/hull: `PrioritySound(-1.6)` (:263-264)
- **voices / warnings**: `SerializedSound(MutexSal, DefaultSalTimeout=10, …, true, …)` (:71-79,327) — a mutex group, never stacked.

### 1c. Distance / rolloff / audible radius
`DS3DVirtualSoundBuffer::Update` computes distance gain `min(0, fRolloff × −10·ln(d²/min²))`
(ds3dvirtualbuffer.cpp:795-801), clamps the max distance to
`minDist × c_fMinToMaxDistanceRatio` (ds3dbuffer.h:15, ds3dvirtualbuffer.cpp:747-755)
and drops anything beyond it or below −60 dB. The port's existing positional
curves (`ProjectileAmbientSoundCurve`, `ExplosionSoundCurve`, `HitSoundCurve`,
…) already model this and the planners/router drop out-of-range sounds before
they ever reach the mixer.

---

## 2. What was implemented

### New pure core: `src/…/Audio/VoiceBudget.cs`
- `SoundCategory` enum + `SoundCategories.Priority` / `GroupCap` / `Label` —
  the priority ladder and per-kind limit groups.
- `BudgetSound` — a voice's inputs (id, category, loudness, audible, age, playing).
- `VoiceBudgetConfig(DesiredVoices = 16, MaxVoices = 24)`.
- `VoiceBudget.Decide(sounds, cfg)` — ranks audible candidates by native
  dynamic priority, admits the top `Desired` subject to per-kind group caps,
  and returns the admitted ids. Excludes inaudible (out-of-range) sounds.

### Category ladder (each anchored to native; refinements flagged)
| Category | Priority | GroupCap | Native anchor |
|---|---|---|---|
| Warning / voice | +9 | 4 | SerializedSound(MutexSal,…) sounddef.mdl:71-79,327 |
| Weapon burst | +5 | 4 | PrioritySound(5) sounddef.mdl:18 |
| Explosion **big** (station/capital) | +4 | 4 | largeExplosionSound default (:268) — tiered above ship for P614 cut-through* |
| Explosion **ship** (fighter) | +3 | 6 | mediumExplosionSound default (:269) — tiered* |
| Explosion **detonation** (missile/small) | +2 | 8 | smallExplosionSound PrioritySound(-1) (:270) — tiered* |
| Engine bed (own-ship, exempt) | +2 | 6 | — (never culled) |
| Hit **mine** | +1 | 6 | myShieldHitSound default (:250,256) |
| Missile ambient | +1 | 6 | HornetMissileFlight default (:1303) |
| World ambient (warp/station/probe) | +1 | 8 | AmbientSoundSite loops (trekigc.cpp:1436-1468) |
| Hit **other** | −1.6 | 4 | PrioritySound(-1.6) (:263-264) |
| Weapon fire | −1.5 | 4 | PrioritySound(-1.5) single-shot (:38…) |
| **Projectile ambient** | −1.5 | **6** | PrioritySound(-1.5) er_beam (:14) — nearest-6, the rattle cap |

\* Native ties the large/medium explosion templates at the default priority
(sounddef.mdl:268-269), so a fighter blast would not reliably beat a large one
on priority alone. The P614 brief explicitly requires **station & big-ship >
fighter > missile detonation**, so the port tiers those three (4 / 3 / 2) —
a documented refinement; the native-priority anchors are preserved and cited.

`GroupCap` is the mutex/limit group: once a kind reaches its cap, a new
lower-priority member is dropped (native's serialized "discarded", soundtemplates.h:101-106).
Projectile ambients (cap 6) therefore can never flood the budget or mask an
explosion — only the **nearest 6** (loudest) are audible at once.

### `SoundManager` integration
- A `Voice` registry (`_managedVoices`) tracks every loud world sound — loop
  ambients (projectile/missile/warp/probe/station, weapon bursts) registered on
  Start and released on Stop, and one-shots (weapon fire, explosions, hits,
  collisions, missile launch, deploy, warnings/voices) registered at play.
- `ApplyVoiceBudget(dt)` each frame: prune finished one-shots → `Decide` →
  admitted loops play at `loudness × category × master`; dropped loops are
  muted (kept registered for reactivation, mirroring native keeping the virtual
  sound while releasing the buffer); dropped one-shots are stopped+released
  (native "steals" the buffer, ds3dengine.cpp:460-466).
- One-shots start immediately (so a transient is always heard) and are then
  subject to the per-frame budget — a loud young explosion (prio +4, age boost
  `max(0,2−age)`) wins its buffer over distant projectile ambients (prio −1.5).
- The own-ship cockpit bed (engine, cockpit hum, turn thruster, exterior,
  ambience, missile warning) is **exempt from the budget** (never culled), so
  the pilot's own cues are never starved — the port analogue of native's
  reserved `Max` fade buffers and the MutexSal voice protection.

### Port additions (flagged, native had no equivalent)
- `MasterVolume` (0..1) and per-category `SetCategoryVolume` knobs — native had
  no master slider, only per-template `GainSound`.
- A gentle **"battle ducking" soft limiter** (port addition): as the audible
  combat-voice count climbs past 12 toward the 16 ceiling, master gain eases
  down toward 0.8 so the mix never hard-clips. Native relied on the budget alone.

### Telemetry (debug overlay / log)
Opt-in via `ALLEGIANCE_AUDIO_TELEMETRY=1` (set in `Game1.Lifecycle.cs`).
`GetTelemetry()` / a periodic console line print **active voices by category +
drops per second**, e.g.:
`[monogame] audio budget: audible=12/16 (essential 0) drops/s=0.0 | world-ambient=2, hit-other=4, projectile-ambient=6`.

---

## 3. Unit tests (failing-first)

`tests/…/VoiceBudgetTests.cs` — 9 pure tests, no audio device. These pin
**net-new rules** (there was *no* cap before — the "before" state was an
unbounded one-shot-per-ship flood, evidenced by Domingo's ~67k projectile-
ambient log lines, so there was no prior behavior to regress; the tests pin the
guarantees the new code must uphold):
- global budget never exceeds the cap (and the cap, not a single group, binds under a multi-kind flood);
- priority stealing keeps own-ship warnings under a projectile-ambient flood;
- group limit drops the lowest-priority member (nearest-6 survives, faintest dropped);
- tight group cap cannot mask an explosion (projectile ambients ≤ 6 even as an explosion is admitted);
- **out-of-range sounds are never started**;
- undersubscribed mixes play everything;
- louder wins at equal priority; playing-continuity boost; young-beats-old.

**Result: 9/9.**

---

## 4. Build & suite

Whole-solution build: **0 warnings, 0 errors**.

2 consecutive runs (P471 discipline: build once, then the four assemblies run
sequentially so only one assembly's live network ticks at a time — executed
assembly-by-assembly; the mono-process wall cap precluded one shell command,
numbers identical):

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1760/1760 (3 skip) | 603/603 | 3954/3958 |
| 2 | 12/12 | 1760/1760 (3 skip) | 603/603 | 3956/3958 |

Every Core failure is a **ledgered live-wire load-flake** in
`docs/TEST_FLAKES.md` (unrelated to this client-only audio change): run1 =
`InboundFloodSafety…fmid_flood…rate_limited` (:55), `P590PodRescue…pickup_order`
(:435), `LiveRipcord…anchor_side_change` (:36/182), `GameOverPlayersWire…
Win_by_elimination` (:432); run2 = `LiveRipcord…first_activation` (:36),
`GameOverPlayersWire…Conquest_win…leak` (:30).

---

## 5. Live proof (local server, real windowed client, audio captured)

Method: a **temp worktree** (`/tmp/p614-proof` @ HEAD) running a local
`Allegiance.Server` on the 221 map `m2xspiderw.igc` (testUnlock + adminCheats),
MCP `spawn_scenario {size:N, target_sector:"Omega", arena:true}` combat bots,
and the real windowed MonoGame client (`--auto-goto Omega --auto-target enemy`,
chase cam, real presented-backbuffer PNGs at 1280×720). The client's audio was
captured from the default (null) sink monitor with ffmpeg (`-f pulse
-i auto_null.monitor`, 48 kHz stereo s16). A separate **before** worktree at
the pre-P614 commit `48e98b26` ran the identical 15v15 scenario for the A/B.

### 15v15 — before vs after (48.5 s captures, ~same battle scale)

| metric | BEFORE (no budget) | AFTER (budget) |
|---|---|---|
| projectile-ambient START log lines | 6,136 | 6,333 |
| …of those AUDIBLE at once | unbounded | **6** (nearest cap) |
| audible voices under budget | n/a | ≤ 16 |
| whole-clip RMS (dBFS) | **−3.02** | **−10.67** |
| whole-clip peak (dBFS) | +0.0003 | +0.0003 |
| crest factor (peak−RMS) | ≈ 3.0 dB | ≈ 10.7 dB |
| 2 s-window RMS drift | climbs −4.7 → **−1…−2** (constant max rattle) | holds **−10…−12** |

Plainly: **before**, the bed accumulated overlapping voices and drove toward
full scale continuously (RMS −1…−2 dB, ~3 dB of headroom — the "constant rattle
at the highest volume"). **After**, the bed sits at −10…−12 dB with the same
transients (fires/hits/explosions) peaking near 0 dBFS above it (~10.7 dB of
headroom), so individual events are audible over a controlled bed. The captured
clips (`p614_before_15v15_6s.wav` vs `p614_after_15v15_6s.wav`) make the
difference audible directly.

### Scale invariance — 60v60 and 100v100 (200 ships)

| battle | projectile amb START | audible | projectile-ambient audible | RMS (dBFS) | explosions | hits |
|---|---|---|---|---|---|---|
| 15v15 | 6,333 | ≤ 16 | 6 | −10.67 | 15 | 448 |
| 60v60 (120 ships) | 13,518 | ≤ 16 | 6 | −10.62 | 39 | 675 |
| 100v100 (**200 ships**) | 11,272 | 8–12/16 | 6 | −10.88 | 33 | 436 |

Doubling/tripling the fleet multiplies the projectile-ambient *event* count, yet
the audible mix stays flat at the same controlled level (RMS ≈ −10.6…−10.9 dB,
projectile ambients pinned at 6, total ≤ 16) — the budget is **scale invariant**.
(The largest attempt was 100-per-side = 200 ships; a literal 200-per-side
would be 400 bot connections, not run.)

### Explosions cut through
Crest factor rises from ~3 dB (before: compressed near-full-scale wall) to
~10.7 dB (after: transients ~10 dB above the bed). The 200-ship capture contains
33 explosion transients and 436 hit cues over a −11 dB bed — the explosions and
impacts are clearly separable from the gunfire bed. Real-window frames
(`p614_200ship_frame{1,2,3}.png`, `p614_15v15_frame.png`) confirm the client was
genuinely in the live battle (kill feed of destroyed `mcp-b*` ships).

Assets: `docs/reports/P614_assets/` — `p614_before_15v15_6s.wav`,
`p614_after_15v15_6s.wav`, `p614_200ship_6s.wav`, `metrics.json`,
`p614_{15v15,200ship}_telemetry.txt`, and the PNG frames.

---

## 6. Out of scope

New sound assets; music. (Untouched.)

## 7. Verification checklist

- [x] Whole-solution build 0 warnings / 0 errors.
- [x] 2 consecutive suite runs on the final code, reported as they happened
      (all failures ledgered load-flakes, client-only change).
- [x] 9/9 unit tests (failing-first: the rules are net-new; "before" = unbounded flood).
- [x] Native fidelity — every rule cited above to `soundengine/*` and `sounddef.mdl:*`;
      the one deviation (explosion tiering) is explicitly flagged as a P614 refinement.
- [x] Live A/B proof on a temp worktree with audio captured (dBFS peak/RMS,
      active-voice counts over time, committed clips + frames).
- [x] Committed on `master` (not pushed).
