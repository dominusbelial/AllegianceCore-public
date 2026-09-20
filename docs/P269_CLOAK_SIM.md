# P269 — Cloaking sim mechanic (task 1 of 2: server-side only)

Committed on `master`, not pushed (Domingo pushes). Build 0 warn / 0 err
whole solution; Core.Tests 3123/3124 twice — the sole failure is the
pre-existing `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
(documented flake, confirmed reproducing identically on pristine master).

## 1. Native's real mechanic (cited)

Read `Igc/cloakigc.h` and `Igc/cloakigc.cpp` in full (read-only reference).

- **Activation is a held/toggled state bit, not a discrete event.** The cloak
  is a mounted part that only ever acts from mount 0
  (`CcloakIGC::fActive() = (m_mountID == 0)`, `cloakigc.h:66-69`; `Update`
  asserts `m_mountID == 0`, `cloakigc.cpp:62`). Each tick it tests
  `bActive = (m_ship->GetStateM() & cloakActiveIGC) && (m_mountedFraction >= 1.0)`
  (`cloakigc.cpp:80-81`), where `cloakActiveIGC = 512·coastButtonIGC = 2048 =
  0x800` (`igc.h:820`) is a LOW-WORD button bit — so it rides the compact
  `stateM` on the wire (inside `buttonsMaskIGC = 0x3FFC`). If `bActive`, it
  calls `Activate()` (`cloakigc.h:71-82`), which adds the cloak part's
  signature to the ship's base (`m_ship->ChangeSignature(+signature)`), marks
  the emitter active, and starts the ramp at 0.
- **Fit-to-cloak gates.** `CshipIGC::SetStateM` refuses `cloakActiveIGC` while
  docked, boarded (`m_pshipParent != NULL`), or ripped
  (`shipIGC.cpp:1595-1601`), and docking clears the bit outright
  (`shipIGC.h:1839-1840`).
- **Energy cost while active.** Each active tick costs
  `energyConsumption·dt` (`cloakigc.cpp:89-90`); a pool shorter than needed
  throttles the cloak's target strength by `r²` (`r = energy/needed`,
  `cloakigc.cpp:96-102`) and floors the pool at 0.
- **What `MaxCloaking` controls.** It is the ceiling the cloak ramps *toward*
  (0 → MaxCloaking) at `onRate` while requested, decaying back at `offRate`
  (`cloakigc.cpp:105-116`), and the ship's visible fraction is published as
  `SetCloaking(1 − m_cloaking)` (`cloakigc.cpp:121`). It deactivates (restores
  full visibility, releases the emitter) once `!bActive && m_cloaking == 0`
  (`cloakigc.cpp:118-119`).

### The detection interaction — the scoping crux

Native cloaking **is** a real detection mechanic, not a rendering-only
transparency:

- The ship's effective signature is `GetSignature() = baseSignature ·
  GetCloaking()` (`shipIGC.h:268-270`) — the visible fraction multiplies the
  whole base.
- The per-object detection predicate `CshipIGC::InScannerRange`
  (`shipIGC.h:341-364`) computes its margin as `m = scannerRange ·
  target.GetSignature()` (`shipIGC.h:353`), so a cloaked target (small
  `GetCloaking()`) shrinks its own detection radius toward zero — **a cloaked
  ship becomes undetectable even inside normal detection range**.

Crucially for scope: this is the **object-level** scanner-range mechanic — the
same `InScannerRange` family P259/P260 sit on (this port's `VisibilityMath`
+ `Sector.ResolveCrossSideVisibility`). It is **not** P259's strategic
cluster-level `Side.CanSeeCluster` presence flag: a cloaked ship still occupies
its cluster and can still contribute cluster presence; cloaking only shrinks the
per-object detection radius that determines whether an *enemy scanner* sees it.
So P269 extends the existing `VisibilityMath`/`ResolveCrossSideVisibility`
surface (via the ship's effective signature) rather than touching
`Side.CanSeeCluster` — and P270 (rendering) will be driven by the same
`CloakActive` state bit that already rides `stateM`, plus `Ship.Cloaking`.

## 2. What was built

- `ShipControlState.CloakActive = 0x800` (`ShipControlState.cs`) — the
  `cloakActiveIGC` bit, placed between `AfterburnerButton` (0x400) and
  `MiningMask` (0x2000), consistent with those carrying native values.
- `Ship` cloak sim (`Ship.cs`):
  - `Cloaking` (visible fraction, native `m_cloaking`; starts 1.0) and
    `IsCloaked` (emitter engaged).
  - `EffectiveSignature` = `(HullType.Signature + cloakPart.SignatureWhileActive) *
    Cloaking` — the port of `GetSignature() = base·GetCloaking()`, including the
    cloak's own ±signature contribution (native `ChangeSignature`, cloakigc.h:76/89).
  - `UpdateCloak(dt)` — the port of `CcloakIGC::Update`, driven from `OnUpdate`
    (native drives it from `CshipIGC::Update`'s parts' Update). Activation from
    the `CloakActive` bit gated on not-docked/not-boarded/not-ripped; energy
    drain + r² throttle; onRate/offRate ramp; emitter release at zero.
  - `_mountedCloak` — the `ET_Cloak@0` part (mount-0 only, native `fActive()`).
  - `MountPart`'s "Cloak is inert bookkeeping" doc corrected (it is now realised
    in-sim; Shield/Afterburner/Pack remain inert).
- `Sector.ResolveCrossSideVisibility` now feeds ships as
  `s.EffectiveSignature` (was `s.HullType.Signature`) — a cloaked ship's shrunk
  signature flips its `SeenBySide` for enemy sides.

## 3. Broadcasting to peers

Faithful to native, no new wire message: `CloakActive` (0x800) is a LOW-WORD
bit, so it rides the existing compact `stateM` written verbatim into every
`ServerShipUpdate`/ship-status broadcast (`ServerShipUpdate.FromModel`,
byte 2-3) — receivers learn the ship is cloaked from its state bit (the surface
P270's rendering will key off), and each side's detection is the server's own
`ResolveCrossSideVisibility` sweep using the cloaked `EffectiveSignature`
(`Their own detection logic`, not rendering — exactly the scope split). Energy
draw is visible on the wire `EnergyFraction` byte; no client rendering was
added (P270).

## 4. Tests (7 new — `CloakSimTests`, must-fail proven)

Against real corpus hulls (faohshcar scanner) in a real started mission:
- `Setting_the_cloak_bit_engages_the_cloak_on_a_mounted_cloak` — activation
  via the `CloakActive` control bit; Cloaking < 1.0.
- `Without_the_bit_or_without_a_cloak_the_ship_never_cloaks` — bit without
  cloak / cloak without bit both stay uncloaked.
- `A_cloak_on_a_non_zero_mount_never_engages` — mount-0 only.
- `Cloaking_drains_energy_each_active_tick_and_not_when_idle` — exact
  `EnergyConsumption·dt` drain.
- `A_short_energy_pool_throttles_the_cloaks_target_strength` — the r² throttle
  (starved pool keeps Cloaking ≫ the full floor).
- `A_cloaked_enemy_becomes_undetectable_within_normal_detection_range` — live
  `SeenBySide` flip: uncloaked 2000 u (inside the 5700 radius) seen → cloaked
  (collapsed ~332 radius) unseen → decloaked seen again. This is the
  discriminating detection proof.
- `Decloaking_restores_full_visibility_within_one_ramp_period` — offRate ramp
  back to 1.0.

Must-fail proven twice: (a) reverting the sector sweep to
`s.HullType.Signature` makes the detection test red; (b) disabling the
`UpdateCloak` hook makes 5 of 7 red. Both reverted; all green.

## 5. Build / test status

- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
- Core.Tests: **3123/3124**, two consecutive full runs, identical. The single
  failure is the pre-existing `LiveTurretGunnerBindingTests` flake — confirmed
  reproducing on pristine `master` (unchanged by this stage).
- Related visibility suites (CrossSideVisibility sweep/foundation, Probe,
  Side cluster) all green alongside the new cloak tests (37/37 combined).
