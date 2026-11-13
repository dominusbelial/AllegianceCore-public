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
`flight` captures hangar, docked command, a hidden Loadout click, explicit F4
loadout, launch, and command in space with hidden hangar input. A 180-second
watchdog bounds every run; the loopback ticker is cancelled and joined on exit.

Use `--baseline` with a pre-fix client assembly. It records failed checks
without rejecting that client; the flag only relaxes assertions and does not
select or recreate an older implementation.
The P1006 lane's initial baseline execution failed before rendering because its
container lacks OpenGL/EGL; see the report for qualification status.
