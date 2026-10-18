"""P738/P734b: serial exact-method triplets, five parking runs, suites once.

Runs built assemblies directly and never invokes git. Each completed process
must produce nonempty TRX evidence; a zero exit alone cannot qualify a pass.
"""
import fcntl, gzip, hashlib, json, os, re, subprocess, sys, time
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
ENV = dict(os.environ)
for line in (ROOT / 'local.env').read_text().splitlines():
    if line.strip() and not line.lstrip().startswith('#') and '=' in line:
        k, v = line.split('=', 1)
        ENV[k.strip()] = v.strip().strip('"').strip("'")
for flag in ('ALLEGIANCE_LIVE_P660_SHOWCASE', 'ALLEGIANCE_LIVE_P542_PROOF',
             'ALLEGIANCE_LIVE_CAPTURE_PROOF', 'ALLEGIANCE_LIVE_P539_BASELINE', 'ALLEGIANCE_LIVE_P539C_REPRO'):
    ENV[flag] = '1'
for flag in ('ALLEGIANCE_LIVE_P539_PROOF', 'ALLEGIANCE_LIVE_P539B_PROOF', 'ALLEGIANCE_LIVE_P539D',
             'ALLEGIANCE_LIVE_P553', 'ALLEGIANCE_LIVE_P610', 'ALLEGIANCE_LIVE_P622',
             'ALLEGIANCE_LIVE_P632', 'ALLEGIANCE_LIVE_P632_ASSERT', 'ALLEGIANCE_LIVE_P634', 'ALLEGIANCE_LIVE_P635'):
    ENV.pop(flag, None)
ENV['NUGET_HTTP_CACHE_PATH'] = '/tmp/p738-p734b-nuget-http'
ENV['ALLEGIANCE_P733_PROOF_DIRECTORY'] = str(OUT / 'research-proof')
ENV['ALLEGIANCE_P705_PROOF_DIRECTORY'] = str(OUT / 'research-proof')
LEDGER = OUT / 'verification.json'
records = json.loads(LEDGER.read_text()) if LEDGER.exists() else []

def sources():
    return {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
            for folder in ('src', 'tests', 'tools') for p in (ROOT / folder).rglob('*')
            if p.is_file() and p.suffix in ('.cs', '.csproj', '.mgcb', '.fx', '.json')
            and not {'bin', 'obj'}.intersection(p.relative_to(ROOT).parts)}

def assembly(project):
    return ROOT / f'tests/Allegiance.{project}.Tests/bin/Debug/net10.0/Allegiance.{project}.Tests.dll'

def test(label, project, filt=None):
    previous = next((r for r in records if r['label'] == label), None)
    if previous: return previous
    command = ['dotnet', 'vstest', str(assembly(project)),
               '--logger:trx;LogFileName=' + label + '.trx', '--ResultsDirectory:' + str(OUT)]
    if filt: command.append('--TestCaseFilter:' + filt)
    active = dict(label=label, project=project, command=command, started=time.time())
    (OUT / 'active-run.json').write_text(json.dumps(active, indent=2) + '\n')
    with (OUT / (label + '.log')).open('w') as log:
        process = subprocess.run(command, cwd=ROOT, env=ENV, stdout=log, stderr=subprocess.STDOUT)
    row = dict(active, exit_code=process.returncode, seconds=round(time.time() - active['started'], 3))
    trx = OUT / (label + '.trx')
    if trx.exists():
        tree = ET.parse(trx)
        row['counters'] = tree.find('.//t:Counters', NS).attrib
        methods = {d.attrib['id']: d.find('t:TestMethod', NS).attrib for d in tree.findall('.//t:UnitTest', NS)}
        row['cases'] = []
        for result in tree.findall('.//t:UnitTestResult', NS):
            method = methods[result.attrib['testId']]
            case = dict(name=result.attrib['testName'], method=method['className'].split(',')[0] + '.' + method['name'],
                        outcome=result.attrib['outcome'])
            if case['outcome'] != 'Passed':
                case['message'] = result.findtext('.//t:Message', '', NS)
                case['stack'] = result.findtext('.//t:StackTrace', '', NS)
            output = result.findtext('t:Output/t:StdOut', '', NS)
            if output and (label.startswith('showcase-') or case['outcome'] != 'Passed'):
                case['output'] = output
            row['cases'].append(case)
        with gzip.open(str(trx) + '.gz', 'wb') as stream: stream.write(trx.read_bytes())
        trx.unlink()
    records.append(row)
    LEDGER.write_text(json.dumps(records, indent=2) + '\n')
    (OUT / 'active-run.json').write_text(json.dumps(dict(label=label, finished=True, exit_code=process.returncode)) + '\n')
    print(json.dumps({k: row[k] for k in ('label', 'exit_code', 'seconds', 'counters') if k in row}), flush=True)
    assert row.get('cases') and int(row['counters']['executed']) > 0, 'Empty/incomplete invocation: ' + label
    if filt and filt.startswith('FullyQualifiedName=') and '&' not in filt:
        expected = filt.split('=', 1)[1]
        assert {c['method'] for c in row['cases']} == {expected}, 'Incorrect method selected: ' + label
    return row

