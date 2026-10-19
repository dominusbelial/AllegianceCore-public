#!/usr/bin/env python3
"""P740 light gate: exact-method triplets, five showcases, one broad sweep.
Raw logs/TRX live outside the repository. Resume by label; never rerun a suite.
"""
import fcntl, hashlib, json, os, re, signal, subprocess, time
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
RAW = Path('/tmp/allegiance-evidence/P740/final-ui')
RAW.mkdir(parents=True, exist_ok=True)
ENV = dict(os.environ)
for line in (ROOT / 'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key, value = line.split('=', 1); ENV[key] = value
for key in ('ALLEGIANCE_LIVE_P660_SHOWCASE', 'ALLEGIANCE_LIVE_P542_PROOF',
            'ALLEGIANCE_LIVE_CAPTURE_PROOF', 'ALLEGIANCE_LIVE_P539_BASELINE'):
    ENV[key] = '1'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
LEDGER = OUT / 'verification.json'
records = json.loads(LEDGER.read_text()) if LEDGER.exists() else []

def save():
    temp = LEDGER.with_suffix('.tmp')
    temp.write_text(json.dumps(records, indent=2) + '\n'); temp.replace(LEDGER)

def test(label, project, filter_text=None):
    if (RAW.parent / 'STOP').exists():
        raise SystemExit('P740 stop requested before ' + label)
    prior = [r for r in records if r['label'] == label]
    if prior:
        assert len(prior) == 1 and 'counters' in prior[0], 'ambiguous/incomplete result: ' + label
        return prior[0]
    assembly = ROOT / f'tests/Allegiance.{project}.Tests/bin/Release/net10.0/Allegiance.{project}.Tests.dll'
    command = ['dotnet', 'test', str(assembly), '--nologo', '--logger', f'trx;LogFileName={label}.trx', '--results-directory', str(RAW)]
    if filter_text: command += ['--filter', filter_text]
    start = time.time()
    with (RAW / f'{label}.log').open('w') as log:
        child = subprocess.Popen(command, cwd=ROOT, env=ENV, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            exit_code = child.wait()
        except BaseException:
            try: os.killpg(child.pid, signal.SIGTERM)
            except ProcessLookupError: pass
            try: child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL); child.wait()
            raise
    result = dict(label=label, project=project, command=command, exit_code=exit_code,
                  started=start, seconds=round(time.time() - start, 3))
    trx = RAW / f'{label}.trx'
    if trx.exists():
        tree = ET.parse(trx)
        result['trx_sha256'] = hashlib.sha256(trx.read_bytes()).hexdigest()
        result['counters'] = tree.find('.//t:Counters', NS).attrib
        methods = {}
        for definition in tree.findall('.//t:UnitTest', NS):
            method = definition.find('t:TestMethod', NS)
            methods[definition.attrib['id']] = method.attrib['className'].split(',')[0] + '.' + method.attrib['name']
        cases = tree.findall('.//t:UnitTestResult', NS)
        result['failures'] = [dict(method=methods[c.attrib['testId']], name=c.attrib['testName'],
            message=''.join(c.itertext())) for c in cases if c.attrib['outcome'] == 'Failed']
        result['skips'] = [c.attrib['testName'] for c in cases if c.attrib['outcome'] == 'NotExecuted']
    records.append(result); save()
    print(json.dumps({key: result[key] for key in ('label', 'exit_code', 'seconds', 'counters') if key in result}), flush=True)
    return result

def methods(project, filename, predicate=lambda method: True):
    path = ROOT / f'tests/Allegiance.{project}.Tests/{filename}.cs'
    source = path.read_text()
    namespace = re.search(r'namespace ([\w.]+);', source)[1]
    cls = re.search(r'public (?:sealed )?class (\w+)', source)[1]
    return [(project, f'{namespace}.{cls}.{method}') for method in re.findall(r'^    public (?:async )?(?:void|Task) (\w+)\(', source, re.MULTILINE)
            if method != 'Dispose' and predicate(method)]

def main():
    with (RAW.parent / 'task.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        for revision in ('final', 'final-v2'):
            with (RAW.parent / revision / 'runner.lock').open('r+') as old_lock:
                fcntl.flock(old_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        selected = []
        groups = {
            'ClientCore': ['P740FlightInterfaceTests', 'FedMsgDispatchCoverageTests', 'P578EjectDeathFeedbackTests',
                           'P648RelaunchVisibilityTests', 'DeadLatchRespawnTests', 'P718InterfaceMessageTests'],
            'ClientCore.MonoGame': ['P740FlightInputTests', 'P709MoneyRequestInputTests', 'P625bActionMapGuardTests'],
            'Core': ['P674ParityScorecardTests', 'Wire/GainFlagWireTests', 'Wire/RelaunchShipWireTests'],
        }
        for project, files in groups.items():
            for filename in files: selected += methods(project, filename)
        selected += methods('ClientCore', 'P709ClientCallbacksTests', lambda m: any(s in m for s in ('Money_request', 'Approval_', 'Unknown_request')))
        selected += methods('ClientCore', 'P709CallbacksLiveTests', lambda m: m.startswith('Money_request'))
        selected += methods('ClientCore', 'ClientWorldSoundEventTests', lambda m: 'Flag' in m or 'Artifact' in m)
        selected += methods('ClientCore.MonoGame', 'FlightInputTests', lambda m: 'Throttle' in m or m.startswith('Presets_'))
        selected += methods('ClientCore.MonoGame', 'JoystickFlightControllerTests', lambda m: 'Throttle' in m)
        selected += methods('ClientCore.MonoGame', 'P624FlightKeysPackTests', lambda m: 'ThrottleFull_' in m)
        selected = sorted(set(selected))
        (OUT / 'focused-methods.json').write_text(json.dumps(selected, indent=2) + '\n')
        print('Focused methods: ' + str(len(selected)), flush=True)
        for index, (project, method) in enumerate(selected):
            for repeat in range(1, 4):
                result = test(f'focused-{index:03}-{repeat}', project, 'FullyQualifiedName=' + method)
                if result['exit_code'] or int(result.get('counters', {}).get('passed', 0)) == 0:
                    raise RuntimeError('Affected test failed or did not execute: ' + method)
        for repeat in range(1, 6):
            test(f'showcase-{repeat}', 'ClientCore', 'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
        for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
            test('suite-' + project, project)
        failures = sorted({(r['project'], f['method']) for r in records if r['label'].startswith('suite-') for f in r.get('failures', [])})
        for index, (project, method) in enumerate(failures):
            for repeat in range(1, 4): test(f'isolation-{index:02}-{repeat}', project, 'FullyQualifiedName=' + method)
        print('Scheduled gates finished. Review every result; only one broad pass per assembly.', flush=True)

if __name__ == '__main__': main()
