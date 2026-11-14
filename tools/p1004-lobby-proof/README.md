# P1004 browser proof

Runs the production `Game1` browser with an injected local HTTP handler and saves
real backbuffer PNGs at 1280×720 in Classic and Modern. Scenarios: password/free,
full teams, no open team slots, connection refused, HTTP 503, filters hiding every
game, and all servers requiring a newer client. No network listener, public host
or artwork is required. The password capture presses the actual join action to
open its inline prompt.

From the repository root, after building the solution:

```sh
dotnet build tools/p1004-lobby-proof --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
mkdir -p .scratch/p1004
p1004_profile=$(mktemp -d "$PWD/.scratch/p1004/profile.XXXXXX")
xvfb-run -a -s '-screen 0 1280x720x24 -nolisten tcp' \
  env SDL_VIDEODRIVER=x11 LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null ALLEGIANCE_NOVSYNC=1 \
  XDG_CONFIG_HOME="$p1004_profile" ALLEGIANCE_GAME_PASSWORDS="$p1004_profile/passwords.json" \
  dotnet tools/p1004-lobby-proof/bin/Debug/net10.0/p1004-lobby-proof.dll --out docs/reports/P1004_assets
```

Use a scratch `XDG_CONFIG_HOME` and `ALLEGIANCE_GAME_PASSWORDS` path to isolate
player preferences and saved passwords. The proof exits after fourteen captures
and has a ninety-second timeout.
Use Xvfb for the requested 1280×720 window: SDL offscreen on the lane initially
allocates an 800×480 surface and clips this larger browser's captures. P1019
reviewed all fourteen Xvfb captures; the complete software-GL run takes about
26–29 seconds on the lane, excluding compilation. Rendering qualifications are
recorded in `docs/reports/SCREENSHOT_DEBT_P1019.md`.
