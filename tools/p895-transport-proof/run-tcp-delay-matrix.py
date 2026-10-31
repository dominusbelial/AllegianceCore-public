#!/usr/bin/env python3
"""P898: real TCP/netem delay sweep in an existing, private local container.

Container prerequisites: NET_ADMIN, iproute2, the worktree at /work, dotnet at
/dotnet/dotnet, artwork configured. Changes only that container's loopback qdisc.
No sudo, remote hosts, or host networking. Build the Release proof first.
"""
import argparse
import csv
import json
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--container', required=True)
p.add_argument('--seconds', type=int, default=20)
p.add_argument('--repeats', type=int, default=3)
p.add_argument('--delays', default='0,10,20,30,40,60,80,120')
p.add_argument('--bots', type=int, default=4)
p.add_argument('--mtu', type=int, default=1500)
p.add_argument('--output', type=Path, default=Path('.scratch/p898/matrix'))
p.add_argument('--protocol', choices=('tcp', 'udp'), default='tcp')
p.add_argument('--arms', choices=('both', 'off', 'on'), default='both')
p.add_argument('--direct', action='store_true', help='no qdisc; requires delays=0 and a clean loopback qdisc')
a = p.parse_args()
delays = [int(d) for d in a.delays.split(',')]
if not 2 <= a.seconds <= 120 or not 1 <= a.repeats <= 5 or not 2 <= a.bots <= 200 or any(d < 0 or d > 120 for d in delays):
    p.error('seconds 2..120, repeats 1..5, bots 2..200, delays 0..120')
if a.mtu not in (1500, 65536):
    p.error('MTU must be 1500 or 65536')
if a.direct and delays != [0]:
    p.error('--direct requires --delays 0')
output = a.output.resolve()
try:
    relative = output.relative_to(ROOT)
except ValueError:
    p.error('output must be inside the mounted worktree')
container = json.loads(subprocess.check_output(['docker', 'inspect', a.container]))[0]
if container['HostConfig']['NetworkMode'] == 'host':
    p.error('refusing host network namespace')
if not any(m['Destination'] == '/work' and Path(m['Source']).resolve() == ROOT for m in container['Mounts']):
    p.error('container must mount this worktree at /work')
output.mkdir(parents=True, exist_ok=True)

def execute(*command, **kwargs):
    return subprocess.run(['docker', 'exec', '-w', '/work', a.container, *command], check=True, **kwargs)

results = []
arms = [False, True] if a.arms == 'both' else [a.arms == 'on']
if a.direct:
    qdiscs = json.loads(execute('tc', '-j', 'qdisc', 'show', 'dev', 'lo', stdout=subprocess.PIPE).stdout)
    if any(q['kind'] != 'noqueue' for q in qdiscs):
        p.error('--direct requires no qdisc on private loopback')
execute('ip', 'link', 'set', 'lo', 'mtu', str(a.mtu))
try:
    for delay in delays:
        if not a.direct:
            execute('tc', 'qdisc', 'replace', 'dev', 'lo', 'root', 'netem', 'delay', f'{delay}ms', 'limit', '10000')
        for rep in range(a.repeats):
            # Alternate ordering to reduce time/load bias.
            for enabled in arms if rep % 2 == 0 else arms[::-1]:
                name = f'{a.protocol}-d{delay}-batch{int(enabled)}-n{a.bots}-r{rep+1}-mtu{a.mtu}'
                state = relative / name
                if (ROOT / state / 'result.json').exists():
                    raise RuntimeError(f'refusing to overwrite existing evidence: {state}')
                command = ['/dotnet/dotnet', 'tools/p895-transport-proof/bin/Release/net10.0/p895-transport-proof.dll',
                           '--protocol', a.protocol, '--bots', str(a.bots), '--seconds', str(a.seconds),
                           '--kernel-delay', str(delay), '--state', str(state)]
                if enabled:
                    command += ['--tcp-batch-writes']
                print('Running', name, flush=True)
                before = Path('/proc/loadavg').read_text().strip()
                with (output / (name + '.log')).open('w') as log, (output / (name + '-ss.txt')).open('w') as ss:
                    run = subprocess.Popen(['docker', 'exec', '-w', '/work', a.container, *command], stdout=log, stderr=subprocess.STDOUT)
                    started = time.monotonic()
                    while run.poll() is None:
                        if time.monotonic() - started > 180:
                            run.kill()
                            raise TimeoutError(name)
                        ss.write(f'\nTIME {time.monotonic()}\n')
                        ss.flush()
                        execute('ss', '-tinm', stdout=ss)
                        time.sleep(.25)
                    if run.returncode:
                        raise RuntimeError(f'{name} failed: see log')
                result = json.loads((ROOT / state / 'result.json').read_text())
                if result['TcpBatchWrites'] != enabled or result['ConnectedServer'] != a.bots:
                    raise RuntimeError('server option/coverage mismatch')
                with (ROOT / state / 'sends.csv').open() as f:
                    sends = list(csv.DictReader(f))
                if sends:
                    # First row includes pre-measurement join activity. Lifetime
                    # coalescing counts are differenced; timing rows exclude it.
                    body = sends[1:]
                    result['Superseded'] = int(sends[-1]['superseded']) - int(sends[0]['superseded'])
                    result['PollBlocked'] = int(sends[-1]['pollBlocked']) - int(sends[0]['pollBlocked'])
                    result['WriteCalls'] = sum(int(s['writeCalls']) for s in body)
                    result['WriteBusyMs'] = sum(float(s['busyWriteMs']) for s in body)
                    result['LongestWriteMs'] = max((float(s['longestWriteMs']) for s in body), default=0)
                    result['MaxAppInflightBytes'] = max(int(s['inflight']) for s in body)
                    result['MaxAppQueuedBytes'] = max(int(s['queued']) for s in body)
                    result['MaxNoProgressMs'] = max(int(s['noProgressMs']) for s in body)
                result.update(Name=name, Repetition=rep+1, Mtu=a.mtu, LoadBefore=before,
                              LoadAfter=Path('/proc/loadavg').read_text().strip(), DirectLoopback=a.direct)
                execute('tc', '-s', 'qdisc', 'show', 'dev', 'lo', stdout=(output / (name + '-qdisc.txt')).open('w'))
                results.append(result)
                (output / 'results.json').write_text(json.dumps(results, indent=2) + '\n')
                print(f'  p50/p95={result["PoseP50Ms"]:.1f}/{result["PoseP95Ms"]:.1f}ms '
                      f'gap={result["LongestPoseGapMs"]:.1f}ms bytes/s={result["BytesPerSecond"]:.0f}', flush=True)
finally:
    if not a.direct:
        execute('tc', 'qdisc', 'del', 'dev', 'lo', 'root')
