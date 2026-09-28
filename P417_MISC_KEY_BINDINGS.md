# P417 — Small Leftover Key-Binding Batch (Target Self/Nothing/Command/Center + Screenshot)

Port of the remaining small targeting keys and the standalone screenshot command
into the MonoGame client, the leftovers explicitly deferred from P415's main
scope. Branch `master` (not pushed — Domingo pushes).

---

## TL;DR — built vs. skipped

| Native command | Ported? | Why |
|---|---|---|
| `TK_TargetSelf` | ✅ Built | Select your own ship (trivial, real backing state). |
| `TK_TargetNothing` | ✅ Built | Clear target + disengage autopilot (mirrors this port's existing clear path). |
| `TK_TargetCenter` | ✅ Built | Select the candidate nearest the reticle/crosshair (reuses the existing nearest-to-crosshair ranking). |
| `TK_TargetCommand` | ❌ Documented no-equivalent | The MonoGame client has NO command-console / command-target concept. |
| `TK_ScrnShot` | ✅ Built | Save the current frame to a timestamped PNG in `./screenshots/`. |

New bindings all registered in `KeyBindingTable.cs`'s help overlay.

---

## 1. What was built

### `TK_TargetSelf` ("Target Self") — key `Y`
Native: `SetTarget(trekClient.GetShip(), c_cidNone)` (WinTrek.cpp:10440-10443) —
select your own ship, no command issued. Port: `Game1.SelectSelfTarget()`
(`Game1.Targeting.cs`) reads the real `World.MyShipId`, resolves the own ship's
real `CharacterName` from the client world, and sets `_autoTarget` to
`AutopilotTarget(TargetKind.Ship, me, name)` — the exact T/G target-selection
state. Selecting self is a harmless no-op for autopilot steering (resolving your
own live position keeps you in place), matching native's "bring the selection
back to me" intent.

**Why `Y`:** the targeting family is already `T`/`G`/`U`/`Z`; `Y` is unclaimed in
flight (the ready-check screen's `Y` is a different state, and cross-screen key
reuse like `R` quick-chat / `F` cloak is the established pattern).

### `TK_TargetNothing` ("Target Nothing") — key `;`
Native: `SetTarget(NULL, c_cidNone)` (WinTrek.cpp:10446-10449) — clear the
target. Port: `Game1.ClearTarget()` reuses `DisengageAutopilot("target nothing")`,
the exact clear-target-and-autopilot path the G-key toggle-off and manual-input
disengage already use (Game1.FlightInput.cs / Game1.Targeting.cs). Clears both
`_autoTarget` and `_autoPilotEngaged` in one move, mirroring how target-clear
already works in this port.

**Why `;`:** punctuation unclaimed by every binding (grep-verified; the keys in
use are `,`/`.` starmap nav, `[`/`]` weapon cycle, `` ` `` group-fire, `+`/`-`
music).

### `TK_TargetCenter` ("Target Center") — key `'`
Native: `FindTarget(c_ttFront | c_ttAllTypes | c_ttAllSides, …, orientation)`
(WinTrek.cpp:10127-10151) — the model nearest the reticle/forward vector.
Port: `Game1.SelectCentermostTarget()` reuses the port's **existing**
nearest-to-crosshair ranking — `PickNearestTarget` → `CollectTargetCandidates` →
`SelectCandidates`, whose ordering is the screen-space squared distance of each
candidate's projected position from the crosshair centre (`sx²+sy²`,
Game1.Targeting.cs:289). This is exactly the "closest to view center" concept the
brief asked to look for: no new ranking was needed, only a key that drives the
existing one. The priority-tier rule (ships over warps over stations over
asteroids) still applies, so TargetCenter surfaces the meaningful object, not a
dead-centre rock.

**Why `'`:** punctuation unclaimed by every binding, consistent with `;`.

### `TK_TargetCommand` ("Target Command") — documented no-equivalent, NOT built
Native: `SetTarget(GetCommandTarget(c_cmdAccepted), GetCommandID(c_cmdAccepted))`
(WinTrek.cpp:10408-10412) — target whatever the current **command-console**
target is. Investigation (per the brief's "investigate first" instruction):
the MonoGame client has **no command-console / command-target concept at all**.
Verified by grep across `Allegiance.ClientCore` and the MonoGame client — there is
no `GetCommandTarget`, no command-id state, no strategic/command view; the only
`c_cmdAccepted` references are in the P366 leader-bot `ORDER_CHANGE` wire (an
unrelated bot-order feature). Since "add a key to an existing mechanic" requires
an existing mechanic to attach to, and the brief forbids building new UI
infrastructure to support it, this is documented and skipped. No binding, no help
entry (there is no behaviour to advertise).

### `TK_ScrnShot` ("Take a screen shot") — key `F11`
Native: `TakeScreenShot()` grabs the screen and saves a bitmap named by local
time (WinTrek.cpp:4038-4105; the shipped Steam-overlay stub notwithstanding, the
intent is a standalone frame capture). Port: a purely client-side render-target
capture with **no server/wire dependency**:

- New `Capture/ScreenShot.cs`: `ScreenShot.CaptureBackbuffer(GraphicsDevice)`
  reads the current backbuffer into a `Color[]`, and writes it to
  `./screenshots/shot_<yyyy-MM-dd_HH-mm-ss_fff>.png` under the working dir. The
  filename is timestamped with milliseconds so rapid shots don't collide (native
  names by local time too; this is the collision-resistant version). `BuildPath`
  is a pure, unit-tested path builder.
- `CapturePngSaver.Encode` (the shared pure RGBA→PNG encoder already proven by
  the P306 video-capture path) is now `public` so the two capture features share
  one encoder.
- The key (F11, edge-triggered, composing-gated in Game1.FlightInput.cs) latches
  `_screenshotRequested`; the Draw-end hook in `Game1.Rendering.cs` (after every
  draw pass, before Present — the moment the backbuffer holds the real frame)
  calls `CaptureBackbuffer` and logs the path.

**Why F11:** unclaimed (only F4 is used, for the Alt+F4 quit) and the
conventional screenshot key; avoids OS-intercept-prone keys (the F1 lesson from
P258).

---

## 2. Verification (real proof)

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors.**

### Live proof — screenshot (required)
A real MiniGame harness (`/tmp/p417shot`) boots a real MonoGame `GraphicsDevice`
(display :0 available), renders a REAL frame to the backbuffer (dark clear +
red top-left quadrant + lime-green bottom-right quadrant), then calls the exact
production method the F11 key invokes — `ScreenShot.CaptureBackbuffer` at the
Draw-end — and verified:
- a real PNG was written to `./screenshots/shot_….png`;
- PNG signature valid, **320×200 = the real backbuffer dimensions**;
- decoded pixels confirm the REAL rendered content: TL=(255,0,0) red,
  BR=(50,205,50) lime, 3 distinct colours (bg+red+green) — i.e. not a fixed
  placeholder, the actual frame. (The two-frames-encode-differently unit test
  independently pins that the bytes vary with content.)

Evidence: `/tmp/p417shot/screenshots/shot_2026-09-27_22-43-06_432.png` and the
decoded-pixel check above.

### Live proof — targeting keys (required; no input-injection tool on this host)
`MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship`
boots a REAL in-process server (m2xspiderw, 2 sides), seats a real peer in the
Ganymede sector, and places real enemy (side 1) + friendly (side 0) NPC ships in
that same sector, delivered to the peer's `ClientWorld` over the real wire. It
proves the backing concepts the keys drive against real data:
- **TargetSelf**: the peer's own ship (`World.MyShipId`) resolves to a live,
  real-identity `ShipEntity` in its own world — the exact datum `SelectSelfTarget`
  reads to select "me".
- **TargetCenter**: the production `GatherSectorCandidates` + `SelectCandidates`
  (the reticle-ranked selection) over the real gathered candidates yields a real
  in-sector ship (the enemy or friendly drone) — never empty, real id.
- **TargetNothing**: reuses the already-live-proven DisengageAutopilot clear
  path (P212 / G-key); its only new code is the call-through, pinned by this
  context + unit coverage.

The keys' selection/capture paths are byte-identical to the code these tests
drive (the established no-input-injection convention throughout P413-P416).

### Unit tests (7 new, all green; must-fail proven)
`MiscKeyBindingTests`:
- **TargetCenter reticle-vs-distance** (the brief's central discriminator): a
  physically-nearer but off-reticle candidate loses to an on-reticle one;
  out-of-frustum candidates are ignored regardless of raw distance; priority
  tiers still apply (ship beats a dead-centre asteroid).
  **Must-fail proven**: removing the `SelectCandidates` `DistSq` sort turned
  `Center_picks_on_reticle_candidate_over_a_physically_closer_off_reticle_one`
  red (`[1,2]` instead of `[2,1]`), then restored to green.
- **ScreenShot**: path is timestamped under `screenshots/`; encoder produces a
  structurally-valid PNG of the requested size (signature + IHDR dims + IEND);
  two frames encode to different bytes.

### Full test suite (≥2×)
- `Allegiance.ClientCore.MonoGame.Tests`: **810/810 green twice** (incl. the 7
  new + the HelpOverlay keybinding-table layout/ASCII tests after the 5 new
  entries; group count stays 8 so the layout contract is unchanged).
- `Allegiance.Core.Tests`: 3497 total; run 1 → 5 failures, run 2 → 4 failures,
  **different rotating live-wire timing tests each time** (LiveWinCondition,
  LiveTurretGunnerBinding, LiveTeamLobby, LiveMoney, LiveTreasurePickup,
  ConquestTerritoryLive, BotFlight). None in code touched here — Core.Tests does
  not reference the changed MonoGame assemblies.
- `Allegiance.ClientCore.Tests`: 227 total; only the two documented persistent
  live-wire flakes (`ShipDeleteGoneLiveTests.DockOut_…`,
  `JoinFlowLiveTests.EntityTable_…`) which are pre-existing/environmental
  (P415/P416-recorded), plus a third that rotated green on run 2. ClientCore.Tests
  does not reference the changed assemblies.

---

## 3. Scope boundaries honored

- Not touched: P413 (cloak), P414 (ripcord), P415 (friend/foe family), P416
  (turret seats), `TK_ConMode*`/`TK_ToggleGrid`/`TK_CycleRadar` (confirmed
  no-equivalent this session), P368-P416.
- No server / `Allegiance.Core` / `Allegiance.ClientCore` change was made — every
  change is in the MonoGame client.

Files changed: `Capture/CapturePngSaver.cs` (Encode → public),
`Capture/ScreenShot.cs` (new), `Game1.cs` (key latches + screenshot latch),
`Game1.FlightInput.cs` (Y/;/'/F11 keys), `Game1.Targeting.cs` (TargetSelf/Nothing/
Center), `Game1.Rendering.cs` (Draw-end screenshot hook), `Rendering/KeyBindingTable.cs`
(Y/;/'/F11 help entries), `MiscKeyBindingTests.cs` (new), `MiscKeyBindingLiveTests.cs`
(new), this report.
Committed on `master` (not pushed).
