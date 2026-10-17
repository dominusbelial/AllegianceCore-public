#!/usr/bin/env python3
"""Reproducible P708 local validation. Run from the repository root."""
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
os.environ['NUGET_HTTP_CACHE_PATH'] = '/tmp/p708-nuget-http'
os.environ['ALLEGIANCE_LIVE_P542_PROOF'] = '1'
os.environ['ALLEGIANCE_LIVE_P660_SHOWCASE'] = '1'
LEDGER = OUT / 'verification.json'
results = json.loads(LEDGER.read_text()) if LEDGER.exists() else []

def save(): LEDGER.write_text(json.dumps(results, indent=2) + '\n')

def test(project, name, filter_text=None, project_path=None):
    stem = re.sub(r'[^A-Za-z0-9_.-]', '-', name)
    trx = OUT / (stem + '.trx')
    cmd = ['dotnet', 'test', project_path or f'tests/Allegiance.{project}.Tests', '--no-build', '--nologo',
           '--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(OUT)]
    if filter_text: cmd += ['--filter', filter_text]
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
        row['evidence'] = trx.name + '.gz'
        with gzip.open(str(trx) + '.gz', 'wb') as z: z.write(trx.read_bytes())
        trx.unlink()
    results.append(row); save()
    print(json.dumps(row), flush=True)
    return row

mode = sys.argv[1]
if mode == 'build':
    with (OUT / 'build.log').open('w') as log:
        rc = subprocess.run(['dotnet', 'build', 'AllegianceCore.slnx', '-m:8', '-v:q'], stdout=log, stderr=subprocess.STDOUT).returncode
    assert rc == 0, (OUT / 'build.log').read_text()
elif mode == 'development':
    r = test('Core', 'development-final', 'FullyQualifiedName~P708ShipUpdateTests|FullyQualifiedName~NpcWingman|FullyQualifiedName~NpcRipcordTravel|FullyQualifiedName~P652EconomyDroneAiTests|FullyQualifiedName~P676SimRulesTests')
    assert r['exit'] == 0
elif mode in ('focus', 'focus-final'):
    methods = []
    for filename in ['P708ShipUpdateTests', 'P652EconomyDroneAiTests', 'NpcWingmanAttackLiveTests', 'NpcWingmanDefendLiveTests', 'NpcWingmanLeadLiveTests', 'NpcWingmanRangeMaintenanceLiveTests', 'NpcRipcordTravelLiveTests']:
        source = (ROOT / f'tests/Allegiance.Core.Tests/{filename}.cs').read_text()
        methods += [('Core', f'Allegiance.Core.Tests.{filename}.{m}') for m in re.findall(r'^    public void (\w+)\(', source, re.M)]
    methods += [('Core', 'Allegiance.Core.Tests.P676SimRulesTests.' + m) for m in ['Combat_uses_native_point_one_radian_lead_gate', 'Combat_range_gate_uses_intercept_time_and_relative_speed', 'Allied_neighbor_does_not_starve_default_or_defend_enemy_acquisition']]
    methods += [('Core', 'Allegiance.Core.Tests.P650ServerRulesTests.Local_mining_battle_reports_income_and_leaves_drained_rocks'), ('ClientCore', 'Allegiance.ClientCore.Tests.BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights'), ('Core', 'Allegiance.Core.Tests.LiveInSpaceEjectPodAliveTests.Ejected_pod_is_born_alive_and_can_be_rescued')]
    (OUT / 'affected-methods.json').write_text(json.dumps(methods, indent=2) + '\n')
    for idx, (project, method) in enumerate(methods):
        for run in range(1, 4):
            test(project, f'{mode}-{idx:03}-{run}', 'FullyQualifiedName=' + method)
elif mode in ('gate', 'gate-final'):
    for run in range(1, 11):
        test('ClientCore', f'parking-{"final-" if mode == "gate-final" else ""}{run:02}', 'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
elif mode == 'suites':
    # Final accepted-order replacement correction is rebuilt and re-isolated
    # after the initial gate completes, before the definitive gate and suites.
    subprocess.run([sys.executable, __file__, 'build'], check=True)
    subprocess.run([sys.executable, __file__, 'focus-final'], check=True)
    subprocess.run([sys.executable, __file__, 'gate-final'], check=True)
    results = json.loads(LEDGER.read_text())
    for project in ['Core', 'ClientCore', 'ClientCore.MonoGame']:
        test(project, 'suite-' + project)
elif mode == 'retries':
    # Final native repair-input reset landed while the initial Core suite started.
    # Run Core again on the final build before isolating any suite failures.
    test('Core', 'suite-Core-final')
    cases = sorted({(r['project'], f['method']) for r in results if r['name'].startswith('suite-') for f in r.get('failures', [])})
    for idx, (project, method) in enumerate(cases):
        for run in range(1, 4):
            test(project, f'suite-retry-{idx:03}-{run}', 'FullyQualifiedName=' + method)
    for run in range(1, 4):
        test('ClientCore', f'entry-baseline-{run}', 'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds', '/tmp/p708-entry-baseline/tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj')
    for run in range(1, 4):
        test('ClientCore', f'parking-retry-{run}', 'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
elif mode == 'baseline':
    for run in range(1, 4):
        test('ClientCore', f'entry-baseline-{run}', 'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds', '/tmp/p708-entry-baseline/tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj')
else: raise ValueError(mode)
