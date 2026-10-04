# P523 — self-target Y conflict + shaky target bracket/label

Date: 2026-10-03. Landed on `master` (not pushed).

## Summary

Domingo's live playtest (`~/monogame-client-run-selftarget.log`) showed
`[monogame] target: self (domingo1 #8107)` right after connect, with the
targeting HUD label shaking badly in chase view. Two distinct bugs:

1. **Bug 1 (self-target leak):** the `Y` key was bound to BOTH the ready-check
   "toggle ready" action (P282) and the in-flight "target self" command
   (P417, `TK_TargetSelf`). Pressing `Y` to ready up left the press live as
   the mission started, so flight selected the player's own ship. **Native
   readies with a UI button, so it never had this conflict — and, checked
   against native's default input map, native does not bind `TK_TargetSelf` to
   ANY key at all.**
2. **Bug 2 (shaky bracket/label):** the own ship is drawn from P508's local
   sim every frame, and remote ships from P448b's dead reckoner, but the target
   bracket/label (and the missile-lock box) were anchored to each entity's raw
   20 Hz `ServerShipUpdate` pose. The label jittered around the smoothly-drawn
   hull.

---

## Bug 1 — the native-faithful fix

### Finding: native leaves `TK_TargetSelf` unbound

`Artwork/defaultinputmap.mdl` is a plain-text list of `(VK, modifier, TK id)`
triples in `keyCommandMap` (decoded by the P445 method; `TK_*` values from
`Igc/igc.h:226-451`):

- `TK_TargetSelf = 100` (`igc.h:323`)
- `TK_TargetNothing = 103` (`igc.h:326`)
- `TK_TargetCenter = 90` (`igc.h:313`)

Scanning the whole `keyCommandMap` (109 entries) and the
`mouseButtonCommandMap` / `buttonCommandMap`: **no entry binds TK 100 or
TK 103**. TK 90 (`TargetCenter`) is bound to **mouse button 1** (`(1, 90)` in
`mouseButtonCommandMap`), not to a key. So native's default input map has **no
keyboard key for TargetSelf at all** — the P417 choice of `Y` was a port
deviation, and that deviation is what created the conflict with the P282 ready
key.

### Changes

1. **Removed the `Y → target self` in-flight binding** (`Game1.FlightInput.cs`,
   the P417 dispatch block): `Y` is now exclusively the ready-check key, and
   flight has no target-self key at all — matching native's default input map.
   The `SelectSelfTarget` command is retained for completeness (it remains the
   faithful port of `WinTrek.cpp:10440-10443`), but no key calls it. `;` stays
   `TargetNothing`; `'` stays `TargetCenter` (this port's own free-key choices,
   documented as such).
2. **KeyBindingTable**: removed the `Y → "Target self [TK_TargetSelf]"` row;
   kept `;` / `'`.
3. **Edge-gated the pre-flight→flight transition** (`PrimeInFlightKeyLatches`,
   called at the ReadyCheck→Flying handoff beside P445's throttle reset). The
   ready/side-select screens use their own edge latches, but the IN-FLIGHT edge
   latches are only written once `UpdateFlying` runs — so on the first flight
   frame they are all `false`, and a key physically held across the transition
   (Enter/R confirming a side, Y readying, …) would be read as a fresh press
   and fire its in-flight edge action (open chat, open quick-chat, select
   self). Every in-flight edge latch is now primed from the current key state
   at the handoff, marking a held key as "already pressed".

   This is the second, independent layer: even if a key were rebound to a
   self-target action later, it could no longer leak across the transition.

### Bugs prevented / symptoms fixed

- The reported `target: self` no longer happens: there is no flight binding of
  `Y` to target-self, so readying can never select the player's own ship.

---

## Bug 2 — ship HUD anchors use the render pose

### Root cause

The renderer draws each hull from a **per-frame render pose** (`Game1._poses`,
built each flight frame under the gate lock):

- **self** → P508's local simulation pose (`_poses[me]`),
- **remotes** → P448b's dead-reckoned pose (`RemoteShipPose` →
  `world.TryGetRenderPose`).

But the HUD ship anchors read a **third, raw path**:

- `ComputeTargetSelectionBracket` / `ComputeTargetBearing` →
  `ResolveTargetPosition` → `world.Ships[id].Latest.Position` (raw 20 Hz
  `ServerShipUpdate`);
