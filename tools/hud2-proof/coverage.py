#!/usr/bin/env python3
"""P980 — HUD golden coverage inventory.

Maps EVERY `hud` row of docs/parity/parity-matrix.csv to the hud2-proof
scenario(s) whose semantic snapshot shows it, and lists the rows no scenario
shows today (those need a scenario before the skin work can prove them).
Every `screens` row is inventoried the same way: no hud2-proof scenario renders
a screen (the fixture draws the flight HUD only), so each row records the screen
family its port cite names plus the test files that mention that family.

Coverage kinds
  element        the row is a HudElement; the scenario shows it VISIBLE
  element-model  the row is a HudElement that is available but never visible by
                 design or by a model gap (recorded, not covered visually)
  text           the row has no HudElement; a renderer text box (draw call) is
                 matched in the scenario's measured layout
  structural     the row is a visibility/slide/style behaviour shown by running
                 that scenario (or every combination) in both styles
  elsewhere      no hud2-proof scenario can show it; the note names the surface
                 that owns it (world effect, audio, screen, n/a)
  screen         a `screens` row: no fixture scenario; family + test evidence

Usage:
  tools/hud2-proof/coverage.py --baseline ~/allegiance-evidence/hud-goldens/<commit> \
      --out docs/reports/P980_assets/coverage.json [--markdown docs/reports/P980_assets/COVERAGE.md]

Exit status: 0 when every hud row is accounted for, 1 when a row is unmapped
(the inventory must fail loudly instead of silently dropping a new parity row).
"""
import argparse
import collections
import csv
import json
import os
import re
import subprocess
import sys

# ── rows the fixture cannot express as a visible HudElement ───────────────────
# `note` is what the report prints; `scenarios` may be computed or fixed.
SPECIAL = {
    'Roll tutorial cue': dict(
        kind='text', match='PUSH Q AND E TO ROLL LEFT AND RIGHT',
        note='P785 DrawTrainingCallout text + roll instrument; no HudElements row'),
    'Training HUD callouts': dict(
        kind='text', match='PUSH Q AND E TO ROLL LEFT AND RIGHT',
        note='P785 native RenderTrainingOverlay geometry table via the training scenario'),
    'Command recipient verb and wing controls': dict(
        kind='text', match='attack Enemy fighter',
        note='HUD-10 DrawCommandee text (commandee scenario) plus the order lines'),
    'Console pane visibility and slide animation': dict(
        kind='structural', scenarios=['slide'],
        note='the slide scenario renders an inventory pane mid-slide (InventorySlide .5)'),
    'HUD style and software layouts': dict(
        kind='structural', scenarios=None,
        note='every combination is rendered in Classic and Modern with size/colour-blind variants; '
             'the native ShowCenterHUD/ShowTargetHUD gates are P805 unit-tested'),
    'Observer and turret board state': dict(
        kind='element-model',
        note='native BoardTextImage is loadout-only (dialog.mdl:2106); HUD-20 exposes the value in the model, not in flight'),
    'Money credits': dict(
        kind='element-model',
        note='native moneyPane lives only in the F5 investment pane (dialog.mdl:5199-5203); no flight credits readout'),
    'Inventory turret gunner identity': dict(
        kind='element-model',
        note='HudElements hard-codes this row unavailable (no gunner field on the pane model): P972 1D/2D work'),
    'Target model preview': dict(
        kind='elsewhere',
        note='P805 3-D target preview renderer (Game1.Rendering), not a HUD widget; needs a real-window scenario'),
    'Teleport lead-up sparks (ripcord + aleph)': dict(
        kind='elsewhere',
        note='WarpTransitParticles EmitTeleport world effect (Game1), never a HUD draw'),
    'Mission countdown': dict(
        kind='elsewhere',
        note='native game-state screen clock (Game1.Rendering, MissionClock); not drawn by either HUD renderer'),
    'Incoming missile warning': dict(
        kind='elsewhere',
        note='source-ship audio loop (Audio/MissileWarningController), no HUD pixels by design'),
    'Radar range fade': dict(
        kind='elsewhere',
        note='native 100/1000 m alpha block is commented out (radarimage.cpp:689-700); nothing to draw'),
    'HUD base class contract': dict(kind='elsewhere', note='n/a: abstract C++ wrapper contract, no visual row'),
    'Game list screen boundary': dict(kind='elsewhere', note='implemented; merged with the server-browser screen row (screens area)'),
}

