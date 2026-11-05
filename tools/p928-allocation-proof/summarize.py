#!/usr/bin/env python3
"""Summarise P928's saved bench/GC windows without modifying raw evidence."""
import csv
import json
from pathlib import Path
import re
import statistics
import sys

root = Path(sys.argv[1])
bench_tag = sys.argv[2] if len(sys.argv) > 2 else "paired"

def numbers(pattern, text):
    match = re.search(pattern, text)
    if not match:
        raise ValueError(f'Missing {pattern}')
    return [float(x) for x in match.groups()]

def bench(path):
    text = path.read_text()
    mb, kb, pause, pause_tick, share, g0, g1, g2 = numbers(
        r'P838-GC: alloc=([\d.]+)MB \(([\d.]+)KB/tick\) pause=([\d.]+)ms \(([\d.]+)ms/tick, ([\d.]+)% of measured wall\) gc gen0=(\d+) gen1=(\d+) gen2=(\d+)', text)
    mean, median, p95, p99, maximum = numbers(
        r'Mission.Update mean=\s*([\d.]+) ms  median=\s*([\d.]+) ms  p95=\s*([\d.]+) ms  p99=\s*([\d.]+) ms  max=\s*([\d.]+) ms', text)
    steps = {k: float(v) for k, v in re.findall(r'P838-GC: step-alloc (\S+)\s+([\d.]+)MB', text)}
    timings = {k: float(v) for k, v in re.findall(r'^  (\S+)\s+amort=\s*([\d.]+)', text, re.M)}
    return dict(mb=mb, kb_tick=kb, pause_ms=pause, pause_ms_tick=pause_tick, pause_share=share,
                gen0=int(g0), gen1=int(g1), gen2=int(g2), mean_ms=mean, median_ms=median,
                p95_ms=p95, p99_ms=p99, max_ms=maximum, step_alloc_mb=steps, bucket_amort_ms=timings)

def showcase(path):
    text = path.read_text()
    rows = []
    current = None
    for line in text.splitlines():
        if '[tickprof] window' in line:
            mean, p95, maximum = numbers(r'tick-wall mean=([\d.]+)ms p95=([\d.]+)ms max=([\d.]+)ms', line)
            current = dict(mean_ms=mean, p95_ms=p95, max_ms=maximum, steps={}, alloc_steps={})
        elif current is not None and '[gcprof] window' in line:
            ticks, pause, pause_tick, share, g0, g1, g2, mb, kb = numbers(
                r'ticks=(\d+) pause=([\d.]+)ms \(([\d.]+)ms/tick, ([\d.]+)% of tick-wall\) gc gen0=(\d+) gen1=(\d+) gen2=(\d+) alloc=([\d.]+)MB \(([\d.]+)KB/tick\)', line)
            current.update(ticks=int(ticks), pause_ms=pause, kb_tick=kb, pause_ms_tick=pause_tick,
                           pause_share=share, gen0=int(g0), gen1=int(g1), gen2=int(g2), alloc_mb=mb)
            rows.append(current)
        elif current is not None and '[gcprof] top-alloc' in line:
            current['alloc_steps'] = {k: float(v) for k, v in re.findall(r'(step:\S+?)=([\d.]+)MB', line)}
        elif current is not None:
            match = re.search(r'^  (step:\S+)\s+mean\s+([\d.]+)', line)
            if match:
                current['steps'][match[1]] = float(match[2])
    selected = rows[-20:]  # same predeclared number of final complete windows in each arm
    ticks = sum(r['ticks'] for r in selected)
    wall = sum(r['ticks'] * r['mean_ms'] for r in selected)
    def spread(key):
        vals = [r[key] for r in selected]
        return dict(min=min(vals), mean=statistics.mean(vals), max=max(vals))
    keys = set().union(*(r['steps'] for r in selected))
    alloc_keys = set().union(*(r['alloc_steps'] for r in selected))
    return dict(total_windows=len(rows), selected_windows=len(selected), ticks=ticks,
                kb_tick=spread('kb_tick'), tick_mean_ms=spread('mean_ms'),
                pause_share=100 * sum(r['pause_ms'] for r in selected) / wall,
                pause_ms_tick=sum(r['pause_ms'] for r in selected)/ticks,
                gen0=sum(r['gen0'] for r in selected), gen1=sum(r['gen1'] for r in selected),
                gen2=sum(r['gen2'] for r in selected),
                step_mean_ms={k: statistics.mean(r['steps'].get(k, 0) for r in selected) for k in sorted(keys)},
                step_alloc_mb_window={k: statistics.mean(r['alloc_steps'].get(k, 0) for r in selected) for k in sorted(alloc_keys)},
                windows=selected)

result = dict(base=root.joinpath('base-revision.txt').read_text().strip(),
              bench={arm: [bench(root/f'bench-{arm}-{bench_tag}-{i}.log') for i in range(1,4)] for arm in ['before','after']},
              stages={stage:bench(root/f'bench-{stage}-aot.log') for stage in ['a','b','c']},
              showcase={arm:[showcase(root/f'showcase-{arm}-{i}.log') for i in range(1,4)] for arm in ['before','after']})
print(json.dumps(result, indent=2, sort_keys=True))
