#!/usr/bin/env python3
"""P738/P726 merge gates; no git commands, sequential fresh test processes."""
import json
import os
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
ENV = dict(os.environ)
for line in (ROOT / 'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key, value = line.split('=', 1)
        ENV[key] = value
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records = []


def invoke(label, command):
    print(label, flush=True)
    start = time.time()
    with (OUT / f'{label}.log').open('w') as log:
        result = subprocess.run(command, cwd=ROOT, env=ENV, stdout=log,
                                stderr=subprocess.STDOUT)
    record = dict(label=label, command=command, exit_code=result.returncode,
                  seconds=round(time.time() - start, 3))
    trx = OUT / f'{label}.trx'
    if trx.exists():
        tree = ET.parse(trx)
        record['counters'] = tree.find('.//t:Counters', NS).attrib
        record['failures'] = []
        methods = {}
        for definition in tree.findall('.//t:UnitTest', NS):
            method = definition.find('t:TestMethod', NS)
            methods[definition.attrib['id']] = method.attrib['className'].split(',')[0] + '.' + method.attrib['name']
        for case in tree.findall('.//t:UnitTestResult', NS):
            if case.attrib['outcome'] == 'Failed':
                record['failures'].append(dict(method=methods[case.attrib['testId']],
                                               message=''.join(case.itertext())))
        record['skips'] = [r.attrib['testName'] for r in tree.findall('.//t:UnitTestResult', NS)
                           if r.attrib['outcome'] == 'NotExecuted']
    records.append(record)
    (OUT / 'verification.json').write_text(json.dumps(records, indent=2) + '\n')
    print(json.dumps({k: record[k] for k in ('label', 'exit_code', 'seconds', 'counters') if k in record}), flush=True)
    return record


def csharp(label, project, filter_text=None):
    assembly = ROOT / f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'
    command = ['dotnet', 'test', str(assembly), '--nologo',
               '--logger', f'trx;LogFileName={label}.trx', '--results-directory', str(OUT)]
    if filter_text:
        command += ['--filter', filter_text]
    return invoke(label, command)


def main():
    focused = {
        'ClientCore': ['ClientWorldSoundEventTests', 'ShipStatusAlarmSoundTests'],
        'ClientCore.MonoGame': ['MusicLoadingTests', 'MeshyNoticeTests', 'MusicPlannerTests',
                               'P680CountdownSoundTests', 'P714AnnounceSoundTests',
                               'P717DeploySoundTests', 'P719StationEconomySoundTests',
                               'P721ThrusterLoopSoundTests', 'P721ShipLoopBedsTests',
                               'P723SoundAssetTests', 'P664StationEventSoundTests'],
    }
    selected = []
    for project, classes in focused.items():
        for cls in classes:
            source = ROOT / f'tests/Allegiance.{project}.Tests/{cls}.cs'
            for method in re.findall(r'public (?:async )?(?:void|Task) (\w+)\(', source.read_text()):
                selected.append((project, f'Allegiance.{project}.Tests.{cls}.{method}'))
    (OUT / 'focused-methods.json').write_text(json.dumps(selected, indent=2) + '\n')
    for index, (project, method) in enumerate(selected):
        for repeat in range(1, 4):
            record = csharp(f'focused-{index:03}-{repeat}', project, 'FullyQualifiedName=' + method)
            if record['exit_code'] or int(record.get('counters', {}).get('passed', '0')) == 0:
                raise RuntimeError(f'Focused test did not pass: {method}')
    # Importing these modules never invokes package.source; its git call is mocked
    # in test_local_override_must_match_pin, preserving the edit-only lane.
    import importlib.util
    spec = importlib.util.spec_from_file_location('p738_packaging_tests', ROOT / 'packaging/test_packaging.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    for name in sorted(n for n in dir(module.PackagingTests) if n.startswith('test_')):
        for repeat in range(1, 4):
            record = invoke(f'packaging-{name}-{repeat}', ['python3', 'packaging/test_packaging.py',
                                                          'PackagingTests.' + name])
            if record['exit_code']:
                raise RuntimeError(f'Packaging test failed: {name}')
    for repeat in range(1, 6):
        csharp(f'showcase-{repeat}', 'ClientCore',
               'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
        filter_text = ('FullyQualifiedName!~MeshyGuardTests&FullyQualifiedName!~PrivatePathGuardTests'
                       if project == 'Core' else None)
        csharp('suite-' + project, project, filter_text)
    # Failures retain their original TRX and get three exact-method reruns.
    failures = [(r['label'], f['method']) for r in list(records) for f in r.get('failures', [])]
    for index, (label, method) in enumerate(failures):
        project = 'ClientCore.MonoGame' if '.MonoGame.' in method else ('ClientCore' if '.ClientCore.' in method else 'Core')
        for repeat in range(1, 4):
            csharp(f'isolation-{index}-{repeat}', project, 'FullyQualifiedName=' + method)
    print('Completed all gates.', flush=True)


if __name__ == '__main__':
    main()
