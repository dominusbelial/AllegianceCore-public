#!/usr/bin/env python3
"""Validate parity CSVs and score completed rows; partial earns no credit."""

import argparse
import csv
from decimal import Decimal, InvalidOperation
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
MATRICES = ("parity-matrix.csv", "sim-rules-matrix.csv",
            "lobby-matrix.csv", "soundengine-matrix.csv", "allsrvui-matrix.csv",
            "effect-matrix.csv", "bots-matrix.csv", "model-tools-matrix.csv")
STATUSES = {"done", "done-visual", "implemented", "partial", "missing", "na", "n/a"}
COMPLETE = {"done", "done-visual", "implemented"}
EXCLUDED = {"na", "n/a"}
REACHABILITY = {"production", "config-only", "test-only"}


def read_matrix(path):
    """Return validated rows, rejecting shifted fields even with valid CSV syntax."""
    with Path(path).open(encoding="utf-8-sig", newline="") as source:
        reader = csv.reader(source, strict=True)
        try:
            header = next(reader)
            if len(set(header)) != len(header) or not {
                "area", "native_item", "native_cite", "port_status",
                "port_cite_or_reason", "weight",
            } <= set(header):
                raise ValueError(f"{path}: invalid or duplicate columns")
            if (Path(path).name == "sim-rules-matrix.csv" or "native_rule" in header) and "reachability" not in header:
                raise ValueError(f"{path}: sim rows require a reachability column")
            rows = []
            identities = set()
            for fields in reader:
                location = f"{path}:{reader.line_num}"
                if len(fields) != len(header):
                    raise ValueError(f"{location}: expected {len(header)} columns, got {len(fields)}")
                row = dict(zip(header, fields))
                if "reachability" in header and row["reachability"] not in REACHABILITY:
                    raise ValueError(f"{location}: invalid reachability {row['reachability']!r}")
                if row["port_status"] not in STATUSES:
                    raise ValueError(f"{location}: invalid status {row['port_status']!r}")
                if row["port_status"] == "done-visual":
                    if not row["port_cite_or_reason"].startswith("VISUAL:"):
                        raise ValueError(
                            f"{location}: done-visual row must start its reason with 'VISUAL:'")
                try:
                    weight = Decimal(row["weight"])
                except InvalidOperation:
                    raise ValueError(f"{location}: invalid weight {row['weight']!r}") from None
                if not weight.is_finite() or weight <= 0:
                    raise ValueError(f"{location}: weight must be finite and positive")
                identity = row.get("row_id", (row["area"], row["native_item"]))
                if not row["area"] or not row["native_item"] or not identity or identity in identities:
                    raise ValueError(f"{location}: empty or duplicate row identity {identity!r}")
                identities.add(identity)
                rows.append(row)
            return rows
        except StopIteration:
            raise ValueError(f"{path}: empty CSV") from None
        except csv.Error as error:
            raise ValueError(f"{path}:{reader.line_num}: {error}") from error


def score(rows):
    eligible = [row for row in rows if row["port_status"] not in EXCLUDED]
    completed = [row for row in eligible if row["port_status"] in COMPLETE
                 and row.get("reachability", "production") == "production"]
    visual = [row for row in completed if row["port_status"] == "done-visual"]
    weight = sum((Decimal(row["weight"]) for row in eligible), Decimal(0))
    credit = sum((Decimal(row["weight"]) for row in completed), Decimal(0))
    visual_weight = sum((Decimal(row["weight"]) for row in visual), Decimal(0))
    return len(completed), len(eligible), credit, weight, visual_weight


def scorecard(rows):
    return [(area, score([row for row in rows if row["area"] == area]))
            for area in sorted({row["area"] for row in rows})] + [("TOTAL", score(rows))]


def percentage(numerator, denominator):
    return f"{100 * numerator / denominator:.2f}%" if denominator else "n/a"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="*", type=Path,
                        help="CSV paths (default: both repository matrices)")
    args = parser.parse_args(argv)
    paths = args.paths or [ROOT / "docs/parity" / name for name in MATRICES]
    try:
        matrices = [(path, read_matrix(path)) for path in paths]
    except (ValueError, OSError) as error:
        print(error, file=sys.stderr)
        return 1
    for path, rows in matrices:
        print(f"{path.name}: validated {len(rows)} rows")
        print("area | weighted (credit/weight) | unweighted (complete/eligible) | visual-weight")
        for area, (done, count, credit, weight, visual_weight) in scorecard(rows):
            print(f"{area} | {percentage(credit, weight)} ({credit}/{weight}) | "
                  f"{percentage(done, count)} ({done}/{count}) | visual {visual_weight}")
        if rows and "reachability" in rows[0]:
            print("Reachability (non-production stays in the denominator; earns no headline credit):")
            for reachability in sorted(REACHABILITY):
                group = [row for row in rows if row["reachability"] == reachability]
                eligible = [row for row in group if row["port_status"] not in EXCLUDED]
                implemented = [row for row in eligible if row["port_status"] in COMPLETE]
                print(f"{reachability}: {len(group)} rows; {len(eligible)} eligible; "
                      f"{len(implemented)} implemented; implemented weight "
                      f"{sum((Decimal(row['weight']) for row in implemented), Decimal(0))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
