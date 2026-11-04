#!/usr/bin/env python3
"""Summarize timestamped, filtered P924 server evidence without inventing missing events."""
import argparse
import collections
import gzip
import hashlib
import json
import re
from datetime import datetime
from pathlib import Path


def lines(path):
    opener = gzip.open if path.suffix == '.gz' else open
    with opener(path, 'rt', errors='replace') as stream:
        for line in stream:
            stamp, _, body = line.strip().partition(' ')
            yield datetime.fromisoformat(stamp.replace('Z', '+00:00')), body


def analyze(path):
    # Ship IDs are monotonic in this capture; learn identities from later final
    # scores as well as pod-trip/chat receipts. Never guess a side from parity.
    identity = {}
    for _, line in lines(path):
        for pattern in [r'\[scoring\] ship (\d+) \(side (\d+)\) final score',
                        r'\[pod-trip\] \w+ ship=(\d+) side=(\d+)',
                        r'\[chat\] \d+ ship (\d+) side (\d+)']:
            if m := re.search(pattern, line):
                ship, side = map(int, m.groups())
                if ship in identity and identity[ship] != side:
                    raise ValueError('capture reuses a ship ID across sides')
                identity[ship] = side
    matches = []
    def new(number, stamp=None):
        return dict(match=number, openingUtc=stamp.isoformat() if stamp else None,
                    openingCensored=stamp is None, sides=[dict(kills=0, deaths=0, finalKills=0,
                    finalDeaths=0, finalPlayers=0, preKills=0, preDeaths=0, finaleKills=0,
                    finaleDeaths=0, firstUndockUtc=None, firstStrikeOrderUtc=None,
                    ejectionSectors=collections.Counter(), podOutcomes=collections.Counter(),
                    oxygenEndSectors=collections.Counter(), money=collections.Counter(),
                    timeline=collections.defaultdict(collections.Counter)) for _ in range(2)],
                    firstObservedUtc=None, lastObservedUtc=None, finaleUtc=None,
                    stationAttacks=[], stationLosses=[], unknownScoring=collections.Counter())
    current = new(1)
    between = False
    for stamp, line in lines(path):
        if m := re.search(r'\[matchup\].*match=(\d+)', line):
            if not between and current['lastObservedUtc'] is not None and 'endUtc' not in current:
                matches.append(current)
            current = new(int(m[1]), stamp)
            current['matchup'] = line
            between = False
        if between:
            continue
        current['firstObservedUtc'] = current['firstObservedUtc'] or stamp.isoformat()
        current['lastObservedUtc'] = stamp.isoformat()
        if '[finale]' in line and 'bounded finale armed' in line:
            current['finaleUtc'] = stamp.isoformat()
            current['armedBy'] = int(re.search(r'armedBy=(\d+)', line)[1])
        if m := re.search(r'\[scoring\] (killer|victim) ship (\d+) (kill|death) \+1', line):
            metric = 'kills' if m[3] == 'kill' else 'deaths'
            side = identity.get(int(m[2]))
            if side not in (0, 1): current['unknownScoring'][metric] += 1
            else:
                data = current['sides'][side]
                data[metric] += 1
                data[('finale' if current['finaleUtc'] else 'pre') + metric.title()] += 1
                # Bins are UTC wall-clock, not inferred simulation seconds.
                origin = datetime.fromisoformat(current['openingUtc'] or current['firstObservedUtc'])
                data['timeline'][str(int((stamp-origin).total_seconds() // 300))][metric] += 1
        if m := re.search(r'\[scoring\] ship (\d+) \(side (\d+)\) final score .* kills=(\d+) deaths=(\d+)', line):
            data = current['sides'][int(m[2])]
            data['finalKills'] += int(m[3]); data['finalDeaths'] += int(m[4]); data['finalPlayers'] += 1
        if m := re.search(r'\[pod-trip\] eject ship=\d+ side=(\d+) sector=(\d+)', line):
            current['sides'][int(m[1])]['ejectionSectors'][m[2]] += 1
        if line.startswith('[pod-trip] {'):
            trip = json.loads(line.split(' ', 1)[1])
            if trip.get('Side') in (0, 1):
                data = current['sides'][trip['Side']]
                data['podOutcomes'][trip['Outcome']] += 1
                if trip['Outcome'] == 'oxygen':
                    data['oxygenEndSectors'][str(trip['EndSector'])] += 1
        if m := re.search(r'\[undock\] ship (\d+) undocked', line):
            if (side := identity.get(int(m[1]))) in (0, 1):
                data = current['sides'][side]; data['firstUndockUtc'] = data['firstUndockUtc'] or stamp.isoformat()
        # Native Igc/igc.h:902,907: Attack=1; Repair=6 is support, not a strike.
        if m := re.search(r'\[queuedcmd\] queued order commandID=1 on ship (\d+) .*Station', line):
            if (side := identity.get(int(m[1]))) in (0, 1):
                data = current['sides'][side]; data['firstStrikeOrderUtc'] = data['firstStrikeOrderUtc'] or stamp.isoformat()
        if m := re.search(r'\[moneychange\] side (\d+).* dMoney=(-?\d+)', line):
            money = current['sides'][int(m[1])]['money']; value = int(m[2])
            money['events'] += 1; money['netDelta'] += value
            money['positiveDelta' if value >= 0 else 'negativeDelta'] += value
        if '[station]' in line and '[under attack]' in line:
            side = re.search(r'-> side (\d+)', line)
            current['stationAttacks'].append(dict(utc=stamp.isoformat(), defender=int(side[1]) if side else None, receipt=line))
        if '[station] station' in line and 'destroyed' in line:
            launcher = re.search(r'launcher (\d+)', line)
            current['stationLosses'].append(dict(utc=stamp.isoformat(), attacker=identity.get(int(launcher[1])) if launcher else None, receipt=line))
        if '[finale] deadline winner' in line: current['deadline'] = line
        if '[finale] Station kills:' in line: current['stationKills'] = line
        if m := re.search(r'\[gameover\] mission .*Stage Over, iSideWinner=(-?\d+).*match=(\d+)', line):
            current.update(winner=int(m[1]), match=int(m[2]), endUtc=stamp.isoformat(), result=line)
            matches.append(current)
            # Ignore trailing rebuild chatter until its matchup opens the next round.
            current = new(int(m[2])+1)
            between = True
    if not between and current['lastObservedUtc'] is not None:
        matches.append(current)
    return dict(input=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                matches=matches, limits=[
                    'Filtered server logs omit spawn, full ship-warp, damage and worker telemetry; pod-trip routes are partial observations. Identity comes only from explicit side receipts.',
                    'Observed scoring is counted from kill +1 / death +1 receipts; final peer scores are separate and may omit NPC/departed scores.',
                    'Ejection sectors locate hull losses, not all kills or true deaths. No invented sector attribution for other events.',
                    'First queued station Attack is an order opportunity, not proof of launch, weapon fire or arrival.',
                    'Money deltas are logged transactions, not commander wallet balances, income or mining totals.',
                    'Pod outcomes count completions inside the observed match window; removed is not death and unfinished trips remain censored.',
                    'UTC bins are 300 wall seconds from matchup (or first observation for a censored opening). Opening includes ready/countdown time.'
                ])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path); parser.add_argument('output', type=Path)
    args = parser.parse_args()
    args.output.write_text(json.dumps(analyze(args.input), indent=2) + '\n')
