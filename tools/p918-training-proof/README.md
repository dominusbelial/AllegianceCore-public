# P918 training buoy and multiplayer proofs

Build with `dotnet build tools/p918-training-proof`. Source `local.env` first.

```
SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
ALLEGIANCE_NOVSYNC=1 DOTNET_gcServer=1 ALLEGIANCE_TRANSPORT_MODE=pool \
ALLEGIANCE_BROADCAST_PARALLEL_BUILD=1 \
dotnet tools/p918-training-proof/bin/Debug/net10.0/p918-training-proof.dll \
  window src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content \
  .scratch/p918/window Classic
```

Repeat with `Modern`. Use a writable task-local `XDG_CONFIG_HOME` and
`ALLEGIANCE_HUD_SETTINGS` to isolate preferences. The actual Game1 starts native
Mission2. Ordinary creation/queued-order actions add three visual controls in
its cockpit view: a visible held buoy, a hidden held buoy and an unheld visible
buoy. The observer checks the shipping scene's snapshot and native texture,
saves a screenshot, clears the final visible slot, checks retirement and saves
another screenshot. It changes camera presentation through the normal facility;
it does not change a lesson condition, time or outcome.

`P918TrainingWindowTests` runs the same process with a bounded timeout for both
HUD styles. `P918TrainingOrderTests` separately advances the native Mission2
script to its authored commander waypoint and exercises real death, server
orders, side departure, travel, carrier resupply and mission exit through an
embedded host and loopback client.

```
dotnet tools/p918-training-proof/bin/Debug/net10.0/p918-training-proof.dll \
  sim-cost "$ALLEGIANCE_ARTWORK_PATH" .scratch/p918/sim-cost.json
```

`SimCost.cs` uses only dispatch-era Core APIs. It can also be compiled in a
scratch console project referencing the saved dispatch Core assembly, then run
with the exact same source against the changed assembly. It simulates 32
multiplayer wingmen with point and typed-ship goals for 250 ticks with native GotoPlan, replacing current and
accepted commands every ten ticks. Nine samples follow one warmup; fingerprints
include poses, fractions, slots, command event count and lazy buoy count. This
is a local simulation cost check; it does not measure transport/broadcast or
claim a rig performance result. No artificial load is generated.
