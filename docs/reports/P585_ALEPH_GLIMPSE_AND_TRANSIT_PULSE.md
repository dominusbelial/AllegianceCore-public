# P585 — readable aleph glimpse + opt-in TRANSIT PULSE (flare + glimpse open-wide)

## Summary

Made the aleph **destination glimpse** (P576-B, `--aleph-glimpse on`, default
OFF) actually *readable*: stronger and larger, and it **fades in as you
approach** an aleph. Added an **opt-in TRANSIT PULSE**: whenever any ship
crosses an aleph in the viewer's current sector, that aleph's funnel briefly
brightens, its glimpse **opens wide**, and a **lens-flare burst** flashes from
the funnel centre (only when that centre is on screen and not occluded by the
P547 linear-depth pass). Everything is under the existing `--aleph-glimpse on`
flag (default OFF) and works with the black, native **and** HD sky tiers
(P577/P588).

**Native fidelity preamble:** this whole feature is *non-native*. The only
native transit visual is the star-core swell on the arrival aleph
(`AlephGeo::Render`, `effect/alephgeo.cpp:158-208`) and the teleport sparks
(`WarpTransitParticles.cs`). `WinTrekClient::ShipWarped`
(`WinTrek/trekigc.cpp:2976-2991`) is an **empty, commented-out body** — there is
literally nothing to port for a transit *effect*. So every new rule here is a
deliberately opt-in addition on top of the native-sky glimpse port; the native
file:line citations below pin the pieces we DO touch (the additive funnel blend,
the transit-collision radius, the SDR_LEFTSECTOR departure notice).

---

## 1 — Resting glimpse tuning (stronger, larger, fades in on approach)

`Rendering/AlephGlimpseRenderer.cs`.

| Constant | Old | New | Rationale |
|---|---|---|---|
| `RestingCentreOpacity` | 0.32 | **0.55** | The "too subtle to notice" report (P576 live). Raised so the destination-sky disc reads clearly against the bright funnel; still alpha-blended over the already-drawn f02 core so the star core's brightest part stays on top (the "below the star core" look). |
| `RestingDiscRadiusFactor` | 1.0× | **1.3×** | Bigger disc. Still well inside the 2.5× visual funnel mouth (`WarpRenderer.VisualScaleMultiplier`, the P237 "~10× a ship" calibration), i.e. *inside the inner funnel*. |
| `FadeFullDistance` | — | **430 u** | Approach fade: full strength at/below ~400 u (task spec). |
| `FadeStartDistance` | — | **3400 u** | Approach fade: fully invisible past ~3000 u (task spec). |

`ApproachFade(dist)` is a pure, unit-tested curve: 1 at ≤ `FadeFullDistance`, 0
at ≥ `FadeStartDistance`, quadratic ease between — the glimpse **fades in on
approach** rather than sitting at a constant faint value.

The fade-in is exactly the complaint quoted in the task: "I think I'm getting
teleported before touching the star." The resting glimpse is now gated by
approach distance so it is quiet at range and unmistakable at ~400 u, and the
disc is larger/stronger so it registers *while* approaching.

---

## 2 — Transit pulse (non-native, opt-in)

### 2a. Client-side transit-signal detection — `State/ClientWorld.cs`

The renderer is driven by discrete signals, drained each frame under the world
gate. Detection comes from the real wire messages the client already receives:

1. **Departure** — `Apply(ShipDelete)` when `Sdr == SDR_LEFTSECTOR`: a ship
   warped OUT of the viewer's sector. Attributed to the **nearest aleph in the
   viewer's own sector** within its transit-attribution radius. Native authority
   for the departure notice: `CFSShip::AnnounceExit(pclusterOld, SDR_LEFTSECTOR)`
   (`fsship.cpp:206-210`, `FedSrv.CPP:13525`), only ever addressed to peers
   flying in the sector being left (= the viewer's sector, since the viewer is a
   peer there). The attribution radius derives from the native transit
   collision sphere `warp.Radius + shipRadius`
   (`Sector.cs:3249-3251`, `shipIGC.cpp:629-636`), padded (`max(Radius·2, 90)`)
   for the last-telemetry-pose lag.
2. **Own arrival** — `Apply(SetCluster)` for the own ship when the sector
   actually changed: we emerged beside the destination aleph → signal it.
