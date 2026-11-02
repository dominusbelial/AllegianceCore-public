# P915 real-window proof

Build from the repository root with the .NET SDK, using a worktree-local
`TMPDIR` and `NUGET_HTTP_CACHE_PATH` on the Pi lane:

```sh
~/.dotnet/dotnet build tools/p915-smoke-proof
~/.dotnet/dotnet tools/p915-smoke-proof/bin/Debug/net10.0/p915-smoke-proof.dll \
  docs/reports/P915_assets modern pod after
```

Arguments: output directory, `classic|modern`, `pod|fighter`, capture name,
optional diagnostic `white|unflip|no-soft|hull-only|clean|pbr`.
Set the standard `ALLEGIANCE_HD_PACK_PATH` to an installed HD pack. Use a
scratch `XDG_CONFIG_HOME`/`XDG_DATA_HOME` to isolate player settings. Effects
are Enhanced/Medium, HD textures on, PBR off unless requested. The proof
logs real HD uploads; it does not ship textures or edit the pack.

This runs the actual Game1 flight window after loopback logon/launch. It then
pauses server ticking and injects repeatable client telemetry (45% hull,
afterburner button held, throttle idle, no part on the pod / mounted part on
the fighter), invokes the production pose/effect gather, and captures Game1's
backbuffer. The pod uses the real PCore001 `utl203` hull (length 4); the
fighter uses `wc_bifig` (length 13). A fixed player zoom of 13 frames the pod
outside the existing 10-unit chase near plane; no production camera setting
is changed. HUDs remain visible. `white` replaces native texture pixels only
inside the proof and suppresses hull particles; `unflip` samples native rows
in file order and suppresses hull particles; `no-soft` keeps the original
texture/smoke but disables soft particles. Neither changes production assets.

For before captures run this harness against the dispatch-base game library;
for after captures use the changed game library. Copy the **whole** output
folder into an isolated scratch directory for each version before running.
Never build over DLLs loaded by a running proof window. The same source and
fixtures are used on both sides. Screenshots may be losslessly converted to
WebP for the task's 2 MB evidence budget.
