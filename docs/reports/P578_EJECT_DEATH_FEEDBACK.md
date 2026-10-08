# P578 — eject/death feedback like native: red critical message + sound, eject-pod HUD panel, camera swing to the killer, death banner, rescue messages

Branch: `master`, commit `a5616c7f` (amended after the 3× full-suite run). Not pushed.
Build: `/home/domingo/.dotnet/dotnet build -m:8` — **0 warnings, 0 errors.**

---

## 1. What was missing

Domingo: "wasn't there some kind of warning banner when you ejected in your pod?"
Yes. Native traces showed **the port had none of it** — no `CriticalTip`, no
`ejectpodbmp` / `conejectpoddisplaybmp`, no `deathbannerbmp`, no eject-pod panel,
and the client ignored `PLAYER_RESCUED(16)`. When you were beaten the client
silently flipped to the pod (P540/P571) with no red message, no sound, no camera
moment, no panel, and no banner.

Native reference (traced 2026-10-07):
- `clintlib/appmsg.cpp:2797-2844` (`FM_S_KILL_SHIP`) — self / you-killed /
  observer lines, `PostText(bCritical, …)` routing.
- `WinTrek/trekigc.cpp:3067-3083` (`PostPlainText`) — critical →
  `newCriticalMsgSound` + `SetCriticalTipText`; non-critical →
  `newNonCriticalMsgSound` + `SetTipText`.
- `WinTrek/console.cpp:913` — tip duration `fTipTextDuration = 7.5 s`.
- `Artwork/dialog.mdl:1926-1960, 1984-1996` — `EjectPodImage` = conejectpoddisplaybmp
  + a softgaugebmp of `GetEndurance(Me)`; `IsNotEjected` (:208,:435,:470,:507)
  zeroes ammo/energy/fuel in a pod.
- `WinTrek/trekigc.cpp:3101-3105` (`EjectPlayer` → `OverrideCamera(now,pcredit,true)`),
  `WinTrek/WinTrek.cpp:6501` — override lasts `now + 5.0f`.
- `Artwork/dialog.mdl:5438-5439` — `DeathBannerPane = SwitchPane(IsGhost, deathbannerbmp)`.
- `clintlib/appmsg.cpp:311-340` — `FM_S_PLAYER_RESCUED`: "You rescued %s."
  non-critical + `rescuePlayerSound`; "%s rescued you." critical + `jumpSound`.
- `FedSrv.CPP:11793-11812` — KILL_SHIP(105) is sent on BOTH death **and** eject,
  `bDeathCredit = bKilled`, carrying the credited killer — the port previously
  suppressed KILL_SHIP on eject, so the client could not learn the killer.

`sounddef.mdl` (verbatim): `newCriticalMsgSound = ImportWave("criticalmessage")`,
`newNonCriticalMsgSound = ImportWave("noncriticalmessage")`,
`rescuePlayerSound = ImportWave("hoover")`, `jumpSound = ImportWave("jumpin")`.

## 2. The port

### 2.1 Server — send KILL_SHIP(105) on eject too (native fidelity)

`Server.Combat.cs` `EjectSurvivor` now emits a `KILL_SHIP(105)` to the same
same-sector audience **before** `EJECT(77)`, with `bDeathCredit=false`,
`sideidKiller`, and `typeCredit`/`idCredit` = the credited killer
(`FedSrv.CPP:11794-11812`). `bKillCredit` mirrors native
(`!bLifepod && psideCredit != pside`). This is the wire change that lets the
client know WHO forced the eject — the basis for the "You were forced to eject by
X" line and the camera swing. A true death still sends `bDeathCredit=true`.

### 2.2 Pure text/routing — `EjectFeedback` (headless, unit-tested)

`src/Allegiance.ClientCore/State/EjectFeedback.cs` + `FeedbackLine.cs` port the
`appmsg.cpp` rules exactly:

