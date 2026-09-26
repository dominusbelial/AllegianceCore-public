# P359 — Target info readout: pilot name + health bar + bearing vector on the selection bracket

## Why this task exists

Domingo remembered a real native HUD feature, confirmed by re-watching
actual native gameplay footage this session: targeting any object
shows a bounding reticle, and when the target is a ship, it displays
the pilot's name with a small health bar below it. Confirmed against
native source: `TK_TargetCenter` ("target the ship closest to the
reticle", `~/Development/Allegiance/src/WinTrek/WinTrek.cpp:10127`)
plus native's data-driven HUD gauge system
(`GaugeValuePane`/`GaugePaneFactory`,
`~/Development/Allegiance/src/WinTrek/trekmdl.cpp:196-430`).

**This port has no such readout at all.** The only targeting concept
that exists is P212's click-and-go autopilot target (`T`/`G` keys),
visualized by P345's bracket
(`TargetSelectionView` — `Rendering/TargetSelectionView.cs:19`, just
`Center`/`Engaged`, no name or health data at all).

**This is purely a rendering addition — no new wire data needed.**
Every field required is already decoded client-side for any visible
ship (not just the local player's own):
- `ShipEntity.CharacterName` (`Allegiance.ClientCore/State/ShipEntity.cs:88`).
- `ServerShipUpdate.HullFraction` / `.ShieldFraction`
  (`Allegiance.Core/Wire/ServerShipUpdate.cs:73,75`) — the exact same
  fields the player's OWN hull/shield gauges already read.

**Confirmed multiplayer behavior, already true for free**: because
`HullFraction`/`ShieldFraction` are broadcast the same way to every
peer who can see a given ship (not a special "attacker-only" channel),
this feature will automatically let every player who targets the same
large ship watch its health drop live and simultaneously — exactly
what Domingo remembered — with zero extra synchronization work needed.
Don't build anything special for this, it falls out of using the
existing per-ship broadcast data as-is.

**Also add a target bearing vector** (a second, related memory
Domingo confirmed this same session). Native has a genuine dedicated
`targetCamera` — a real second `Camera` object that rendered a
picture-in-picture view of the target
(`~/Development/Allegiance/src/WinTrek/WinTrek.cpp:2939-2954`,
`m_cameraControl.m_pcameraTarget`, registered in the game panes system
as `"targetCamera"`). **Domingo explicitly scoped this DOWN**: don't
build a second camera/render pass — instead show a simple directional
indicator (an arrow/vector, or however cleanly this reads as a small
HUD element) pointing toward the target's real relative bearing from
the player's own ship, reusing the same target-position data this task
already resolves for the bracket (`ResolveTargetPosition`,
`Game1.cs:4874`). This should work even when the target is off-screen/
out of the view frustum (arguably MORE useful there — a bearing
indicator's whole point is knowing which way to turn when you can't
see the target directly), unlike the bracket itself which only shows
when the target projects into view. Simple, cheap, no second render
target needed — just real vector math (target position − own
position, projected into screen-relative or HUD-relative space) and a
small arrow/icon draw.

## Real precedent to reuse — don't invent new drawing code

`HudRenderer.cs` already has exactly the primitives this needs, used
today for the player's OWN hull/shield gauges:
- `DrawBar(float fraction, float x, float y, float w, float h)`
  (`HudRenderer.cs:249`) — the generic fraction-bar primitive.
- `DrawGauge(GaugeKind kind, float fraction, Vector2 anchor, float
  gaugeScale)` (`HudRenderer.cs:318`) and the `("HULL",
  f.HullFraction)`/`("SHIELD", f.ShieldFraction)` pattern
  (`HudRenderer.cs:221-222,295-296`) — the exact same two fractions
  this task needs, just for the TARGET instead of the local player.
Reuse `DrawBar` directly for the target readout — don't write a new
bar-drawing routine.

## Scope

1. Extend `TargetSelectionView` (or add a small sibling record/struct
   next to it — your call, whichever is cleaner) to carry the target's
   `CharacterName`, `HullFraction`, and `ShieldFraction` when the
   target is a ship (a station/asteroid/warp target has no pilot name
   or hull/shield fractions — the readout should simply not show for
   those, matching native's own "if it is a ship" gate from Domingo's
   memory).
2. In `Game1.cs`'s `ComputeTargetSelectionBracket`
   (`Game1.cs:4858-4878`), when the resolved target is a ship, look up
   its live `ShipEntity` (the same `world.Ships` dictionary this
   codebase already reads everywhere else) and populate the new
   name/fraction fields.
3. Render the readout near the existing bracket position: pilot name
   as text (reuse whatever HUD font/text-drawing convention
   `HudRenderer.cs` already establishes elsewhere, e.g. its existing
   `SpriteFont` usage — don't introduce a new font), a small health
   bar below it using `DrawBar` (hull fraction at minimum; shield too
   if it reads cleanly at this small size — your call on layout, this
   is a new small HUD element with no existing pixel-perfect spec to
   match beyond "name, then a small health bar below it").
4. Make sure it works for **any** targeted ship the client can see,
   not just ones on the player's own side (the client already only
   ever knows about ships the server actually exported to it — same
   trust boundary every other HUD element already relies on, nothing
   new to gate here).
5. Add the bearing-vector indicator (see above) — this one should work
   for ANY targetable object kind (ship/station/asteroid/warp), not
   just ships, since it's pure directional math with no pilot-specific
   data involved; keep it visible whenever a target is selected,
   whether or not the target itself is currently on-screen.

## Explicitly out of scope

- Any change to the actual targeting/selection logic itself (P212's
  `T`/`G` autopilot-target system, `_autoTarget`) — this task only
  adds a readout to what's already selected, it doesn't change how
  targets get chosen.
- A separate "weapons target" system distinct from the autopilot
  target — native's real combat-targeting reticle may be a genuinely
  separate concept from this port's click-and-go autopilot target, but
  building that distinction is a bigger, separate task; this task
  reuses the existing P345 target concept as-is.
- P354/P355/P356/P357/P358 — unrelated, already landed or in flight,
  don't touch.
- Station/asteroid/warp target readouts — no pilot name or hull/shield
  fraction exists for those object kinds, they keep the existing
  bracket-only behavior.

## Verification — real proof, not just build/test

- **Live proof, required**: real client against a real server (2+ real
  ships, e.g. `vizfight proof` or `stress` mode, or the standalone
  server), target a real ship, confirm the pilot name and a real,
  live-updating health bar render near the bracket. Damage the
  targeted ship (real combat or the collision-physics pass) and
  confirm the bar visibly drops in real time.
- Confirm a station/asteroid/warp target shows the bracket with NO
  name/health readout (no crash, no bogus empty bar), but DOES still
  show the bearing vector (that part applies to all target kinds).
- Confirm the bearing vector actually points the right direction when
  the target is behind/beside the camera (off-screen) — turn away from
  a real target and verify the indicator still reads correctly, not
  just when the target happens to already be in view.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The target info readout, committed on `master` (not pushed —
   Domingo pushes). Do not create a side branch.
2. Report doc `P359_TARGET_INFO_READOUT.md` in the repo root: what was
   built, the live verification performed (describe what you saw),
   and confirmation the non-ship-target case degrades cleanly.
