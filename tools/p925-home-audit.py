#!/usr/bin/env python3
"""Offline P925 receipts. No inferred damage, invented timestamps or EOF wins."""
import argparse
import collections
import gzip
import hashlib
import json
import math
import re
import statistics
from pathlib import Path


def lines(path):
    with (gzip.open if path.suffix == '.gz' else open)(path, 'rt') as stream:
        yield from stream


def stage(trip):
    return next((s for s in ('Bay', 'Radius', 'Home') if trip.get(s + 'At') is not None), 'Away')


def distribution(values):
    values = sorted(values)
    return dict(n=len(values), median=statistics.median(values) if values else None,
                p95=values[min(len(values)-1, math.ceil(.95*len(values))-1)] if values else None)


def fields(line):
    return dict(re.findall(r'(\w+)=([^\s]+)', line))


def analyze(server, workers, geometry=None):
    matches, identities, trips, damage = {}, {}, [], []
    number = None
    for line in lines(server):
        if '[matchup]' in line:
            f = fields(line)
            number = int(f['match'])
            matches[number] = dict(match=number, opening=line.split()[0], complete=False,
                                   homes=[int(f['home0']), int(f['home1'])],
                                   civs=[int(f['side0'].split(':')[0]), int(f['side1'].split(':')[0])],
                                   stationLosses=[])
        if number is None:
            continue
        # IDs are explicit match-local identities; assert no reuse across rounds.
        for pattern in (r'\[pod-trip\] eject ship=(\d+)', r'\[scoring\] ship (\d+) \(side'):
            if m := re.search(pattern, line):
                ship = int(m[1])
                if ship in identities and identities[ship] != number:
                    raise ValueError('ship id reused across matches')
                identities[ship] = number
        if '[pod-trip] {' in line:
            trip = json.loads(line.split('[pod-trip] ', 1)[1])
            trip['Match'] = number
            trips.append(trip)
            identities.setdefault(trip['Ship'], number)
        if '[station-damage]' in line:
            damage.append(dict(match=number, utc=line.split()[0], **fields(line), receipt=line.strip()))
        if '[station] station' in line and 'destroyed' in line:
            matches[number]['stationLosses'].append(line.strip())
        if '[gameover]' in line and 'Stage Over' in line:
            matches[number].update(complete=True, result=line.strip())
    observed = {}
    if geometry is not None:
        for row in json.loads(geometry.read_text())['Stations']:
            if 'Geometry' in row:
                observed[(row['Match'], row['Geometry']['StationId'], row['Geometry']['Type'])] = row['Geometry']
    groups = []
    for number, match in matches.items():
        for side in (0, 1):
            subset = [t for t in trips if t['Match'] == number and t['Side'] == side]
            oxygen = [t for t in subset if t['Outcome'] == 'oxygen']
            bay_distances, bay_offsets = [], []
            for t in oxygen:
                shape = observed.get((number, t['Station'], t['StationType']))
                if shape is None or t['EndSector'] != t['HomeSector'] or not shape['Dock']:
                    continue
                measurements = []
                for bay in shape['Dock']:
                    plane = bay['Planes'][0]
                    delta = [a-b for a, b in zip(t['EndPosition'], plane['Position'])]
                    direction = plane['Direction']
                    along = sum(a*b for a, b in zip(delta, direction)) / math.sqrt(sum(x*x for x in direction))
                    measurements.append((math.sqrt(sum(x*x for x in delta)),
                                         math.sqrt(max(0, sum(x*x for x in delta) - along*along))))
                bay_distances.append(min(d for d, _ in measurements))
                bay_offsets.append(min(o for _, o in measurements))
            groups.append(dict(match=number, complete=match['complete'], side=side,
                               home=match['homes'][side], civ=match['civs'][side],
                               outcomes=dict(collections.Counter(t['Outcome'] for t in subset)),
                               oxygenStages=dict(collections.Counter(stage(t) for t in oxygen)),
                               dockStages=dict(collections.Counter(stage(t) for t in subset if t['Outcome'] == 'docked')),
                               radiusToOxygen=distribution(t['EndAt']-t['RadiusAt'] for t in oxygen if t['RadiusAt'] is not None),
                               radiusReachedThenEndedOutside=sum(t['RadiusAt'] is not None and t['EndSector'] == t['HomeSector']
                                   and t.get('StationPosition') is not None and math.dist(t['EndPosition'], t['StationPosition']) > t['StationRadius'] + 2
                                   for t in oxygen),
                               endDistance=distribution(math.dist(t['EndPosition'], t['StationPosition']) for t in oxygen
                                                        if t.get('StationPosition') and t['EndSector'] == t['HomeSector']),
                               endBayMouthDistance=distribution(bay_distances), endBayLineOffset=distribution(bay_offsets),
                               podHulls=dict(collections.Counter(t['PodHull'] for t in subset))))
    waves, finales, unmatched = [], [], []
    for worker in workers:
        current = None
        active = {}
        for row, line in enumerate(lines(worker), 1):
            # Worker streams lack UTC; only advance on an explicit server-known
            # identity. Do not split at wave resets or infer times from line order.
            ids = re.findall(r'\b(?:ship[ =]|sender=)(\d+)', line)
            known = [identities[int(s)] for s in ids if int(s) in identities]
            if known:
                current = max([current or 0] + known)
            if '[strike]' not in line:
                continue
            f = fields(line)
            if 'wave' not in f:
                if ' finale commit ' in line:
                    finales.append(dict(worker=worker.name, row=row, match=current, **f))
                continue
            key = (current, f['side'], f['wave'])
            item = dict(worker=worker.name, row=row, match=current, **f)
            if ' commit ' in line:
                item['kind'] = 'commit'
                waves.append(item)
                active[key] = item
            elif ' end ' in line:
                if key in active:
                    active.pop(key)['end'] = item
                else:
                    unmatched.append(item)
            elif ' abort ' in line:
                item['kind'] = 'abort'
                waves.append(item)
    # Keep the exact native end pose for deterministic replay, not a fabricated
    # ejection pose. At most three near-home oxygen endings per home/civ/model.
    samples = []
    counts = collections.Counter()
    for t in trips:
        if t['Outcome'] != 'oxygen' or t['EndSector'] != t['HomeSector'] or not t.get('StationPosition'):
            continue
        if t['Station'] not in (4328, 4329):
            continue
        key = (t['HomeSector'], t['PodHull'], math.dist(t['EndPosition'], t['StationPosition']) <= 1500)
        if counts[key] < 3:
            samples.append(t)
            counts[key] += 1
    stations = {}
    for t in trips:
        if t.get('StationPosition'):
            stations[(t['Match'], t['Station'], t['StationType'])] = {k: t[k] for k in (
                'Match', 'Station', 'Side', 'HomeSector', 'StationType', 'StationModel',
                'StationRadius', 'StationPosition', 'StationForward', 'StationUp')}
    return dict(inputs=[dict(name=p.name, sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in [server, *workers]],
                matches=list(matches.values()), pods=groups, waves=waves, finales=finales,
                unmatchedWaveEnds=unmatched, damage=damage, stations=list(stations.values()), samples=samples,
                limits=['Worker match attribution uses preceding explicit server-known ship IDs; no worker UTC exists.',
                        'Sample poses are oxygen endings, restarted with fresh oxygen; original radius/ejection poses were not logged.',
                        'Wave station fractions are observations, not damage credited to that wave; -1 means unknown.',
                        'Station-damage receipts are rate-limited and cannot yield total damage or exact first damage.',
                        'Partial match and unended waves remain censored. Diagnostic HomeSector may differ from bot target.'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('server', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('workers', nargs='*', type=Path)
    parser.add_argument('--geometry', type=Path)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(analyze(args.server, args.workers, args.geometry), indent=2) + '\n')
