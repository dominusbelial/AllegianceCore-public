#!/usr/bin/env python3
"""Bound temporary-role observations by adjacent worker-clock log records."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re


def audit(path, catalogue, roles, civs):
    starting = {h['HullId']: side for side, civ in enumerate(civs)
                for c in catalogue if c['CivilizationId'] == civ for h in c['Initial']}
    available = [{h['Role'] for c in catalogue if c['CivilizationId'] == civ for h in c['Initial']} for civ in civs]
    names, epoch, last_step = {}, [0, 0], [0, 0]
    initial, fallbacks = [Counter(), Counter()], Counter()
    episodes, starts, pending, clock = {}, {}, [], 0.0
    def flush(upper):
        for event in pending:
            event['upper'] = upper
        pending.clear()
    for line in path.open(errors='replace'):
        m = re.match(r'\[(?:intel|scout|opening)\] t=([\d.]+)s', line)
        if m:
            clock = max(clock, float(m[1]))
            flush(clock)
        m = re.search(r'\[research\] side=(\d+).*step=(\d+)/(\d+)', line)
        if m:
            side, step = int(m[1]), int(m[2])
            if step < last_step[side]: epoch[side] += 1
            last_step[side] = step
            starts.setdefault((side, epoch[side]), clock)
        m = re.search(r'\[loadout\] (\S+)( initial)?: (.*)', line)
        if not m: continue
        name, is_initial, reason = m.groups()
        hull = re.search(r"(?:human hull |hull=)(\d+) '([^']+)'", reason)
        labels = re.findall(r'\brole=(\w+)', reason)
        if not hull or not labels: continue
        hull = int(hull[1]); actual = roles[hull]
        if is_initial:
            side = starting[hull]
            names[name] = (side, labels[0])
            initial[side]['Support' if labels[-1] == 'Support' else actual] += 1
        if name not in names: continue
        side, duty = names[name]
        if 'temporary ' in reason:
            fallbacks[f'side{side}:{duty}->{actual}'] += 1
        if duty not in {'Fighter', 'Interceptor', 'Scout'} or duty in available[side]: continue
        key = (side, epoch[side])
        ep = episodes.setdefault(key, dict(side=side, episode=epoch[side], firstExact=None,
                                          lastFallback=None, exactPurchases=0, fallbackPurchases=0))
        bound = dict(lower=clock, upper=None, researchStep=last_step[side], hull=hull)
        pending.append(bound)
        if actual == duty:
            ep['exactPurchases'] += 1
            if ep['firstExact'] is None: ep['firstExact'] = bound
        elif 'temporary ' in reason:
            ep['fallbackPurchases'] += 1
            ep['lastFallback'] = bound
    for key, ep in episodes.items():
        ep['firstResearchClock'] = starts.get(key)
        if ep['firstExact'] and key in starts:
            ep['firstExactElapsedBounds'] = [ep['firstExact']['lower'] - starts[key], ep['firstExact']['upper'] - starts[key] if ep['firstExact']['upper'] is not None else None]
    return dict(file=str(path), initialActual=initial, fallbackLines=fallbacks,
                lastWorkerClock=clock, episodes=list(episodes.values()),
                limits='Bounds use adjacent intel/scout/opening worker clocks (seconds since worker start), not exact purchase timestamps. Research step is last observed status, not proof of completion. First exact hull is an observed purchase; flying survivors may retain old hulls. Missing upper bound is right-censored. Defensive mine duty is not a temporary-role failure.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('logs', type=Path, nargs='+')
    parser.add_argument('--catalogue', type=Path, required=True)
    parser.add_argument('--hull-roles', type=Path, required=True)
    parser.add_argument('--civs', type=int, nargs=2, default=[25, 3])
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    catalogue = json.loads(args.catalogue.read_text())
    roles = {h['HullId']: h['Role'] for h in json.loads(args.hull_roles.read_text())}
    args.output.write_text(json.dumps([audit(p, catalogue, roles, args.civs) for p in args.logs], indent=2) + '\n')
