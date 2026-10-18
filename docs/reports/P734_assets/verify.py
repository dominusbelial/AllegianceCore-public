#!/usr/bin/env python3
"""Reproducible P734 merge validation. Run from the repository root."""
import gzip, json, os, re, subprocess, sys, time
import xml.etree.ElementTree as ET
from pathlib import Path
ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
os.chdir(ROOT)
# Same machine-local corpus configuration as scripts/run-tests.sh, without printing it.
for line in (ROOT / 'local.env').read_text().splitlines():
    if line.strip() and not line.lstrip().startswith('#') and '=' in line:
        key, value = line.split('=', 1)
        os.environ[key.strip()] = value.strip().strip('"').strip("'")
os.environ['NUGET_HTTP_CACHE_PATH'] = '/tmp/p734-nuget-http'
os.environ['ALLEGIANCE_LIVE_P542_PROOF'] = '1'
os.environ['ALLEGIANCE_LIVE_P660_SHOWCASE'] = '1'
if len(sys.argv) > 1 and sys.argv[1] in ('suites', 'retries') and (OUT / 'BLOCK_SUITE_LAUNCH').exists():
    raise SystemExit('Final suites blocked pending docking correction and diagnostic cleanup')
LEDGER = OUT / os.environ.get('P734_LEDGER', 'verification.json')
results = json.loads(LEDGER.read_text()) if LEDGER.exists() else []

def save(): LEDGER.write_text(json.dumps(results, indent=2) + '\n')

def test(project, name, filter_text=None, project_path=None):
    stem = re.sub(r'[^A-Za-z0-9_.-]', '-', name)
    trx = OUT / (stem + '.trx')
    cmd = ['dotnet', 'test', project_path or f'tests/Allegiance.{project}.Tests', '--no-build', '--nologo',
           '--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(OUT)]
    if project_path and project_path.endswith('.dll'):
        cmd = ['dotnet', 'vstest', project_path, '--logger:trx;LogFileName=' + trx.name,
               '--ResultsDirectory:' + str(OUT)]
        if filter_text: cmd += ['--TestCaseFilter:' + filter_text]
    elif filter_text: cmd += ['--filter', filter_text]
    start = time.time()
    with (OUT / (stem + '.log')).open('w') as log:
        proc = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT)
    row = dict(name=name, project=project, filter=filter_text, exit=proc.returncode, seconds=round(time.time()-start, 3))
    if project_path: row['project_path'] = project_path
    if trx.exists():
        tree = ET.parse(trx)
        row['counters'] = tree.find('.//t:Counters', NS).attrib
        row['failures'] = []
        row['skipped_tests'] = [r.attrib['testName'] for r in tree.findall('.//t:UnitTestResult', NS) if r.attrib['outcome'] == 'NotExecuted']
        row['observed_skipped'] = len(row['skipped_tests'])
        defs = {d.attrib['id']: d.find('t:TestMethod', NS).attrib for d in tree.findall('.//t:UnitTest', NS)}
        for r in tree.findall('.//t:UnitTestResult', NS):
            if r.attrib['outcome'] == 'Failed':
                method = defs[r.attrib['testId']]
                row['failures'].append(dict(method=method['className'] + '.' + method['name'],
                    message=r.findtext('.//t:Message', '', NS)))
        row['output'] = [r.findtext('t:Output/t:StdOut', '', NS) for r in tree.findall('.//t:UnitTestResult', NS) if r.findtext('t:Output/t:StdOut', '', NS)]
        row['evidence'] = trx.name + '.gz'
        with gzip.open(str(trx) + '.gz', 'wb') as z: z.write(trx.read_bytes())
        trx.unlink()
    results.append(row); save()
    print(json.dumps(row), flush=True)
    return row


if __name__ == '__main__':
    mode = sys.argv[1]
    if mode in ('focus', 'focus-final'):
        methods = json.loads((ROOT / 'docs/reports/P708_assets/affected-methods-complete.json').read_text())
        src = (ROOT / 'tests/Allegiance.ClientCore.Tests/P707ParkingTests.cs').read_text()
        methods += [('ClientCore', 'Allegiance.ClientCore.Tests.BotFixRegressionTests.' + m) for m in re.findall(r'^    public void (\w+)\(', src, re.M)]
        if mode == 'focus-final':
            src = (ROOT / 'tests/Allegiance.Core.Tests/P699cStationCorpusTests.cs').read_text()
            methods += [('Core', 'Allegiance.Core.Tests.P699cStationCorpusTests.' + m) for m in re.findall(r'^    public void (\w+)\(', src, re.M)]
        (OUT / ('affected-methods-final.json' if mode == 'focus-final' else 'affected-methods.json')).write_text(json.dumps(methods, indent=2) + '\n')
        for idx, (project, method) in enumerate(methods):
            for run in range(1, 4):
                if any(r['name'] == f'{mode}-{idx:03}-{run}' and r['filter'] == 'FullyQualifiedName=' + method and r['exit'] == 0 and int(r['counters']['executed']) > 0 for r in results): continue
                r = test(project, f'{mode}-{idx:03}-{run}', 'FullyQualifiedName=' + method)
                assert r['exit'] == 0 and int(r['counters']['executed']) > 0 and int(r['counters']['notExecuted']) == 0, r
    elif mode == 'parking':
        for run in range(1, 11):
            test('ClientCore', f'parking-{run:02}', 'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    elif mode == 'suites':
        for sweep in range(1, 3):
            for project in ['Core', 'ClientCore', 'ClientCore.MonoGame', 'Lobby']:
                test(project, f'suite-{sweep}-' + project)
    elif mode == 'retries':
        cases = {(r['project'], f['method']) for r in results if r['name'].startswith('suite-') for f in r.get('failures', [])}
        # Preserve and classify unrelated failures in the earlier Core-only
        # diagnostics too. The docking assertion was a corrected merge bug.
        for artifact in OUT.glob('diagnostic-Core-*.trx.gz'):
            with gzip.open(artifact, 'rb') as z: tree = ET.fromstring(z.read())
            defs = {d.attrib['id']: d.find('t:TestMethod', NS).attrib for d in tree.findall('.//t:UnitTest', NS)}
            for result in tree.findall('.//t:UnitTestResult', NS):
                if result.attrib['outcome'] != 'Failed': continue
                method = defs[result.attrib['testId']]
                full = method['className'] + '.' + method['name']
                if full.endswith('.Npc_docking_thrusters_survive_idle_combat_and_clear_on_next_order'): continue
                cases.add(('Core', full))
        cases = sorted(cases)
        for idx, (project, method) in enumerate(cases):
            for run in range(1, 4):
                test(project, f'suite-retry-{idx:03}-{run}', 'FullyQualifiedName=' + method)
    else: raise ValueError(mode)
