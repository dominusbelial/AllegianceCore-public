# P907 scheduling proofs

These run locally and never tune thread-pool settings. Build with the ordinary
external artwork configured and keep temporary files in task scratch:

```bash
~/.dotnet/dotnet build tools/p907-scheduling-proof
DOTNET_PROCESSOR_COUNT=4 ~/.dotnet/dotnet \
  tools/p907-scheduling-proof/bin/Debug/net10.0/p907-scheduling-proof.dll
```

The stall proof establishes a real UDP session, queues 32 bounded blocked waits,
queues a pool probe, sends a real reliable UDP frame, and releases the waits
after 800 ms from its dedicated main thread. It asserts a pending/completed
probe stall and subsequent recovery. Each wait has a five-second timeout;
cleanup releases and joins all 32. `sentToObservedMs` uses the actual receiver
callback timestamp. No busy loops or extra CPU load are created.

Actual embedded hosts and the game client's `CrashLog.Install` tee:

```bash
~/.dotnet/dotnet tools/p907-scheduling-proof/bin/Debug/net10.0/p907-scheduling-proof.dll \
  --local-host .scratch/p907/local-skirmish skirmish
~/.dotnet/dotnet tools/p907-scheduling-proof/bin/Debug/net10.0/p907-scheduling-proof.dll \
  --local-host .scratch/p907/local-lan lan
```

The normal bot driver must be built. Both hosts use the real owned-host
lifecycle, assert bot liveness and verify metrics reached the actual client
rolling log. The proof opens no graphics window. This measures logging shared
by both HUD styles, with no UI changes.

200-client TCP and UDP cost proof, profiler enabled in every arm:

```bash
~/.dotnet/dotnet build tools/p895-transport-proof -c Release
python3 tools/p907-scheduling-proof/run-cost.py --allocation-events
```

The script alternates metrics off/on order across three 20-second repeats per
protocol after ten seconds of seated warmup, and saves raw host logs plus summary
JSON. It reuses P895's real socket
clients, native broadcast budget, snapshot/encode-once, parallel build, pooled
TCP, native GotoPlan and server GC. Crowding is disabled identically to preserve
all 200 connections. Allocation is the process-wide server allocation delta
over the complete measured interval, including transport workers and logging;
it excludes the separate client process. The local runtime sometimes reports a
negative cumulative allocation-counter delta at a gen2, making that endpoint
unsuitable. `--allocation-events` independently sums runtime GCAllocationTick's
`AllocationAmount64` samples. This is a sampled allocation estimate with roughly
100 KB per-thread sampling boundaries, not an exact byte census. Both arms must
enable the listener; its allocation/event-dispatch overhead is included equally.
The tool fails if no allocation events arrive and retains the original endpoint
alongside the independent estimate. No GC settings are changed. No other local
proof/build should run concurrently with this cost comparison.