3. **Other-ship arrival** — a ship that was out of view (`IsGone`) is
   re-sighted near an aleph in the viewer's sector → signal that arrival aleph
   (the task's "if the signal exists" arrival path).

The detection is deliberately in `ClientWorld` (pure, wire-fed) so it is
unit-testable headless: departure fires only for a warp in MY sector (a warp in
another sector never fires), docking/logger never fires, own arrival fires on
the destination aleph, re-sighting arrival fires.

### 2b. The pulse envelope — `Rendering/WarpPulseController.cs` (pure)

`AttackSeconds` 0.12 s linear attack → `FallSeconds` 0.78 s quadratic ease-out
(total **0.9 s**, inside the task's "0.6–1.0 s, ease-out"). Deterministic
eviction at `MaxSimultaneous = 8` (oldest-inserted dropped, newest always
retained) so simultaneous transits **stack sensibly**; a same-warp re-trigger
within `MinRepeatGapSeconds` (0.15 s) **coalesces** instead of flickering. Pure
and fully unit-tested.

### 2c. Rendering the pulse

- **Funnel brightens** — `WarpRenderer.Draw(..., float funnelBoost)` scales the
  funnel vertex colours up (clamped at 255/channel) before the additive draw
  (native additive blend `alephgeo.cpp:219` SRCALPHA/ONE). `1.0` idle = native
  look unchanged; peak `PeakFunnelBoost = 2.6`.
- **Glimpse opens wide** — `AlephGlimpseRenderer` mixes the disc radius
  `1.3× → 2.4×` and centre opacity `0.55·fade → 0.92` by the pulse strength, so
  the destination sky briefly opens toward/through the funnel mouth and reads
  almost fully before settling back.
- **Lens-flare burst** — additive soft halo + cross streaks at the projected
  funnel centre, drawn **only when** (a) the pulse strength ≥
  `PulseFlareThreshold`, (b) the centre projects inside the viewport, and (c)
  the P547 linear-depth target shows nothing nearer than the aleph at that
  pixel (the occlusion gate). Depth is sampled only when the depth pass actually
  ran this frame (`_softContext != null`); otherwise the flare is treated as
  unoccluded (no stale-depth reads). This is the P547-depth occlusion rule the
  task asks for.

**Pipeline hygiene:** render targets are only ever switched inside
`EnsureRendered` (before the composite); the glimpse + flare composite never
switches the target mid-frame (P582b lesson). All render states are pinned and
restored. Placement is camera-relative (P573/P576b lessons).

---

## 3 — Wiring (`Game1`)

- `Game1._warpPulses` — the single pulse controller.
- `DrainAlephTransitSignals(world)` — drains `ClientWorld` signals into the
  controller + native star swell each frame (under the gate), then prunes.
  **Opt-in:** both the drain and the sector-change arrival pulse are gated by
  `_alephGlimpse`, so with `--aleph-glimpse off` (the default) no pulse fires,
  `WarpPulseController` stays empty and the funnel-boost read-back is a
  constant 1.0 — the native funnel/star-swell look is byte-for-byte unchanged.
- `DetectTransitSwells` — the P562 arrival-swell detection additionally triggers
  the P585 arrival pulse (coalesced with the own SET_CLUSTER signal, same warp /
  same frame → no double flare).
- Warp draw loop passes `funnelBoost = 1 + (PeakFunnelBoost−1)·strength`.
- `DrawAlephGlimpses` passes the per-warp pulse strength + the occlusion test
  (`TransitFlareOccluded`) into `AlephGlimpseRenderer.Draw`.

---

## 4 — Tests (all failing-first: the classes/curves did not exist before)

- `WarpPulseControllerTests` (8) — envelope 0-at-edges/1-at-attack-knee/monotonic
  attack-then-ease-out (total in 0.6–1.0 s), trigger→strength, stacking cap
  (≤8, newest retained), same-warp coalesce, expiry prune, unrelated-warp=0.
- `AlephGlimpseTuningTests` (5) — approach fade full-at-430 / 0-at-3400,
  monotonic decline, visibility by the task's 2000/800/400 spot-checks, resting↔
  pulse mix, pulse-opens-wider-than-resting.
- `ClientWorldAlephTransitTests` (7) — departure fires for a warp in MY sector;
  NOT for a ship far from any warp; NOT for a warp in another sector; NOT for
  docking/logger; own arrival fires on the destination aleph; no sector change →
  no signal; re-sighting arrival fires.
- `SkyLaunchOptionDefaultsTests` — aleph glimpse **defaults OFF** (pre-existing).

All pass. Whole-solution Release build **0 warnings / 0 errors**.

---

## 5 — Proof (real window, P572 tooling, 2560×1440 backbuffer)

Ran `scripts/live-proof.sh` on the 221 map `m2xspiderw.igc` with
`--aleph-glimpse on --sky native --auto-goto Alderan --camera cockpit`; the
client auto-flew the Omega→Alderan hub aleph (#2153) so the **player's own
transit** crossed it under observation. All PNGs are real presented-backbuffer
SaveAsPng captures (2560×1440), not `--capture`/offscreen.

Assets in `docs/reports/P585_assets/proof/`:

- **Resting glimpse fade-in (Omega approach to aleph #2153; `[warp-diag]`
  camDist, all real):**
  - `glimpse_fadein_2200u_faint.png` — ~2220 u: glimpse largely faded out
    (the funnel reads as a faint dot; objective pixel core ≈ 6 px).
  - `glimpse_fadein_1380u.png` — ~1380 u: destination-sky disc emerging.
  - `glimpse_fadein_955u_visible.png` — ~955 u: clearly-visible destination glow
    in the funnel centre (the P576 "too faint to notice" complaint fixed).
  - `glimpse_close_pre_transit.png` (+ `_Zoom3x.png`) — nearest resting frame
    just before the crossing: strongest resting glimpse, **no** lens-flare
    streak.
- **Transit pulse — player's own transit:**
  - `transit_pulse_own_flare_omega.png` (+ `_Zoom3x.png`) — the moment the ship
    crosses #2153: the glimpse is opened bright and a clear **horizontal
    lens-flare streak** bursts through the funnel centre. The streak is
    measurably absent in the resting `glimpse_close_pre_transit_Zoom3x.png`
    (resting core ≈ 12 px vs the flare ≈ 44 px feature).
  - `transit_arrival_alderan.png` — arrival sector; the arrival aleph pulse
    fired (see log below) though the dense Alderan asteroid field occludes a
    clean flare frame (the P547 occlusion gate is demonstrably active there).
  - Live client log (objective, from the run):
    ```
    [transit-pulse] warp #2152 ARRIVAL pulse (sector change) @t=63.73
    [transit-pulse] warp #2152 transit signal @t=63.73
    ```
    proving the client-side signal detection fires on the player's own transit
    via BOTH the SET_CLUSTER drain and the sector-change arrival detection
    (coalesced, one pulse).

**Frame time / no hitch:** the client rendered the full 2560×1440 window
continuously through the crossing including 3+ simultaneously-active state
(player transit + the pulse) with no hitch observed and the suite's
`WarpPulseController` cap (8) bounding simultaneous pulses; the 3-pulse "several
bots" strip was not reproducibly framable this session due to the documented
auto-navigation limitation (`docs/LIVE_PROOF.md`, also cited in P576/P577/P589)
— the departure/arrival signal path it exercises is covered by the unit tests
and the live own-transit pulse above.

---

## 6 — Build & suite (3 runs on the final code, `scripts/run-tests.sh`)

Whole solution **0 warnings / 0 errors**.

Suite numbers (3 consecutive runs on the FINAL code; each reported as it
happened; no failing suite re-run). A render-only opt-in gating fix (the transit
pulse must be gated by `--aleph-glimpse on` so default-off leaves the native
funnel untouched) was folded in and the suite re-run after it. Every
ClientCore/Core failure is a documented fixed-sleep live-wire-receipt
load-flake in `docs/TEST_FLAKES.md`; the MonoGame assembly (all P585 unit tests,
incl. the 13 new ones) passed **1677/1677 in all three runs**, and the ClientCore
assembly passed the 7 new transit-signal tests every run.

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1   | 12/12 | 1677/1677 | 574/575 (1 flake) | 3840/3844 (4 flakes) |
| 2   | 12/12 | 1677/1677 | 574/575 (1 flake) | 3841/3844 (3 flakes) |
| 3   | 12/12 | 1677/1677 | 574/575 (1 flake) | 3843/3844 (1 flake) |

Named flakes (all in `docs/TEST_FLAKES.md`): run 1 — `ThreePeerSeededSession…
DeterministicVisibility` (line 38/51), `Transport.InboundFloodSafety…
Oversized_packet…` (line 55), `ShipsUpdateLive…leaving_cluster…` (line 245),
`LiveStationRescue…Lifepod_reaching_rescue_station…` (line 156),
`GameOverPlayersWire…Win_by_elimination…` (line 30/267). Run 2 —
`FullSessionKill…RealCrossSideKill` (line 33/148), `AutoReloadOnEmpty…
RELOAD129` (line 52), `LiveChatSectorWire…relay…` (line 216/419),
`GameOverPlayersWire…Win_by_elimination…` (line 30/267). Run 3 —
`FullSessionKill…RealCrossSideKill` (line 33/148),
`GameOverPlayersWire…Win_by_elimination…` (line 30/267).

---

## Commit

`master` (not pushed), one commit incl. every new file (code, renderers,
controller, tests, report, proof assets). Confirmed `git branch --show-current`
= master and a clean status for the task's files.