- `ComputeLockBracket` → `world.MissileLock.TargetPosition` (server's lock
  update, also 20 Hz).

So the bracket/label/box jumped in coarse 20 Hz steps while the hull moved
smoothly every frame — the "shaky, ugly" look.

### Fix: one shared render-pose anchor

Added `Game1.ResolveShipAnchor` — the single shared "render pose" accessor for
ship-id HUD anchors. It returns the pose the renderer drew THIS frame
(`_poses`, own local sim for self / dead-reckoned for remotes), falling back to
the raw `ServerShipUpdate` pose only when the ship was not rendered this frame
(no drawn hull to glue to). Wired into:

- `ResolveTargetPosition` (Ship case) — the target-selection bracket, its
  label, and the bearing arrow all anchor to the render pose (and the autopilot
  now steers at the same predicted pose the renderer drew, which is more
  faithful to native's client-side interpolated ships);
- `ComputeLockBracket` — the missile-lock box anchors to the render pose of
  `MissileLock.TargetId`.

There is now a single anchor path for every ship-anchored HUD element, not
three.

### Scope item 3 — does native show a self bracket/label?

Native's `TK_TargetSelf` is unbound by default (above), so self-targeting never
occurs through the input map in native's default config — there is no native
self-bracket behaviour to reproduce. Since this port also no longer binds
target-self, the "my own name on the targeting HUD" symptom is gone at the
source. If a self-target were somehow selected, the bracket now anchors to the
own-ship local-sim pose (consistent with the drawn hull).

---

## Tests (failing-first, must-fail proven)

New `tests/Allegiance.ClientCore.MonoGame.Tests/P523SelfTargetAndBracketJitterTests.cs`
(5 tests, pure/no graphics device, `InternalsVisibleTo`):

1. `KeyBindingTable_DoesNotBindY_ToTargetSelf_KeepsSemicolonTargetNothing` —
   the TARGETING group has no `Y` entry; `;` → TargetNothing and `'` →
   TargetCenter survive.
   **Must-fail proven:** re-adding the `Y → Target self` row to the table makes
   this fail (`Assert.DoesNotContain`), then passes again on revert.
2. `SharedPreFlightKeys_AreEdgePrimed_WhenHeldAcrossTransition` —
   `Game1.ComputeSharedEdgePrimes`: Enter/R held at the transition are primed
   (marked already-pressed) so their in-flight edges (chat / quick-chat) can't
   fire on the first flight frame; a released key is not primed.
   **Must-fail proven:** forcing the prime to always return `(false,false)`
   makes this fail, then passes again on revert.
3. `ResolveShipAnchor_UsesRenderPose_ForSelfAndRemote` — the bracket anchor
   equals the render pose for BOTH self (local-sim pose over the raw server
   pose) and a remote (dead-reckoned pose over the raw pose).
   **Must-fail proven:** reversing the precedence to raw-first makes this fail
   (`Expected 10 Actual 720`), then passes again on revert.
4. `ResolveShipAnchor_FallsBackToRawPose_WhenNotRendered` — a ship absent from
   the render-pose table falls back to its raw last-known pose.
5. `RenderPoseAnchor_JitterDropsVersusRawPoseAnchor_InScriptedRun` — scripted
   100-frame run, ship advancing 10 u/frame; raw 20 Hz pose steps every 5th
   frame while the render pose is smooth. Jitter metric = sum of squared
   frame-to-frame anchor deltas (measured from the real test):
   - render-pose anchor: **9,900** (10² per frame);
   - raw-pose anchor: **47,500** (50² every 5th frame).
   **4.8× reduction.** Render anchor jitter is clearly below raw (asserted `<`
   and `< raw/3`). **Must-fail proven:** raw-first precedence made both these
   fail (render=raw = 47,500), then pass again on revert.

---

## Verification (measured)

### Build
`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`:
**0 warnings, 0 errors**.

### Full suite — `scripts/run-tests.sh` (P471 runner), 3 consecutive runs

Totals per run (Lobby 12 / MonoGame 1202 / ClientCore 433 / Core 3708 = **5355**):

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | **1202/1202** (clean) | 432/433 | 3704/3708 | CC: `SessionLossLiveTests.EstablishedSession_ServerKilled…`; Core: `AutoReloadOnEmpty…RELOAD129`, `LiveStationRescueTests.Lifepod_reaching…`, `GameOverPlayersWireTests.Conquest_win_in_one_mission…`, `AddPartTests.Part_pickup_destroys…` |
| 2 | 12/12 | **1202/1202** (clean) | 432/433 | 3704/3708 | CC: `CrossSideCombatVisibilityLiveTests.CrossSide_sector…warp_return`; Core: `StationUnderAttackAlertLiveTests.Second_hit_within…`, `LiveWinConditionTests.Board_refused_once_mission_over`, `LiveCountdownStartPingTests.Multi_mission_countdown…`, `CargoBayReloadTests.SwapPart_out_of_range…` |
| 3 | 12/12 | **1202/1202** (clean) | 432/433 | 3707/3708 | CC: `FullSessionKillIntegrationTests.FullSession_ThreePeers…RealCrossSideKill`; Core: `LiveCountdownStartPingTests.Multi_mission_countdown…` |

Every listed failure is a **pre-existing live-wire load-flake** documented in
`docs/TEST_FLAKES.md` (the fixed-sleep live-receipt / full-suite-load timing
class): `SessionLossLive` (P320 area), `AutoReloadOnEmpty` (line 52),
`LiveStationRescue` (line 156/364), `GameOverPlayers` (line 31/267),
`AddPart` (line 247), `StationUnderAttackAlert` (line 375),
`LiveWinCondition.Board_refused` (line 34), `LiveCountdownStartPing` (line
37/283), `CargoBayReload` (line 53-54), `FullSessionKill` (line 33/148), and
`CrossSideCombatVisibility` (same class). None reference any P523 symbol and
all live in `Allegiance.Core` / `Allegiance.ClientCore` — the P523 change is
confined to `Allegiance.ClientCore.Game` (MonoGame game) + the MonoGame test
project. The changed area (`Allegiance.ClientCore.MonoGame.Tests`) passed
**1202/1202 in all three runs**, including the 5 new P523 tests.

Isolated re-runs of the flaked Core/ClientCore tests (after the 3 runs) were
green — `LiveCountdownStartPing.Multi_mission…` 1/1, `AutoReloadOnEmpty` 4/4,
`AddPartTests` 6/6, `StationUnderAttackAlert` 3/3, `Board_refused` 1/1,
`CargoBayReload` 9/9, `GameOverPlayers…conquest_leak` 1/1,
`LiveStationRescue…Lifepod_reaching` 1/1, `SessionLossLive` 1/1,
`CrossSideCombatVisibility` 3/3 — confirming the environmental timing class.
`FullSessionKillIntegration` (a heavy 3-peer full-session test, documented in
`docs/TEST_FLAKES.md` line 33/148 as a live-wire load-flake) also flapped in
isolation on this loaded box; it has **zero reference to any P523 symbol and
`Allegiance.ClientCore.Tests` does not even reference the `Allegiance.ClientCore.Game`
project** (it references only ClientCore/Core/Server/allegiance-mcp), so it is
provably untouched by this change.

### Changed-area tests in isolation
`P523SelfTargetAndBracketJitterTests`: 5/5 green.

---

## Files changed

- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — removed the `Y →
  target-self` dispatch; added `PrimeInFlightKeyLatches` +
  `ComputeSharedEdgePrimes`; wired the prime at the ReadyCheck→Flying handoff;
  `ResolveTargetPosition` (Ship) now uses `ResolveShipAnchor`.
- `src/Allegiance.ClientCore.Game/Game1.Targeting.cs` — added
  `ResolveShipAnchor`; updated the P417 comment block with the native
  unbound-key finding.
- `src/Allegiance.ClientCore.Game/Game1.Camera.cs` — `ComputeLockBracket` now
  uses `ResolveShipAnchor`.
- `src/Allegiance.ClientCore.Game/Game1.cs` — removed the now-unused
  `_yTargetSelfKeyWasDown` field.
- `src/Allegiance.ClientCore.Game/Rendering/KeyBindingTable.cs` — removed the
  `Y → Target self` row.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P523SelfTargetAndBracketJitterTests.cs` — 5 new tests.

## Out of scope (unchanged, per brief)

- Death latch (P521), pregame flight (P522), lock-on mechanics (P230/P428),
  the lead-indicator reticle (P266), radar blips.

Committed on `master` (not pushed).
