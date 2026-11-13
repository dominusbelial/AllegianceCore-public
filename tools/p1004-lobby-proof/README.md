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
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null \
  dotnet run --project tools/p1004-lobby-proof --no-build -- --out docs/reports/P1004_assets
```

Use a scratch `XDG_CONFIG_HOME` and `ALLEGIANCE_GAME_PASSWORDS` path to isolate
player preferences and saved passwords. The proof exits after fourteen captures
and has a ninety-second timeout.
