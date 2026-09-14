# P216 — Mission-start countdown and game-over announcer voice lines

## Summary

P216 extends the P213 real-audio `SoundManager`
(`src/Allegiance.ClientCore.MonoGame/Audio/`) with two more real, confirmed-present
announcer cues, reusing the exact same MonoGame `SoundEffect`-stack infrastructure
and asset-resolution approach P213 landed — no second audio subsystem:

1. **Mission-start countdown announcer.** During the 15-second pre-launch
   briefing, a `countdownstart` callout voices the opening, then a per-second
   voice (`countdown10`…`countdown1`) speaks each of the last ten seconds.
2. **Game-over announcer.** `vo_ma_gameover` plays exactly once the moment the
   client observes the real `Stage → Over` transition.

Both are short, one-shot, non-looping — deliberately played via simple
`SoundEffect.Play()` one-shots (the same pattern P213 already used for the
autopilot engage/disengage chimes), not the looping `SoundEffectInstance`
machinery reserved for the engine/cockpit/ambience loops.

## Real assets — confirmed present, cited directly

From `/home/domingo/Development/Artwork` (the port's `ALLEGIANCE_ARTWORK_PATH`,
the same tree P213 read):

- `countdownstart.ogg` — the countdown-opening callout (P216's decision: voice
  the first moments rather than leave them silent; see below).
- `countdown10.ogg` … `countdown1.ogg` — the per-second tail, one file per
  second 10→1.
- `vo_ma_gameover.ogg` — the game-over callout.

