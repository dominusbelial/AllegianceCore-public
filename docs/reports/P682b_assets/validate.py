#!/usr/bin/env python3
"""P682b: isolated methods three times, then sequential unfiltered real suites."""
import json
import os
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
ASSETS = Path(__file__).resolve().parent
LOGS = Path('/tmp/p682b-validation')
LOGS.mkdir(exist_ok=True)
ENV = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/p682b-nuget-cache', ALLEGIANCE_LIVE_P660_SHOWCASE='1')
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results = []


def run(label, project, method=None):
    trx = LOGS / (label + '.trx')
    cmd = ['dotnet', 'test', 'tests/Allegiance.' + project + '.Tests', '--no-build', '--no-restore',
           '--logger', 'trx;LogFileName=' + str(trx)]
    if method:
        cmd += ['--filter', ('FullyQualifiedName=' + method if method.startswith('Allegiance.')
                             else 'FullyQualifiedName~.' + method)]
    start = time.monotonic()
    with (LOGS / (label + '.log')).open('w') as log:
        process = subprocess.run(cmd, cwd=ROOT, env=ENV, stdout=log, stderr=subprocess.STDOUT)
    record = dict(label=label, project=project, method=method, exit=process.returncode,
                  seconds=round(time.monotonic() - start, 3), command=cmd)
    if trx.exists():
        tree = ET.parse(trx)
        record['counts'] = tree.find('.//t:Counters', NS).attrib
        record['cases'] = []
        for case in tree.findall('.//t:UnitTestResult', NS):
            item = dict(name=case.attrib['testName'], outcome=case.attrib['outcome'])
            error = case.find('.//t:Message', NS)
            if error is not None:
                item['message'] = error.text
            if method or item['outcome'] != 'Passed':
                record['cases'].append(item)
    results.append(record)
    (ASSETS / 'results.json').write_text(json.dumps(results, indent=2) + '\n')
    print(label, 'exit=' + str(process.returncode), record.get('counts', {}), flush=True)
    if int(record.get('counts', {}).get('executed', 0)) == 0:
        raise SystemExit('No tests executed: ' + label)
    return record


if __name__ == '__main__':
    methods = [
        'P682b_initial_launch_is_consumed_once_even_with_partial_grant_or_immediate_ejection',
        'P682b_restored_bombers_and_escorts_can_join_the_next_finale_wing',
        'P682b_second_match_relaunches_mature_and_strike_then_third_match_starts',
        'Mcp_bots_buy_launch_see_enemies_and_kill_in_both_matches',
        'DockedPod_BuysPlayerLoadoutAndLaunches_WithBoundedRetries',
        'PodLife_AckUpdatesGrant_LaunchConfirmsNewLife_Twice',
        'NewLife_ClearsAllPodTravelAndTargetLatches_ForTwoDeaths',
        'MissionRotation_ResolvesNamedObjectiveAndClearsOldArrival_BeforeDriving',
        'SameShipId_NewMissionGenerationStillResetsLife',
        'ChangedIdentity_WithSameShipId_ResetsArrival',
        'DefenseRelaunch_EngagesVisibleBaseAttackerBeforeLeaving_AndPrioritizesBomber',
        'P656b_NoBuyableBomberKeepsCurrentHullPartsAndVerifiesNormalLaunch',
        'P656_ControllerReservesOnlyBombersAndBuysOnNextPodLaunch_EscortsAreFreshAndCapped',
        'P656_BeforeTriggerControllerLeavesOpeningDecisionsIdenticalToDisabled',
        'P656_EscortControllerUsesBomberOwnHitEventsAndReturnsWhenOutsideLeash',
        'P656_StrikeKeepsAttackingItsGarrisonUnderFire_InBothP622Modes',
        'P656_StatusIncludesGatesOutcomeAndNextMissionReset',
        'P656_OnlyCompletedDeathDockAndRelaunchCountsAsMature_OffRegistersNothing',
        'P660_Finale_rearms_once_all_bot_generations_and_station_objectives_are_fresh',
        'Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms',
    ]
    for index, method in enumerate(methods):
        for repeat in range(1, 4):
            record = run(f'focused-{index + 1:02}-{repeat}', 'ClientCore', method)
            if record['exit']:
                raise SystemExit('Focused test failed; inspect before continuing: ' + method)
    failures = []
    for project in ['Core', 'ClientCore', 'ClientCore.MonoGame']:
        record = run('suite-' + project, project)
        failures.extend((project, case['name'].split('(')[0]) for case in record.get('cases', [])
                        if case['outcome'] == 'Failed')
    for index, (project, method) in enumerate(failures):
        for repeat in range(1, 4):
            run(f'failure-{index + 1}-{repeat}', project, method)
