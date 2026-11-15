# Loadout real-window proof (P1014–P1018)

Runs production Game1 against an in-process loopback server with PCore001 and
native artwork. No replacement UI, prices or purchase rules. The orbit angle
is held fixed for reproducible captures. Pointer, wheel and keys enter through
the shipping P1015 input path. Every assertion is a `P1018_CHECK`; success ends
with `P1018_PASS` and exit 0, including assertion count and elapsed seconds.

Build from the repository root:

```sh
dotnet build tools/p1014-ui-proof/p1014-ui-proof.csproj \
  --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
```

Set `ALLEGIANCE_ARTWORK_PATH` to your local corpus. Run with a fresh profile,
X11/Xvfb and software GL:

```sh
mkdir -p .scratch/p1018/example-profile
SDL_VIDEODRIVER=x11 LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
ALLEGIANCE_NOVSYNC=1 XDG_CONFIG_HOME="$PWD/.scratch/p1018/example-profile" \
xvfb-run -a -s '-screen 0 1920x1080x24' \
  timeout --signal=TERM --kill-after=10s 240s dotnet \
  tools/p1014-ui-proof/bin/Debug/net10.0/p1014-ui-proof.dll \
  .scratch/p1018/example-captures classic 1920 1080 --walkthrough \
  --capture=p1017-1-cannot-afford
```

Repeat for `modern`, and for `1280 720` with an Xvfb screen of that size.
All game traffic is loopback. The proof cancels and joins its server ticker on
exit. Its internal watchdog is 210 seconds for the walkthrough, 150 otherwise.

| Mode | Coverage |
| --- | --- |
| `--walkthrough` | A complete session lasting at least 120 seconds: first dock inventory; F4; mouse hull selection; slots/inline offers/deltas; independent wheel scrolling; Tab focus and picker keys; Escape; Left/Right hull changes; actual turret hover/marker; Default by D and button; Revert; Need $X and unaffordable Buy; tech loss causing server refusal; money refresh; real affordable Buy and exact wallet debit; Launch; collision re-dock; reopen V2. Every stage has state assertions. |
| No mode switch | Original P1015 input sequence, without purchases/launch. Starts a stock scout display choice; tests mouse, wheel, keys, Default/Revert and Escape to hangar. |
| `--off` | Old three-page screen, its unchanged keys, and no V2 frame/router creation. |
| `--buy` | Original P1017 shortfall, tech-race refusal and successful Buy script. Checks the real server's hull, wallet and mounted parts. |
| `--hardpoints=fighter` | Authentic PCore001 Fighter display fixture; Tab focuses the slot through P1015; validates its fixed mount marker. |
| `--hardpoints=bomber` | Authentic Bomber display fixture; pointer hovers a turret through P1015; validates its guide and gunner label. |
| `--hardpoints=capital` | Authentic Cruiser display fixture; validates its several turret rows, marker and gunner. |
| `--extras` | With hardpoint modes: opposite orbit still projects a marker; shield hover produces no invented socket marker. |

Hardpoint-only modes are display fixtures: they set up real core hull/default
parts in ShopModel without buying locked hardware. The gunner enters through
production EXPORT/LOADOUT_CHANGE handlers. The integrated walkthrough uses
server purchases. Its return leg holds the server ship at an authored native
landing-bay point across launch cooldown/client flight updates until the normal
collision path docks it; it never sets a client dock flag. Setup grants side
techs and changes wallets through the same server broadcasts as normal play.

The proof uses Classic Allegiance keyboard layout: Space chooses and Enter
opens chat. Tab cycles focus; direct hull/slot focus verbs are unbound. HUD
style and key layout are separate settings.

Without `--capture`, the input sequence writes four PNGs, Buy writes three,
the walkthrough writes nine, and hardpoints write one (three with extras).
`--capture=SUFFIX` selects one named checkpoint while **all actions and
assertions still run**. Useful checkpoints:

- `1-click-slot-picker`, `2-offer-mounted-wheel-scrolled`, `3-keys-picker-focus`;
- `5-turret-hover`, `4-default-button`;
- `p1017-1-cannot-afford`, `p1017-2-server-refused`, `p1017-3-bought`;
- `6-redocked`;
- `hardpoint-fighter`, `hardpoint-bomber`, `hardpoint-capital`,
  `opposite-orbit`, `shield-no-marker`.

Captures are original Game1 backbuffer PNGs, never composed UI. This lane
reproduces P1019's intermittent missing draw/glyph defect, in continuous capture
and some first-capture hardpoint frames. The P1018 final matrix selects one
checkpoint per fresh session for focused media review; the integrated checks
still cover the whole two-minute session. This does not fix the renderer:
qualifications for the retained images are in the report. Readback/texture-binding
experiments did not fix the defect and are not included. Review every generated image before retaining
media. `compare.py` validates PNG CRC/dimensions and compares pixel buffers;
it remains the original P1014 flag-off comparison helper.

See [the player guide](../../docs/LOADOUT_V2.md) and
[the integration report](../../docs/reports/LOADOUT_V2_INTEGRATION_P1018.md).