# A scenario whose only purpose is to exercise a structural row is credited even
# though no element id exists for it.
STRUCTURAL_SCENARIOS = {'Console pane visibility and slide animation': ['slide']}


def load_snapshots(baseline):
    manifest = json.load(open(os.path.join(baseline, 'manifest.json')))
    visible = collections.defaultdict(lambda: collections.defaultdict(set))
    available = collections.defaultdict(set)
    text_scenarios = collections.defaultdict(set)   # scenario -> set(text)
    element_ids = set()
    for combo in manifest['Combinations']:
        snapshot = json.load(open(os.path.join(baseline, combo['Snapshot'])))
        scenario = combo['Scenario']
        for element in snapshot['Elements']:
            element_ids.add(element['Id'])
            if element.get('Visible'):
                visible[element['Id']][scenario].add(combo['Style'])
            if element.get('Available'):
                available[element['Id']].add(scenario)
        for box in snapshot['TextBoxes']:
            text_scenarios[scenario].add(box['Text'])
    return manifest, visible, available, text_scenarios, element_ids


def matrix_rows():
    with open('docs/parity/parity-matrix.csv', newline='') as stream:
        return list(csv.DictReader(stream))


def hud_rows(baseline):
    _, visible, available, texts, _ = load_snapshots(baseline)
    result = []
    for row in matrix_rows():
        if row['area'] != 'hud':
            continue
        name = row['native_item']
        if visible.get(name):
            scenarios = {s: sorted(styles) for s, styles in sorted(visible[name].items())}
            kind = 'element'
            note = 'visible HudElement in the semantic snapshot'
        elif name in SPECIAL:
            spec = SPECIAL[name]
            kind = spec['kind']
            note = spec['note']
            if kind == 'text':
                scenarios = {s: [] for s, texts_in in sorted(texts.items())
                             if any(spec['match'] in text for text in texts_in)}
            elif spec.get('scenarios'):
                scenarios = {s: [] for s in spec['scenarios']}
            elif kind == 'structural':
                scenarios = {s: [] for s in sorted({c for c in texts})}
            else:
                scenarios = {}
            if kind == 'element-model':
                scenarios = {s: [] for s in sorted(available.get(name, ()))}
        else:
            result.append(dict(row=name, status=row['port_status'], kind='UNMAPPED', scenarios={},
                               note='not a HudElement and missing from the P980 coverage table'))
            continue
        result.append(dict(row=name, status=row['port_status'], kind=kind, scenarios=scenarios, note=note))
    return result


SRC_PATH = re.compile(r'((?:src|tests)/[A-Za-z0-9_./-]+\.cs)')


