# P363 — Ship side-colored motion trail (a real, distinct native effect, not engine exhaust)

## Why this task exists

Domingo remembered ALL ships leaving a long, bluish trail as they
move. Investigated against native source this session and confirmed
this is a **third, genuinely distinct** visual system from the two
already found tonight (native `AddDamage`'s damage-vent smoke, already
faithfully ported as `DamageDebrisParticles.cs`; and native
`AddThrustParticles`'s engine fire/smoke, being built right now as
P358, queued ahead of this task — confirmed P358 uses orange fire +
gray smoke, NOT blue, so it does not cover this memory).

**The real mechanism**: `SetTrailColor`
(`~/Development/Allegiance/src/effect/thinggeo.cpp:921-954`) builds a
genuine continuous **ribbon/strip geometry trail** (`CreateTrailGeo`,
declared `~/Development/Allegiance/src/effect/efgeo.h:24`, implemented
in `efgeo.cpp` — read its real algorithm, this prompt doesn't
reproduce it) anchored at the model's `"trail"`-named hardpoint frame
(`thinggeo.cpp:935`, `data.m_strName == "trail"` — the SAME hardpoint
name already confirmed present on real ship `.mdl` files this session,
e.g. `quizfigv2.mdl`'s `thrust`/`trail`/`smoke` hardpoints — this is a
DIFFERENT hardpoint than the ones P358 uses for exhaust). This is NOT
a particle-puff system like the other two — it's real strip geometry
built from a rolling history of recent anchor positions, updated every
frame (`UpdateTrailPosition`, `thinggeo.cpp:1151-1159`,
`SetPosition`/`SetOrientation`, `thinggeo.cpp:1161-1183`), only active
while the ship is actually moving (`m_pbooleanMoving`, set from
`m_vecPosition != vec` on each position update).

**The color is the ship's real SIDE/TEAM color**, confirmed via both
real call sites:
- `~/Development/Allegiance/src/Igc/shipIGC.cpp:149`:
  `pThingSite->SetTrailColor(pside->GetColor());`
- `~/Development/Allegiance/src/WinTrek/trekigc.cpp:4419`:
  `ship->GetThingSite()->SetTrailColor(ship->GetSide()->GetColor());`

Applied at HALF intensity (`thinggeo.cpp:941`: `color.R()*0.5f` etc.)
before being handed to `CreateTrailGeo`. This explains every part of
Domingo's memory at once: "bluish" (whichever side/faction he usually
played had a blue team color), "all ships" (not thrust-gated at all,
purely motion-gated), "very long" (real continuous ribbon geometry
tracking real position history, not discrete particle puffs that fade
individually).

**This port has zero equivalent** — confirmed only the two
particle-puff systems exist (damage-vent, and P358's in-flight engine
exhaust); no ribbon/strip trail geometry system at all.

**Real precedent already in this port for the color decode**: 
`GameOver.cs` already decodes a real `pside->GetColor()`-sourced RGBA
`Color` from the wire for the endgame screen (`GameOver.cs:15-90`,
citing the exact same native call this task needs,
`trekigc.cpp:5438`). Reuse/generalize that decode pattern rather than
inventing a new one — it's currently endgame-only, this task needs the
same side-color concept available for any live ship in real time.

## Scope

1. **Read `efgeo.cpp`'s real `CreateTrailGeo` implementation** before
   writing any port code — understand exactly how it builds the strip
   mesh from a rolling position history (how many segments, how the
   ribbon width/taper works, whether it fades/shrinks toward the tail
   or is uniform) so the port is a faithful reproduction, not a
   guessed approximation.
2. **Determine where the real side-color data would need to come
   from for a general (not just endgame) live ship** — likely a real
   per-side color already exists somewhere in the static core/mission
   data this port already decodes (check `DataConstantsIGC`-style or
   `SideSpec`/civilization data for an existing but unused color
   field, following the same "check what's already decoded before
   assuming new wire work is needed" discipline every other task
   tonight has used) — or, if it genuinely isn't available client-side
   yet, that's real new wire work, scope it honestly rather than
   faking a hardcoded color.
3. **Build the trail rendering**, anchored at each visible ship's real
   `"trail"` hardpoint position if that data is available (the same
   category of question P358 had to resolve for its own hardpoint
   positions — check what P358 landed on and reuse the same approach
   here for consistency, whether that's real per-hull hardpoint
   extraction or an aft-point approximation), tinted by the resolved
   side color at the native's real half-intensity, visible only while
   the ship is actually in motion (matching native's `m_pbooleanMoving`
   gate).
4. Confirm this needs to render for every visible ship the client
   knows about, not just the local player's own — same pattern as
   P358's engine trail and the damage-vent smoke, both already
   confirmed working for remote ships too.

## Explicitly out of scope

- Engine exhaust fire/smoke particles — that's P358, already landed
  or in flight by the time this task starts (queued behind it).
  Don't touch `ShipEngineTrailParticles.cs`/`ShipEngineTrailRenderer.cs`.
- Damage-vent smoke — already correctly implemented
  (`DamageDebrisParticles.cs`), don't touch.
- The target readout/bearing vector (P359), persistent mini-map
  (P360), auto-reload (P361), weapon status HUD (P362) — unrelated,
  don't touch.
- P354-P357 — unrelated, already landed, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real client against a real server, fly a
  ship, confirm a real, continuous, side-colored ribbon trail renders
  behind it while moving and correctly stops extending (or fades)
  when the ship stops. Confirm it renders for a REMOTE ship too, with
  that ship's own real side color (test with ships on two different
  sides if possible, to confirm the color genuinely varies by side,
  not hardcoded).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The ship side-colored trail effect, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P363_SHIP_SIDE_COLOR_TRAIL.md` in the repo root: the
   real native ribbon-geometry algorithm as actually read from
   `efgeo.cpp`, where the real side-color data came from, the hardpoint-
   position approach chosen (and why, consistent with or diverging
   from P358's own choice), and the live multi-side verification
   performed.
