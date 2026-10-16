#!/usr/bin/env python3
"""Sequential local validation; each affected method runs in its own process."""
import gzip, hashlib, json, os, pathlib, re, subprocess, sys, time, xml.etree.ElementTree as ET
ROOT = pathlib.Path(__file__).resolve().parents[3]
OUT = pathlib.Path('/tmp/p699c-verified-validation'); OUT.mkdir(exist_ok=True)
ASSETS = ROOT / 'docs/reports/P699c_assets'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
ENV = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/p699c-nuget', ALLEGIANCE_LIVE_P660_SHOWCASE='1', ALLEGIANCE_LIVE_P542_PROOF='1')
phase = sys.argv[1]
records = []
def run(project, name='', tag='', env=None):
    assert not (OUT/'CANCEL').exists(), 'validation canceled before next invocation'
    key = f'{phase}-{len(records):03}-{tag}'
    args = ['dotnet', 'test', f'tests/Allegiance.{project}.Tests', '--no-build', '--nologo',
        '--logger', f'trx;LogFileName={key}.trx', '--results-directory', str(OUT)]
    if not name: args += ['--logger', 'console;verbosity=normal']
    if project == 'Core' and not name: args += ['--blame']
    if name: args += ['--filter', 'FullyQualifiedName=' + name]
    started = time.time()
    (OUT / f'{key}.trx').unlink(missing_ok=True)
    with (OUT / f'{key}.log').open('w') as log:
        process = subprocess.run(args, cwd=ROOT, env=env or ENV, stdout=log, stderr=subprocess.STDOUT)
    trx = OUT / f'{key}.trx'
    results, portable = [], None
    if trx.exists():
        root = ET.parse(trx)
        definitions = {d.get('id'): d.find('t:TestMethod', NS) for d in root.findall('.//t:UnitTest', NS)}
        for r in root.findall('.//t:UnitTestResult', NS):
            error = r.find('.//t:Message', NS)
            method = definitions.get(r.get('testId'))
            results.append({'name': r.get('testName'), 'outcome': r.get('outcome'),
                'method': method.get('className') + '.' + method.get('name') if method is not None else None,
                **({'error': error.text} if error is not None else {})})
        data = trx.read_bytes()
        portable = key + '.trx.gz'
        (ASSETS / portable).write_bytes(gzip.compress(data, mtime=0))
    row = {'project': project, 'filter': name or '(unfiltered)', 'exit': process.returncode if results else 1, 'process_exit': process.returncode,
        'started': started, 'finished': time.time(), 'seconds': round(time.time() - started, 2),
        'log': str(OUT / f'{key}.log'), 'trx': portable,
        'passed': sum(r['outcome'] == 'Passed' for r in results),
        'failed': [r for r in results if r['outcome'] == 'Failed'],
        'skipped': [r['name'] for r in results if r['outcome'] == 'NotExecuted']}
    records.append(row)
    (ASSETS / f'{phase}-results.json').write_text(json.dumps(records, indent=2) + '\n')
    print(f"{key} {project} {name or 'FULL'} exit={process.returncode} pass={row['passed']} fail={len(row['failed'])}", flush=True)
    return row
if phase.endswith('focused'):
    files = {
        'Core': ['P699cStationCorpusTests', 'DockDockingTests', 'P699bPodLoadTests', 'P699bPodNavigationTests', 'P699bPodTelemetryTests', 'DockApproachTests',
            'P699StationBayTests', 'StationRescueModelTests', 'LiveStationRescueTests', 'P550DockAndRespawnTests', 'P550LiveDockGateTests'],
        'ClientCore': ['P699PodHomeLiveTests', 'P699PodOutcomeTests', 'P560PodHomeStationTests', 'P560PodWarpLegClosedLoopTests'],
        'ClientCore.MonoGame': ['P699HumanPodHomeLiveTests', 'P540bPodAutopilotCrossSectorLiveTests', 'P550MonoTestDockHome', 'P541GotoFlyLiveTests'],
    }
    for project, classes in files.items():
        for cls in classes:
            source = (ROOT / f'tests/Allegiance.{project}.Tests/{cls}.cs').read_text()
            namespace = re.search(r'namespace ([\w.]+);', source)[1]
            declared_class = re.search(r'public (?:sealed |static |partial )*class (\w+)', source)[1]
            # Attribute-annotated methods only (exclude helpers, IDisposable).
            methods = re.findall(r'(?:\[[^\]]+\]\s*)+public (?:async Task|void|Task) (\w+)\(', source)
            for method in methods:
                for n in range(1, 4): run(project, f'{namespace}.{declared_class}.{method}', f'{cls}-{method}-{n}')
elif phase == 'completed-core':
    run('Core', tag='Core')
elif phase == 'corrected-monogame':
    run('ClientCore.MonoGame', tag='MonoGame')
elif phase.endswith('suites'):
    for project in ['Core', 'ClientCore', 'ClientCore.MonoGame', 'Lobby']: run(project, tag=project)
elif phase.startswith('isolate'):
    for project, method in json.loads(pathlib.Path(sys.argv[2]).read_text()):
        for n in range(1, 4): run(project, method, f'isolate-{n}')
elif phase == 'baseline':
    run('Core', 'Allegiance.Core.Tests.P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic',
        'old-flight-law', dict(ENV, P699B_BASELINE='1'))
else: raise SystemExit('choose focused, suites, isolate METHODS.json, baseline')
raise SystemExit(int(any(r['exit'] or r['failed'] for r in records)))
