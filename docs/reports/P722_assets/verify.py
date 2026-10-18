#!/usr/bin/env python3
"""P722: isolated triplets, ten local showcases, exactly two complete sweeps.
Raw logs/TRX go to /tmp/p722; the portable invocation ledger lives beside this script.
Run from the repository root after loading local.env. No rig opt-ins are enabled.
"""
import json
import os
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
OUT = Path('/tmp/p722/verification')
OUT.mkdir(parents=True, exist_ok=True)
LEDGER = Path(__file__).with_name('results.json')
results = []
projects = {name: ROOT / 'tests' / f'Allegiance.{name}.Tests' / f'Allegiance.{name}.Tests.csproj'
            for name in ('Core', 'ClientCore', 'ClientCore.MonoGame', 'Lobby')}

def run(label, project, filter_=None):
    trx = OUT / (label + '.trx')
    log = OUT / (label + '.log')
    cmd = ['dotnet', 'test', str(project), '--no-build', '--nologo',
           '--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(OUT),
           '--logger', 'console;verbosity=normal']
    if filter_:
        cmd += ['--filter', filter_]
    start = time.time()
    with log.open('w') as output:
        rc = subprocess.run(cmd, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT).returncode
    row = dict(label=label, command=cmd, exit=rc, seconds=round(time.time()-start, 3), log=str(log), trx=str(trx))
    if trx.exists():
        ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        tree = ET.parse(trx)
        row['counters'] = tree.find('.//t:Counters', ns).attrib
        row['failures'] = []
        for r in tree.findall('.//t:UnitTestResult', ns):
            if r.get('outcome') == 'Failed':
                row['failures'].append(dict(name=r.get('testName'),
                    message=r.findtext('.//t:Message', default='', namespaces=ns),
                    stack=r.findtext('.//t:StackTrace', default='', namespaces=ns)))
    results.append(row)
    LEDGER.write_text(json.dumps(results, indent=2) + '\n')
    print(label, rc, row.get('counters', {}), flush=True)
    return row

cohorts = {
    'Core': ['StationCombatTests', 'StationCombatLiveTests', 'StationCaptureTests', 'StationCaptureLiveTests',
             'MissileCombatTests', 'MissileUnitTests', 'LeadIndicatorTests', 'LeadIndicatorLiveTests',
             'ClusterWarningsTests', 'LiveLogoffTests', 'ConnectionCacheCleanupTests',
             'P655DuplicateIdentityTests.Disconnect_releases_pending_or_seated_name', 'LiveTeamLeadershipTests',
             'Wire.LeadIndicatorUpdateWireTests', 'Wire.RipcordWireTests', 'Reduced_light_anchor_is_valid_only_for_a_light_capable_ripper'],
    'ClientCore': ['FedMsgDispatchCoverageTests', 'P722InterfaceLoopbackTests', 'ClientWorldClusterWarningTests', 'ClientWorldLiveAsteroidOreTests', 'NaniteThreatBonusLiveTests'],
    'ClientCore.MonoGame': ['P722InterfaceModelTests', 'TeamPaneLayoutTests', 'TargetSelectionStyleTests',
                          'RadarTargetSelectionTests', 'ConnectScreenLayoutTests', 'ConnectProfileTests', 'LobbyFilterTests'],
}
if __name__ == '__main__':
    ordinal = 0
    for name, classes in cohorts.items():
        filter_ = '|'.join('FullyQualifiedName~' + c for c in classes)
        cmd = ['dotnet', 'test', str(projects[name]), '--no-build', '--nologo', '--list-tests', '--filter', filter_]
        listing = subprocess.run(cmd, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (OUT / (name + '-list.log')).write_text(listing.stdout)
        if listing.returncode:
            raise SystemExit(listing.stdout)
        methods = sorted({line.strip().split('(')[0] for line in listing.stdout.splitlines()
                          if line.strip().startswith('Allegiance.')})
        if not methods:
            raise SystemExit('No tests discovered for ' + name)
        for method in methods:
            ordinal += 1
            for repeat in range(1, 4):
                run(f'isolated-{ordinal:03d}-{repeat}', projects[name], 'FullyQualifiedName=' + method)
    for repeat in range(1, 11):
        run(f'showcase-{repeat:02d}', projects['ClientCore'],
            'FullyQualifiedName=Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')
    # Exactly two sweeps, each assembly sequential; do not hide the LiveTiming lane.
    for sweep in (1, 2):
        for name in projects:
            run(f'suite-{sweep}-{name}', projects[name])
