#!/usr/bin/env python3
"""Stream copied showcase logs into small, per-side evidence; no remote access.

python3 tools/p819-side-bias-proof/audit.py --server .scratch/rig/server.log \
  --bots .scratch/rig/bots/err.worker-*.log --output .scratch/p819/rig-audit.json
"""
import argparse
import collections
import json
import re
import statistics
from pathlib import Path


def quantiles(values):
    values = sorted(values)
    return {"count": len(values), "median": statistics.median(values) if values else None,
            "p95": values[min(len(values) - 1, int(len(values) * .95))] if values else None}


def audit(server, bots):
    sides = [collections.defaultdict(collections.Counter) for _ in range(2)]
    pods = [collections.defaultdict(list) for _ in range(2)]
    ship_side, last_killer, miners, hull_names, last_sector = {}, {}, {}, {}, {}
    miner_kills = []
    matches, skills, research_plans = [], [], set()
    ping_clock, awaiting_ping, trip_ended = None, [], {}
    relaunch = [collections.defaultdict(list), collections.defaultdict(list)]
    sim, first_sim = 0., None
    for line in server.open(errors="replace"):
        # Initial miners spawn BEFORE countdown. Clear the old mission at
        # GAME_OVER, before its replacement is assembled, not at countdown.
        if line.startswith("[gameover] mission") and "Stage Over" in line:
            miners.clear()
            trip_ended.clear()
        if line.startswith("[loadout]") and "swapping base hull" in line:
            hull = re.search(r"ship (\d+).* -> (\d+) \('([^']+)'\)", line)
            if hull:
                hull_names[int(hull[1])] = hull[3]
        elif line.startswith("[warp]"):
            warp = re.search(r"ship (\d+) crossed from .* to '([^']+)' \((\d+)\)", line)
            if warp:
                last_sector[int(warp[1])] = warp[3] + ":" + warp[2]
        if line.startswith("[ping]"):
            clock = re.search(r", server (\d+)\)", line)
            if clock:
                now = int(clock[1]) / 1000
                for side, outcome, end, lower in awaiting_ping:
                    if now >= lower and now >= end:
                        relaunch[side][outcome].append((max(0, lower - end), now - end))
                awaiting_ping.clear()
                ping_clock = now
        elif line.startswith("[undock]"):
            undock = re.search(r"ship (\d+) undocked", line)
            if undock and int(undock[1]) in trip_ended and ping_clock is not None:
                side, outcome, end = trip_ended.pop(int(undock[1]))
                awaiting_ping.append((side, outcome, end, ping_clock))
        match = re.search(r"(?:sim-t |\bat=)([\d.]+)", line)
        if match:
            sim = float(match[1])
            if first_sim is None:
                first_sim = sim
        match = re.search(r"\[spawn\] ship (\d+) seated on side (\d+)", line)
        if match:
            ship_side[int(match[1])] = int(match[2])
        match = re.search(r"\[initial-miner\] side (\d+).* ship (\d+)", line)
        if match:
            miners[int(match[2])] = int(match[1])
            sides[int(match[1])]["minerEvents"]["initialSpawns"] += 1
        match = re.search(r"\[combat\] ship (\d+) killed by (.*?) at sim-t ([\d.]+)", line)
        if match:
            last_killer[int(match[1])] = match[2]
            if int(match[1]) in miners:
                victim = int(match[1]); side = miners.pop(victim)
                sides[side]["minerEvents"]["loggedKills"] += 1
                killer = re.search(r"ship (\d+)", match[2])
                killer_id = int(killer[1]) if killer else None
                miner_kills.append({"side": side, "ship": victim, "atSim": float(match[3]),
                    "lastLoggedSector": last_sector.get(victim), "credit": match[2],
                    "killerSide": ship_side.get(killer_id), "killerHull": hull_names.get(killer_id)})
        if line.startswith("[research-plan]"):
            research_plans.add(line.strip())
        match = re.search(r"\[mine-sell\].*sold ([\d.]+) ore.*side (\d+)", line)
        if match:
            sides[int(match[2])]["economy"]["oreSold"] += float(match[1])
            sides[int(match[2])]["economy"]["sales"] += 1
        match = re.search(r"\[devcomplete\] side (\d+): completed development (\d+) '(.*?)'", line)
        if match:
            sides[int(match[1])]["developments"][match[3]] += 1
        match = re.search(r"\[drone\].*complete on side (\d+).*hull '(.*?)'", line)
        if match:
            sides[int(match[1])]["droneBuilds"][match[2]] += 1
            if match[2] == "Miner":
                spawned = re.search(r"spawned drone ship (\d+)", line)
                if spawned:
                    miners[int(spawned[1])] = int(match[1])
                    sides[int(match[1])]["minerEvents"]["builtSpawns"] += 1
        if line.startswith("[pod-trip] {"):
            trip = json.loads(line[len("[pod-trip] "):])
            side = trip["Side"]
            if side not in (0, 1):
                continue
            sides[side]["podOutcomes"][trip["Outcome"]] += 1
            sides[side]["ejectSectors"][str(trip["EjectSector"])] += 1
            pods[side][trip["Outcome"]].append(trip["Seconds"])
            if trip["Outcome"] in ("docked", "rescued", "probe-rescued", "oxygen"):
                trip_ended[trip["Ship"]] = (side, trip["Outcome"], trip["EndAt"])
            # KILL_SHIP does not identify projectile/mine/ram subtype here.
            # Keep the logged source; never label every ejection a gun kill.
            sides[side]["ejectKillSource"][last_killer.get(trip["Ship"], "unlogged/ejection")] += 1
        if line.startswith("[finale] deadline"):
            winner = int(re.search(r"winner side=(\d+)", line)[1])
            scores = re.findall(r"(\d+):kills=(\d+)/deaths=(\d+)", line)
            matches.append({"match": len(matches) + 1, "winner": winner,
                            "scores": [{"side": int(s), "kills": int(k), "deaths": int(d)} for s, k, d in scores],
                            "trackedMiners": [sum(v == s for v in miners.values()) for s in (0, 1)]})
        if re.match(r"\[skills\] match \d+ side \d+: flight", line):
            m = re.match(r"\[skills\] match (\d+) side (\d+): (.*)", line)
            skills.append({"match": int(m[1]), "side": int(m[2]),
                           "counts": {k: int(v) for k, v in re.findall(r"([\w-]+) (\d+)", m[3])}})
    name_sides = {}
    for path in bots:
        for line in path.open(errors="replace"):
            m = re.search(r"\[p598\] re-readied bot '(.*?)' ship (\d+) after rotation", line)
            if m and int(m[2]) in ship_side:
                name_sides[m[1]] = ship_side[int(m[2])]
    workers = []
    for path in bots:
        orders, promotion_orders = {}, []
        trigger_sides, commits, staging, warnings = collections.Counter(), [], [], collections.Counter()
        promotions = 0
        composition = [collections.Counter(), collections.Counter()]
        requested = [collections.Counter(), collections.Counter()]
        selected_hulls = [collections.Counter(), collections.Counter()]
        research = [[], []]
        wallets = [[], []]
        bot_time = 0
        for line in path.open(errors="replace"):
            # Finale trigger t= is relative to registration, whereas these
            # telemetry prefixes use the worker's monotonic lifetime clock.
            m = re.match(r"\[(?:intel|scout|opening)\] t=([\d.]+)s", line)
            if m:
                bot_time = float(m[1])
            m = re.search(r"\[research\] side=(\d+).*step=(\d+)/(\d+)", line)
            if m:
                side, step = int(m[1]), int(m[2])
                wallet = re.search(r"wallet=(\d+)", line)
                if wallet:
                    wallets[side].append(int(wallet[1]))
                if not research[side] or step < research[side][-1]["lastStep"]:
                    research[side].append({"firstLogAt": bot_time, "lastStep": step,
                                           "totalSteps": int(m[3]), "milestones": {}})
                episode = research[side][-1]
                episode["lastStep"] = step
                episode["lastLogAt"] = bot_time
                if step in (1, 25, 50, 75, 90):
                    episode["milestones"].setdefault(str(step), bot_time)
            m = re.search(r"\[loadout\] (\S+) initial: (.*)", line)
            if m and m[1] in name_sides:
                side = name_sides[m[1]]
                roles = re.findall(r"\brole=(\w+)", m[2])
                if roles:
                    composition[side][roles[-1]] += 1
                    requested[side][roles[0]] += 1
                hull = re.search(r"human hull (\d+) '([^']+)'", m[2])
                if hull:
                    selected_hulls[side][hull[1] + ":" + hull[2]] += 1
            m = re.search(r"\[p431\].*\(ship (\d+)\) AUTO-ACCEPTED.*cmdID=(\d+) target=(\w+)#(\d+)", line)
            if m:
                orders[int(m[1])] = {"command": int(m[2]), "type": m[3], "target": int(m[4])}
            # Both the real life reset and next-mission reseating clear orders.
            m = re.search(r"\[p539\].*\(ship (\d+)\).*relaunched", line)
            if m:
                orders.pop(int(m[1]), None)
            m = re.search(r"\[p598\].*ship (\d+) after rotation", line)
            if m:
                orders.pop(int(m[1]), None)
            m = re.search(r"\[finale-duty\].*ship=(\d+) hull=.*accepted strike.*target=(\d+) sector=(\d+)", line)
            if m:
                promotions += 1
                ship = int(m[1])
                if ship in orders:
                    promotion_orders.append({"ship": ship, "side": ship_side.get(ship),
                                             "old": orders[ship], "sector": int(m[3])})
            m = re.search(r"\[finale\] trigger sides=\[([^]]+)\]", line)
            if m:
                trigger_sides[m[1]] += 1
            m = re.search(r"\[finale\] side (\d+).*no buyable bomber strike loadout", line)
            if m:
                warnings[m[1]] += 1
            if "finale commit" in line or "finale staging" in line:
                record = dict(re.findall(r"(\w+)=([^\s]+)", line))
                (commits if "finale commit" in line else staging).append(record)
        workers.append({"file": path.name, "triggers": trigger_sides, "commits": commits,
                        "staging": staging, "bomberTechWarnings": warnings, "flyingPromotions": promotions,
                        "promotionsWithPriorAcceptedOrder": promotion_orders,
                        "initialPurchasesByRole": composition, "initialRequestedRoles": requested,
                        "initialPurchasedHulls": selected_hulls, "researchEpisodes": research,
                        "researchWallets": [quantiles(v) for v in wallets]})
    elapsed = sim - (first_sim or 0)
    for side in sides:
        side["economy"]["orePerSimMinute"] = side["economy"]["oreSold"] / (elapsed / 60) if elapsed else 0
        # Keep high-cardinality killer IDs out of the review artifact.
        sources = side.pop("ejectKillSource", {})
        side["ejectKillSources"] = collections.Counter()
        for source, count in sources.items():
            side["ejectKillSources"]["credited ship" if source.startswith("ship ") else source] += count
    return {"matches": matches, "skills": skills, "researchPlans": sorted(research_plans),
            "minerKills": miner_kills,
            "sides": sides, "podSecondsByOutcome": [{k: quantiles(v) for k, v in p.items()} for p in pods],
            "tripEndToUndockPingEstimates": [{k: {"lower": quantiles([b[0] for b in v]),
                "upper": quantiles([b[1] for b in v])} for k, v in p.items()} for p in relaunch],
            "observedSimSpan": elapsed, "workers": workers,
            "limits": "No per-bot pose timestamps, live miner census or income ledger in the copied logs. Ore/min is sold ore across the observed simulation span, not potential extraction. Prior order reconstruction uses accept and lifecycle logs; it is corroborating evidence, not a wire replay."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--bots", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.server, args.bots)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(f"{len(result['matches'])} completed matches; audit: {args.output}")
