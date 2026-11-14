# P1006 real-client screen/input proof

Runs the shipping `Game1` against a loopback server and photographs its real
backbuffer. It uses existing proof keyboard/mouse seams and production screen
handlers. Intro runs require a fresh profile, including the default first-run
training suggestion. No replacement UI or synthetic screenshots are used.

Build `p1006-ui-proof.csproj`, then from the repository root, for each style
(`classic`, `modern`) and scene (`intro`, `flight`):

```sh
mkdir -p .scratch/p1006/fresh-classic-intro
SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
ALLEGIANCE_NOVSYNC=1 XDG_CONFIG_HOME="$PWD/.scratch/p1006/fresh-classic-intro" \
dotnet tools/p1006-ui-proof/bin/Debug/net10.0/p1006-ui-proof.dll \
  docs/reports/P1006_assets/after classic intro
```

Supply `ALLEGIANCE_ARTWORK_PATH` for the local native artwork. A working OpenGL
runtime is required, including when using SDL's offscreen driver. The harness
requests 800×480 to match the offscreen driver's initial surface.

`intro` captures first start, splash-skip Escape, suggestion dismissal and an
intentional root Escape popup, checking that only the popup receives Down.
`flight` captures hangar, docked command, a hidden Loadout click, explicit inventory
loadout, launch, and command in space with hidden hangar input. A 180-second
watchdog bounds every run; the loopback ticker is cancelled and joined on exit.
The inventory press resolves the live binding: fresh profiles use Port layout's
Shift+F4. Add `--native-layout` to a flight run to verify native's plain F4;
the proof asserts that binding and reports each action's actual chord. HUD style
and keyboard layout are independent. Intro freshness checks require no saved
player/keymap, the default `pilot` name, and the first-run training suggestion.

Use `--baseline` with a pre-fix client assembly. It records failed checks
without rejecting that client; the flag only relaxes assertions and does not
select or recreate an older implementation.
The original P1006 lane lacked OpenGL/EGL. P1019 completed software-GL captures;
see `docs/reports/SCREENSHOT_DEBT_P1019.md` for results and timings.
