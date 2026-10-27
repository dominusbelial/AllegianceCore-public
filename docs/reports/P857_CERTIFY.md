# P857 — Certify NPC warp parity: drop the port-only NPC aleph cooldown and arrival latch (SIM-0150, SIM-0208)

**Worktree:** `/home/domingo/allegiance-hermes-p857` (public AllegianceCore worktree at master, branch `hermes/p857`)
**Native reference (read-only):** `~/Development/Allegiance/src/`
**Rows:** SIM-0150 (periodic), SIM-0208 (callbacks) — both **partial → implemented**
**Date:** 2026-10-27 · **Not committed / not pushed** (left in the working tree, per pi-lane rules)

---

## Summary

P788c closed the player warp re-arm handshake but left two **port-only NPC rules** in
`Sector.ResolveAlephShip`, recorded as the remaining gap of SIM-0150 (periodic) and SIM-0208
(callbacks): (1) a 2 s NPC re-warp cooldown (`Sector.WarpCooldownSeconds` on `Ship.LastWarpTime`),
and (2) an opt-in 2× arrival-volume safety latch (`Ship.LastArrivalWarpId` +
`Sector.WarpReEntryGuardEnabled`). Native NPCs (drones: miners, constructors, carriers, fighters)
stay `Ready` and have neither (`FedSrv/fsship.cpp:71-137`; `Igc/shipIGC.cpp:620-636` hands the warp
collision to the site).

**P857 removes both for NPCs**, so NPCs warp exactly as native: they no longer wait out a cooldown
and are no longer held by the arrival latch. Both rules remain **player-only** (the cooldown window
doubles as native's player `m_warpState` re-arm floor; the arrival latch stays the documented opt-in
non-native safety net for players). The change is gated on `ship.IsNpc == false` in the three places
those rules act.

**Result:** SIM-0150 and SIM-0208 marked **implemented**, each pinned by a new failing-first test
that fails on the pre-change code and passes after.

---

## Why the port-only rules existed, and why they are now removable

`git log -S` traces the two rules' origins:

- **2 s cooldown** — introduced with warp transit/docking (P41/P53, `now - LastWarpTime <
  WarpCooldownSeconds`), retained through P760/P776 as the crude port stand-in for native's player
  `m_warpState` cycle.
- **Arrival latch** — added in **P591b** to stop a real, *measured* mine-layer ping-pong
  (Organia↔Gaia, 1453 crossings; Rigel↔Alderan), and re-labelled in **P591c** as a NON-NATIVE safety
  net once the true root cause was found.

**Root cause (P591c):** the port's shared steering applied its arrival-brake distance ramp to every
goal, *including an intermediate warp goal on a cross-cluster leg* — so drones braked to ~0 before
warping, exited with a weak push, could not clear the arrival aleph's transit volume inside ~2 s,
and re-crossed. Native `GotoPlan` flies a ship **through** a warp at cruise speed
(`shipIGC.cpp:629-636`) and only decelerates for the final waypoint; the transit exit velocity is
`backward × (speed + c_fcidExitWarpSpeed)` (`fsship.cpp:93,126`), so a cruise-speed transit is pushed
clear of the destination aleph on its own. P591c fixed `Ship.SteerToward` with a `brakeOnArrival`
flag (`false` for cross-cluster warp legs), and the P776 geometry fix changed the cone hit-test.

Because the real cause (brake-before-warp routing) is fixed natively, the port-only cooldown and
latch are no longer needed for NPCs — native NPCs never had them, and native shows **no** NPC
re-entry failure requiring such a rule. Per the brief's instruction, I reproduce the failure the
native way rather than keep a port-only rule. This is verified by `P591cDroneRoutingTests` (below):
NPC mine-layers still reach their lay target and lay with exactly route-length crossings and **0**
guard blocks, now with the NPC guard exemption in place.

---

## Native branch mapping (scope item 1) — every native NPC warp branch → port

Native NPC warp handling (`FedSrv/fsship.cpp:71-137`, re-grepped this task):

