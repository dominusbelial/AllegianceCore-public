# P463b — shield-hit flare (the shield bubble flash)

Part 2 of the three-way hit-feedback split. **P463 must have landed
first.** Check `git log --oneline | grep P463` and confirm the
shield-absorbed hit signal exists. If it doesn't, stop and report.

## Why this task exists

This came out of the 2026-09-29 gameplay-parity pass. In native, every
shield-absorbed hit draws a flare on the struck ship's shield:
`m_ship->GetThingSite()->AddFlare(timeCollision, deltaP, 0,
m_ship->GetHitTest()->GetEllipseEquation())`
(`~/Development/Allegiance/src/Igc/shieldIGC.cpp:170-171`). The only
exception is a `c_dmgidNoFlare` damage type. The call is gated on the
shield still being above the downed threshold. The client renders it
through `ThingGeo::AddFlare` (`WinTrek/trekigc.cpp:1624-1640` →
`effect/thinggeo.cpp`, find the flare implementation there). It is the
classic "shield bubble lights up where you hit it" cue.

The port has no flare or shield visual anywhere in
`src/Allegiance.ClientCore.MonoGame`. P463 added the wire signal for
shield-absorbed hits; this task draws the flare.

## Scope

1. Read native's flare implementation in `effect/thinggeo.cpp`. It
   covers:
   - the ellipsoid it's drawn on (the hull hit-test ellipse)
   - its texture or colour, if one is loaded, checked against
     `~/Development/Artwork`
   - its lifetime and fade
   - how the hit direction places it

   Document it with file:line in the report.
2. Implement a `ShieldFlareRenderer` (pure piece plus renderer, following
   the `HullHitSparkParticles`/`HullHitSparkRenderer` split). On each
   shield-absorbed hit signal, draw a flare on an ellipsoid around the
   struck ship. Size it from the ship's real render scale (P333
   `_hullLengthById`), place it by the hit direction, and use native's
   lifetime and fade.
3. It must be visible on our own ship in chase view and on other ships.
   Only draw it in our own sector, using the same cluster gate as the
   spark renderer.

## Explicitly out of scope

- The sounds and the damage flash (P463) and collision sounds (P463c).
- Any server or wire change. If the P463 signal lacks something the flare
  needs, such as a direction, stop and report rather than changing the
  wire here.
- The P364 hull spark. Leave it untouched.

## Verification

- Pure unit tests for placement, size and lifetime math. They **fail
  first**: the type doesn't exist yet, so write the test against the
  intended API and record the failing run.
- A live screenshot or capture: shoot a shielded ship and see the flare,
  and see it on our own ship when hit.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
  `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Committed on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P463B_SHIELD_HIT_FLARE.md`: the native flare
   details, the implementation and the live evidence.
