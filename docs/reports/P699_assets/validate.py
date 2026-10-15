#!/usr/bin/env python3
"""P699 sequential, exact-method validation. Raw logs/TRX remain in /tmp."""
import json, os, pathlib, re, subprocess, sys, time, xml.etree.ElementTree as ET
ROOT = pathlib.Path(__file__).resolve().parents[3]
OUT = pathlib.Path('/tmp/p699-validation'); OUT.mkdir(exist_ok=True)
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
ENV = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/p699-nuget')
if len(sys.argv) > 1 and sys.argv[1] == 'suites': ENV['ALLEGIANCE_LIVE_P660_SHOWCASE'] = '1'
records = []
mode = sys.argv[1]
phase = os.environ.get('P699_PHASE', mode)
def run(project, name, tag, full=False):
    key = f'{phase}-{len(records):03}-{tag}'
    args = ['dotnet', 'test', f'tests/Allegiance.{project}.Tests', '--no-build', '--nologo',
            '--logger', f'trx;LogFileName={key}.trx', '--results-directory', str(OUT)]
    if not full: args += ['--filter', f'FullyQualifiedName={name}']
    started = time.monotonic()
    with (OUT / f'{key}.log').open('w') as log:
        result = subprocess.run(args, cwd=ROOT, env=ENV, stdout=log, stderr=subprocess.STDOUT)
    trx = OUT / f'{key}.trx'
    results = []
    if trx.exists():
        tree = ET.parse(trx)
        for r in tree.findall('.//t:UnitTestResult', NS):
            error = r.find('.//t:Message', NS)
            results.append({'name': r.get('testName'), 'outcome': r.get('outcome'),
                **({'error': error.text} if error is not None else {})})
    row = {'project': project, 'filter': name if not full else '(unfiltered)', 'exit': result.returncode,
        'seconds': round(time.monotonic() - started, 2), 'log': str(OUT / f'{key}.log'),
        'trx': str(trx), 'passed': sum(r['outcome'] == 'Passed' for r in results),
        'failed': [r for r in results if r['outcome'] == 'Failed'],
        'skipped': [r['name'] for r in results if r['outcome'] == 'NotExecuted']}
    records.append(row)
    (ROOT / f'docs/reports/P699_assets/{phase}-results.json').write_text(json.dumps(records, indent=2) + '\n')
    print(f"{key} {project} {name}: exit={result.returncode} passed={row['passed']} failed={len(row['failed'])}", flush=True)
    return row
if mode == 'focused':
    files = {
        'Core': ['P699StationBayTests', 'LandingBayTests', 'DockApproachTests', 'P550DockAndRespawnTests', 'P550LiveDockGateTests'],
        'ClientCore': ['P699PodOutcomeTests', 'P699PodHomeLiveTests'],
        'ClientCore.MonoGame': ['P699HumanPodHomeLiveTests'],
    }
    for project, classes in files.items():
        for filename in classes:
            source = (ROOT / f'tests/Allegiance.{project}.Tests/{filename}.cs').read_text()
            namespace = re.search(r'namespace ([\w.]+);', source)[1]
            cls = re.search(r'public (?:sealed )?(?:partial )?class (\w+)', source)[1]
            for method in re.findall(r'public (?:async Task|void) (\w+)\(', source):
                if method in {'Invoke', 'Dispose'}: continue
                for repetition in range(1, 4):
                    run(project, f'{namespace}.{cls}.{method}', f'{filename}-{method}-{repetition}')
elif mode == 'suites':
    for project in ['Core', 'ClientCore', 'ClientCore.MonoGame']:
        run(project, '', project, full=True)
elif mode == 'isolate':
    for project, method in json.loads(pathlib.Path(sys.argv[2]).read_text()):
        for repetition in range(1, 4): run(project, method, f'isolation-{repetition}')
else: raise SystemExit('choose focused, suites, or isolate METHODS.json')

raise SystemExit(1 if any(row['exit'] or row['failed'] for row in records) else 0)
