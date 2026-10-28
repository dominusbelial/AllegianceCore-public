#!/usr/bin/env python3
"""Validate the P867 case inventory against the read-only native switch.

Usage: python3 tools/p867-dispatch-audit.py [--native-root PATH]
This checks coverage/citations, not behavioral equivalence. The case table and
its referenced tests/review supply the behavioral evidence.
"""
import argparse
import csv
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native-root', type=Path, default=Path.home() / 'Development/Allegiance/src')
    args = parser.parse_args()
    native = (args.native_root / 'FedSrv/FedSrv.CPP').read_text()
    # Keep newlines so every citation is checked against today's native source.
    code = re.sub(r'/\*.*?\*/|//[^\n]*', lambda m: '\n' * m[0].count('\n'), native, flags=re.S)
    begin = code.index('HRESULT FedSrvSiteBase::OnAppMessage(')
    end = code.index('return(S_OK);', begin)
    labels = {m[1]: code.count('\n', 0, m.start()) + 1 for m in
              re.finditer(r'\bcase\s+(FM_\w+)\s*:', code, re.M) if begin < m.start() < end}
    table = list(csv.DictReader((ROOT / 'docs/parity/p867-onappmessage.csv').open()))
    matrix = {r['row_id']: r for r in csv.DictReader((ROOT / 'docs/parity/sim-rules-matrix.csv').open())}
    errors = []
    names = [r['native_case'] for r in table]
    if len(set(names)) != len(names):
        errors.append('duplicate table cases')
    if set(names) != set(labels):
        errors.append(f'case coverage differs: missing={set(labels)-set(names)}, extra={set(names)-set(labels)}')
    for row in table:
        case = row['native_case']
        if any(not v.strip() for v in row.values()):
            errors.append(f'{case}: empty field')
        match = re.fullmatch(r'FedSrv/FedSrv.CPP:(\d+)-(\d+)', row['native_cite'])
        if not match or int(match[1]) != labels.get(case):
            errors.append(f'{case}: stale native case cite')
        elif int(match[2]) < int(match[1]) or int(match[2]) > code.count('\n', 0, end) + 1:
            errors.append(f'{case}: invalid native span')
        prior = matrix.get(row['prior_certification'])
        if prior is None or case not in prior['native_item']:
            errors.append(f'{case}: wrong prior certification row')
        if row['result'] not in {'equal', 'classified'}:
            errors.append(f'{case}: unresolved result')
        if row['result'] == 'classified' and not any(c in row['classification_or_difference'] for c in
                                                   ('design:', 'era constraint:', 'bug:')):
            errors.append(f'{case}: difference lacks native-intent classification')
        for path, line in re.findall(r'(src/[\w./-]+\.cs):(\d+)', row['port_cite']):
            source = ROOT / path
            if not source.is_file() or not 1 <= int(line) <= len(source.read_text().splitlines()):
                errors.append(f'{case}: invalid port cite {path}:{line}')
    if 'FM_C_BANDWIDTH' in labels:
        errors.append('native bandwidth patch is now active; certify it too')
    if errors:
        raise SystemExit('\n'.join(errors))
    count = sum(r['result'] == 'equal' for r in table)
    print(f'P867: {len(labels)} active cases covered; {count} equal, {len(table)-count} explicitly classified; '
          'disabled BANDWIDTH excluded; native and port citations validated')
    print('Inventory validation only; behavioral evidence is the per-case test/certification column.')


if __name__ == '__main__':
    main()
