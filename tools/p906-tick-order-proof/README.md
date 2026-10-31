# P906 local tick-order proof

Run from the repository root with artwork configured:

```sh
~/.dotnet/dotnet run --project tools/p906-tick-order-proof -- off .scratch/p906/off 20
~/.dotnet/dotnet run --project tools/p906-tick-order-proof -- phases .scratch/p906/phases 20
~/.dotnet/dotnet run --project tools/p906-tick-order-proof -- collisions .scratch/p906/collisions 20
~/.dotnet/dotnet run --project tools/p906-tick-order-proof -- broadcast .scratch/p906/broadcast 20
```

Each arm creates 200 real TCP clients and a separate server process with server
GC, native ship updates, pool transport, immutable snapshots, encode-once and
parallel build. The server holds the firing-range controls; clients observe
through real sockets and send their normal keepalives. There is a ten-second battle warm-up before the timed sample.
The bounded firing range uses durable, 100-unit spherical hulls and tops up
ammunition/energy outside the timed tick. All clients initially request the
spawner's side 0; after seating, the fixture assigns 100 opposing pairs in one
arena. The arena has 24 fixed asteroids, its warps/stations are removed, and
random treasure regeneration is disabled so file-map top-up cannot confound the
comparison. `result.json` includes tick mean/p95, process-wide allocation and
combat counters. An idle sample, a missing arena ship/opposing pair, or a
simulation-stage failure rejects the run. Only loopback is contacted.

For an exact off-path comparison, run the identical tool against the dispatch
base and working tree with `off OUTPUT 20 --trace`. This fixed-input replay drives
the production mission update with 200 ships and 1,000 real projectiles. Compare
`trace-result.json`: its SHA-256 covers every ship's position, velocity,
orientation, hull, controls, energy, ammunition, death/docking state and sector
population for 400 ticks. It complements the nondeterministic socket timings.

The two new config switches are set through reflection solely so this same proof
builds against the dispatch base, where those switches do not exist. This tool
is not the rig's acceptance run. See [the report](../../docs/reports/P906_REPORT.md)
for limitations and Claude's separate two-match 200v200 plan.
