# roster-capture

Regenerates the P305 roster reference sheets from the real ship roster, using
the exact rendering path the in-game client uses — `HullModelResolver` +
`ShipRenderer.Draw` + compiled P287 content models. Built for P331 to move the
original `/tmp/p305verify` + `/tmp/p305net` throwaway harnesses into the repo
as a real, reusable tool.

## What it produces

`docs/P305_roster/`:

- **roster_all.jpg** — all 72 distinct models in one image, 9 faction columns
  with headers (`BIOS (13)`, `GT (16)`, …) over a cell grid.
- **roster_\<civ\>.jpg** — one legible sheet per faction with a green
  `civ-class` label under every ship.
- **roster_net_\<civ\>_wire.jpg** — the true networked end-to-end proof: a real
  `ConnectionSession` client joins the live `vizfight roster` server over the
  wire, seats on a side, and renders the ships it **actually receives** as
  same-side teammates (real EXPORT/UPDATE path). `roster_net_bios_wire.jpg`
  corresponds to side 0.
- **roster_legend.md** — asset → civ/class/hull/model/display mapping.
- **roster_variety.txt** — objective per-asset geometry: triangle counts,
  unit-normalized local radius, and the **true rendered radius** (see the
  framing note below).

`docs/meshy_hints/` (P348, `turnaround` mode):

- **\<asset\>/\<asset\>_front|right|back|left.png** — one clean 4-angle turnaround
  reference set per hull (camera orbited around the ship's vertical axis at a
  fixed low elevation) on a plain solid neutral background (RGB 78,100,88 sage
  green), every hull centred + scaled so all 72 read at the same apparent size
  and margin — the standard multi-image input shape for image-conditioned 3D
  generation (Meshy-style multi-image-to-3D).
- **turnaround_manifest.json** — asset → civ/class + the 4 output paths for
  every hull, so a future pass can iterate the catalogue programmatically
  without re-deriving the roster.

## Modes

```
roster-capture dump [--dump PATH] [--art PATH] [--content PATH] [--out DIR]
roster-capture wire [--port USHORT] [--side SHORT] [--art PATH]
                    [--content PATH] [--out DIR]
roster-capture turnaround [--dump PATH] [--art PATH] [--content PATH] [--out DIR]
```

Defaults resolve relative to the repo root (found by walking up to
`AllegianceCore.slnx`):

- `--dump`    → `<root>/tools/vizfight/roster_dump.json`
- `--art`     → `<root>/../Artwork` (the PCore001 core art)
- `--content` → `<root>/src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content`
- `--out`     → `<root>/docs/P305_roster` (`turnaround` → `<root>/docs/meshy_hints`)

`dump` needs only a `roster_dump.json` (kept up to date by running
`vizfight roster`, which rewrites it on boot). `wire` additionally needs a live
`vizfight roster` server running so the client can connect over the wire —
`wire` prints the real transport port the server binds.

## Requirements / why it's a standalone-built tool

- Requires the MonoGame client's **compiled Content** to exist (build
  `Allegiance.ClientCore.MonoGame` first — the `--content` path above points at
  a build output), because it `Content.Load`s the compiled `.xnb` ship models
  through the real renderer.
- `wire` requires a running `vizfight roster` server and opens a real network
  client connection.

Despite these runtime requirements it is added to `AllegianceCore.slnx` (under
`/tools/`) so it compiles as part of the normal whole-solution build — the
project itself has no special runtime coupling, it just needs built content
when you actually run it.

## The framing fix (P331)

Every P305 sheet originally showed **one giant close-up of a single mesh
surface**. Root cause: the content pipeline unit-normalizes each model
(`MergedSphere` local radius ≈ 1.0), but `ShipRenderer.Draw` draws through
`mesh.ParentBone.Transform`, which bakes a **100× uniform scale** restoring
real-world units (~190-unit hulls). The old harness normalized each tile by the
model-**local** radius alone, so every ship rendered 100× too big and filled
(or spilled far beyond) its tile. `RenderRadius(model)` in `Program.cs`
accounts for the parent-bone scale by transforming each mesh's local bounding
sphere through its bone before merging, so the per-tile normalization matches
what actually rasterizes. This affects both the offline sheets and the wire
render (they shared the same formula).
