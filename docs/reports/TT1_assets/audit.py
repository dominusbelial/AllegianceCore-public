#!/usr/bin/env python3
"""Audit TT-1's single sweep without rerunning tests or rewriting its evidence."""
import argparse
from collections import Counter
import json
import gzip
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'tools/test-tiers'))
from runner import PROJECTS, TIERS, read_trx, check_coverage, timing_offenders


def read_text(path):
    if path.exists():
        return path.read_text()
    with gzip.open(str(path) + '.gz', 'rt') as stream:
        return stream.read()


def read_result(path):
    path = Path(path)
    if not path.is_absolute():
        path = ROOT / path
    if path.exists() and path.suffix != '.gz':
        return read_trx(path)
    if path.suffix != '.gz':
        path = Path(str(path) + '.gz')
    with gzip.open(path, 'rb') as stream:
        return read_trx(stream)


def audit(args):
    inventory = {}
    for project in PROJECTS:
        records = list(map(json.loads, read_text(args.inventory / (project + '.jsonl')).splitlines()))
        assert not any('error' in row for row in records), project
        inventory[project] = list({row['id']: row for row in records}.values())
    report = {'projects': {}, 'tiers': {}, 'coveragePassed': True,
              'timingLayout': 'original full sweep before final P705 tier correction'}
    rechecks = {project: [] for project in PROJECTS}
    if args.perf_recheck:
        for project in PROJECTS:
            for lane in ('main', 'quarantine'):
                path = args.perf_recheck / 'perf' / (project + '-' + lane + '.trx')
                if path.exists() or Path(str(path) + '.gz').exists():
                    rechecks[project].extend(read_result(path))
            perf_names = {r['name'] for r in inventory[project] if r['tier'] == 'Perf'}
            assert all(r['outcome'] == 'Passed' and r['name'] in perf_names for r in rechecks[project])
        report['perfRecheck'] = dict(count=sum(map(len, rechecks.values())),
            originalTier='Live', finalTier='Perf',
            seconds=json.loads(read_text(args.perf_recheck / 'perf/summary.json'))['seconds'])
    all_fast = []
    all_inventory = [row for rows in inventory.values() for row in rows]
    for tier in TIERS:
        actual_by_project = {}
        for project in PROJECTS:
            actual = []
            for lane in ('main', 'quarantine'):
                path = args.results / tier / (project + '-' + lane + '.trx')
                if path.exists() or Path(str(path) + '.gz').exists():
                    actual.extend(read_result(path))
            relocated_names = {r['name'] for r in rechecks[project]}
            actual = [r for r in actual if r['name'] not in relocated_names]
            if tier == 'perf':
                actual.extend(rechecks[project])
            expected = [row['name'] for row in inventory[project] if row['tier'].lower() == tier]
            check_coverage(expected, [row['name'] for row in actual])
            actual_by_project[project] = actual
            report['projects'].setdefault(project, {})[tier] = len(actual)
        rows = [row for actual in actual_by_project.values() for row in actual]
        original = json.loads(read_text(args.results / tier / 'summary.json'))
        report['tiers'][tier] = dict(count=len(rows), observedOriginalTierWallSeconds=original['seconds'],
            outcomes=dict(Counter(row['outcome'] for row in rows)),
            failed=[row for row in rows if row['outcome'] == 'Failed'])
        if tier == 'fast':
            all_fast = rows
    limit = json.loads(read_text(args.results / 'fast/calibration.json'))['fastLimitSeconds']
    report['rawTimingOffenders'] = timing_offenders(all_fast, all_inventory, limit)
    report['guardRepairs'] = {}
    # Repairs are explicit three-run measurements of changed tests. Preserve
    # original outcomes and raw timing failures; audit current timings separately.
    if args.repairs:
        repairs = json.loads(args.repairs.read_text())
        for name, paths in repairs.items():
            measured = []
            for path in paths:
                rows = [row for row in read_result(path) if row['name'] == name]
                assert len(rows) == 1 and rows[0]['outcome'] == 'Passed', path
                measured.append(rows[0]['seconds'])
            assert len(measured) == 3, name
            assert sum(row['name'] == name for row in all_fast) == 1, name
            report['guardRepairs'][name] = measured
        current = [dict(row, seconds=max(report['guardRepairs'][row['name']]))
                   if row['name'] in report['guardRepairs'] else row for row in all_fast]
    else:
        current = all_fast
    report['currentTimingOffenders'] = timing_offenders(current, all_inventory, limit)
    report['fastLimitSeconds'] = limit
    report['runtimeTotal'] = sum(tier['count'] for tier in report['tiers'].values())
    report['baselineRuntimeTotal'] = sum('.TierConventionTests.' not in row['name'] for row in all_inventory)
    args.output.write_text(json.dumps(report, indent=2))
    print(json.dumps({key: report[key] for key in ('coveragePassed', 'runtimeTotal', 'baselineRuntimeTotal', 'currentTimingOffenders')}))
    return bool(report['currentTimingOffenders'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--results', type=Path, required=True)
    parser.add_argument('--inventory', type=Path, required=True)
    parser.add_argument('--repairs', type=Path)
    parser.add_argument('--perf-recheck', type=Path, help='targeted P705 Perf run after tier correction')
    parser.add_argument('--output', type=Path, required=True)
    sys.exit(audit(parser.parse_args()))
