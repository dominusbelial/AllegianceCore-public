#!/usr/bin/env python3
"""Audit P1001 coverage stages from ONE server lifetime and its worker logs.

Absent mechanisms stay absent. Selected hardware is never treated as granted,
launched, fired, hit or completed. A reachable catalogue is not an exercise quota.
"""
import argparse
import json
import math
from collections import Counter
from pathlib import Path

PREFIX = "[civ-coverage] "
STAGES = {"selected", "granted", "launched", "fired", "hit", "completed"}


def read_server(lines):
    matches = {}
    for line in lines:
        if PREFIX not in line:
            continue
        text = line.split(PREFIX, 1)[1].strip()
        if not text.startswith("{"):
            continue  # loopback captures can contain worker selections too
        row = json.loads(text)
        key = (row["match"], row["cookie"])
        match = matches.setdefault(key, dict(match=key[0], cookie=key[1], sequence=0, final=False,
                                            stages=Counter(), reachable={}, overflow=0))
        if match["final"] or row["sequence"] != match["sequence"] + 1:
            raise ValueError("duplicate, missing or post-final coverage window")
        if len(row["events"]) > 596:
            raise ValueError("coverage window exceeds producer cardinality bound")
        match["sequence"] = row["sequence"]
        match["final"] = row["final"]
        match["overflow"] += row["overflowEvents"]
        for event in row["events"]:
            if event["stage"] not in STAGES - {"selected"} or event["count"] < 0:
                raise ValueError("invalid server coverage stage/count")
            match["stages"][f'{event["side"]}/{event["stage"]}/{event["mechanism"]}/{event["id"]}'] += event["count"]
        for reachable in row["reachable"]:
            side = str(reachable["side"])
            if side in match["reachable"]:
                raise ValueError("duplicate reachable catalogue")
            match["reachable"][side] = reachable
    return [{**m, "stages": dict(m["stages"])} for m in matches.values()]


def read_worker(lines):
    selections = []
    previous = {}
    for line in lines:
        if PREFIX not in line:
            continue
        text = line.split(PREFIX, 1)[1].strip()
        if text.startswith("{"):
            continue  # in-process proofs can capture both server and worker
        row = dict(field.split("=", 1) for field in text.split())
        if row["stage"] != "selected":
            raise ValueError("unknown worker coverage stage")
        share = float(row["share"])
        if not math.isfinite(share) or not 0 <= share <= .25:
            raise ValueError("invalid coverage share")
        selected, covered = int(row["selected"]), int(row["covered"])
        if not 0 <= covered <= math.floor(selected * share):
            raise ValueError("coverage slot budget exceeded")
        if int(row["coverageMoney"]) + int(row.get("pendingCoverageMoney", 0)) > int(row["strategyMoney"]) * share / (1 - share) + 1e-6:
            raise ValueError("coverage resource budget exceeded")
        key = (row["epoch"], row["side"])
        if selected != previous.get(key, 0) + 1:
            raise ValueError("duplicate or missing worker selection")
        previous[key] = selected
        selections.append(row)
    return selections


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--worker", action="append", type=Path, default=[])
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    result = dict(server=read_server(args.server.read_text().splitlines()),
                  workers=[dict(source=p.name, selections=read_worker(p.read_text().splitlines())) for p in args.worker])
    args.out.write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
