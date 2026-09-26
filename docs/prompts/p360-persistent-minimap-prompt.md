# P360 — Persistent mini-starmap (open/closed states), not just a full-screen toggle

## Why this task exists

Domingo remembered the normal (always-on) HUD had a mini-starmap
embedded in it, not just an on-demand full-screen map. Confirmed
against native source: `~/Development/Allegiance/src/WinTrek/sectormap.cpp:140-168`
(`SectorInfoPane`) has a real, documented dual-mode design — *"The
minimap has an open and closed view. The selected background image
controls which view is shown"* (`sectormap.cpp:160`) — with THREE real
background images loaded: `consectorinfobmp` (small/closed),
`conesectorinfobmp` (large/open), and `consectorexpandbmp` (the
expand-toggle button graphic), `sectormap.cpp:156-158`. This is a
single, persistent HUD widget that starts small and expands on
request — not a hidden-until-toggled full-screen overlay.

**This port likely has related pieces already, but probably not this
exact behavior — investigate before assuming a gap or duplicating
work**: this port has BOTH `StarmapLayout.cs`/`StarmapRenderer.cs`
(a full ~80%-viewport-width panel, hidden until the `starmap` key
toggles it — confirmed via `Game1.cs`'s `_starmapVisible`/
`DrawStarmap()`) AND `SectorInfoPaneLayout.cs`/
`SectorInfoPaneRenderer.cs` (already exists, presumably covers some of
native's `SectorInfoPane` class). It's possible native's ONE dual-mode
widget got split into these two SEPARATE port constructs during
earlier work, with neither one covering the "small, ALWAYS visible,
expandable" behavior Domingo actually remembers. Read both existing
port files' real behavior first — don't assume they're wrong or right,
confirm.

## Scope

1. **Investigate first** (this project's standing discipline): read
   `sectormap.cpp`'s full `SectorInfoPane` class (the excerpt above
   stops at line 168 — read further for the actual open/closed toggle
   logic, `HitTest`'s button-click handling starting at line 189, and
   whatever draws the small/closed view's actual mini-map content) to
   understand exactly what native's "closed" (small) state shows vs.
   its "open" (large) state, and confirm precisely which of this
   port's TWO existing related files (`StarmapLayout`/`StarmapRenderer`
   vs `SectorInfoPaneLayout`/`SectorInfoPaneRenderer`) already covers
   which native behavior, and what's genuinely missing (the persistent
   always-on "closed" mini state, most likely, based on what's known
   so far — confirm this, don't just assume it).
2. **Build the missing piece**: a persistent, always-visible small
   mini-map HUD widget (native's "closed" view) that's ALWAYS on
   screen during normal flight (not hidden until a key toggle, unlike
   this port's current full starmap panel), showing at minimum the
   player's current sector and nearby connected sectors/warps at a
   glance — reuse `StarmapLayout`'s existing real sector-position/
   warp-edge math and rendering primitives, don't rewrite that from
   scratch, just add a compact rendering mode and persistent visibility
   for it.
3. **Wire up the expand/collapse behavior**: a way to expand the small
   persistent mini-map into the existing full starmap view (or
   something equivalently useful) and back — matching native's real
   expand-button concept (`consectorexpandbmp`), though the exact input
   binding (mouse click vs. a key) is your call, whichever fits this
   port's existing HUD interaction conventions better.

## Explicitly out of scope

- P354/P355/P356/P357/P358/P359 — unrelated, already landed or in
  flight, don't touch (this task is queued behind P359, only start
  once it's confirmed complete on `master`).
- Any change to the underlying `ClusterGraph`/warp-edge data or
  networking — this is a pure rendering/UI addition using data this
  port's starmap already has.
- The target-camera/bearing-vector work — that's P359's scope, not
  this one, even though both came from the same research session.

## Verification — real proof, not just build/test

- **Live proof, required**: launch a real client against a real
  server, confirm the mini-map is visible DURING NORMAL FLIGHT without
  pressing any toggle key (the actual gap being fixed), confirm it
  expands to a larger/full view on request and collapses back, and
  confirm its content (current sector, nearby connections) is
  accurate against the real `ClusterGraph` data for whatever map is
  loaded.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The persistent mini-map + expand/collapse behavior, committed on
   `master` (not pushed — Domingo pushes). Do not create a side
   branch.
2. Report doc `P360_PERSISTENT_MINIMAP.md` in the repo root: what
   native's real open/closed behavior turned out to be (confirm or
   correct the summary above), which existing port file(s) already
   covered what, what was actually built, and the live verification
   performed.
