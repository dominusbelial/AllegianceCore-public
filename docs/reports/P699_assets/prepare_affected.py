#!/usr/bin/env python3
"""Select failed initial checks and the direct callers changed by P699."""
import json, pathlib, re
root = pathlib.Path(__file__).resolve().parents[3]
assets = pathlib.Path(__file__).resolve().parent
suites = json.loads((assets / 'suites-results.json').read_text())
assert len(suites) == 3, 'finish the three initial suites first'
methods = {(suite['project'], failed['name'].split('(')[0]) for suite in suites for failed in suite['failed']}
def include(project, filename, selected=None):
    source = (root / f'tests/Allegiance.{project}.Tests/{filename}.cs').read_text()
    namespace = re.search(r'namespace ([\w.]+);', source)[1]
    cls = re.search(r'public (?:sealed )?(?:partial )?class (\w+)', source)[1]
    pending = False
    found = set()
    for line in source.splitlines():
        if line.lstrip().startswith('//'): continue
        if re.search(r'\[(?:Skippable)?(?:Fact|Theory)', line): pending = True
        method = re.search(r'public (?:async Task|void|Task) (\w+)\(', line)
        if method:
            if pending and (selected is None or method[1] in selected):
                methods.add((project, f'{namespace}.{cls}.{method[1]}'))
                found.add(method[1])
            pending = False
    if selected is not None: assert set(selected) <= found, (filename, set(selected) - found)
for project, filename in [('Core', 'P699StationBayTests'), ('ClientCore', 'P699PodOutcomeTests'),
                          ('ClientCore', 'P699PodHomeLiveTests'), ('ClientCore.MonoGame', 'P699HumanPodHomeLiveTests')]:
    include(project, filename)
for project, filename in [('Core', 'SideEliminationLiveTests'), ('Core', 'StationCaptureLiveTests'),
                          ('Core', 'LiveLifepodOxygenTests'), ('Core', 'P550LiveDockGateTests'),
                          ('ClientCore.MonoGame', 'P653DPodPurchaseLiveTests'),
                          ('ClientCore.MonoGame', 'P540bPodAutopilotCrossSectorLiveTests')]:
    include(project, filename)
for project, filename, selected in [
    ('Core', 'P550DockAndRespawnTests', ['Docking_requires_native_landing_geometry_not_just_a_200u_sphere']),
    ('Core', 'DockAutoRestockLiveTests', ['Docking_rebuys_paid_desired_launcher_and_charges_the_native_amount']),
    ('Core', 'DockDockingTests', ['HostileStation_in_radius_does_not_trigger_dock']),
    ('Core', 'BuildingTechsLiveTests', ['BuyLoadout_gate_grants_the_techbase_hull_after_the_station_is_placed']),
    ('Core', 'RepairAndRefuelTests', ['Live_launch_from_station_without_bits_does_not_heal_or_rearm']),
    ('ClientCore', 'BotAcceptsAndEngagesOrderLiveTests', ['Mcp_debug_bot_buys_relaunches_and_fights']),
    ('ClientCore', 'BotFixRegressionTests', ['PodLife_AckUpdatesGrant_LaunchConfirmsNewLife_Twice',
                                           'NewLife_ClearsAllPodTravelAndTargetLatches_ForTwoDeaths']),
]: include(project, filename, selected)
# Methods in changed shared fixtures that passed the initial suite are still audited.
include('ClientCore.MonoGame', 'DispenserEquipFireLiveTests')
result = sorted(methods)
(assets / 'affected-methods.json').write_text(json.dumps(result, indent=2) + '\n')
print(f'{len(result)} exact methods selected; each will run three times in a separate invocation', flush=True)