| Perspective | Death | Eject | Critical |
|---|---|---|---|
| victim is me, killer | `You were killed by X` | `You were forced to eject by X` | yes (:2812/:2834) |
| victim is me, no killer | `You were killed` | `You ejected` | yes |
| killer is me | `You killed X` | `You forced X to eject` | yes (:2838-2842) |
| observer (another player) | `X was killed` | `X ejected` | no (:2801-2810) |

Rescue (`appmsg.cpp:311-340`): rescuer → `You rescued X.` non-critical +
`hoover`; rescuee → `X rescued you.` critical + `jumpin`. A `CameraOverrideRequest`
is queued exactly when the victim is me and a killer is credited
(`EjectPlayer`, appmsg.cpp:2900-2902 / clintlib.cpp:3063).

### 2.3 ClientWorld consumption

`Apply(KillShip)` now honours `bDeathCredit`: a true death marks the entity
dead/gone as before; an **eject** (`bDeathCredit=false`) does **not** mark it
dead (the pod survives — previously the port never sent an eject KILL_SHIP, so
the unconditional mark-dead was safe; it is now guarded). Each KILL_SHIP enqueues
its feedback line (+ the camera override for my own eject/kill). `Apply(PlayerRescued)`
(new dispatch case) enqueues the rescue line. `TakeFeedbackLines()` /
`TakeCameraOverrideRequests()` drain atomically under the gate.

### 2.4 Game1 — sounds, tip lines, camera swing

`Game1.Feedback.cs` drains the queues each frame: plays
`criticalmessage`/`noncriticalmessage` (plus `hoover`/`jumpin` for rescues),
keeps the HUD tip lines for native's 7.5 s, and turns each camera request into a
**5-second `ExternalOverride`** aimed at the killer (`RequestOverrideCamera`,
mirroring `OverrideCamera(now,pcredit,true)` / `m_timeOverrideStop = now+5`).
The override reverts to chase on expiry (`UpdateOverrideCameraExpiry`,
`EndOverrideCamera`). `Game1.Director.TryOverrideTargetPos` prefers the explicit
override anchor over the explosion fallback. `SoundManager` gained the four cues.

### 2.5 HUD overlays — `DrawEjectDeathOverlays`

`Game1.Rendering.cs` draws, after the fight HUD: the critical tip centred **red**
near the top and the non-critical tips in the side accent colour below; the
**eject-pod panel** (real `conejectpoddisplaybmp.mdl` when the artwork is present,
else a procedural panel) with `YOU HAVE EJECTED.` and the endurance gauge
(`O : mm:ss`, from `LifepodHud.OxygenSeconds` = the pod's `GetEndurance`); and the
**death banner** (real `deathbannerbmp.mdl` when present, else a red banner) when
the local ship is a ghost. `FeedbackTextures.cs` loads the real `.mdl` bitmaps via
the port's `MdlTextureDecoder` pipeline with graceful fallback. The ammo/energy
gauges read **zero while in a pod** (native `IsNotEjected`), keeping the FUEL slot
as the oxygen countdown (P377).

P540/P571 eject autopilot and P575 docking are untouched: the eject KILL_SHIP is
non-fatal (`bDeathCredit=false`), the EJECT still drives the pod pose, and the
pod still flies home / docks on rescue.

## 3. Failing-first tests

`tests/.../P578EjectDeathFeedbackTests.cs` — 16 tests covering the text per
(self/other, killed/ejected/forced), critical vs non-critical routing, rescue
routing + extra cues, the eject-vs-death `IsDead` distinction, the observer line,
and the camera-override request. Every one fails on today's pre-P578 code (no
`EjectFeedback`, no lines, `Apply(KillShip)` marked every victim dead,
`PLAYER_RESCUED` ignored).

Existing live tests updated for the native-faithful change:
- `LiveInSpaceEjectTests` — now expects the eject `KILL_SHIP` (`bDeathCredit=false`,
  `idCredit` = the cross-side observer) while still asserting no SHIP_DELETE /
  respawn / ENTER_LIFEPOD (mutually exclusive with the true-death branch).
- `LiveLifepodOxygenTests` — the DEATH KILL_SHIP is now found by
  `bDeathCredit=true` (the observer also saw the earlier eject KILL_SHIP).

