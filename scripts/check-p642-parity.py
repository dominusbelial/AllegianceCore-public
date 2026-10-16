#!/usr/bin/env python3
"""Validate P642's row audit against the matrix and existing source citations."""
import csv
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(name):
    with (ROOT / name).open(newline="") as source:
        return list(csv.DictReader(source))

matrix = read("docs/parity/parity-matrix.csv")
audit = read("docs/reports/P642_VERIFIED_ROWS.csv")
indexed = {(r["area"], r["native_item"]): r for r in matrix}
assert len(indexed) == len(matrix), "duplicate matrix identity"
verified = {(r["area"], r["native_item"]) for r in audit}
assert len(verified) == len(audit), "duplicate audit identity"
assert {(r["area"], r["native_item"]) for r in matrix if r["area"] == "commands"} <= verified
for row in audit:
    identity = row["area"], row["native_item"]
    assert row["status"] == indexed[identity]["port_status"], identity
    for column in ("action_cite", "handler_cite", "test_cite"):
        for path, line in re.findall(r"((?:src|tests)/[^; ,]+\.cs):(\d+)", row[column]):
            target = ROOT / path
            assert target.is_file(), target
            assert 0 < int(line) <= len(target.read_text().splitlines()), (target, line)
    if row["status"] == "done":
        assert row["handler_cite"] and row["test_cite"], identity
for row in matrix:
    assert row["port_status"] in {"done", "partial", "missing", "na"}, row
    assert 1 <= int(row["weight"]) <= 5, row
print(f"Validated {len(audit)} audit rows; {len(matrix)} matrix rows")
for area in ("commands", "fedmsg", "screens", "parts", "commander", "lobby", "total"):
    rows = [r for r in matrix if (area == "total" or r["area"] == area) and r["port_status"] != "na"]
    done = [r for r in rows if r["port_status"] == "done"]
    weighted = 100 * sum(int(r["weight"]) for r in done) / sum(int(r["weight"]) for r in rows)
    print(f"{area}: weighted {weighted:.1f}%, unweighted {100 * len(done) / len(rows):.1f}%")
