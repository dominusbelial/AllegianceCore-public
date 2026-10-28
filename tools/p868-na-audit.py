#!/usr/bin/env python3
"""Validate all 199 P868 classifications against the live parity matrices.

General-matrix row IDs use area::native_item because that matrix has no row_id.
Optional --native-root verifies every native source citation against a local,
read-only source tree. No native files or private paths are copied to the repo.
"""

import argparse
import collections
import csv
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
MATRICES = {"sim-rules-matrix.csv": 164, "parity-matrix.csv": 35}
CITE = re.compile(r"([\w/]+\.(?:cpp|CPP|h|H)):(\d+)(?:-(\d+))?")


def read(path):
    with path.open(encoding="utf-8-sig", newline="") as source:
        reader = csv.DictReader(source, strict=True)
        rows = list(reader)
        if any(None in row or any(value is None for value in row.values()) for row in rows):
            raise ValueError(f"{path.name}: malformed CSV row")
        return rows


def validate(root=ROOT, native_root=None):
    audit = read(root / "docs/parity/p868-na-audit.csv")
    expected_columns = {"row_id", "matrix", "class", "reason", "native_cite", "action"}
    if not audit or set(audit[0]) != expected_columns:
        raise ValueError("audit columns must be row_id,matrix,class,reason,native_cite,action")
    if collections.Counter(row["matrix"] for row in audit) != collections.Counter(MATRICES):
        raise ValueError("audit must contain the original 164 sim and 35 general exclusions")
    current = {}
    for matrix in MATRICES:
        for row in read(root / "docs/parity" / matrix):
            identity = row.get("row_id") or row["area"] + "::" + row["native_item"]
            key = (matrix, identity)
            if key in current:
                raise ValueError(f"duplicate matrix identity: {key}")
            current[key] = row
    seen = set()
    sources = {}
    for row in audit:
        key = (row["matrix"], row["row_id"])
        if key in seen or key not in current:
            raise ValueError(f"duplicate or unknown audit identity: {key}")
        seen.add(key)
        if row["class"] not in {"A", "B", "C"} or not all(row.values()):
            raise ValueError(f"invalid/incomplete classification: {key}")
        status = current[key]["port_status"]
        if row["class"] == "A":
            if status not in {"na", "n/a"} or not row["action"].startswith("retain n/a:"):
                raise ValueError(f"A must retain an explicit n/a reason: {key}")
        elif row["class"] == "C":
            if status != "partial" or not row["action"].startswith("decision:"):
                raise ValueError(f"C must remain partial with a decision: {key}")
        else:
            expected = "implemented" if row["action"].startswith("implemented:") else "partial"
            if status != expected or not row["action"].startswith(expected + ":"):
                raise ValueError(f"B action/status mismatch: {key}")
        if current[key]["native_cite"] != row["native_cite"]:
            raise ValueError(f"native citation drift: {key}")
        cites = list(CITE.finditer(row["native_cite"]))
        if not cites:
            raise ValueError(f"native file:line missing: {key}")
        if native_root:
            for cite in cites:
                name, start, end = cite.groups()
                if name not in sources:
                    sources[name] = (native_root / name).read_text(errors="replace").splitlines()
                first, last = int(start), int(end or start)
                if not 1 <= first <= last <= len(sources[name]):
                    raise ValueError(f"native citation out of bounds: {key}: {cite.group()}")
                if not any(line.strip() for line in sources[name][first - 1:last]):
                    raise ValueError(f"native citation contains no source text: {key}: {cite.group()}")
    for key, row in current.items():
        if (row["port_status"] in {"na", "n/a"}
                or row["port_cite_or_reason"].startswith("P868 ")) and key not in seen:
            raise ValueError(f"unclassified current exclusion: {key}")
    counts = collections.Counter(row["class"] for row in audit)
    completed = sum(row["action"].startswith("implemented:") for row in audit)
    print(f"P868: {len(audit)} rows; A={counts['A']} B={counts['B']} C={counts['C']}; "
          f"B implemented={completed}, partial={counts['B'] - completed}")
    if native_root:
        print(f"Native citation bounds/text checked in {len(sources)} files; "
              "this is a citation integrity check, not proof of behavioral equivalence.")
    return audit


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-root", type=Path)
    args = parser.parse_args()
    try:
        validate(native_root=args.native_root)
    except (ValueError, OSError, csv.Error) as error:
        parser.exit(1, str(error) + "\n")


if __name__ == "__main__":
    main()
