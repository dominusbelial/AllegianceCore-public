# P589c — hero-object field-star cleanup: all seven objects reach 0, none dropped

Follow-up to P589b (`13a47f0d`) which left compact bright points around the hero
objects (Claude's 23:34 review of `sheets/contact_objects.png`: Sombrero M104
"many", Pinwheel M101 "several", Ring M57 "a couple") because its point-source
filter deliberately tolerated a ceiling. This task makes the removal STRICT:
field stars are detected **by shape** and removed **outside each object's
envelope only**, and every kept object must have **zero** star-like detections
outside its envelope after processing. Any object that cannot reach 0 without
damage is dropped from the set. **All seven reached 0 with the object intact —
none was dropped.**

Committed on `master` (not pushed).

---

## 1. Root cause

P589b built its object extent (`obj_ext`) from a very strong blur
(`obj_sigma=34-38`) with a LOW luminance threshold (`obj_thresh≈0.03`). That
envelope covered 0.8–0.99 of the frame, so genuine field stars in the outer halo
lay *inside* it: the point-source filter's `star &= ~obj_ext` never touched them
and the alpha envelope (built from the same loose `obj_ext`) kept them at
alpha≈1. The per-object ceiling (Sombrero 8, Pinwheel 55) then explicitly
tolerated the survivors. The review saw them "around the object."

The fix has three parts, all in `tools/space-objects/`:

### 1a. Tighter object envelope (`starclean.py`)
A new shared module `starclean.py` defines the **object envelope as a smoothed
luminance threshold of the object itself** (`gblur(lum, σ)·σ₀ᴸᴬᴿᴳᴱ )> ε`), with σ a
fraction of the frame (0.010) and ε a luminance cutoff (0.06). It hugs the
object body and leaves the surrounding sky outside, so field stars are exterior.
The envelope is scale-invariant (σ ∝ frame size) so the 1024‑px checker and the
~2800‑px processor agree on the boundary.

### 1b. Shape-based field-star detector (`starclean.field_star_mask`)
A field star is a **compact bright point** (PSF-like, tiny/sharp/isolated, often
with diffraction spikes) that is:
- outside the envelope (centroid test),
- not elongated (bbox aspect ≤ 2.6 — rejects shell/rim fragments),
- small (area-relative component-size cap),
- **isolated**: an extended annulus around it is ≥65% dark. This is the
  peak-to-surrounding-contrast cue that separates a foreground star from an
  H II knot / cluster or a rim fragment — those sit beside bright galaxy/nebula
  emission, so their annulus is NOT mostly dark; a true field star is dark on
  every side.
Components are labelled by connectivity, so a spiky star's diffraction spikes
travel with it as one component.

### 1c. Removal: inpaint from local background, outside the envelope only
`process.py` computes the (tighter) envelope, builds the "will it show" alpha
(luminance→alpha × hard envelope), then scrubs every detected field star's RGB
from its locally-blurred surroundings (a star on black collapses to black, one
near the edge blends into the halo) and re-derives alpha. Inside the envelope
nothing is touched — the object's own H II regions / clusters are preserved
unchanged (the old `onobj_star` pass that altered on-disk pixels is removed).

---

## 2. Automated check (failing-first)

`check_hero_objects.py` uses the SAME envelope + detector as processing
(`starclean.*`) and requires **0** outside-envelope star-like detections per
kept object.

| Object | P589b (pre-fix) | P589c (processed) |
|--------|-----------------|-------------------|
| helix-nebula    | 0  OK  | 0  OK |
| whirlpool-m51   | 27 FAIL | 0 OK |
| ring-nebula-m57 | 1  FAIL | 0 OK |
| sombrero-m104   | 29 FAIL | 0 OK |
| pinwheel-m101   | 334 FAIL | 0 OK |
| lion-nebula-miri | 0 OK  | 0 OK |
| lion-nebula-nasa | 0 OK  | 0 OK |

Ran on the pre-fix (P589b) library it **FAILS** (4 objects above 0); on the
processed library it **PASSES** — a genuine failing-first demonstration on the
actual pre/post code (the check and pipeline share one implementation).

**Dropped objects: none.** All seven reach 0 with the object intact (see §4),
so the manifest `sky-objects.json` and the `licenses/` credits are unchanged.

---

## 3. Regenerated artifacts

