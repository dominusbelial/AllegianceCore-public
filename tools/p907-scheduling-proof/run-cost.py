#!/usr/bin/env python3
"""Bounded, sequential loopback A/B; server and 200 clients are separate processes."""
import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument("--output", default=".scratch/p907/cost")
parser.add_argument("--seconds", type=int, default=20)
parser.add_argument("--repeats", type=int, default=3)
parser.add_argument("--warmup-ms", type=int, default=10000)
parser.add_argument("--allocation-events", action="store_true")
args = parser.parse_args()
if not 10 <= args.seconds <= 60 or not 2 <= args.repeats <= 5 or not 1500 <= args.warmup_ms <= 15000:
    parser.error("bounded proof: 10..60 seconds, 2..5 repeats")
output = (ROOT / args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
dotnet = os.environ.get("DOTNET", str(Path.home() / ".dotnet/dotnet"))
proof = ROOT / "tools/p895-transport-proof/bin/Release/net10.0/p895-transport-proof.dll"
env = dict(os.environ, DOTNET_gcServer="1", ALLEGIANCE_TICK_PROFILER="1",
           ALLEGIANCE_TRANSPORT_MODE="pool", ALLEGIANCE_SIM_NATIVE_GOTOPLAN="1",
           ALLEGIANCE_BROADCAST_PARALLEL_BUILD="1", ALLEGIANCE_SHIP_UPDATE_MODEL="native",
           ALLEGIANCE_BROADCAST_SNAPSHOT="1", ALLEGIANCE_BROADCAST_ENCODE_ONCE="1",
           ALLEGIANCE_BROADCAST_TIERS="0", ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER="0",
           ALLEGIANCE_RECEIVE_DECOUPLE="0")
rows = []
for protocol in ("tcp", "udp"):
    for repeat in range(args.repeats):
        for arm in (("off", "on") if repeat % 2 == 0 else ("on", "off")):
            state = output / f"{protocol}-{repeat + 1}-{arm}"
            state.mkdir(exist_ok=True)
            load = os.getloadavg()
            command = [dotnet, str(proof), "--profile", "--protocol", protocol,
                       "--bots", "200", "--seconds", str(args.seconds), "--warmup-ms", str(args.warmup_ms),
                       "--scheduling-metrics", arm, "--state", str(state)]
            if args.allocation_events:
                command.append("--allocation-events")
            with (state / "driver.log").open("w") as log:
                subprocess.run(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                               check=True, timeout=180)
            metrics = json.loads((state / "host-metrics.json").read_text())
            assert metrics["Connected"] == 200 and metrics["Profiler"]
            assert metrics["SchedulingMetrics"] == (arm == "on")
            host_log = (state / "host.log").read_text()
            assert ("[thread-pool]" in host_log) == (arm == "on")
            assert ("[udp-receive]" in host_log) == (protocol == "udp" and arm == "on")
            rows.append(dict(protocol=protocol, repeat=repeat + 1, arm=arm, warmupMs=args.warmup_ms,
                             loadAverage=load, **metrics))
            (output / "runs.json").write_text(json.dumps(rows, indent=2) + "\n")
            print(json.dumps(rows[-1]), flush=True)
summary = []
for protocol in ("tcp", "udp"):
    for arm in ("off", "on"):
        group = [row for row in rows if row["protocol"] == protocol and row["arm"] == arm]
        keys = ["TickMeanMs", "TickP50Ms", "TickP95Ms", "AllocatedBytesPerSecond", "CpuCores"]
        if args.allocation_events:
            keys.append("SampledAllocatedBytesPerSecond")
        summary.append(dict(protocol=protocol, arm=arm, runs=len(group),
                            **{key: statistics.median(row[key] for row in group)
                               for key in keys}))
(output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
print(json.dumps(summary, indent=2))
