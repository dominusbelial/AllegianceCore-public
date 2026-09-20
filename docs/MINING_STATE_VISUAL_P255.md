# P255 — Client-visible mining state + a real orbit/mining visual cue

Scope: the **local player's own ship** only. `Sector.ResolveMining` / the
mining economy were already real (P55/P156/P254) and were **not touched**.
The gap this stage closes is wire plumbing: `Ship.IsMining`/`MiningAsteroid`
never reached the client.

---

## 1. What native actually does for observer visibility (cited)

Native broadcasts mining state to **every** observer via a **state bit** in the
ship's regular status broadcast — not a dedicated message:

- `miningMaskIGC = 2048 * coastButtonIGC` (`src/Igc/igc.h:822`, coastButtonIGC=4
  at `igc.h:809`) = **0x2000**, a LOW-WORD bit inside `buttonsMaskIGC`
  (`igc.h:824`), so it rides the compact `stateM` transmitted on the ship-status
  broadcasts (`ServerSingleShipUpdate`, `SingleShipUpdate` 78, and the light /
  heavy ships-update family).
- The mining state machine keeps this bit set while drilling and drives the
  ship to face the rock:
  `SetStateBits(miningMaskIGC | wantsToMineMaskIGC, miningMaskIGC)`
  (`shipIGC.cpp:2185`), and while mining:
  ```cpp
  // Keep facing the target, and add a little roll for effect
  Vector dp = pasteroid->GetPosition() - GetPosition();
  turnToFace(dp, dT, this, &m_controls, 1.0f);
  m_controls.jsValues[c_axisRoll] = 1.0f;          // shipIGC.cpp:2251-2253
  ```
  The ship's actual SIM orientation is turned toward the rock, and both the bit
  and the orientation reach observers through the normal ship-status broadcast.

**Consequence for scope.** Observers in native see mining because the *bit*
broadcasts; they do NOT need to be told *which* rock (they infer it locally from
the sim). This renderer-only MonoGame client has no sim/core to do that
inference from, so scoping the wire to own-ship-only here is a **port staging
choice, not a native-fidelity constraint** — native exposes mining to everyone
via the bit. That is exactly why the faithful bit (below) is set on the own
ship's existing update, ready for the observer follow-up.

---

## 2. The wire mechanism chosen, and why

Two complementary signals, both driven from the per-tick "mining this tick"
state `Ship.MiningAsteroid` (P254) — which is set by `ResolveMining` to the rock
being drilled and reset to `null` the moment no rock qualifies (moved / left
proximity / bag full). It is deliberately NOT the latched transition flag
`Ship.IsMining` (which never auto-clears; see Ship.cs:1637-1646) — the client's
cue must clear when mining stops, so the per-tick signal is the honest one.

**(a) The faithful native bit — no new message.** Added
`ShipControlState.MiningMask = 8192` (0x2000, igc.h:822) and stamped it into the
own ship's `ServerShipUpdate.stateM` inside the existing per-tick
`SingleShipUpdate(78)` broadcast (`Server.BroadcastShipStates`), via a new
optional `extraStateBits` arg on `ServerShipUpdate.FromModel`. The mining-start
transition forces that broadcast (the dirty gate gained
`miningNow != LastBroadcastMining`), so a parked miner's start/stop is always
delivered. This is the minimal "bit on an existing per-tick own-ship update this
port already sends" the brief preferred, and it is the native mechanism.

**(b) A small owner-only target message — genuine reason a new message is
cleaner.** The `ServerShipUpdate` payload is fully packed (47 bytes) — the rock
id fits nowhere. Native never transmits the rock id (its client infers it), but
this renderer-only client cannot. So, mirroring the exact P230 (`MissileLockUpdate`
204) / P234 (`MissileUpdate` 205) owner-only-extension precedent, added
`OwnShipMiningUpdate` (**fmid 206**, 8 B: `clusterID` + `asteroidID`, NA/NA when
not mining), streamed per-tick to the owner only (`Server.BroadcastOwnShipMining`).
The client's orientation cue keys off this real wire data.

---

## 3. The visual cue built (client-side)

While the local ship is mining, `Game1.Update` overrides its **rendered facing**
to aim at the rock (`MiningOrientation.Facing`, the pure shared helper) — the
client-side mirror of native's `turnToFace` — plus a gentle continuous roll "for
effect" (native sets the roll axis to 1.0; we use a modest 0.35 rad/s). The
override applies in **both** camera modes through the same pose the camera and
hull draw from: in **chase** you see the hull turned toward the rock; in
**cockpit** the camera pans to face the rock (you can't see your own hull there
anyway). A simple **HUD "MINING" tag** (top-centre, amber) is drawn by the real
`HudRenderer` via a new `HudFrame.Mining`. When mining stops (or the rock is
gone) the server-authoritative orientation and no-tag state are restored —
verified to clear.

---

## 4. Live proof (real, measured — `.verify_p255/`)

Throwaway MiniGame (`/tmp/p255verify`) with a **real** in-process server + real
ConnectionSession; synthetic He3 rock injected into the zero-asteroid demo map;
the peer's ship teleported to start/stop real mining. Rendered with the REAL
production renderers, offscreen PNGs.

- Facing metric `align` = dot(forward, towardRock): **A departed −1.0 (away) →
  B mining +1.0 (nose on rock) → C departed −1.0 (cleared)**.
- `MiningAsteroidId` (REAL wire, 206): −1 → **6100** (the rock) → −1; clears.
- Pixel metric: amber MINING-tag pixels **0 → 253 → 0**.
- Vision-confirmed: mining frame = ship nose on the rock + MINING tag; departed
  frame = no rock in view (camera faces away), no tag.

---

## 5. Build / test status

- Whole solution builds **0 warnings, 0 errors**.
- `Allegiance.Core.Tests`: **3074** total, 3× runs → 3073 pass + the one
  documented pre-existing `LiveTurretGunnerBindingTests.Board_binds_gunner_and_
  active_turret_update_roundtrips_live` flake. Proven NOT mine: it still fails
  with my own-ship gate reverted to pristine (2/2), and its assertion targets
  the untouched P85 teammate gate of a non-mining boarded ship. New
  `MiningStateWireTests` (+4): codec round-trip, `FromMining`, the extraStateBits
  bit, and a live-loopback that asserts the 206 rock id + mining bit over real
  TCP and their clear-on-departure. **Must-fail proven** (bit stamped / 206 sent
  removed ⇒ the live test goes red; green after revert).
- `Allegiance.ClientCore.Tests`: **85/85** (+2 `ClientWorldMiningTests`).
- `Allegiance.ClientCore.MonoGame.Tests`: **287/287** (+3 `MiningOrientationTests`).

---

## 6. Named follow-up (NOT built this task)

Observing **other** ships' (teammate/enemy) mining state. The faithful bit is
already set server-side, so extending is mostly plumbing the bit/rock to the
teammate ships-update path (and streaming a target id per mining ship). Deliberately
out of scope here. Also out of scope (per brief): ship-vs-asteroid collision
(P254, already landed) and any change to the mining economy.