| Native branch | Native cite | Port site (before) | Verdict |
|---|---|---|---|
| NPC warp state stays `warpReady` (only `IsPlayer()` enters `warpNoUpdate`) | `fsship.cpp:51,74-82` | `Ship.WarpReady => IsNpc \|\| PlayerWarpState == Ready`; P857 removes the NPC cooldown so `IsNpc` ships have no effective gate | **equal** (P857) |
| Andon positive-mass-limit NPC exemption (`mass <= limit \|\| !player && limit > 0 \|\| limit < 0`) | `fsship.cpp:77` | `ServerHost.HitWarpEvent` mass gate (audited P788c) — NPCs pass any positive limit | equal |
| Exit speed bonus (`speed = -(speed + c_fcidExitWarpSpeed)`) | `fsship.cpp:126` | `AlephExit` (P777) uses `FloatConstants[3]` | equal (unchanged) |
| Exit wobble (random pitch/yaw/roll bump, mass>300 scaling) | `fsship.cpp:96-122` | `AlephExit` wobble (P777) | equal (unchanged) |
| Exit position offset (up/right jitter + (radius+5) backward) | `fsship.cpp:129-132` | `AlephExit` (P777) | equal (unchanged) |

The warp frame's model spin (`Igc/warpIGC.cpp:40-70`) is **visual only** — confirmed it changes no
behaviour, and left as-is per the brief.

---

## Implementation

Four files changed (working tree only):

1. **`src/Allegiance.Core/Model/Sector.cs`** — `ResolveAlephShip`:
   - cooldown gate `now - LastWarpTime < WarpCooldownSeconds` → gated on `!ship.IsNpc`;
   - the arrival-latch release block and the re-entry guard block both gated on `!ship.IsNpc`.
   Doc comments on `WarpCooldownSeconds` and `WarpReEntryGuardEnabled` updated to reflect the
   player-only scope.
2. **`src/Allegiance.Core/Sim/Ship.cs`** — doc comments on `LastWarpTime`, `WarpReady`,
   `LastArrivalWarpId` updated (NPCs: no cooldown / not latched).
3. **`src/Allegiance.Server/ServerHost.cs`** — exit-comment updated (NPCs have no handshake/cooldown;
   the cruise-speed transit + exit push clears the cone).
4. **`tests/Allegiance.Core.Tests/P857NpcWarpParityTests.cs`** — new failing-first tests (4).

The `ResolveDockCollisions` use of `WarpCooldownSeconds` (dock-after-warp debounce) is **unchanged** —
it is a docking gate, out of this task's warp re-entry scope (see Follow-ups).

---

## Tests (fail before the change)

All four fail on the pre-change code (verified by temporarily restoring the old gates; the two NPC
tests failed 2/2, the two player tests still passed), and pass 3/3 ×3 after.

| Test | Proves |
|---|---|
| `Npc_recently_warped_and_still_inside_cone_rewarps_immediately` | an NPC that just warped re-triggers on re-entering the cone with no 2 s cooldown (native `fsship.cpp:74-82`) — **fails before** (cooldown silences it) |
| `Player_recently_warped_and_still_inside_cone_is_debounced` | a player is still debounced by the 2 s window (native `m_warpState` re-arm) — pins the removal is NPC-only |
| `Npc_is_not_held_by_arrival_latch_even_with_guard_enabled` | with the opt-in re-entry guard ENABLED, an NPC that just arrived is not held from re-crossing — **fails before** (guard blocks it) |
| `Player_is_held_by_arrival_latch_when_guard_enabled` | with the guard ENABLED, a player is still held — pins the latch removal is NPC-only |

---

## Light gate

