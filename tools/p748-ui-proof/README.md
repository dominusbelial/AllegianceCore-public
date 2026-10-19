# P748 real-client UI evidence

Run from the repository root after `dotnet build AllegianceCore.slnx`:

```sh
mkdir -p .p748-scratch .p748-evidence
TMPDIR="$PWD/.p748-scratch" NUGET_HTTP_CACHE_PATH="$PWD/.p748-scratch/nuget-cache" \
  dotnet build tools/p748-ui-proof
TMPDIR="$PWD/.p748-scratch" SDL_VIDEODRIVER=x11 LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
  dotnet tools/p748-ui-proof/bin/Debug/net10.0/p748-ui-proof.dll docs/reports/P748_assets \
  > .p748-evidence/client-proof.log 2>&1
```

Requires the configured native Artwork corpus, a working X11 display, and the built DesktopGL content.
The tool boots an ephemeral two-team loopback server, starts its mission, joins the actual
`Game1` DesktopGL client, and drives its production hangar dispatcher. Show All is enabled
for the two investment captures so locked rows are visible. It captures the real backbuffer
using Game1's existing autoshot hook; it does not compose screenshots or inject result rows.
`client-proof.json` records live ship/station IDs, selected groups, hover and visible bucket IDs.
The harness changes the local pointer position to exercise actual hover feedback. It exits
the client and disposes the server when all seven captures complete. Scratch is workspace-local
because this lane cannot write the brief's preferred home directories. Remove `.p748-scratch`
after verification; raw logs remain ignored in `.p748-evidence`.

SDL's offscreen backend on this machine clipped the default framebuffer to 800x480 while
reporting 1280x720; use X11 for the screenshots. The retained captures use X11.
