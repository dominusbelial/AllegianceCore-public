#!/usr/bin/env python3
"""From repo root, run each affected test method in its own process three times."""
import json, os, re, subprocess, time
from pathlib import Path
import xml.etree.ElementTree as ET
root = Path.cwd()
raw = root / '.p748-evidence/isolated'
raw.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, TMPDIR=str(root / '.p748-scratch'))
project = 'tests/Allegiance.ClientCore.MonoGame.Tests'
files = ['P748InterfaceTests', 'InvestmentPaneModelTests', 'InvestmentLayoutTests',
         'InvestmentBuildGatesTests', 'P728CommanderTests', 'ShopNoAutoLaunchTests',
         'P625bActionMapBehaviorTests', 'P625bActionMapGuardTests']
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results = []
for filename in files:
    source = (root / project / (filename + '.cs')).read_text()
    cls = re.search(r'public (?:sealed )?class (\w+)', source)[1]
    methods = re.findall(r'\[(?:SkippableFact|Fact|Theory)[^\]]*\][\s\S]*?public (?:async )?(?:void|Task) (\w+)\(', source)
    for method in methods:
        name = 'Allegiance.ClientCore.MonoGame.Tests.' + cls + '.' + method
        for repeat in range(1, 4):
            stem = cls + '.' + method + '.' + str(repeat)
            start = time.monotonic()
            with (raw / (stem + '.log')).open('w') as log:
                code = subprocess.run(['dotnet', 'test', project, '--no-build', '--nologo',
                    '--filter', 'FullyQualifiedName=' + name,
                    '--logger', 'trx;LogFileName=' + stem + '.trx',
                    '--results-directory', str(raw)], stdout=log, stderr=subprocess.STDOUT, env=env).returncode
            trx = raw / (stem + '.trx')
            cases = []
            if trx.exists():
                cases = [dict(r.attrib) for r in ET.parse(trx).findall('.//t:UnitTestResult', ns)]
            results.append(dict(method=name, repeat=repeat, exitCode=code, elapsed=round(time.monotonic()-start, 3),
                                cases=[{k: r.get(k) for k in ('testName', 'outcome', 'duration')} for r in cases]))
            (root / 'docs/reports/P748_assets/isolated-tests.json').write_text(json.dumps(results, indent=2))
            if code or not cases or any(r['outcome'] != 'Passed' for r in cases):
                print('FAILED', name, repeat, raw / (stem + '.log'), flush=True)
        print('3x', name, flush=True)
failed = [r for r in results if r['exitCode'] or not r['cases'] or any(c['outcome'] != 'Passed' for c in r['cases'])]
print(f'{len(results)} isolated processes; {sum(len(r["cases"]) for r in results)} cases; {len(failed)} failures', flush=True)
raise SystemExit(bool(failed))