| Check | Result |
|---|---|
| Whole-solution build (`AllegianceCore.slnx -c Release`) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **90/90 tool projects built**, 0/0 |
| Rig AOT publish (`src/Allegiance.Server`, `-r linux-x64 --self-contained -p:PublishAot=true`) | ELF produced (15 MB); **no IL2026/IL3050** |
| New tests ×3 alone | `P857NpcWarpParityTests` **4/4, ×3** |
| Affected tests ×3 alone | `P591cDroneRoutingTests` 6/6; `P591bLayerPingPongTests`/`WarpProximityTests`/`P777WarpExitWobbleTests`/`P857NpcWarpParityTests` batch 27/27 ×3; `P788cConfig/NativeTickOrder/ScriptedWarp` + `P760ServerCallbacks` + `P650ServerRules` + `WarpTransitServer` + `P777bMissileAlephCone` batch 53/53 ×3; `P560PodWarpLegClosedLoopTests` 1/1 |
| ONE `tools/run-tests.sh --tier fast` | 11,098 tests, 319.6 s — **7 failures, all unrelated / environmental** (below) |
| Showcase never-parks (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`) | **5/5 pass** (+1 probe = 6 total). Natural mining, research, kills, pods and relaunch all continued — no stuck or ping-ponging drones in the 15v15 showcase |

### Fast-tier failure disposition (none caused by P857)

- **`PlayerBundlePathsTests` (5)** — the documented environmental failure: the fast-tier runner sets
  `TMPDIR` under the worktree, so the test's ancestor walk finds the checkout's `local.env`
  (P788C §preserved-failures). Confirmed **9/9 pass** when run with a `/proc` fd alias TMPDIR that has
  no repo ancestor. Orthogonal to warp.
- **`RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` (1)** — load flake;
  **passes 3/3 alone**.
- **`DockedStationUpdateLiveTests.Docked_player_receives_own_station_update_when_own_station_has_no_scanner`
  (1)** — Live/load flake; **passes 3/3 alone**.

Raw fast-tier artifacts: `~/allegiance-evidence/P857/fast-tier/` (summary.json, per-project logs).
Public report assets below are kept ≤ 2 MB.

---

## Native-intent classification

**Design.** The removal is a fidelity fix: native NPCs stay `Ready` and have neither a cooldown nor an
arrival latch (`fsship.cpp:74-82`), and the exit transform already matches native (P777). The port-only
rules were workarounds for a routing bug (brake-before-warp) that is now fixed natively (P591c), so
dropping them for NPCs brings the port to exact native behaviour. No native bug was encountered and no
existing failure needs a port-only rule.

## Modern / better-approach note

Rather than tune a flat debounce, the port now relies on native's actual mechanism — transit at cruise
speed plus the `+c_fcidExitWarpSpeed` exit push carrying the NPC clear of the arrival cone (P591c). This
is strictly closer to native than a 2 s timer and needs no special-casing for ship mass or speed. The
remaining opt-in player re-entry guard stays OFF by default (P594) and is now player-only. A future
clean-up could fold the player cooldown floor fully into the `WarpState` handshake and retire
`WarpCooldownSeconds`'s `ResolveAlephShip` use entirely, leaving only the docking gate.

## Unfinished / follow-ups

- **NPC dock-after-warp window (residual, out of scope):** `ResolveDockCollisions` still enforces
  `now - LastWarpTime < WarpCooldownSeconds` before docking. Native NPCs (always `warpReady`) can dock
  immediately after a warp, so this is a small residual divergence for NPCs. Left unchanged because it
  is a *docking* gate, not a warp re-entry rule; a follow-up would gate it on `!ship.IsNpc` to match
  native. Not covered by SIM-0150/0208's warp-collision rows.
- **Warp frame model spin** (`Igc/warpIGC.cpp:40-70`): visual only, confirmed no behaviour change, left
  as-is per the brief.
- **221 two-match A/B** remains Claude's rig work (per P788C); not run in this lane.

## Evidence

- Source diff: `git diff` (working tree) — `Sector.cs`, `Ship.cs`, `ServerHost.cs` + new
  `P857NpcWarpParityTests.cs`.
- Rows updated: `docs/parity/sim-rules-matrix.csv` (SIM-0150, SIM-0208 → implemented).
- `scripts/parity-scorecard.py`: **validates clean** (both rows now implemented; CSV LF-only, no CR).
