# P270 — Cloaking visual effect (task 2 of 2: the client-visible effect)

Committed on `master`, not pushed (Domingo pushes). Whole-solution build
0 warn / 0 err; Core.Tests 3125/3126 twice (sole failure both runs is the
pre-existing `LiveTurretGunnerBindingTests` flake), MonoGame.Tests 371,
ClientCore.Tests 123.

## 1. Native's real rendering: NONE EXISTS — built as original UX (honestly)

I searched `WinTrek/`, `thinglib/`, `trek3d/`, `thinggeo/` for any alpha /
shimmer / transparency effect tied to `CcloakIGC` / `GetCloaking()` state:

- The ONLY client consumers of cloak state are the **loadout/console pane**
  (`WinTrek/consoledata.cpp`):
  - `ModelData::GetCloaking()` — the % cloaking for the loadout screen
    (`consoledata.cpp:480-488`, `f = 100.0f * (1.0f - pship->GetCloaking())`).
  - `ModelData::IsCloaked()` — a 0/1 flag (`consoledata.cpp:565-573`).
  - `PartWrapper::GetCloakTimeLeft()` — a loadout-only duration (`consoledata.cpp:967-984`).
- There is **no 3D-viewport rendering** for a cloaked ship anywhere in the
  reference client: no `SetOpacity`/alpha/blend-factor on the model renderers,
  no shimmer/shader keyed on cloaking. (The input side is present —
  `TK_ToggleCloak` at `WinTrek/WinTrek.cpp:9468-9471`, training overlay
  "PUSH G TO TOGGLE CLOAK" at `indicator.cpp:694-699` / `training/mission4.cpp:719-722` —
  but no visual payoff of it.)

So, matching this project's precedent for P245 (the warp-transit flash, also
honestly built as new UX when no native visual existed to port), **P270 builds
an original, clearly-labelled cloak effect**. The data-level invisibility,
however, is NOT original — it is native's real mechanic (see §3).

## 2. The effect chosen: real-mesh translucency + subtle shimmer

- **Translucency** (`ShipRenderer`, `Rendering/ShipRenderer.cs`): the ship's
  actual model is alpha-blended at `CloakVisual.Opacity(t)`. When `opacity < 1`
  the pass runs under `BlendState.AlphaBlend` with `BasicEffect.Alpha` set, and
  the prior blend state is restored afterwards so the opaque passes that follow
  are untouched. This is a genuine partial transparency of the real mesh, so a
  teammate can still clearly tell the cloaked ally is there.
- **Shimmer** (`Flight/CloakVisual.cs`, a pure MonoGame-free helper): a slow
  ~0.8 Hz ripple about a 0.32 base opacity (amplitude 0.14, clamped to
  [0.06, 0.8]) so the cloak reads as a living ripple, never a static glass
  shell and never blinking the ally fully out.
- **Driver** (`Game1.cs`): the client knows a ship is cloaked purely from the
  wire — the `CloakActive` stateM bit (0x800, P269) that already rides every
  `ServerShipUpdate`/`SingleShipUpdate`. Ships whose received `StateM` carries
  it are collected into a per-frame cloaked set and drawn at the shimmering
  opacity. No new wire message.