## 4. Suite runs (scripts/run-tests.sh, P471, on the final code)

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1584/1584 | 560/561 | 3780/3781 |
| 2 | 12/12 | 1584/1584 | 560/561 | 3777/3781 |
| 3 | 12/12 | 1584/1584 | 561/561 | 3780/3781 |

Run 1 failures — pre-existing live-wire load-flakes already ledged in
`docs/TEST_FLAKES.md`, unrelated to P578 (all 16 P578 tests pass in every run):
`ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone` and
`LiveRipcordTests.Cancel_while_active_aborts_sector_wide`.

Run 2 failures — the same rotating live-wire load-flake class (the ledger's
"Failures rotated run-to-run… fixed-sleep live-wire-receipt class"):
`FullSessionKillIntegrationTests.FullSession_ThreePeers_…_RealCrossSideKill`,
`LiveRipcordTests.Re_target_activates_to_requester_only`,
`MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range…`,
`ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget…`,
`StationUnderAttackAlertLiveTests.Sub_30_percent_fraction_…`.

See `docs/TEST_FLAKES.md`. None of the failures involve P578 (the 16 new tests
pass; the changed eject live tests pass).

Run 3 failure — same class: `LiveChatSectorWireTests.Individual_AllSector_and_
FriendlySector_relay_to_real_peers_over_the_wire`. MonoGame was clean in all 3
runs and ClientCore clean in run 3.

## 5. Real-window proof (P572 tooling)

Ran `scripts/live-proof.sh` on the 221 map (`m2xspiderw.igc`) with MCP combat
scenarios, real windowed client at 2560×1440, `ALLEGIANCE_ARTWORK_PATH` set,
`SaveAsPng` backbuffer autoshots. Evidence in `docs/reports/P578_assets/`.

**Real-window screenshots of the tip-line HUD (P578 item 1):**
`docs/reports/P578_assets/proof_t030.1_sOmega_cchase.png` shows the real client
window with the four centred cyan observer tip lines stacked above the ship,
quoting the native text verbatim:

```
mcp-b003-6129 ejected
mcp-b004-5547 ejected
mcp-b004-5547 was killed
mcp-b003-6129 was killed
```

The `ejected`/`was killed` split and the side-accent colour match the ported
rules (appmsg.cpp:2801-2810). These are genuine presented backbuffer frames
from the live server-driven client — not `--capture`/offscreen renders.

**Server eject path (item 3's wire half) fired live:** the arena combat logs
show `[eject] KILL_SHIP(105) bDeathCredit=false for ejecting ship … killer ship …`
followed by `[eject] player ship … ejected -> Lifepod hull 33 … EJECT(77)`,
i.e. the new server-side eject KILL_SHIP + the client's tip lines for them.

**Honest limitation:** the *player's own* eject — the red critical
`"You were forced to eject by X"` line, the eject-pod panel, the 5 s camera
swing, and the death banner — could not be captured in the real window in this
session. Across seven live scenario attempts (arena in the player's home sector,
single/multi side-B combat bots, long-horizon transits) the MCP combat bots
reliably fought each other and never engaged the stationary auto-launched player,
so the player's own ship never ejected. These code paths are nonetheless covered
by the 16 failing-first unit tests and the headless live tests
(`LiveInSpaceEjectTests`, `LiveLifepodOxygenTests`) that exercise the eject
KILL_SHIP end-to-end; the observer-tip rendering and the server eject wire are
the live-proven half. Forcing a deterministic player eject is a scenario-engineering
problem (getting a bot to prioritise the human ship), not a defect in the port.

## 6. Notes / gaps

- Native colours the victim/killer name with the side colour; the port's
  spritefont has no rich colour spans, so the line text is plain (side colour
  used for the non-critical tips, red for critical) — noted, not silently altered.
- The eject-pod panel renders the real `conejectpoddisplaybmp.mdl` / `deathbannerbmp.mdl`
  when `ALLEGIANCE_ARTWORK_PATH` is set; without the artwork it falls back to the
  procedural panel/banner (identical info).