def cohort():
    selected = {tuple(row) for row in json.loads((OUT.parent / 'P734b_assets/cohort.json').read_text())}
    renamed = {
        'Allegiance.ClientCore.Tests.ClientWorldSoundEventTests.AcquirePart_RecordsNoPickupSound':
            'Allegiance.ClientCore.Tests.ClientWorldSoundEventTests.AcquirePart_RecordsPickupPartSound',
        'Allegiance.ClientCore.Tests.ClientWorldSoundEventTests.ClassifySonicChat_IsSilent':
            'Allegiance.ClientCore.Tests.ClientWorldSoundEventTests.ClassifySonicChat_SelectsVoiceOverBeforePlainChat',
    }
    selected = {(project, renamed.get(method, method)) for project, method in selected}
    (OUT / 'historical-method-renames.json').write_text(json.dumps(renamed, indent=2) + '\n')
    classes = {
        'Core': ['P674ParityScorecardTests', 'P725DroneChatterSeamTests', 'P725DroneChatterWireLiveTests'],
        'ClientCore': ['P709ClientCallbacksTests', 'P732VoiceOverTests', 'ClientWorldSoundEventTests', 'ShipStatusAlarmSoundTests'],
        'ClientCore.MonoGame': ['P714AnnounceSoundTests', 'P717DeploySoundTests', 'P719StationEconomySoundTests',
            'P721ThrusterLoopSoundTests', 'P723SoundAssetTests', 'P723DroneChatterCueTests', 'P732VoiceCatalogTests',
            'P735SoundAssetTests', 'P736SoundAssetTests', 'P737SoundAssetTests'],
    }
    for project, names in classes.items():
        for cls in names:
            source = (ROOT / f'tests/Allegiance.{project}.Tests/{cls}.cs').read_text()
            namespace = re.search(r'namespace ([\w.]+);', source)[1]
            selected.update((project, f'{namespace}.{cls}.{method}') for method in
                            re.findall(r'^    public (?:async )?(?:void|Task) (\w+)\(', source, re.M) if method != 'Dispose')
    selected = sorted(selected, key=lambda row: ('P709ClientCallbacksTests' not in row[1], row))
    (OUT / 'focused-methods.json').write_text(json.dumps(selected, indent=2) + '\n')
    return selected

def isolate():
    failures = sorted({(r['project'], c['method'], c['name']) for r in records
                       if not r['label'].startswith('isolation-') for c in r['cases'] if c['outcome'] == 'Failed'})
    qualifications = []
    for index, (project, method, name) in enumerate(failures):
        # The method filter executes all theory cases; prove the failed case is
        # present and passes in each fresh process, rather than silently omitting it.
        triplet = [test(f'isolation-{index:03}-{repeat}', project, 'FullyQualifiedName=' + method) for repeat in range(1, 4)]
        clean = all(r['exit_code'] == 0 and all(c['outcome'] == 'Passed' for c in r['cases'])
                    and any(c['name'] == name and c['outcome'] == 'Passed' for c in r['cases']) for r in triplet)
        qualifications.append(dict(project=project, method=method, case=name,
                                   three_clean_passes=clean, invocations=[r['label'] for r in triplet]))
    (OUT / 'flake-qualification.json').write_text(json.dumps(qualifications, indent=2) + '\n')
    assert all(q['three_clean_passes'] for q in qualifications), 'A failure requires investigation'

def main():
    lock = (OUT / 'runner.lock').open('a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if sys.argv[1:] == ['isolate']:
        isolate()
        return
    stamp = OUT / 'source-fingerprints.json'
    if stamp.exists(): assert json.loads(stamp.read_text()) == sources(), 'Sources changed after build'
    else: stamp.write_text(json.dumps(sources(), indent=2) + '\n')
    selected = cohort()
    print('Selected methods: ' + str(len(selected)), flush=True)
    for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
        result = subprocess.run(['dotnet', 'vstest', str(assembly(project)), '--ListTests'], cwd=ROOT, env=ENV, capture_output=True, text=True)
        (OUT / f'discovered-{project}.txt').write_text(result.stdout + result.stderr)
        assert result.returncode == 0
        for proj, method in selected:
            if proj == project: assert method in result.stdout, 'Discovery miss: ' + method
    for index, (project, method) in enumerate(selected):
        for repeat in range(1, 4):
            test(f'focused-{index:03}-{repeat}', project, 'FullyQualifiedName=' + method)
    for repeat in range(1, 6):
        test(f'showcase-{repeat}', 'ClientCore', 'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    for project in ('Core', 'ClientCore', 'ClientCore.MonoGame'):
        # These two fixtures execute git commands, prohibited by the user.
        filt = 'FullyQualifiedName!~MeshyGuardTests&FullyQualifiedName!~PrivatePathGuardTests' if project == 'Core' else None
        test('suite-' + project, project, filt)
    isolate()
    assert json.loads(stamp.read_text()) == sources(), 'Sources changed during validation'
    print('All scheduled invocations and failure qualifications completed.', flush=True)

if __name__ == '__main__': main()
