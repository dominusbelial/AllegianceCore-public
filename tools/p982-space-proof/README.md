# P982 space proof

Run from the repository root with the artwork, native source and optional sky
pack configured in gitignored `local.env` (see `local.env.example`). Uses the real
sky, dust, ship and Classic/Modern HUD renderers. No network sessions are opened.

```sh
set -a
source local.env
set +a
export NuGetAudit=false
export SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null
timeout 300 "$HOME/.dotnet/dotnet" build tools/p982-space-proof -v:q
timeout 300 "$HOME/.dotnet/dotnet" tools/p982-space-proof/bin/Debug/net10.0/p982-space-proof.dll --out .scratch/p982/stills
timeout 300 "$HOME/.dotnet/dotnet" tools/p982-space-proof/bin/Debug/net10.0/p982-space-proof.dll --clips --out .scratch/p982/clips
timeout 300 "$HOME/.dotnet/dotnet" tools/p982-space-proof/bin/Debug/net10.0/p982-space-proof.dll --cost --out .scratch/p982/cost
python3 tools/p982-space-proof/make-media.py
```

Stills cover all four flight scenes, all three sky tiers, both HUD styles, all
flags off, each flag alone, and all four on. Native accessories (galaxy paintings,
moons and distant objects) are disabled identically in both arms to isolate the
changed layers. The fixture uses native fighter geometry, no replacement media.

Each four-second clip has black/native/HD across columns and Classic/Modern
across rows; pixel checks require content in all six panels before export.
The cost mode submits 400 native hulls, measures CPU submission and
available GL timer queries separately, and records draw-call counts. It excludes
HUD, readback and image encoding from the measured interval. Twenty warm-up and
80 measured frames per condition; no fullscreen pass is introduced by a flag.

Every mode also checks actual GPU submissions: rotation gives tails, stationary
views and cuts do not, postcard draws preserve flight history, mode gaps reset
history, and disabling streaks submits no tail batch. Exceptions exit nonzero.
