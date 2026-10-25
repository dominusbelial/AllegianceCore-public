#!/usr/bin/env python3
"""Summarize P821 local proof logs; actual roles always come from hulls, not duty labels."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re


def summarize(receipt, catalogue):
    result = json.loads(receipt.read_text())
    snapshots, initial, sales, winner = [], {}, [0.0, 0.0], None
    miner_ids, miner_deaths = {}, [0, 0]
    miner_sectors = [Counter(), Counter()]
    for line in receipt.with_name(result['Label'] + '.log').open(errors='replace'):
        if line.startswith('[p819-snapshot] '):
            sample = json.loads(line.split(' ', 1)[1])
            snapshots.append(sample)
            for miner in sample['Miners']:
                miner_sectors[miner['SideId']][miner['Sector']] += 1
        m = re.search(r'\[loadout\] (\S+) initial: (.*)', line)
        if m:
            hull = re.search(r"(?:human hull |hull=)(\d+) '([^']+)'", m[2])
            if hull:
                roles = re.findall(r'\brole=(\w+)', m[2])
                initial[m[1]] = (int(hull[1]), 'Support' if roles[-1] == 'Support' else None, roles[0])
        m = re.search(r'\[mine-sell\] ship \d+ sold ([\d.]+) ore .*to side (\d+)', line)
        if m:
            sales[int(m[2])] += float(m[1])
        m = re.search(r'\[initial-miner\] side (\d+).*ship (\d+)', line)
        if m:
            miner_ids[int(m[2])] = int(m[1])
        m = re.search(r'\[combat\] ship (\d+) killed by', line)
        if m and int(m[1]) in miner_ids:
            miner_deaths[miner_ids.pop(int(m[1]))] += 1
        m = re.search(r'\[gameover\].*iSideWinner=(-?\d+)', line)
        if m:
            winner = int(m[1])
    names = {b['Name']: b['Side'] for sample in snapshots for b in sample['Bots']}
    first_ships = {ship['Id']: ship for ship in snapshots[0]['Actual']}
    first_bots = {bot['Name']: bot for bot in snapshots[0]['Bots']}
    counts, requested = [Counter(), Counter()], [Counter(), Counter()]
    receipt_mismatches = []
    for name, (hull, support, duty) in initial.items():
        side = names[name]
        # Require an observed server hull, not merely the selector's proposal.
        observed = first_ships[first_bots[name]['ShipId']]
        if observed['Hull'] != hull:
            receipt_mismatches.append(dict(name=name, selected=hull, observed=observed['Hull']))
        counts[side][support if support and observed['Hull'] == hull else observed['Role']] += 1
        requested[side][duty] += 1
    role_samples = [Counter(), Counter()]
    for sample in snapshots:
        own = {s['Id']: s for s in sample['Actual']}
        for bot in sample['Bots']:
            if bot['ShipId'] in own:
                ship = own[bot['ShipId']]
                role_samples[bot['Side']]['Pod' if catalogue[ship['Hull']]['IsPod'] else ship['Role']] += 1
    return dict(source=str(receipt.parent), label=result['Label'], civs=result['Civs'], winner=winner,
                sides=result['Sides'], stage=result['Stage'], sim=result['Sim'], wall=result['Wall'],
                requested=requested, initialActual=counts, oreSold=[round(v, 1) for v in sales],
                initialReceiptMismatches=receipt_mismatches,
                minersAtEnd=result['Miners'], initialMinerDeaths=miner_deaths,
                minerSectorSamples=miner_sectors, hullRoleSamples=role_samples,
                failures=result['Failures'], samples=len(snapshots),
                limits='Initial classes use the first authoritative server snapshot; selected/observed hull mismatches are explicit; support is identified by negative-power purchase policy; repeated hull samples count support by hull class and identify pods separately by native ability bit. Ore is logged sale quantity, not credits. Miner census is authoritative; sector counts are 30-second repeated samples. Short matches are not a balance estimate.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('receipts', type=Path, nargs='+')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--hull-roles', type=Path, required=True)
    args = parser.parse_args()
    catalogue = {h['HullId']: h for h in json.loads(args.hull_roles.read_text())}
    args.output.write_text(json.dumps([summarize(p, catalogue) for p in args.receipts], indent=2) + '\n')
