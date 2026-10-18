#!/usr/bin/env python3
"""Resolve P722 CSVs by row with read-only index/blob access; no git commands."""
import csv
import io
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(OUT.parent / 'P738_P720_assets'))
from object_reader import ObjectReader

meta = ROOT / '.git'
gitdir = (ROOT / meta.read_text().strip().split(': ', 1)[1]).resolve() if meta.is_file() else meta
common = (gitdir / (gitdir / 'commondir').read_text().strip()).resolve() if (gitdir / 'commondir').exists() else gitdir
objects = ObjectReader(common)
stages = json.loads((OUT / 'index-stages.json').read_text())
audit = []
for name in ('docs/parity/parity-matrix.csv', 'docs/parity/fedmsg-wiring.csv'):
    versions = []
    for stage in (1, 2, 3):
        reader = csv.DictReader(io.StringIO(objects.read(stages[name][str(stage)]).decode()))
        header = reader.fieldnames
        rows = list(reader)
        key = lambda r: (r['area'], r['native_item']) if 'area' in r else r['id']
        versions.append({key(r): r for r in rows})
    base, ours, theirs = versions
    assert base.keys() == ours.keys() == theirs.keys()
    result = []
    for key, b in base.items():
        o, t = ours[key], theirs[key]
        if o == t:
            row, decision = dict(o), 'identical'
        elif o == b:
            row, decision = dict(t), 'incoming only'
        elif t == b:
            row, decision = dict(o), 'master only'
        else:
            status = 'port_status' if 'port_status' in o else 'status'
            # A concrete implementation supersedes an inventory-only exclusion.
            rank = {'missing': 0, 'partial': 1, 'na': 2, 'n/a': 2, 'done': 3, 'implemented': 3}
            row = dict(o if rank[o[status]] >= rank[t[status]] else t)
            for field in header:
                if field not in (status, 'weight', 'id', 'message', 'area', 'native_item', 'direction') and o[field] != t[field]:
                    row[field] = '; '.join(dict.fromkeys((o[field], t[field])))
            decision = 'both changed: more complete status and both evidences'
            if key == ('screens', 'Selection pane'):
                row['port_cite_or_reason'] += '; P738: P713 scaffolding classification retained as historical context; P722 now supplies the selected-model binding and tests, so done supersedes the inventory exclusion'
            elif key in (('fedmsg', '124 QUIT_MISSION'), ('fedmsg', '126 QUIT_SIDE')):
                row['port_cite_or_reason'] += '; P738: P718 completes the transitions that P722 left for the separate fedmsg audit; completion retained'
        result.append(row)
        audit.append(dict(file=name, key=key, decision=decision, base=b, ours=o, theirs=t, resolved=row))
    with (ROOT / name).open('w', newline='') as output:
        writer = csv.DictWriter(output, header, lineterminator='\n')
        writer.writeheader()
        writer.writerows(result)
(OUT / 'parity-row-audit.json').write_text(json.dumps(audit, indent=2) + '\n')
print(json.dumps({name: {d: sum(r['file'] == name and r['decision'] == d for r in audit)
                        for d in sorted({r['decision'] for r in audit if r['file'] == name})}
                  for name in sorted({r['file'] for r in audit})}, indent=2))
