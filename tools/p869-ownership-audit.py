#!/usr/bin/env python3
"""Validate P869's per-row behavioral evidence and optional native citations.

This is an evidence/inventory check. The referenced behavioral tests, rather
than citation validity or managed ownership alone, establish equivalence.
"""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
EXPECTED = {f"SIM-{n:04d}" for n in [170, 631, 634, 685, 695, 788, 837,
    *range(704, 732), *range(733, 745)]} | {"fedmsg::11 STATIC_MAP_INFO"}


def read(path):
    with path.open(newline="", encoding="utf-8-sig") as source:
        return list(csv.DictReader(source))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-root", type=Path)
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()
    evidence = read(ROOT / "docs/parity/p869-ownership.csv")
    assert len(evidence) == len(EXPECTED) and {r["row_id"] for r in evidence} == EXPECTED
    current = {r["row_id"]: r for r in read(ROOT / "docs/parity/sim-rules-matrix.csv")}
    current.update({r["area"] + "::" + r["native_item"]: r for r in read(ROOT / "docs/parity/parity-matrix.csv")})
    audit = {r["row_id"]: r for r in read(ROOT / "docs/parity/p868-na-audit.csv")}
    tests = "\n".join(p.read_text() for p in (ROOT / "tests/Allegiance.Core.Tests").glob("*.cs"))
    native = {}
    ranges = []
    for row in evidence:
        key = row["row_id"]
        assert all(row.values()), f"empty evidence: {key}"
        assert row["port_status"] == current[key]["port_status"], key
        assert row["native_cite"] == current[key]["native_cite"] == audit[key]["native_cite"], key
        assert audit[key]["class"] == "B" and audit[key]["action"].startswith(row["port_status"] + ":"), key
        for method in row["tests"].split("; "):
            symbol = method.split(".", 1)[1].split("(", 1)[0]
            assert re.search(r"\b" + re.escape(symbol) + r"\s*\(", tests), (key, method)
        for path in row["port_paths"].split("; "):
            assert (ROOT / path).is_file(), (key, path)
        if not args.native_root:
            continue
        for path, first, last in re.findall(r"([\w/]+\.(?:cpp|CPP|h|H)):(\d+)(?:-(\d+))?", row["native_cite"]):
            if path not in native:
                native[path] = (args.native_root / path).read_text(errors="replace").splitlines()
            first, last = int(first), int(last or first)
            assert 1 <= first <= last <= len(native[path]), (key, path, first, last)
            source = "\n".join(native[path][first-1:last])
            assert source.strip(), key
            # Independently re-find the named native body at today's line number.
            item = current[key].get("native_item", "")
            if path.endswith(("missionigc.cpp", "FedSrv.CPP")) and "@" in item:
                item_name, at = item.rsplit("@", 1)
                function = item_name.rsplit("::", 1)[-1].rsplit("/", 1)[-1]
                if at.isdigit() and int(at) == first:
                    assert function in native[path][first-1], (key, "stale function", function)
            ranges.append({"row_id": key, "file": path, "first": first, "last": last,
                "sha256": hashlib.sha256(source.encode()).hexdigest()})
    result = {"rows": len(evidence), "implemented": sum(r["port_status"] == "implemented" for r in evidence),
        "partial": sum(r["port_status"] == "partial" for r in evidence), "native_ranges": ranges}
    if args.summary:
        args.summary.write_text(json.dumps(result, indent=2) + "\n")
    print(f"P869: {result['rows']} rows, {result['implemented']} implemented, {result['partial']} partial; evidence symbols and paths validated")
    if args.native_root:
        print(f"Native ranges re-read: {len(ranges)}; named definitions re-found at their cited lines")


if __name__ == "__main__":
    main()