- **HUD indicator** (requirement 4, included): `HudFrame.Cloaked` (from the
  local ship's own `CloakActive` bit) drives a small violet "CLOAKED" tag under
  the weapon readout — tells the local pilot their OWN hull is cloaked (which
  otherwise has no on-screen cue in cockpit view).

## 3. Respecting P269's detection mechanic — the enemy gets NO data, not a render trick

This is the scoping crux. P269 made cloaking a real detection mechanic:
`GetSignature() = baseSignature·GetCloaking()` (`shipIGC.h:268-270`) feeds the
per-object `InScannerRange` margin (`shipIGC.h:353`), so a fully-cloaked ship
collapses its own detection radius toward zero. The P269 sweep
(`Sector.ResolveCrossSideVisibility`) already flips `SeenBySide` for every
side using the cloaked `EffectiveSignature`.

But native's server transmits every cluster ship to every flying player
(`SendShipUpdates`, `FedSrv.CPP:4150-4165`) and the REAL client culls visibility
in its own local IGC sim. **This port's MonoGame client is a PURE renderer with
no client-side sim to cull** — so the server must cull on its behalf, or the
cloaked enemy's data would still reach the enemy client as a transparent ship
they could see.

`Server.BroadcastShipsUpdate` now gates each peer's heavy-update payload:

    bool peerSees(Ship c) => !c.IsCloaked || self.SideId is not { } sd || c.SeenBySide(sd);

- A **non-cloaked** ship is unchanged (always sent) — broadening cross-side
  fog-of-war to all ships is deliberately OUT of P270's scope.
- A **cloaked teammate** (own side) is always seen (`SeenBySide` own-side rule)
  → it still reaches its side, carrying the `CloakActive` bit so the client
  renders it translucent.
- A **cloaked enemy** the side cannot detect (`SeenBySide == false`, beyond the
  collapsed radius) → **dropped from the peer's update entirely: no position,
  no state, no data at all.** An enemy close enough to genuinely detect it
  (inside the collapsed radius) gets it back — and renders it translucent,
  since the `CloakActive` bit is on the entry.

Same-side-only paths already existed for the rest (SHIP_STATUS/EXPORT/PLAYERINFO/
teammate-position are same-side by construction), so this one gate closes the
only leak path for the MonoGame client.

## 4. Verification

### Data-level (automated, in-suite) — `CloakVisibilityGateLiveTests`
Two live loopback tests over a real in-process server + real seated bot peers
(faohshcar scanner ↔ faohshcar cloaked target, 2000 u apart — inside the 5700 u
uncloaked detection radius, beyond the ~332 u collapsed radius):
1. `Cloaked_enemy_receives_no_data_while_teammate_still_sees_it_transparent` —
   uncloaked baseline: enemy receives the ship; cloaked: the enemy peer's
   captured HEAVY updates contain **zero** entries for it, while the teammate's
   still contain it **with the CloakActive bit**.
2. `Detectable_enemy_still_receives_the_cloaked_ship_inside_the_collapsed_radius` —
   enemy moved to within the collapsed radius flips `SeenBySide` back on and the
   enemy receives it again (with the cloak bit) — proving the gate is the real
   detection mechanic, not "never send cloaked ships to enemies".
   Both **must-fail-if-removed**: neutralizing the gate makes both red.

### Visual (offscreen real-renderer) — teammate view, end to end
An out-of-repo harness (`/tmp/p270live`) boots a REAL in-process server with two
real `ConnectionSession`s seated on the same side; the second peer carries a
real mounted cloak + `CloakActive` bit and cruises. The viewer's real client
world **receives the cloaked teammate at its real position with the cloak bit
set** (`world.Ships[8097].Latest.StateM & CloakActive == true`), and that same
received ship is rendered through the REAL `ShipRenderer` + real `CloakVisual`
opacity to offscreen PNGs:
- `viewer_opaque.png` — solid, crisp-lit ship.
- `viewer_cloaked_t0.png` / `viewer_cloaked_t6.png` — the SAME ship faded/
  ghostly/translucent (vision-confirmed; numeric: ship-region luma 66 → 41
  toward the dark background; shimmer diff between the two cloak frames > 0,
  proving the time-modulated alpha). Still recognisably a ship — a teammate can
  tell the cloaked ally is there.
(A second harness, `/tmp/p270verify`, renders the effect over a stark green
backdrop quantifying see-through: cloaked ship carries 66% background green
bleed and luma 38 → 92, 44–51% faded toward the backdrop.)

## 5. Scope boundary / exclusions
- No change to P269's sim mechanic (`Ship.Cloaking`/`EffectiveSignature`/
  `UpdateCloak` untouched) — P270 only renders it and gates delivery.
- Native reference source (`~/Development/Allegiance`) untouched (read-only).
- Broadening cross-side fog-of-war to ALL ships (not just cloaked) is out of
  scope and deliberately left as-is.

## 6. Deliverables summary
- `Server.cs` — P270 broadcast gate (data-level cloak invisibility).
- `Rendering/ShipRenderer.cs` — alpha-blend translucency path.
- `Flight/CloakVisual.cs` (new) — pure opacity/shimmer helper.
- `Game1.cs` — cloaked-ship collection + shimmering draw; `HudFrame.Cloaked`.
- `Rendering/HudRenderer.cs` + `HudFrame.cs` — "CLOAKED" HUD tag.
- Tests: `CloakVisualTests` (5, MonoGame) + `CloakVisibilityGateLiveTests` (2,
  Core) — both must-fail-if-removed for the gate.
