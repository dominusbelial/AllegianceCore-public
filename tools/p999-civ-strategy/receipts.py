#!/usr/bin/env python3
"""Read bounded P1000 JSON receipts. Missing observations remain missing, not zero.

Usage: python3 tools/p999-civ-strategy/receipts.py server.log [...] --out receipts.json
Match identity includes the input file and server mission cookie; do not concatenate
restarted server lifetimes into one file without keeping their source boundary.
"""
import argparse
import json
from collections import defaultdict
from pathlib import Path

PREFIX = "[civ-strategy] "


def read_receipts(lines):
    matches = {}
    for number, line in enumerate(lines, 1):
        if PREFIX not in line:
            continue
        try:
            row = json.loads(line.split(PREFIX, 1)[1])
            key = (int(row["match"]), int(row["cookie"]))
            match = matches.setdefault(key, dict(match=key[0], cookie=key[1], choices={}, research=[],
                                                windows=[], grants=[], damage=[], sides={}, complete=False,
                                                overflow_events=0, overflow_amount=0))
            kind = row["kind"]
            if kind == "choice":
                side = str(row["side"])
                if side in match["choices"]:
                    raise ValueError("duplicate side choice (possible concatenated server lifetimes)")
                match["choices"][side] = row
            elif kind == "research":
                if any(r["side"] == row["side"] and r["tech"] == row["tech"] for r in match["research"]):
                    raise ValueError("duplicate research completion")
                match["research"].append(row)
            elif kind == "window":
                if match["complete"] or row["sequence"] != len(match["windows"]) + 1:
                    raise ValueError("missing, repeated, or post-final receipt window")
                if len(row["grants"]) > 256 or len(row["damage"]) > 256:
                    raise ValueError("receipt exceeds producer cardinality bound")
                match["windows"].append(dict(sequence=row["sequence"], sim=row["sim"], final=row["final"]))
                match["complete"] = row["final"]
                match["overflow_events"] += row["overflowEvents"]
                match["overflow_amount"] += row["overflowAmount"]
                for field in ("grants", "damage"):
                    match[field].extend(dict(window=row["sequence"], sim=row["sim"], **entry) for entry in row[field])
                for entry in row["sides"]:
                    side = str(entry["side"])
                    totals = match["sides"].setdefault(side, dict(side=entry["side"], civ=entry["civ"],
                        paydays=0, income=0, ore=0, oreCredits=0, stationKills=0, stationLosses=0, samples=[]))
                    for field in ("paydays", "income", "ore", "oreCredits", "stationKills", "stationLosses"):
                        totals[field] += entry[field]
                    totals["samples"].append(dict(sim=row["sim"], miners=entry["miners"], oreAboard=entry["oreAboard"]))
            else:
                raise ValueError("unknown receipt kind: " + str(kind))
        except (ValueError, TypeError, KeyError) as error:
            raise ValueError(f"line {number}: {error}") from error
    return [matches[key] for key in sorted(matches)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    result = [dict(source=path.name, matches=read_receipts(path.read_text().splitlines())) for path in args.logs]
    args.out.write_text(json.dumps(result, indent=2) + "\n")
    print(f"{sum(len(r['matches']) for r in result)} matches, "
          f"{sum(m['complete'] for r in result for m in r['matches'])} final windows")


if __name__ == "__main__":
    main()
