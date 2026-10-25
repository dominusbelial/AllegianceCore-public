#!/usr/bin/env python3
"""Tally P825 assembly/game-over logs by civilization and side position, locally.

python3 tools/p819-side-bias-proof/matchups.py --server server.log --output tally.json
Each GAME_OVER carries the actual civ tuple, so lobby changes and interleaved
missions require no inference from a preceding assembly or bot log.
"""
import argparse
import collections
import json
import re
from pathlib import Path


SIDES = re.compile(r"side(\d+)=(-?\d+):(.*?)\(([^()]*)\)(?=\s+side\d+=|$)")


def tally(lines):
    civs, positions = {}, {}
    assembled, completed = [], []
    for line in lines:
        if not (line.startswith("[matchup]") or (line.startswith("[gameover] mission") and "Stage Over" in line)):
            continue
        header = re.search(r"mission #(\d+).*?match=(\d+) seed=(-?\d+)", line)
        if not header:
            continue  # Pre-P825 logs have no civ data; do not invent it.
        sides = [{"side": int(s), "civ": int(c), "name": n, "plan": p}
                 for s, c, n, p in SIDES.findall(line.strip())]
        record = {"mission": int(header[1]), "match": int(header[2]), "seed": int(header[3]), "sides": sides}
        if line.startswith("[matchup]"):
            assembled.append(record)
            continue
        winner = re.search(r"iSideWinner=(NA\(draw\)|\d+)", line)
        if not winner:
            continue
        record["winner"] = None if winner[1] == "NA(draw)" else int(winner[1])
        completed.append(record)
        for side in sides:
            civ = civs.setdefault(str(side["civ"]), {"name": side["name"], "appearances": 0, "wins": 0, "losses": 0, "draws": 0})
            position = positions.setdefault(str(side["side"]), {"appearances": 0, "wins": 0, "losses": 0, "draws": 0})
            outcome = "draws" if record["winner"] is None else "wins" if record["winner"] == side["side"] else "losses"
            for counter in (civ, position):
                counter["appearances"] += 1
                counter[outcome] += 1
    tuples = collections.Counter(",".join(str(s["civ"]) for s in match["sides"]) for match in completed)
    return {"assembled": assembled, "completed": completed, "byCiv": civs, "byPosition": positions,
            "completedMatchups": dict(tuples), "completedMatches": len(completed)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with args.server.open(errors="replace") as stream:
        result = tally(stream)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(f"{result['completedMatches']} completed matches; {len(result['completedMatchups'])} matchups; tally: {args.output}")
