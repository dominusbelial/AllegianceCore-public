#!/usr/bin/env python3
"""Summarize loopback receipts and 30-second own-client samples into review JSON."""
import argparse
import collections
import json
import re
from pathlib import Path


def summarize(path):
    result = json.loads(path.read_text())
    label = result["Label"]
    log = path.with_name(label + ".log")
    commits = [collections.Counter(), collections.Counter()]
    superseded = [collections.Counter(), collections.Counter()]
    sample_hulls = [collections.Counter(), collections.Counter()]
    armed_at, reached = {}, set()
    transits = [[], []]
    armed_samples = [0, 0]
    staging = [[], []]
    first_trigger = None
    server_winner = None
    commit_events = []
    station_kills = []
    for line in log.open(errors="replace"):
        if line.startswith("[station-damage]") and "killed=True" in line:
            station_kills.append(line.strip())
        m = re.search(r"\[gameover\].*iSideWinner=(-?\d+)", line)
        if m:
            server_winner = int(m[1])
        if "[finale] trigger" in line:
            first_trigger = line.strip()
        m = re.search(r"\[strike\] side=(\d+) finale commit .*reason=(\S+)", line)
        if m:
            commits[int(m[1])][m[2]] += 1
            commit_events.append(line.strip())
            assembled = re.search(r"assembled=(\d+)", line)
            if assembled:
                staging[int(m[1])].append(int(assembled[1]))
        m = re.search(r"\[strike\] side=(\d+) finale staging .*assembled=(\d+).*", line)
        if m:
            staging[int(m[1])].append(int(m[2]))
        m = re.search(r"\[strike-route\] side=(\d+).*supersede=(\S+)", line)
        if m:
            superseded[int(m[1])][m[2].split("#")[0]] += 1
        if not line.startswith("[p819-snapshot] "):
            continue
        sample = json.loads(line[len("[p819-snapshot] "):])
        bots = {b["Name"]: b for b in sample["Bots"]}
        for bot in bots.values():
            sample_hulls[bot["Side"]][bot["Hull"]] += 1
        for finale in sample["Finale"]:
            for wing in finale["wings"]:
                side = wing["side"]
                for grant in wing["grants"]:
                    name = grant["Name"]
                    if grant["isPod"] or grant["docked"] or not grant["verified"]:
                        armed_at.pop(name, None)
                        reached.discard(name)
                        continue
                    if name not in bots:
                        continue
                    armed_samples[side] += 1
                    armed_at.setdefault(name, sample["Wall"])
                    if bots[name]["Sector"] == wing["targetSector"] and name not in reached:
                        reached.add(name)
                        transits[side].append(round(sample["Wall"] - armed_at[name], 2))
    finale = result["Finale"][-1] if result["Finale"] else {}
    defaults = {"Scale": None, "FinaleSeconds": None, "FinaleMinutes": None, "GameSeconds": None}
    if path.parent.name == "fixed-normal":  # receipts from the first harness revision
        defaults.update(Scale=1, FinaleSeconds=300, FinaleMinutes=.5)
    return {"source": path.parent.name, "label": label,
            "configuration": {k: result.get(k, v) for k, v in defaults.items()},
            "civs": result["Civs"], "stage": result["Stage"], "wallSeconds": result["Wall"],
            "simSeconds": result["Sim"], "sides": result["Sides"], "winner": server_winner,
            "reason": finale.get("gameOverReason"), "commits": commits, "supersededOrders": superseded,
            "commitEvents": commit_events,
            "stationKillEvents": station_kills,
            "targets": [{k: w.get(k) for k in ("side", "targetStation", "targetSide", "targetSector", "targetKilledAt")}
                        for w in finale.get("wings", [])],
            "firstTrigger": first_trigger, "armedOwnClientSamples": armed_samples,
            "transitToTargetSectorWallSeconds": transits,
            "peakAssembled": [max(s, default=0) for s in staging],
            "fleetHullSamples": sample_hulls, "simulationFailures": result["Failures"],
            "limits": "Transit is first sampled armed/undocked pose to first sampled target-sector pose in a life (30s sampling). Arriving before the first sample reads 0. Missing arrivals are censored; no inferred transit duration for them. Hull counts are repeated samples, not unique purchases."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("receipts", type=Path, nargs="+")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps([summarize(p) for p in args.receipts], indent=2) + "\n")
