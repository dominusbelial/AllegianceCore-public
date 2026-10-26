#!/usr/bin/env python3
"""Measure logged warp legs for named flying-strike promotions in copied logs.

This is a subset audit, not a census of all reserved bombers. Delays start at
the server's finale arm, so they include refit/waiting as well as travel.
"""
import argparse
import collections
import json
import re
import statistics
from pathlib import Path
from audit import log_lines


def quantiles(values):
    values = sorted(values)
    return {"count": len(values), "median": statistics.median(values) if values else None,
            "p95": values[min(len(values) - 1, int(len(values) * .95))] if values else None}


def analyze(server, workers, stop_after=None):
    named = {}
    for path in workers:
        episodes = collections.Counter()
        for _, line in log_lines(path):
            if line.startswith("[p598]"):
                m = re.search(r"re-readied bot '(.*?)' ship (\d+) after rotation", line)
                if m:
                    episodes[m[1]] += 1
            elif line.startswith("[finale-duty]") and "accepted strike" in line:
                m = re.search(r"\[finale-duty\] (\S+) ship=(\d+) hull=(\d+).*sector=(\d+)", line)
                if m:
                    named[(episodes[m[1]], int(m[2]))] = int(m[4])
    match, armed = 0, {}
    hulls, pods, side_by_ship, last_warp, life, arrived = {}, set(), {}, {}, collections.Counter(), set()
    edges = [collections.Counter(), collections.Counter()]
    residence = [collections.defaultdict(list), collections.defaultdict(list)]
    arrivals, deaths = [], []
    bounds = []
    for _, line in log_lines(server, stop_after):
        if line.startswith("[start]") and "countdown begun" in line:
            match += 1
            hulls.clear(); pods.clear(); last_warp.clear(); life.clear()
        elif line.startswith("[spawn]"):
            m = re.search(r"ship (\d+) seated on side (\d+)", line)
            if m:
                side_by_ship[int(m[1])] = int(m[2])
        elif line.startswith("[finale] research"):
            m = re.search(r"deadline=([\d.]+); timeout=([\d.]+)", line)
            if m:
                armed[match] = float(m[1]) - float(m[2])
                bounds.append({"match": match, "armSim": armed[match], "timeout": float(m[2])})
        elif line.startswith("[loadout]") and "swapping base hull" in line:
            m = re.search(r"ship (\d+).* -> (\d+) \('([^']+)'\)", line)
            if m:
                ship = int(m[1]); hulls[ship] = m[3]
                last_warp.pop(ship, None)
                pods.discard(ship)
        elif line.startswith("[pod-trip] eject"):
            m = re.search(r"ship=(\d+) side=(\d+) sector=(\d+) at=([\d.]+)", line)
            if m:
                ship, side, sector, at = int(m[1]), int(m[2]), int(m[3]), float(m[4])
                if (match, ship) in named and match in armed and at >= armed[match] and ship not in pods \
                        and "bomber" in hulls.get(ship, "").lower() and sector != named[(match, ship)]:
                    deaths.append({"match": match, "ship": ship, "side": side, "sector": sector,
                                   "atSim": at, "source": "not attributed by these log records"})
                pods.add(ship); last_warp.pop(ship, None); life[ship] += 1
        elif line.startswith("[warp]"):
            m = re.search(r"ship (\d+) crossed from .*?\((\d+)\) to .*?\((\d+)\).*t=([\d.]+)", line)
            if not m:
                continue
            ship, origin, target, at = int(m[1]), int(m[2]), int(m[3]), float(m[4])
            if (match, ship) not in named or ship in pods or "bomber" not in hulls.get(ship, "").lower():
                continue
            previous = last_warp.get(ship)
            last_warp[ship] = (at, target)
            if match not in armed or at < armed[match] or ship not in side_by_ship:
                continue
            side = side_by_ship[ship]
            edges[side][f"{origin}->{target}"] += 1
            if previous is not None and previous[0] >= armed[match] and previous[1] == origin:
                residence[side][str(origin)].append(at - previous[0])
            key = (match, ship, life[ship])
            if target == named[(match, ship)] and key not in arrived:
                arrived.add(key)
                arrivals.append({"match": match, "ship": ship, "side": side, "sector": target,
                                 "atSim": at, "secondsSinceServerArm": at - armed[match]})
    return {"namedPromotionKeys": len(named), "bounds": bounds, "edgesAfterServerArm": edges,
            "sectorResidenceBetweenRecordedCrossings": [{k: quantiles(v) for k, v in s.items()} for s in residence],
            "targetSectorArrivals": arrivals, "enRouteBomberEjections": deaths,
            "arrivalDelaySinceServerArm": [quantiles([a["secondsSinceServerArm"] for a in arrivals if a["side"] == s]) for s in (0, 1)],
            "limits": "Only ships explicitly logged as flying-strike promotions, aligned by each bot's reseating episode. Only flights with a logged hull name containing Bomber are included; legal strike fallbacks are excluded. Residence is between crossings within one life, excluding observed refits; it includes combat/holding. No-arrival lives are censored, not assigned a travel time. Arrival is target-sector entry, not physical standoff assembly. Source weapons cannot be inferred from ejection records."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--bots", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--stop-after", type=int, help="Stop after N mission #-1 timing receipts")
    args = parser.parse_args()
    result = analyze(args.server, args.bots, args.stop_after)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print("Named promotions:", result["namedPromotionKeys"], "arrivals:", len(result["targetSectorArrivals"]),
          "en-route ejections:", len(result["enRouteBomberEjections"]))