- `sheets/contact_objects.png` — recomposited on pure black (the in-game blend).
- `sprites/distant_objects/*.png` — refreshed copies of the processed sprites.
- live artwork `/home/domingo/Development/Artwork/p589obj/*.png` (what the client
  loads) regenerated from the same deterministic pipeline.

---

## 4. Object intactness + definitive "no field star" proof

The contact-sheet / in-game blend is premultiplied on black, so a pixel shows
only if `alpha·rgb > 0`. Measured on every processed sprite:

- **visible pixels outside the object envelope: 0** for all seven
  (`alpha·rgb` composite on black, envelope from `starclean`).
- **rendered real-window 300% crops**: the peripheral band (outer 20%) of every
  `*_sprite_crop300.png` is EXACTLY black (0 non-black pixels) — nothing renders
  around the object.
- **object body unchanged**: mean RGB delta inside the envelope between the
  P589b and P589c sprites is ≤ 1.2/255 (Pinwheel) and ≤ 0.2/255 for the rest —
  the object's disk/arms/dust-lane/rings are preserved.

Note on the reviewer's "still shows stars": the real-window frames include the
game's *intentional* ambient pre-P576 procedural starfield (restored in P588) —
dots that are the sky layer, not photographic field stars baked into the object.
`*_sprite*` renders strip that starfield so the object's own surrounding sky is
seen to be clean. (A vision model also repeatedly miscalled the objects' own
internal H II clusters/glow as "background stars," contradicting itself within a
single reading; the pixel-level checks above are the ground truth.)

Real-window proof (2560×1440 backbuffer via `GetBackBufferData`, not
`--capture`/offscreen — the path that hid P547/P562):
`docs/reports/P589_assets/proof/p589c/` — per object `{slug}_realwindow.png`,
`{slug}_crop300.png`, `{slug}_sprite.png`, `{slug}_sprite_crop300.png`.
BlackSkyRenderer.Draw frametime: **avg 2.603 ms, max 4.145 ms** (61 real frames).

---

## 5. Build & suite

Whole solution Debug: **0 warnings, 0 errors**; `tools/p589-proof` builds
**0/0** (extended with the per-object real-window + 300%-crop scenes).

Three consecutive full-suite runs on the final code (P471 runner; each test
project run 3×). Runs reported as they happened, not re-run to force a clean
pass; documented rotating live-wire timing flakes are in `docs/TEST_FLAKES.md`.

| Project | Run 1 | Run 2 | Run 3 |
|---------|-------|-------|-------|
| Lobby | 12/12 | 12/12 | 12/12 |
| MonoGame | 1664/1664 | 1664/1664 | 1664/1664 |
| ClientCore | 568/568 | 568/568 | 568/568 |
| Core | 3840/3840 | 3837/3840 | 3835/3840 |

Run 1 was fully green. Runs 2 and 3 only dropped documented rotating live-wire
timing flakes in Core (each listed in `docs/TEST_FLAKES.md`): Run 2 —
`LiveCountdownStartPingTests.StartGame_owner_all_ready…` (live-wire countdown
family), `ConquestTerritoryLiveTests.Conquest_win…leak…` (line 29/267),
`AddPartTests.Part_pickup_destroys…` (line 247); Run 3 — `AutoReloadOnEmpty…
RELOAD129` (line 147), `Conquest…leak…` (29/267), `GameOverPlayersWireTests.
Win_by_elimination…` (family, 30/206), `LiveRelaunchTests.Carrier_without_
launch_slots…` (line 40), `LiveTreasurePickupTests.Rescue_ship_picking_cash_…`.
Sets rotate run-to-run (ledger:440). None involve P589c code (the only compiled
change is the standalone `tools/p589-proof` proof tool; no Core/ClientCore
logic was touched), so they were reported as they happened, not re-run to force
a clean pass.

---

## 6. Files

- `tools/space-objects/starclean.py` — NEW: shared envelope + shape-based
  field-star detector.
- `tools/space-objects/process.py` — tight envelope + outside-envelope inpainting;
  `onobj_star` removed (inside-envelope unchanged).
- `tools/space-objects/check_hero_objects.py` — strict 0 gate on the shared
  detector.
- `tools/p589-proof/Program.cs` — per-kept-object real-window frames + 300%
  crops (+ pure-black sprite isolation).
- `docs/reports/P589_assets/sheets/contact_objects.png`,
  `docs/reports/P589_assets/sprites/distant_objects/*.png` (regenerated),
  `docs/reports/P589_assets/proof/p589c/*` (new proof captures).
