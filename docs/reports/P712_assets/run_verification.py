#!/usr/bin/env python3
"""P712 sequential exact-method triplets and exactly two full suite rounds."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
ASSETS = ROOT / 'docs/reports/P712_assets'
RESULTS = Path('/tmp/p712-results')
RESULTS.mkdir(exist_ok=True)
ENV = os.environ.copy()
for line in (ROOT / 'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key, value = line.split('=', 1)
        ENV[key] = value
for key in ['ALLEGIANCE_LIVE_P542_PROOF', 'ALLEGIANCE_LIVE_P660_SHOWCASE',
            'ALLEGIANCE_LIVE_CAPTURE_PROOF', 'ALLEGIANCE_LIVE_P539_BASELINE',
            'ALLEGIANCE_LIVE_P539C_REPRO']:
    ENV[key] = '1'
ENV['NUGET_HTTP_CACHE_PATH'] = '/tmp/p712-nuget-http'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
LEDGER = []

def save():
    pending = ASSETS / 'verification.pending.json'
    pending.write_text(json.dumps(LEDGER, indent=2) + '\n')
    pending.replace(ASSETS / 'verification.json')

def run(project, label, test_filter=None, list_tests=False):
    if Path('/tmp/p712-final-stop').exists():
        raise SystemExit('Stopped at test boundary for review')
    stem = f'{len(LEDGER)+1:04d}-{label}'
    trx, log = RESULTS / (stem + '.trx'), RESULTS / (stem + '.log')
    cmd = ['dotnet', 'test', f'tests/{project}', '--no-build', '--no-restore', '--nologo']
    if ENV.get('P712_ARTIFACTS'):
        cmd += ['--artifacts-path', ENV['P712_ARTIFACTS']]
    if list_tests:
        cmd += ['--list-tests']
    else:
        cmd += ['--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(RESULTS)]
    if test_filter:
        cmd += ['--filter', test_filter]
    env = ENV.copy()
    proof = RESULTS / (stem + '-proof')
    env['ALLEGIANCE_P705_PROOF_DIRECTORY'] = str(proof)
    env['ALLEGIANCE_P703_PROOF_DIRECTORY'] = str(proof)
    start_utc = datetime.datetime.now(datetime.timezone.utc).isoformat()
    start = time.monotonic()
    print(f'START {stem} {project} {test_filter or "UNFILTERED"}', flush=True)
    with log.open('w') as stream:
        p = subprocess.run(cmd, cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT)
    row = dict(label=label, project=project, filter=test_filter, command=cmd,
               startUtc=start_utc,
               seconds=round(time.monotonic()-start, 3), exitCode=p.returncode,
               log=str(log), trx=str(trx), proof=str(proof), failures=[], skipped=[])
    if trx.exists():
        tree = ET.parse(trx)
        counters = tree.find('.//t:Counters', NS)
        row['counts'] = dict(counters.attrib) if counters is not None else {}
        methods = {}
        for ut in tree.findall('.//t:UnitTest', NS):
            method = ut.find('t:TestMethod', NS)
            if method is not None:
                methods[ut.attrib['id']] = method.attrib['className'] + '.' + method.attrib['name']
        for result in tree.findall('.//t:UnitTestResult', NS):
            if result.attrib['outcome'] == 'Failed':
                row['failures'].append(dict(method=methods.get(result.attrib['testId']),
                    name=result.attrib['testName'],
                    message=result.findtext('t:Output/t:ErrorInfo/t:Message', '', NS),
                    stack=result.findtext('t:Output/t:ErrorInfo/t:StackTrace', '', NS)))
            if result.attrib['outcome'] == 'NotExecuted':
                row['skipped'].append(result.attrib['testName'])
        row['trxSha256'] = hashlib.sha256(trx.read_bytes()).hexdigest()
    LEDGER.append(row)
    save()
    print(f'END {stem} exit={p.returncode} seconds={row["seconds"]} counts={row.get("counts", {})}', flush=True)
    return row


def isolate(row):
    methods = sorted({f['method'] for f in row['failures'] if f['method']})
    for index, method in enumerate(methods):
        outcomes = []
        for repeat in range(1, 4):
            retry = run(row['project'], f'isolation-{len(LEDGER)}-{index}-{repeat}', 'FullyQualifiedName=' + method)
            outcomes.append(retry['exitCode'] == 0 and int(retry.get('counts', {}).get('passed', 0)) > 0)
        row.setdefault('isolation', []).append(dict(method=method, passed=outcomes))
        save()
        if not all(outcomes):
            print(f'REQUIRES REVIEW {method}: {outcomes}', flush=True)
    if row['exitCode'] and not methods:
        print('INCOMPLETE RUN: no named failed methods', flush=True)


def main():
    historical = json.loads((ROOT / 'docs/reports/P691_RESULTS.json').read_text())
    selected = {'Allegiance.Core.Tests': set(), 'Allegiance.ClientCore.Tests': set()}
    for row in historical:
        if row.get('phase') == 'discovery':
            selected[row['project']].update(row['methods'])
    patterns = {
        'Allegiance.Core.Tests': [r'P691', r'P702Research', r'P703', r'P705', r'P712',
                                 r'Bucket', r'Economy', r'TechTreeDevelopment', r'LiveBuyGateDevelopment', r'SuccessorUpgrade'],
        'Allegiance.ClientCore.Tests': [r'P691', r'P703', r'P704', r'P705', r'BucketFunding'],
    }
    if ENV.get('P712_RESUME') == '1' and (ASSETS / 'selected-methods.json').exists():
        selected = {k: set(v) for k,v in json.loads((ASSETS / 'selected-methods.json').read_text()).items()}
    for project in ([] if ENV.get('P712_RESUME') == '1' else selected):
        found = run(project, 'discovery-' + project, list_tests=True)
        for line in Path(found['log']).read_text().splitlines():
            name = line.strip().split('(', 1)[0]
            if name.startswith(project + '.') and any(re.search(pattern, name) for pattern in patterns[project]):
                selected[project].add(name)
    (ASSETS / 'selected-methods.json').write_text(json.dumps({k: sorted(v) for k,v in selected.items()}, indent=2) + '\n')
    print('SELECTED', {k:len(v) for k,v in selected.items()}, flush=True)
    # New regressions first, then historical economy/tech/research coverage.
    tasks = [(p, m) for p, methods in selected.items() for m in sorted(methods)]
    tasks.sort(key=lambda task: ('P712' not in task[1], task[0], task[1]))
    for project, method in tasks:
        for repeat in range(1, 4):
            if sum(r['project'] == project and r['label'].startswith('focused-') and r['filter'] == 'FullyQualifiedName=' + method for r in LEDGER) >= repeat:
                continue
            row = run(project, f'focused-{method.split(".")[-1]}-{repeat}', 'FullyQualifiedName=' + method)
            if row['exitCode']:
                isolate(row)
                if not row['failures'] or any(not all(r['passed']) for r in row.get('isolation', [])):
                    raise SystemExit('REQUIRES REVIEW before suites')
    gate = 'Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds'
    for repeat in range(1, 11):
        if any(r['label'] == f'never-parks-{repeat}' for r in LEDGER):
            continue
        row = run('Allegiance.ClientCore.Tests', f'never-parks-{repeat}', 'FullyQualifiedName=' + gate)
        if row['exitCode']:
            isolate(row)
    for repeat in range(1, 3):
        for project in ['Allegiance.Core.Tests', 'Allegiance.ClientCore.Tests',
                        'Allegiance.ClientCore.MonoGame.Tests', 'Allegiance.Lobby.Tests']:
            if any(r['label'] == f'full-{repeat}-{project}' for r in LEDGER):
                continue
            row = run(project, f'full-{repeat}-{project}')
            if row['exitCode']:
                isolate(row)
    print('ALL SCHEDULED VERIFICATION COMPLETE', flush=True)

if __name__ == '__main__':
    main()
