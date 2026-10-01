# P449B — native camera jiggle (afterburner + impacts) and a flight-FOV setting

## Summary

- Ported native's camera **jiggle** (`WinTrek.cpp:374-399`, `503-508`) as a pure,
  RNG-injectable model — `Rendering/FlightJiggle.cs` — and wired it into the
  MonoGame **cockpit** camera (matching native's `bJiggle = InternalCamera(m_cm)`
  gate, which is cockpit-only).
- Changed the in-flight camera **FOV** to native's real default `s_fDefaultFOV =
  50°` (`WinTrek.h:26`) in BOTH normal and debug mode (Domingo's decision), and
  made it configurable via the client-only `--flightFov <deg>` launch setting
  (the old P385 `_debugMode ? 50f : 75f` is gone).
- **Impact source (scope 4):** investigated; there is **no per-collision wire
  signal** reaching the client. Per the brief, we did **not** invent one from
  hull-fraction drops (weapon hits must not jiggle). The gap is documented and a
  smallest-possible wire change is scoped as a named follow-up task.

Branch: `master` (checked with `git branch --show-current`), not pushed.

---

## 1. The pure jiggle model — `Rendering/FlightJiggle.cs`

Reproduces native `CameraImpl::Apply`'s jiggle block exactly
(`WinTrek.cpp:378-399`):

```cpp
const double jiggleHalfLife = 0.25f;
m_jiggle *= (float)pow(jiggleHalfLife, (double)dt);        // decay
if (afterburner || m_jiggle > 1.0) {
    float power = m_jiggle + (afterburner ? afterburner->GetPower() : 0);
    if (power != 0.0f) {
        float amount = RadiansFromDegrees(0.5) * power;
        orientation.Yaw(random(-amount, amount));
        orientation.Pitch(random(-amount, amount));
    }
}
```

- **Half-life note:** the code's named constant `jiggleHalfLife = 0.25f` is the
  *decay base*; the actual per-frame factor is `pow(0.25, dt)` (so the effective
  half-life is 0.5 s). `FlightJiggle` reproduces `pow(0.25f, dt)` literally.
- **Injectable RNG:** the model takes `ITrailRandom` (the shared render-thread
  source in the live client); tests inject a deterministic stub.
- **`SetJiggle(j)` replaces the accumulator** (native `WinTrek.cpp:503-508`) —
  this is the impact entry point.
- **`ImpactIntensity(damage)`** = `2 * damage * (1/20)` (the `effectJiggle`
  intensity, `shipIGC.cpp:624,836,1006`; station-ramming variants
  `shipIGC.cpp:1191-1195`). It is the drop-in call the follow-up wire task will
  use: `SetJiggle(ImpactIntensity(damage))`.
- **`PerturbView(view, yawRad, pitchRad)`** applies the offsets as a pure
  camera-space rotation (view multiplied on the left), mirroring native rotating
  the camera orientation before projection.

### Which cameras shake (correction to the brief)

The brief said "apply it in chase and cockpit modes". Checking native:
`bJiggle` is set from `InternalCamera(m_cm)` (`WinTrek.cpp:7014-7016`) and
`InternalCamera(cm) == (cm == cmCockpit)` (`Treki.h:145`) — **native shakes only
the cockpit view, not the external chase view.** We followed native (cockpit
only) and document the correction; the brief's "chase and cockpit" is one line
away if Domingo wants chase included.

### HUD / jiggle interaction (decision)

Native rotates the whole camera orientation, then projects the world *and* the
world-projected HUD through it: `RenderLeadIndicator` /
`RenderTurnIndicator` call `GetCamera()->TransformDirectionToImage(...)`
(`indicator.cpp:259, 302`), i.e. through the jiggled camera. The centre aiming
reticle is a screen-anchored overlay and does not move.

We match this exactly by perturbing the **shared `view` matrix** in the render
pass *before* the HUD projections (`ComputeLockBracket`, `ComputeLeadIndicator`,
`ComputeTargetSelectionBracket`, `ComputeTargetBearing` all consume that same
`view`). Consequences:

- World shakes. ✔
- Lead indicator / lock / selection brackets shake with the view (native-
  faithful). ✔
- Screen-anchored centre reticle stays put (by construction). ✔
- Aiming is *not* destabilised: the target-picking ray in `Game1.Targeting.cs`
  uses the un-jiggled camera, so the crosshair's world point is stable. ✔

---

## 2. Afterburner source

Native reads the own ship's mounted afterburner power. Two port realities:

1. **`ServerShipUpdate.Power` (byte 46) is a hardcoded flat `1.0`**
   (`ServerShipUpdate.FromModel`: `u._b[46] = Percent(1.0f)`). The port's
   simulator models the afterburner as a binary boost (button → thrust); there is
   **no server-side power ramp** to wire, despite the brief's note that P358's
   nozzle glow reads it — it does not; P358 keys on the `AfterburnerButton`
   StateM bit (`NozzleGlowValue`, `Game1.Rendering.cs:115-117`).
2. Therefore the native `afterburner->GetPower()` term is **approximated as the
   `AfterburnerButton` StateM bit** on the own ship's update: `0` (idle) or `1`
   (boosting) — the closest truthful per-frame power signal the client has.

**"Mounted" gate** (native `GetMountedPart(ET_Afterburner,0) != NULL`): the own
ship's granted loadout (`ShipEntity.Loadout`, set by `BUY_LOADOUT_ACK`) is
scanned for an `Afterburner` part via the shop catalogue
(`OwnShipLoadoutHasAfterburner`, mirroring the P447b predictor's scan). Without
an afterburner mounted, the afterburner term contributes nothing — matching
native — so a boostless fighter does not shake.

Result: the camera tilts only while (a) an afterburner is mounted **and** (b)
the boost button is held (or a residual impact is present). An idle mounted
afterburner does not shake forever. This is documented in the code and in this
report as an honest substitution for native's (absent) power ramp.

---

## 3. Impact source (scope 4) — finding + follow-up scope

**Finding: no real per-collision client signal exists.** Collisions are resolved
server-side: the sector passes `ResolveShipAsteroidCollisions` (P254),
`ResolveShipShipCollisions` (P354), `ResolveShipStationCollisions` (P466) apply
the elastic bounce + `ReceiveDamage` to the `Ship` model directly; nothing is
broadcast to the colliding ship's owner beyond the routine `ServerShipUpdate`
(position/velocity/hull-fraction). P254's own remarks defer the client-side
effects explicitly: *"The client-side collision sound / FF / jiggle effects are
out of scope (a separate task); the position/velocity correction itself rides
real server updates."* (`Sector.cs`). The only hit wire
(`PROJECTILE_HULL_HIT(213)`, P364/P463) is **projectile**-only — weapon hits.
Consequently:

- A hull-fraction drop cannot distinguish a collision from weapon fire. **We did
  not invent a jiggle from hull-fraction drops** — weapon hits must not jiggle
  in native, and neither should the port.
- `FlightJiggle.SetJiggle / ImpactIntensity` are implemented and unit-tested but
  currently only ever called by the (future) impact feed — the afterburner is the
  only active source today.

### Named follow-up task — **P479: per-peer collision-impact wire signal**

Smallest wire change to enable native impact jiggle (and P463c's collision
sounds, which share the same missing signal):

- Emit, from each of the three elastic-collision passes in `Sector` (only for a
  peer-owned ship that actually takes collision damage), a **new lightweight
  message** (e.g. `COLLISION_IMPACT(2xx)`): fixed payload `{shipId, damage}`
  (~8 B), sent to the colliding ship's owning peer only (native's
  `PlayVisualEffect(effectJiggle, ship, …)` fires for the own ship only,
  `trekigc.cpp:4994-5001`).
- Client `ClientWorld.Apply` + `ConnectionSession` dispatch → drain into the
  jiggle feed: `_flightJiggle.SetJiggle(FlightJiggle.ImpactIntensity(damage))`.
- The native intensity formulas to reproduce: asteroid/ship
  `2*damage1*(1/20)`; station ramming `(2*damage1+damage2)*(1/20)`
  (`shipIGC.cpp:836, 1006, 1191-1195`).
- Must exclude the P254 mining case (no bounce for a ship actively mining its
  rock) and the native `c_habmLifepod` no-damage gate.

Deliberately NOT done here: no wire change in P449b, and no hull-fraction
heuristic.

---

## 4. FOV setting — `flightFovDegrees`, default 50°

Domingo's decision: match native. `s_fDefaultFOV = 50°` (`WinTrek.h:26`) is now
the in-flight default in **both** normal and debug mode, replacing the P385
expression `_debugMode ? 50f : 75f` (`Game1.Lifecycle.cs:118`).

- **Setting name:** `FlightFovDegrees`, surfaced as the client-only launch
  setting **`--flightFov <deg>`** (also `--flightFov=<deg>`), default `50f`.
- **Mechanism decision:** the in-flight FOV is a purely client-side rendering
  knob and there is no liveSettings wire channel to the client (P385's own
  `liveSettings.debugMode` is *"paired by hand, never transmitted"*). So, per
  the established P449a convention (the immediately-preceding task mirroring the
  P378 `liveSettings` philosophy as a client-only launch setting — `--debris`),
  `--flightFov` is a client-only liveSettings-style value. It is *not* added to
  the server's `server.json liveSettings` section because that would be dead,
  untransmitted config; the brief's "a `liveSettings` value (P378 section)" is
  honoured in the conventional sense this codebase uses for client knobs.
- Plumbing: `Program.cs` parse → `LaunchOptions.FlightFovDegrees` (default 50) →
  `Game1._flightFovDeg` → all three in-flight cameras
  (chase/cockpit/missile) via the shared `flightFovDeg` in
  `Game1.Lifecycle.cs`. The `--debugMode` flag now affects only the capture-
  overview FOV (100°→50°), unchanged.
- Doc comments updated: `ChaseCamera.cs` (default + rationale superseded),
  `CockpitCamera.cs`, `MissileCamera.cs`, `Game1.Session.cs` (DebugMode).
- No camera-framing or target-projection code/test assumed 75°; a grep of the
  client + tests found no behavioural 75° coupling (only historical comments).

---

## 5. Verification

- **Pure tests** (new, `tests/.../FlightJiggleTests.cs`, 13 facts):
  - Decay multiplies by `pow(0.25, dt)` (incl. fractional intervals).
  - Offset magnitude bounded by `0.5° · power` per axis; the upper bound is
    actually reachable (stub returns `max`).
  - No offset with no afterburner and a residual ≤ 1.
  - Residual > 1 shakes even without an afterburner.
  - `SetJiggle` replaces the accumulator.
  - Afterburner power > 0 ⇒ non-zero offset; power 0 ⇒ none (the "client-level"
    contract, pinned at the render-layer model).
  - `PerturbView` identity/non-zero rotation behaviour.
  - `ImpactIntensity` uses native `2·damage·(1/20)`.
  - FOV: chase/cockpit default projection = cot(50°/2); a custom value is
    applied; `LaunchOptions.FlightFovDegrees` defaults to 50.
  - These fail on the unfixed tree (the model + FOV behaviour did not exist).
- **Whole-solution build** with `/home/domingo/.dotnet/dotnet build
  AllegianceCore.slnx`: **0 warnings, 0 errors**.
- **Full suite** runs and observed flakes are recorded in the run log below
  (see §6). `Allegiance.ClientCore.MonoGame.Tests`: 1036/1036 green (includes the
  13 new).
- **Live visual shake:** not performed in this headless environment (booting the
  real client + server and observing by eye). The deterministic unit tests pin
  the exact magnitude/behaviour; live expectation: a small (~≤0.5°·power, i.e. a
  fraction of a degree at full boost) cockpit tilt while holding the afterburner,
  decaying over ~0.5 s after release, invisible at rest. A live proof can reuse
  the P385 MiniGame offscreen-CockpitCamera harness to measure projected-pixel
  wander under a boosted `Step`+`PerturbView`.

---

## 6. Full-suite run log

Three consecutive `dotnet test AllegianceCore.slnx` runs. The project P449b
actually changed — **`Allegiance.ClientCore.MonoGame.Tests` — is 1036/1036 green
in all three runs** (including the 13 new). Every failure is in the
server/wire assemblies (`Allegiance.Core`, `Allegiance.ClientCore`), which do
**not reference the MonoGame project** (verified in their `.csproj`:
`Allegiance.Core.Tests` → Core/Server/Bot/Lobby; `Allegiance.ClientCore.Tests` →
ClientCore/Core/Server/allegiance-mcp — neither lists `Allegiance.ClientCore.MonoGame`),
so none of P449b's changes can even be loaded by them. Each is a documented
pre-existing live-wire timing flake:

| Run | Changed project | Failures outside it |
|---|---|---|
| 1 | MonoGame.Tests 1036/1036 | Core 10 — ConquestTerritoryLiveTests.*, GameOverPlayersWireTests.*, LiveTurretGunnerBindingTests.Board_binds…, AddPartTests.Second_part_touch_while_offer_pending…, and other live-wire timing tests |
| 2 | MonoGame.Tests 1036/1036 | Core 11 + ClientCore 3 — ConquestTerritoryLiveTests.*, GameOverPlayersWireTests.*, LiveChatSectorWireTests.Individual_AllSector…, LiveRipcordTests.Ship_anchor…, ShipUpdateBudgetLiveTests.Ship_dropped…, WarpBombTests.Live_whole_mission_warp_bomb…, BotFlightTests.Abrupt_disconnect…, LiveTurretGunnerBindingTests.Board_binds…, JoinFlowLiveTests.EntityTable…, ShipDeleteGoneLiveTests.DockOut…, RetriedConnectLiveTests.RepeatedConnects… |
| 3 | MonoGame.Tests 1036/1036 | Core 8 + ClientCore 3 — ConquestTerritoryLiveTests.*, GameOverPlayersWireTests.*, LiveWinConditionTests.Multi_mission_disconnect…, AsteroidMinedLiveTests.Mining_broadcasts…, FullSessionKillIntegrationTests.FullSession_ThreePeers…, LiveTurretGunnerBindingTests.Board_binds…, JoinFlowLiveTests.EntityTable…, ShipDeleteGoneLiveTests.DockOut… |

Families named at least once and their documented status: `ConquestTerritoryLiveTests.*` and
`GameOverPlayersWireTests.*` (win-condition live timing, flap in-suite and sometimes in
batched isolation — P463), `LiveTurretGunnerBindingTests.Board_binds_gunner…` (the repo's
persistent documented machine flake; do not chase), `LiveWinConditionTests.Multi_mission_
disconnect…` (documented environmental, P200), `JoinFlowLiveTests.EntityTable…` /
`ShipDeleteGoneLiveTests.DockOut…` (named in P462/P463), `LiveChatSectorWireTests.*`
(skill-documented family), `RetriedConnectLiveTests.RepeatedConnects…` (ephemeral-port /
socket-exhaustion family, skill P186 caution), `AddPartTests.Second_part_touch_while_offer_
pending…` (pickup-offer timing), `LiveRipcordTests`, `ShipUpdateBudgetLiveTests`,
`WarpBombTests`, `BotFlightTests`, `AsteroidMinedLiveTests`, `FullSessionKillIntegrationTests`
(all live-network timing; none reference any P449b symbol).

---

## 7. Deliverables

1. Code + tests, committed on `master` (not pushed).
2. This report, `docs/reports/P449B_CAMERA_JIGGLE_FOV.md`.

### Out of scope (unchanged)
Space dust (P449a), the aleph proximity FOV squeeze (`WinTrek.cpp:405-420`),
force feedback (`PlayFFEffect`), P438–P440, P445–P448.
