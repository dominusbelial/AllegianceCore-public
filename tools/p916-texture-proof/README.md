# P916 mesh texture proof

Run from the repository root with `local.env` pointing at the real artwork.
Use a worktree-local `TMPDIR`, NuGet cache and isolated XDG settings.

```sh
dotnet build tools/p916-texture-proof
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet tools/p916-texture-proof/bin/Debug/net10.0/p916-texture-proof.dll --audit .scratch/p916/corpus
```

The audit reads production GPU uploads at the real triangle UVs for every
PCore001 hull, station, missile, projectile and probe, the native asteroid
table, cash/artifact treasure models, eight sky domes and debris bits, including
LOD UVs. It also checks 43 planet posters and the aleph funnel. Missing source
textures are listed explicitly. It also guards mine, chaff and effect image
row order. It fails with the dispatch-base renderer.

Real Game1 window captures:

```sh
dotnet tools/p916-texture-proof/bin/Debug/net10.0/p916-texture-proof.dll .scratch/p916/captures modern fighter after off
```

Arguments: output, `classic|modern`, `pod|fighter|capital|station|rock`, name,
`off|hd|pbr`, with optional final `preview` for the station target pane. HD uses the existing `ALLEGIANCE_HD_PACK_PATH`. A real loopback
logon/launch precedes a frozen rendering fixture: full health, stable position,
selected real hull or station/rock at the camera focus. The pod uses the same
13-unit player zoom as P915. The capital fixture seeds its known radius in
the own-ship LOD dictionary to prevent the existing one-unit fallback from
rendering a far sprite instead of the mesh. Stations and rocks use a pod at their centre as
the camera anchor; the tiny own pod can remain visible. This is rendering
evidence, not a simulated combat scenario. Effects run through production;
no server/sim/wire code is changed. Use isolated copies of the entire output
folder for before/after DLLs; do not build over running proof binaries.

P920 adds optional final `hangar` or `target` for the fighter. `hangar` also
writes `NAME-fighter-STYLE-preview.png`: the actual Game1 hangar draw in a
640×640 render target before opaque shop panels cover it, with spin fixed at
0.9 radians. `target` inserts a frozen client-only fighter at (0,5000,-1000)
and selects it in the actual HUD target pane. Use `hd` for that pane's HD
comparison: its existing draw omits the model key needed for PBR, so `pbr`
falls back to native. See `docs/reports/P920_REPORT.md` for the evaluation.

Read-only HD provenance audit (Pillow only; never calls a generation service):

```sh
python3 tools/p916-texture-proof/audit_hd.py --art "$ALLEGIANCE_ARTWORK_PATH" --pack-repo "$HD_SOURCE" --out .scratch/p916/hd-orientation.json
```

This compares every shipped atlas's actual native input PNG byte-for-byte to
file rows and reversed rows, then compares the deployed BC7 basecolor in both
orientations. It handles the text-imported station images independently.
