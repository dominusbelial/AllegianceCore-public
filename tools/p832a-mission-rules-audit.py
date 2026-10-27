#!/usr/bin/env python3
"""Reconcile the P832a event-driven mission-rule inventory with local native sources.

Read-only native checkout. Run with --native-root PATH (the directory containing
FedSrv and Igc). Every CFSMission/CmissionIGC/FedSrvSiteBase out-of-line body in
these three files and every OnAppMessage FM_* case must have a matrix row.
This proves inventory coverage, not behavioral equivalence.
"""
import argparse
import csv
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
FAMILY = "FedSrv mission rules (event-driven)"
FILES = ("FedSrv/fsmission.cpp", "FedSrv/FedSrv.CPP", "Igc/missionigc.cpp")


def mask_cpp(source):
    # Keep offsets and line numbers while hiding comments and string contents.
    pattern = r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
    return re.sub(pattern, lambda m: re.sub(r'[^\n]', ' ', m[0]), source)


def closing(source, start, left, right):
    level = 0
    for i in range(start, len(source)):
        if source[i] == left:
            level += 1
        elif source[i] == right:
            level -= 1
            if level == 0:
                return i
    raise ValueError(f"Unbalanced {left} at {start}")


def inventory(native_root):
    units = []
    candidates = []
    for name in FILES:
        source = (native_root / name).read_text(encoding="utf-8-sig", errors="replace")
        masked = mask_cpp(source)
        for match in re.finditer(r'\b(\w+)::(~?\w+)\s*\(', masked):
            end_args = closing(masked, match.end()-1, '(', ')')
            brace = masked.find('{', end_args)
            semicolon = masked.find(';', end_args)
            if brace < 0 or 0 <= semicolon < brace:
                continue  # invocation / declaration, not a definition
            # Calls may occur in an expression before another block. Require
            # a declaration at the beginning of this line (return type optional).
            line_start = masked.rfind('\n', 0, match.start()) + 1
            prefix = masked[line_start:match.start()]
            if not re.fullmatch(r'[\w\s:*&<>]*', prefix) or re.search(r'\b(return|new|delete)\b', prefix):
                continue
            end = closing(masked, brace, '{', '}')
            line = source.count('\n', 0, match.start())+1
            end_line = source.count('\n', 0, end)+1
            key = f"{name}:{line} {match[1]}::{match[2]}"
            unit = dict(key=key, file=name, line=line, end_line=end_line,
                        method=f"{match[1]}::{match[2]}", kind="method", _body=masked[brace:end])
            candidates.append(unit)
            if match[1] in ("CFSMission", "CmissionIGC", "FedSrvSiteBase") or match[1].endswith("Ballot"):
                units.append(unit)
            if match[2] == "OnAppMessage":
                cases = list(re.finditer(r'\bcase\s+(FM_\w+)\s*:', masked[brace:end]))
                for index, case in enumerate(cases):
                    pos = brace+case.start()
                    next_pos = brace+cases[index+1].start() if index+1 < len(cases) else end
                    start_line = source.count('\n', 0, pos)+1
                    stop_line = source.count('\n', 0, next_pos-1)+1
                    units.append(dict(key=f"{name}:{start_line} OnAppMessage/{case[1]}",
                                      file=name, line=start_line, end_line=stop_line,
                                      method=f"OnAppMessage/{case[1]}", kind="handler"))
        class_spans = []
        for declaration in re.finditer(r'\bclass\s+(ThingSiteImpl|ClusterSiteImpl)\s*:[^{]+\{', masked):
            class_spans.append((declaration[1], declaration.end()-1,
                                closing(masked, declaration.end()-1, '{', '}')))
        # Include local handler-called helpers (including free functions and
        # Ballot-derived overrides), not just the three headline classes.
        for match in re.finditer(r'(?m)^[ \t]*[A-Za-z_][\w:*&<> \t]*?\b(\w+)\s*\(', masked):
            if "::" in match[0] or re.search(r"\b(return|new|delete)\b", match[0]) or match[1] in ("if", "while", "for", "switch", "return", "assert"):
                continue
            end_args = closing(masked, match.end()-1, '(', ')')
            brace = masked.find('{', end_args)
            semicolon = masked.find(';', end_args)
            if brace < 0 or 0 <= semicolon < brace:
                continue
            try:
                end = closing(masked, brace, '{', '}')
            except ValueError:
                continue  # mutually exclusive preprocessor entry-point signatures
            # Require a typed declaration, excluding bare calls/return expressions.
            line = source.count('\n', 0, match.start())+1
            owner = next((owner for owner, begin, finish in class_spans if begin < match.start() < finish), None)
            method = f"{owner}::{match[1]}" if owner else match[1]
            unit = dict(key=f"{name}:{line} {method}", file=name, line=line,
                        end_line=source.count('\n',0,end)+1, method=method,
                        kind="inline-method" if owner else "helper", _body=masked[brace:end])
            candidates.append(unit)
            if owner:
                units.append(unit)  # virtual site entry points can be called from IGC headers
        for owner, begin, finish in class_spans:
            for match in re.finditer(r'(?m)^[ \t]*(?:virtual[ \t]+)?(~?'+owner+r')\s*\(', masked[begin:finish]):
                start = begin+match.start()
                end_args = closing(masked, begin+match.end()-1, '(', ')')
                brace = masked.find('{', end_args)
                end = closing(masked, brace, '{', '}')
                line = source.count('\n',0,start)+1
                units.append(dict(key=f"{name}:{line} {owner}::{match[1]}", file=name,
                                  line=line, end_line=source.count('\n',0,end)+1,
                                  method=f"{owner}::{match[1]}", kind="inline-method",
                                  _body=masked[brace:end]))
    while True:
        bodies = '\n'.join(u.get('_body', '') for u in units)
        names = set(re.findall(r'\b(~?\w+)\s*\(', bodies))
        present = {u['key'] for u in units}
        added = [u for u in candidates if u['key'] not in present and u['method'].split('::')[-1] in names]
        if not added:
            break
        units.extend(added)
    for unit in units:
        unit.pop('_body', None)
    keys = [u['key'] for u in units]
    if len(keys) != len(set(keys)):
        raise ValueError("Duplicate inventory identity")
    return units


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native-root', type=Path, required=True)
    parser.add_argument('--dump', type=Path)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    units = inventory(args.native_root)
    if args.dump:
        args.dump.write_text(json.dumps(units, indent=2)+'\n')
    if args.check:
        with (ROOT/'docs/parity/sim-rules-matrix.csv').open(newline='') as f:
            rows = [r for r in csv.DictReader(f) if r['area'] == FAMILY]
        represented = {r['native_body'] for r in rows}
        missing = {u['key'] for u in units} - represented
        stale = {key for key in represented if key not in {u['key'] for u in units}}
        if missing or stale:
            raise SystemExit(f"Missing bodies: {sorted(missing)}; stale bodies: {sorted(stale)}")
    print(f"{len(units)} bodies/cases: " + ', '.join(
        f"{name}={sum(u['file']==name for u in units)}" for name in FILES))


if __name__ == '__main__':
    main()
