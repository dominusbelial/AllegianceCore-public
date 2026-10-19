# P729 battle recording

`battle.alr` is a real 180-second loopback capture, recorded on 2026-10-18
through `ConnectionSession.Recording` using the P727 embedded host. It contains
complete server-to-client transport deliveries, including CRC and original
packet boundaries, with monotonic microsecond arrival timestamps. Gzip is only
storage compression. This is not a generated message fixture.

Workload: Small (player + nine combat bots), PCore001, m2xspiderw.igc, civilization
25 versus 3, research mode at 8x research speed, server RNG seed 729. The driver
observed 549 shots and 419 ship hits by the end; the match was running and all
workers were alive. Startup/join data is included so replay needs no server.

Recreate (requires the usual artwork checkout and built bot driver):

```
dotnet run --project tools/benchmark -- --record-once /tmp/battle.alr 180
```

Do not overwrite a recording while a benchmark or bundle build is reading it.
The reader checks version, monotonically increasing timestamps, packet length,
truncation, ten-minute duration and 512 MiB decompressed payload limits.

`Allegiance --benchmark replay` selects the copy under the executable's
`benchmarks/` directory. `--benchmark replay:/path/to/battle.alr` selects another
capture. A replay pumps the original decoder at 60 logical frames per second,
independent of rendering speed. Each baseline run must produce the same ordered
frame-state hashes; JSON stores their combined SHA-256 rather than thousands of
hash strings. Camera-sector/phase markers accompany the untouched stream. The short auto-tune check seeks into the battle and tests
12 logical seconds with the proposed graphics settings.
