"""Recompute the P1104b match and fleet tables from their compact receipts."""

import json
from collections import Counter
from pathlib import Path


def measure(game):
    arm = game["armedLine"]
    changes = game["stationChanges"]
    kills = [c for c in changes if c[2] == "destroyed"]
    assert game["credit"] == [sum(c[3] == side for c in kills) for side in (0, 1)]
    hits = game["firstHit"]
    waves = Counter(c[1] for c in game["legacyCommits"])
    pre_waves = Counter(c[1] for c in game["legacyCommits"] if arm is None or c[0] < arm)
    pre = Counter(c[3] for c in kills if arm is None or c[0] < arm)
    homes = dict(game["home"])
    losses = Counter(homes[i] for i in {c[1] for c in changes} if i in homes)
    raids = game["raidEvents"]
    ends = {(e[1], e[2]): e[5]["result"] for e in raids if e[3] == "outcome"}
    outcomes = [(e[1], ends.get((e[1], e[2]), "unresolved")) for e in raids if e[3] == "launch"]
    return {
        "kills": game["credit"],
        "pre": [pre[0], pre[1]],
        "waves": [waves[0], waves[1]],
        "pre_waves": [pre_waves[0], pre_waves[1]],
        "outcomes": outcomes,
        "first": round(hits[0] - game["start"], 2) if hits else None,
        "homes": [losses[0], losses[1]],
    }


def sides(values):
    return "/".join(map(str, values))


def aggregate(games):
    rows = [measure(g) for g in games]
    outcomes = Counter(o for r in rows for _, o in r["outcomes"])
    return [len(rows), sum(sum(r["kills"]) for r in rows), sum(sum(r["pre"]) for r in rows),
            sum(sum(r["waves"]) for r in rows), sum(outcomes.values()), outcomes["credited-kill"],
            outcomes["repulse"], outcomes["withdrawn"], outcomes["timeout"],
            sum(r["first"] is not None for r in rows), sum(sum(r["homes"]) for r in rows)]


def tables(data):
    print("### Fixed-build match windows\n")
    print("| Seed, combat seats | Arm | Winner, end / decider | Kills S0/S1 | Pre-finale S0/S1 | "
          "Legacy commits S0/S1 (pre-finale) | Raid launches and outcomes | First useful hit (s) | Starting homes lost S0/S1 |")
    print("|---|---|---|---|---|---|---|---:|---|")
    for g in data["P1104b"]:
        r = measure(g)
        teams = list(map(len, g["combatShips"]))
        end, winner, by, _ = g["decision"]
        decider = {"kills": "ship kills", "station-kills": "station kills"}.get(by, by)
        launched = ", ".join(f"S{side} {result}" for side, result in r["outcomes"])
        raid = f'{len(r["outcomes"])} {launched}'.strip()
        print(f'| {g["seed"]}, {teams[0]}v{teams[1]} | {g["arm"].upper()} | S{winner}, {end} / {decider} | '
              f'{sides(r["kills"])} | {sides(r["pre"])} | {sides(r["waves"])} ({sides(r["pre_waves"])}) | {raid} | '
              f'{r["first"] if r["first"] is not None else "—"} | {sides(r["homes"])} |')
    print("\n### Before and after\n")
    print("| Build and fleet | Arm | Games | Station kills | Pre-finale kills | Legacy commits | "
          "Raid launches | Credited raid kills | Repulse | Withdrawn | Timeout | Games with a useful hit | Homes lost |")
    print("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for fleet in ("preset", "delivery", "all"):
        for build in ("P1104", "P1104b"):
            for arm in ("off", "on"):
                games = [g for g in data[build] if g["arm"] == arm and
                         (fleet == "all" or (g["seed"] <= 11044) == (fleet == "preset"))]
                print(f"| {build} {fleet} | {arm.upper()} | " + " | ".join(map(str, aggregate(games))) + " |")


if __name__ == "__main__":
    assets = Path(__file__).resolve().parents[1] / "docs/reports/FIRST_SUSTAINABLE_ATTACK_P1104_assets"
    tables(json.loads((assets / "p1104b-receipts.json").read_text()))