def screen_rows():
    """Every `screens` row: which screen family owns it and what automated
    evidence mentions that family. No hud2-proof scenario renders a screen."""
    test_cache = {}

    def tests_for(stem):
        if stem in test_cache:
            return test_cache[stem]
        try:
            found = subprocess.run(['grep', '-rlF', '--include=*.cs', stem, 'tests'],
                                   capture_output=True, text=True, timeout=120).stdout.split()
        except (OSError, subprocess.SubprocessError):
            found = []
        test_cache[stem] = sorted(os.path.basename(path) for path in found)
        return test_cache[stem]

    rows = []
    for row in matrix_rows():
        if row['area'] != 'screens':
            continue
        cite = row['port_cite_or_reason']
        match = SRC_PATH.search(cite)
        family = os.path.basename(match.group(1))[:-3] if match else 'unknown'
        rows.append(dict(row=row['native_item'], status=row['port_status'], family=family,
                         cite=match.group(1) if match else '', evidence=tests_for(family) if family != 'unknown' else []))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--baseline', required=True, help='capture directory with manifest.json + snapshots/')
    parser.add_argument('--out', default='docs/reports/P980_assets/coverage.json')
    parser.add_argument('--markdown', default='docs/reports/P980_assets/COVERAGE.md')
    args = parser.parse_args()

    baseline = os.path.expanduser(args.baseline)
    hud = hud_rows(baseline)
    screens = screen_rows()
    covered = [r for r in hud if r['kind'] == 'element']
    textually = [r for r in hud if r['kind'] in ('text', 'structural')]
    model_only = [r for r in hud if r['kind'] == 'element-model']
    elsewhere = [r for r in hud if r['kind'] == 'elsewhere']
    unmapped = [r for r in hud if r['kind'] == 'UNMAPPED']

    families = collections.OrderedDict()
    for row in screens:
        families.setdefault(row['family'], []).append(row)
    screens_without_evidence = [row for row in screens if not row['evidence']]

    summary = dict(
        hudRows=len(hud),
        hudCoveredByVisibleElement=len(covered),
        hudCoveredByTextOrStructure=len(textually),
        hudModelOnly=len(model_only),
        hudCoveredByFixture=len(covered) + len(textually),
        hudElsewhere=len(elsewhere),
        hudUnmapped=len(unmapped),
        screenRows=len(screens),
        screenRowsWithoutFixture=len(screens),
        screenRowsWithoutTestEvidence=len(screens_without_evidence),
        screensFamilies=len(families),
        scenarios=len({s for r in hud for s in r['scenarios']}),
    )
    # The repo is public: record the evidence location home-relative, never an
    # absolute /home/<user> path.
    shown = '~/' + os.path.relpath(baseline, os.path.expanduser('~')) if baseline.startswith(os.path.expanduser('~') + os.sep) else args.baseline
    payload = dict(baseline=shown, summary=summary, hud=hud,
                   screens=dict(families={k: v for k, v in families.items()},
                                rows=screens))
    os.makedirs(os.path.dirname(args.out) or '.', exist_ok=True)
    with open(args.out, 'w', newline='') as stream:
        json.dump(payload, stream, indent=1, sort_keys=False)
        stream.write('\n')

    with open(args.markdown, 'w', newline='') as stream:
        out = stream.write
        out('# P980 HUD coverage inventory\n\n')
        out('Generated by `tools/hud2-proof/coverage.py` from the golden baseline; '
            'regenerate after any matrix or fixture change.\n\n')
        out(f"* hud rows: **{len(hud)}/111** inventoried "
            f"({len(covered)} shown as visible elements, {len(textually)} by measured text/structure, "
            f"{len(model_only)} model-only, {len(elsewhere)} owned elsewhere, {len(unmapped)} unmapped)\n")
        out(f"* screens rows: **{len(screens)}** inventoried; **0** are shown by a hud2-proof scenario "
            f"(the fixture renders the flight HUD only); {len(screens_without_evidence)} have no test that "
            f"mentions their renderer family\n\n")
        out('## hud rows → scenario coverage\n\n| # | row | status | kind | scenarios | note |\n|---|---|---|---|---|---|\n')
        for index, row in enumerate(hud, 1):
            scenarios = ', '.join(row['scenarios']) if row['scenarios'] else '—'
            out(f"| {index} | {row['row']} | {row['status']} | {row['kind']} | {scenarios} | {row['note']} |\n")
        out('\n## hud rows NO scenario shows\n\n')
        for row in model_only + elsewhere:
            out(f"* **{row['row']}** ({row['kind']}): {row['note']}\n")
        out('\n## screens rows by family (no fixture scenario)\n\n| family | rows | test evidence |\n|---|---|---|\n')
        for family, rows in families.items():
            evidence = sorted({name for row in rows for name in row['evidence']})
            out(f"| {family} | {len(rows)} | {', '.join(evidence) if evidence else 'none'} |\n")
    print(f"P980_COVERAGE hud={len(hud)} visible={len(covered)} text/structure={len(textually)} "
          f"model-only={len(model_only)} elsewhere={len(elsewhere)} unmapped={len(unmapped)} "
          f"screens={len(screens)} screens-no-evidence={len(screens_without_evidence)}")
    for row in unmapped:
        print('P980_COVERAGE_UNMAPPED ' + row['row'])
    return 1 if unmapped else 0


if __name__ == '__main__':
    sys.exit(main())
