# P1014 / P1015 real-client loadout proof

Runs production Game1 against an in-process loopback server with PCore001 and
native artwork. It docks, opens the loadout with native F4 and selects the
station's stock scout with its native default. No prices, models, buy rules or
rendered pixels are substituted; only the preview's orbit angle is held fixed.

**Flag on (default run)** plays the Loadout V2 screen through its real input
path (P1015). The pointer is the scripted hangar pointer Game1 reads in place
of the mouse (position, left button, wheel); keys are real key states:

- click a hull row (selects it), click back to the stock scout;
- click a slot (inline picker opens), click it again (closes), reopen, hover an
  offer, click it (mounted, picker closed);
- wheel the hull column down, past the end, and back;
- `2`, Down/Up, Space (picker by keyboard), Up, Esc (picker closes, screen
  stays), Right/Left (next/previous hull), `1`;
- `D` (native default), the picker's empty row, the Default button, arrows on
  the button row, Space on Revert, Tab;
- Esc with no picker open (back to the hangar).

Every step is a `P1015_CHECK` line; the run ends with `P1015_PASS` and exit 0.
Four backbuffer PNGs are written per run.

**`--off`** drives the old three-page loadout with its own unchanged keys
(Down, Tab, Space, B) and checks that no V2 frame or V2 input router is ever
built.

**`--buy`** (P1017) plays the Buy / Launch refusals on the V2 screen against
the same loopback server: a fully developed side with $100 picks a hull it
cannot afford (status line, `Need $X`, Launch and Buy pressed with the real
buttons), then gets the money while the side loses the tech (the server refuses
the buy; the screen shows native's message and goes back to the real ship), then
buys for real. The server's ship and wallet are checked at every step. It ends
with `P1017_PASS` and writes three PNGs.

From the repo root, build this project, set `ALLEGIANCE_ARTWORK_PATH` to the
local corpus, then run with a fresh profile:

```sh
mkdir -p .scratch/p1015/profile
SDL_VIDEODRIVER=x11 LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
ALLEGIANCE_NOVSYNC=1 XDG_CONFIG_HOME="$PWD/.scratch/p1015/profile" \
xvfb-run -a -s '-screen 0 1920x1080x24' \
  timeout --kill-after=10s 200s dotnet \
  tools/p1014-ui-proof/bin/Debug/net10.0/p1014-ui-proof.dll \
  .scratch/p1015/captures classic 1920 1080
```

Repeat for `modern`, for `1280 720` with an Xvfb screen of that size, and with
`--off` appended. Extra captures stay in ignored scratch; review images before
adding a retained set to a public change set.

The proof uses the Native key layout, where Enter opens chat, so it chooses
with Space (the hint the screen shows). It has a 150-second watchdog and always
cancels/joins its server ticker when Game1 exits. `compare.py` is the P1014
flag-off pixel comparison helper.
