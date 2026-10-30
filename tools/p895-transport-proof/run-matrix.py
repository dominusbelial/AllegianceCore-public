#!/usr/bin/env python3
"""Bounded local P895 experiment. Does not contact remote hosts or change qdiscs."""
import argparse
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--seconds', type=int, default=12)
p.add_argument('--output', type=Path, default=ROOT / '.scratch/p895/matrix')
p.add_argument('--lan-only', action='store_true')
a = p.parse_args()
if not 2 <= a.seconds <= 120:
    p.error('seconds must be in 2..120')
a.output.mkdir(parents=True, exist_ok=True)
dotnet = os.environ.get('DOTNET', str(Path.home() / '.dotnet/dotnet'))
dll = ROOT / 'tools/p895-transport-proof/bin/Release/net10.0/p895-transport-proof.dll'
cases = [] if a.lan_only else [(proto, loss, delay, 10, 4) for delay in (30, 80) for loss in (0, .01, .02, .05) for proto in ('tcp', 'udp')]
cases += [(proto, 0, 0, 0, 200) for proto in ('tcp', 'udp')]
results = []
for proto, loss, delay, jitter, bots in cases:
    name = f'{proto}-loss{round(loss*100)}-delay{delay}-bots{bots}'
    state = a.output / name
    command = [dotnet, str(dll), '--protocol', proto, '--loss', str(loss), '--delay', str(delay), '--jitter', str(jitter),
               '--bots', str(bots), '--seconds', str(a.seconds), '--state', str(state)]
    print('Running', name, flush=True)
    before = Path('/proc/loadavg').read_text().strip() if Path('/proc/loadavg').exists() else ''
    with (a.output / f'{name}.log').open('w') as log:
        run = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=180)
    if run.returncode:
        raise SystemExit(f'{name} failed ({run.returncode}); see {a.output / (name + ".log")}')
    result = json.loads((state / 'result.json').read_text())
    result['LoadBefore'] = before
    results.append(result)
    (a.output / 'results.json').write_text(json.dumps(results, indent=2) + '\n')
    print(f'  pose p95={result["PoseP95Ms"]:.1f}ms gap={result["LongestPoseGapMs"]:.1f}ms server CPU={result["ServerCpuCores"]:.3f} cores', flush=True)
