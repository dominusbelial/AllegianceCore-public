#!/usr/bin/env python3
"""P738/P722 serial individual triplets, showcases and broad suites; no git."""
import hashlib
import datetime
import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
RAW = Path('/tmp/p738-p722-final-validation')
RAW.mkdir(exist_ok=True)
ENV = dict(os.environ)
for line in (ROOT / 'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key, value = line.split('=', 1)
        ENV[key] = value
ENV['ALLEGIANCE_LIVE_P660_SHOWCASE'] = '1'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
ledger = OUT / 'verification.json'
records = json.loads(ledger.read_text()) if ledger.exists() else []


def assembly(project):
    return ROOT / f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'


def test(label, project, selector=None):
    previous = next((r for r in records if r['label'] == label), None)
    if previous:
        return previous
    assert not (OUT / 'stop-requested').exists(), 'Stopped at a test boundary'
    assert assembly(project).is_file(), f'Missing test assembly for {project}'
    command = ['dotnet', 'test', str(assembly(project)), '--nologo', '--logger',
               f'trx;LogFileName={label}.trx', '--results-directory', str(RAW)]
    if selector:
        command += ['--filter', selector]
    start = time.time()
    (OUT / 'active-test.json').write_text(json.dumps(dict(label=label, project=project,
        selector=selector, started_utc=datetime.datetime.now(datetime.timezone.utc).isoformat())) + '\n')
    with (RAW / f'{label}.log').open('w') as output:
        run = subprocess.run(command, cwd=ROOT, env=ENV, stdout=output, stderr=subprocess.STDOUT)
    row = dict(label=label, project=project, command=command, exit_code=run.returncode,
               seconds=round(time.time() - start, 3), started_unix=start, ended_unix=time.time())
    trx = RAW / f'{label}.trx'
    if trx.exists():
        tree = ET.parse(trx)
        row['counters'] = tree.find('.//t:Counters', NS).attrib
        methods = {}
        for definition in tree.findall('.//t:UnitTest', NS):
            method = definition.find('t:TestMethod', NS)
            methods[definition.attrib['id']] = method.attrib['className'].split(',')[0] + '.' + method.attrib['name']
        row['failures'] = [dict(method=methods[c.attrib['testId']], name=c.attrib['testName'],
                                message=''.join(c.itertext()))
                           for c in tree.findall('.//t:UnitTestResult', NS) if c.attrib['outcome'] == 'Failed']
        row['skips'] = [c.attrib['testName'] for c in tree.findall('.//t:UnitTestResult', NS)
                        if c.attrib['outcome'] == 'NotExecuted']
    records.append(row)
    ledger.write_text(json.dumps(records, indent=2) + '\n')
    print(json.dumps({k: row[k] for k in ('label', 'exit_code', 'seconds', 'counters') if k in row}), flush=True)
    return row


def main():
    selected = set()
    for filename in ('results.json', 'supplemental-results.json', 'cleanup-results.json', 'optin-results.json'):
        for row in json.loads((OUT.parent / 'P722_assets' / filename).read_text()):
            command = row['command']
            if '--filter' not in command:
                continue
            selector = command[command.index('--filter') + 1]
            if not selector.startswith('FullyQualifiedName=') or 'P700ShowcaseLiveTests' in selector:
                continue
            project = next(re.search(r'Allegiance\.([\w.]+)\.Tests\.csproj$', part)[1]
                           for part in command if part.endswith('.Tests.csproj'))
            selected.add((project, selector.split('=', 1)[1]))
    selected = {(project, method.replace('TeamPaneLayoutTests.Build_footer_shows_money_ballot_and_wing_note',
                                        'TeamPaneLayoutTests.Build_footer_shows_money_ballot_and_unknown_wing'))
                for project, method in selected}
    extra = {
        'Core': ['P674ParityScorecardTests'],
        'ClientCore': ['P732VoiceOverTests', 'ClientWorldSoundEventTests', 'P718InterfaceMessageTests', 'P718InterfaceLoopbackTests'],
        'ClientCore.MonoGame': ['P642ConsoleZoomTests', 'P713LobbyScreenLiveTests'],
    }
    for project, classes in extra.items():
        selector = '|'.join('FullyQualifiedName~' + name for name in classes)
        command = ['dotnet', 'test', str(assembly(project)), '--nologo', '--list-tests', '--filter', selector]
        listing = subprocess.run(command, cwd=ROOT, env=ENV, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (RAW / f'list-{project}.log').write_text(listing.stdout)
        assert listing.returncode == 0, listing.stdout
        methods = {line.strip().split('(')[0] for line in listing.stdout.splitlines()
                   if line.strip().startswith('Allegiance.')}
        assert methods, listing.stdout
        selected.update((project, method) for method in methods)
    # Check the merge-specific behavior first, then the rest of the incoming cohort.
    selected = sorted(selected, key=lambda pair: (not any(name in pair[1] for name in
                      ('P722Interface', 'P718Interface', 'P732VoiceOver', 'FedMsgDispatch', 'TeamPaneLayout')), pair))
    assert all(project in ('Core', 'ClientCore', 'ClientCore.MonoGame') and assembly(project).is_file()
               for project, _ in selected), 'Invalid project selection'
    for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
        listing = subprocess.run(['dotnet', 'test', str(assembly(project)), '--nologo', '--list-tests'],
                                 cwd=ROOT, env=ENV, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        assert listing.returncode == 0, listing.stdout
        discovered = {line.strip().split('(')[0] for line in listing.stdout.splitlines()
                      if line.strip().startswith('Allegiance.')}
        assert all(method in discovered for p, method in selected if p == project), 'Stale test selector'
    previous = OUT / 'focused-methods.json'
    if previous.exists():
        assert json.loads(previous.read_text()) == [list(pair) for pair in selected]
    previous.write_text(json.dumps(selected, indent=2) + '\n')
    fingerprints = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                    for top in ('src', 'tests') for p in (ROOT / top).rglob('*')
                    if p.is_file() and p.suffix in ('.cs', '.csproj') and not {'bin', 'obj'} & set(p.parts)}
    previous = OUT / 'source-fingerprints.json'
    if previous.exists():
        assert json.loads(previous.read_text()) == fingerprints, 'Source changed during verification'
    previous.write_text(json.dumps(fingerprints, indent=2) + '\n')
    print(f'Selected {len(selected)} methods, each in three fresh individual invocations.', flush=True)
    for index, (project, method) in enumerate(selected):
        for repeat in range(1, 4):
            row = test(f'focused-{index:03}-{repeat}', project, 'FullyQualifiedName=' + method)
            if row['exit_code'] or not int(row.get('counters', {}).get('passed', 0)):
                print('Focused failure retained: ' + method, flush=True)
    for repeat in range(1, 6):
        test(f'showcase-{repeat}', 'ClientCore',
             'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
        # Both guards invoke git internally, prohibited by this task.
        selector = 'FullyQualifiedName!~MeshyGuardTests&FullyQualifiedName!~PrivatePathGuardTests' if project == 'Core' else None
        test('suite-' + project, project, selector)
    print('Gates finished. Inspect retained failures for individual qualification.', flush=True)


if __name__ == '__main__':
    with (OUT / 'runner.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        main()
