#!/usr/bin/env python3
"""P720 serial verification: exact-method triplets, parking 10x, exactly two sweeps."""
import argparse, fcntl, hashlib, json, os, re, signal, subprocess, time
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[3]
ASSETS = ROOT / 'docs/reports/P720_assets'
RAW = ROOT / '.verify-p720' / 'final'
RAW.mkdir(parents=True, exist_ok=True)
STOP = ROOT / '.verify-p720' / 'stop-verification'
_runner_lock = None

def acquire_lock():
    global _runner_lock
    _runner_lock = (ROOT/'.verify-p720/runner.lock').open('w')
    fcntl.flock(_runner_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if STOP.exists(): raise SystemExit('stop-verification exists; no test started')
ENV = os.environ.copy()
for line in (ROOT / 'local.env').read_text().splitlines():
    if line and not line.startswith('#'):
        key, val = line.split('=', 1); ENV[key] = val
ENV.update(NUGET_HTTP_CACHE_PATH='/tmp/p720-nuget-cache', ALLEGIANCE_LIVE_P660_SHOWCASE='1',
           ALLEGIANCE_LIVE_P542_PROOF='1', ALLEGIANCE_LIVE_CAPTURE_PROOF='1', ALLEGIANCE_LIVE_P539_BASELINE='1')
PROJECTS = {name: f'tests/Allegiance.{name}.Tests/Allegiance.{name}.Tests.csproj'
            for name in ('Core', 'ClientCore', 'ClientCore.MonoGame', 'Lobby')}
SELECTED = {
 'Core': ['SwapPartTests', 'CargoBayReloadTests'],
 'ClientCore': ['P641FlightPartsReceiveTests', 'FedMsgDispatchCoverageTests'],
 'ClientCore.MonoGame': ['P720EquipmentSwapTests', 'P625KeyMapTests', 'P625bActionMapBehaviorTests',
                       'P625bActionMapGuardTests', 'P641FlightPartsTests', 'P641FlightPartsLiveTests', 'P626InFlightUtilityPackTests']}
LEDGER = ASSETS / 'verification.json'
records = json.loads(LEDGER.read_text()) if LEDGER.exists() else []

def run(label, project, test_filter=None):
    if STOP.exists(): raise SystemExit("verification stopped before next test")
    index = len(records) + 1
    name = f'{index:03d}-{label}'
    log, trx = RAW / f'{name}.log', RAW / f'{name}.trx'
    command = ['dotnet', 'test', PROJECTS[project], '--no-build', '--nologo', '-v:minimal',
               '--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(RAW)]
    if test_filter: command += ['--filter', test_filter]
    started = time.time()
    with log.open('w') as output:
        child = subprocess.Popen(command, cwd=ROOT, env=ENV, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            while True:
                try:
                    code = child.wait(timeout=1)
                    break
                except subprocess.TimeoutExpired:
                    if STOP.exists():
                        os.killpg(child.pid, signal.SIGTERM)
                        try: child.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            os.killpg(child.pid, signal.SIGKILL); child.wait()
                        code = 130
                        break
        except BaseException:
            os.killpg(child.pid, signal.SIGTERM)
            child.wait()
            raise
    result = dict(label=label, project=project, filter=test_filter, command=command, exit_code=code,
                  elapsed_seconds=round(time.time()-started, 3), started_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime(started)),
                  log=str(log.relative_to(ROOT)), trx=str(trx.relative_to(ROOT)))
    if trx.exists():
        ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        xml = ET.parse(trx)
        result['counters'] = xml.find('.//t:Counters', ns).attrib
        result['failures'] = [dict(name=t.attrib['testName'], message=t.findtext('.//t:Message', namespaces=ns),
                                   stack=t.findtext('.//t:StackTrace', namespaces=ns))
                              for t in xml.findall('.//t:UnitTestResult', ns) if t.attrib['outcome']=='Failed']
        result['trx_sha256'] = hashlib.sha256(trx.read_bytes()).hexdigest()
    records.append(result)
    temporary = LEDGER.with_suffix('.tmp')
    temporary.write_text(json.dumps(records, indent=2)+'\n'); temporary.replace(LEDGER)
    print(label, code, result.get('counters',{}), flush=True)
    if code == 130: raise SystemExit('verification stopped; child process group terminated')
    return code

def affected():
    cohort=[]
    for project, classes in SELECTED.items():
        command=['dotnet','test',PROJECTS[project],'--no-build','--list-tests','--nologo']
        output=subprocess.check_output(command, cwd=ROOT, env=ENV, text=True)
        (RAW/f'list-{project}.txt').write_text(output)
        methods=sorted(set(line.strip().split('(')[0] for line in output.splitlines()
                           if any(f'.{name}.' in line for name in classes) and line.strip().startswith('Allegiance.')))
        if not methods: raise RuntimeError(f'empty cohort {project}')
        cohort.extend((project, method) for method in methods)
    cohort.extend([
        ('Core', 'Allegiance.Core.Tests.Transport.P673ReceiveDecoupleTests.Identical_tick_inputs_produce_identical_simulation_and_wire_results_off_vs_on'),
        ('Core', 'Allegiance.Core.Tests.P653EconomyLoadoutIntegrityTests.H09_Buy_and_swap_publish_one_final_loadout'),
        ('Core', 'Allegiance.Core.Tests.P653EconomyLoadoutIntegrityTests.P665_Flight_roundtrip_resets_readiness_preserves_refund_provenance_and_restores_publication'),
        ('Core', 'Allegiance.Core.Tests.P653EconomyLoadoutIntegrityTests.H22_Docked_drop_swap_reload_fail_and_flying_actions_work'),
        ('ClientCore.MonoGame', 'Allegiance.ClientCore.MonoGame.Tests.P653DPodPurchaseLiveTests.Hangar_launch_after_ejection_or_pod_death_buys_real_hull'),
    ])
    (ASSETS/'affected-methods.json').write_text(json.dumps(cohort,indent=2)+'\n')
    errors=0
    for project, method in cohort:
        for repeat in range(1,4):
            errors += bool(run(f'affected-{method.split(".")[-1]}-{repeat}',project,'FullyQualifiedName='+method))
    return errors

def showcase():
    return sum(bool(run(f'showcase-{i}','ClientCore','FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')) for i in range(1,11))

def suites():
    if any(r['label'].startswith('suite-') for r in records): raise RuntimeError('two-sweep gate already started; do not repeat it')
    return sum(bool(run(f'suite-{sweep}-{project}',project)) for sweep in (1,2) for project in PROJECTS)

if __name__=='__main__':
    acquire_lock()
    parser=argparse.ArgumentParser(); parser.add_argument('phase', choices=['affected','showcase','suites','all','retry']); parser.add_argument('--project'); parser.add_argument('--method'); args=parser.parse_args()
    if args.phase=='retry':
        result=sum(bool(run(f'retry-{args.method.split(".")[-1]}-{i}',args.project,'FullyQualifiedName='+args.method)) for i in range(1,4))
    elif args.phase=='all':
        result=affected()
        if not result:
            result=showcase()
            result+=suites()
    else: result=globals()[args.phase]()
    raise SystemExit(bool(result))
