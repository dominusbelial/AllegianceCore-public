# P1014 real-client loadout proof

Runs production Game1 against an in-process loopback server with PCore001 and
native artwork. It docks, opens loadout using native F4, selects the station's
stock scout and defaults it, opens the weapon picker with the existing
Tab/Space keys, moves its cursor with Down, then uses a reachable-research
fixture and a zero wallet to select a genuinely priced hull. No prices, models,
buy rules or rendered pixels are substituted. It checks that locked hulls and
inline offers occupy visible layout elements and that the buy button reads the
shared shortfall. The orbital angle alone is held fixed for reproducible images.

From the repo root, build this project, set `ALLEGIANCE_ARTWORK_PATH` to the
local corpus, then run with a fresh profile:

```sh
mkdir -p .scratch/p1014/profile
SDL_VIDEODRIVER=x11 LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
ALLEGIANCE_NOVSYNC=1 XDG_CONFIG_HOME="$PWD/.scratch/p1014/profile" \
xvfb-run -a -s '-screen 0 1920x1080x24' \
  timeout --kill-after=10s 150s dotnet \
  tools/p1014-ui-proof/bin/Debug/net10.0/p1014-ui-proof.dll \
  .scratch/p1014/captures classic 1920 1080
```

Repeat for `modern`, and for `1280 720` with an Xvfb screen of that size. Three
actual backbuffer PNGs are written per run: default (including locked hulls),
open weapon offers, and unaffordable heavy hull (including turret seats).

`--off` captures only the untouched legacy renderer, checks the loadout route,
and checks that no V2 frame was built. The pre-implementation harness/binaries
can run the same off fixture for a pixel comparison; P1014's report records the
baseline provenance and hashes. Baseline and final frames use the same orbit
angle and local fixture. Extra captures stay in ignored scratch; review images
before adding the retained set to a public change set.

No production input is changed. Native Enter opens chat, so the proof uses the
existing Space select binding; the pointer is parked outside legacy hover bands
while verifying keyboard selection. The proof has a 120-second watchdog and
always cancels/joins its server ticker when Game1 exits.