All copied into `Content/Sounds/` and registered in `Content/Content.mgcb`
(as `OggImporter` → `SoundEffectProcessor`, exactly like P213's cues).

## The real native trigger logic — behavior ported, not the UI framework

Native `CheckCountdownSound()` (`src/WinTrek/WinTrek.cpp:11133-11211`) plays a
callout whenever the integer seconds-remaining changes (`m_nLastCountdown`
change-detection, `:11150`), dispatching a per-value sound: coarse checkpoints at
10min / 5min / 1min / 30s, then a per-second voice (`countdown10`…`countdown1`)
over the last ten seconds (`:11156-11210`). The checkpoints are all ≥30s, so
none can occur inside this port's 15-second briefing countdown — only the
sub-60-second tail (10→1) is reachable, exactly as the task directed.

The countdown length is `Allegiance.Server`'s real `BriefingCountdownSeconds =
15.0f` (`Server.cs:129`, itself a port of native `c_fMissionBriefingCountdown =
15.0f`, `src/Igc/igc.h:1005`). The client supplies this length locally
(`CountdownSound.BriefingCountdownSeconds`) — the `SET_START_TIME(14)` wire
carries only the target start time (server-clock ms), not the length, and
native's client likewise uses its own compile-time constant.

Game-over: native plays the announcer on the mission's end; the port fires
`vo_ma_gameover` on the real `Stage → Over` transition (see below).

## Why the client anchors the countdown on SET_START_TIME(14), not Mission.Stage

The task asked to reuse the existing per-frame `world.Mission`/stage read.
Investigation (verified live) shows the client's `Mission.Stage` **cannot**
distinguish the brief `STAGE_STARTING` window: the join burst pins it to
`StageStarted` unconditionally (`Server.cs:2138`, `MISSION_STAGE(37)` with
`StageStarted`), and the server never sends `STAGE_STARTING` over the wire — the
countdown announcement it broadcasts is `SET_START_TIME(14)`
(`Server.cs:4078-4081`, `StartCountdown`). That is the exact message native's
`CheckCountdownSound` anchors on (`timeStart`, `WinTrek.cpp:11144`). So the port
treats the **receipt** of `SET_START_TIME(14)` as the countdown's t=0 — the real
"countdown began" signal — while still reusing the same `world.Mission.Stage`
field for the game-over "ended" signal (below).

Because `TimeStart` is a server-clock millisecond value sharing no epoch with
the client, the client records the local monotonic receipt instant
(`Environment.TickCount64`) once on the first `SET_START_TIME`
(`ClientWorld.CountdownStartLocalMs`), and `Game1` measures elapsed locally.
Each frame it computes `secondsRemaining = ⌈15 − elapsed⌉` and, mirroring
native's `m_nLastCountdown` change-detection (`:11150`), plays each second's
tick exactly once (`countdown10` at ~5s in = 10s remaining, down to `countdown1`
at ~14s in = 1s remaining), plus a single `countdownstart` at the opening.

## Implementation

- `Audio/CountdownSound.cs` (new, pure): `BriefingCountdownSeconds = 15.0f` and
  `TickAssetFor(int secondsRemaining) → string?` mapping `1..10 → countdown{N}`,
  `null` otherwise. **No MonoGame types** (same discipline as
  `Audio/EngineSoundCurve.cs` / `Flight/FlightInput.cs`), so it is
  unit-testable without a device. This is the one genuinely non-trivial pure
  piece (the range→asset-name mapping over the audible tail), extracted for a
  test.
- `Audio/SoundManager.cs` (extended): loads `countdownstart`, `countdown10`…
  `countdown1`, and `vo_ma_gameover`; adds one-shot
  `PlayCountdownStart()` / `PlayCountdownSecond(int)` (dict lookup, silent
  no-op out of range) / `PlayGameOver()`, all guarded by the P213
  `Safe(...)`/`IsLoaded` graceful-degradation pattern; disposed in `Dispose()`.
- `ClientCore/State/ClientWorld.cs` (extended): two new `Apply` branches —
  `Apply(SetStartTime)` records `CountdownStartLocalMs` (set once), and
  `Apply(GameOver)` drives the **existing** `MissionInfo.Stage` to
  `MissionStageMessage.StageOver` (snapshot-preserving, mint-if-absent like
  `Apply(MissionStageMessage)`).
- `ClientCore/Session/ConnectionSession.cs` (extended): routes
  `SetStartTime.Fmid` and `GameOver.Fmid` into `_world.Apply`.
- `Game1.cs` (extended): after the existing auto-ready/auto-undock block,
  fires the countdown cues off `world.CountdownStartLocalMs` (per-second
  change-detection, one-shot `countdownstart` gate) and the game-over cue off
  `world.Mission.Stage == Over` (one-shot `_gameOverCuePlayed` gate) — the same
  `world.Mission`/stage read the auto-undock uses.

## Why the client processes GAME_OVER(62) at all

The client previously never observed the mission ending: `GameOver.Fmid` (62)
was absent from `ConnectionSession`'s receive switch, so `world.Mission.Stage`
could only ever reach `StageStarted`. The server's real end-of-mission broadcast
is `GAME_OVER(62)` (`Server.cs:1384-1399`, `DeclareGameOver` → whole-mission
`GAME_OVER`). Processing it to drive the existing `MissionInfo.Stage` field to
`Over` is precisely "find the same real field for 'mission has ended'" — the
single source of truth the auto-undock already reads, now able to reflect the
end state too. `Game1` then fires `vo_ma_gameover` exactly once on that real
transition (no every-frame refire).

## Out-of-scope adherence

- Quick-chat voice-macro (`quickchat.mdl`) — **not touched** (P217's task).
- Mining / combat / cargo / other announcer categories — not added.
- `Flight/`, `Rendering/`, P214 lobby/chat code — read state, modified none.

## Tests (`tests/Allegiance.ClientCore.MonoGame.Tests/`)

- `CountdownSoundTests.cs` (new): `TickAssetFor` maps every second 1..10 to its
  voice; returns null outside (0, negative, >10); the audible tail is exactly
  10→1; `BriefingCountdownSeconds` matches the server's real 15.0f.
- `ClientWorldAnnouncerTests.cs` (new): `Apply(GameOver)` drives an existing
  mission's stage to `Over` (the same field the auto-undock reads), preserves
  the static-core fields, mints an Over snapshot when no mission preceded, and
  `Apply(SetStartTime)` records the countdown anchor exactly once (a second
  `SET_START_TIME` does not re-anchor).
- **Must-fail-if-removed** verified: temporarily neutering
  `Apply(GameOver)`/`Apply(SetStartTime)` turned all 4 `ClientWorldAnnouncerTests`
  red; restored to green.

## Verification

- `dotnet build AllegianceCore.slnx` → **0 warnings / 0 errors**, whole
  solution. The content builder imported all new `.ogg` files cleanly.
- `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests` → **110 pass, 0 fail,
  clean across 3 consecutive runs** (existing 95 + 15 new).
- **Live check** against a fresh `Allegiance.Server` (real display `:0` +
  PipeWire/OpenAL present on this host), via a **temporary diagnostic log**
  (removed before commit) that probed each one-shot cue by playing a sibling
  `SoundEffectInstance` of the same `SoundEffect` and logging `SoundState`:
  - *Countdown* (temp `teams:1` config, single peer → `Started`): the client
    logged `countdownstart -> SoundState.Playing` at t≈0, then `countdown10`
    exactly ~5s in (=10s remaining of 15s), then `countdown9`…`countdown1` each
    exactly 1s apart — all `SoundState.Playing`, no exceptions; the mission then
    reached `Started` (`countdown elapsed -> STAGE_STARTED`).
  - *Game-over* (temp `teams:2` config, two GUI clients on opposite sides →
    `Started`, then killing client B): server `[conn] peer 2 left` →
    `CheckForVictoryByInactiveSides` → `[gameover] mission #0 -> Stage Over,
    iSideWinner=0` → client A logged `vo_ma_gameover -> SoundState.Playing` at
    that exact transition. 12 cues total on client A (11 countdown + 1
    game-over), all `Playing`, no exceptions.
  - **Honest limitation:** this host has real ALSA/PipeWire devices but no
    speakers/audio-capture tool, so I could not *hear* the output. I verified
    `SoundState.Playing` transitions and absence of exceptions via the temporary
    diagnostic — not that the audio "sounds good". Domingo will do the real
    listening test.
- Wider `Allegiance.Core.Tests` (server-side) is unaffected by this stage — all
  changes are client-side. The only red full-suite results are the three
  documented machine-state live-network flakes (LiveTurretGunnerBindingTests,
  LiveSuicideShipDeleteTests.Combat_death…, LiveCombatWireTests.Real_Bot_class…),
  each confirmed failing in isolation and unrelated to any P216 symbol.

## Assets used (all from `/home/domingo/Development/Artwork`)

- `countdownstart.ogg` — countdown-opening callout (P216 addition; native opens
  with the unreachable 10min checkpoint, so the port voices the opening).
- `countdown10.ogg` … `countdown1.ogg` — per-second tail, `WinTrek.cpp:11172-11210`.
- `vo_ma_gameover.ogg` — game-over callout, fired on `Stage → Over`.

Copied into `Content/Sounds/` and registered in `Content/Content.mgcb`.
