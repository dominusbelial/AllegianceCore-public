#!/usr/bin/env python3
"""Stream copied showcase logs into small, per-side evidence; no remote access.

python3 tools/p819-side-bias-proof/audit.py --server .scratch/rig/server.log \
  --bots .scratch/rig/bots/err.worker-*.log --output .scratch/p819/rig-audit.json
"""
import argparse
import collections
import hashlib
import json
import re
import statistics
from pathlib import Path


def log_lines(path, stop_after=None):
    """Stream bare or docker --timestamps logs, preserving the UTC wall clock.

    stop_after counts mission #-1 rotation timing receipts, not deadlines or countdowns.
    It also works on a truncated capture: EOF is never fabricated as GAME_OVER.
    """
    ended = 0
    with path.open(errors="replace") as stream:
        for raw in stream:
            stamp, sep, rest = raw.partition(" ")
            timestamp = stamp if sep and re.fullmatch(r"\d{4}-\d\d-\d\dT\S+Z", stamp) else None
            line = rest if timestamp else raw
            yield timestamp, line
            if line.startswith("[gameover-timing] mission #-1"):
                ended += 1
                if stop_after is not None and ended >= stop_after:
                    break


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
                    # P821 fallback retains the requested scoring duty in
                    # role=; the temporary label describes the actual hull.
                    temporary = re.search(r"\btemporary (\w+)", m[2])
                    composition[side][temporary[1] if temporary else roles[-1]] += 1
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


def position_audit(server, bots=(), stop_after=None):
    """Per-life, per-match extension of the P819 audit (two-side captures).

    KILL_SHIP flags are the scoring authority. An ejection is a hull loss,
    not a true death; lifepod deaths never earn kill credit. Combat's final
    'killed by' line exists only for true deaths. Do not use it as a kill census.
    """
    matches, current = [], None
    ships, stations = {}, {}
    pending_flags, pending_killer = {}, None
    ping, pending_launch, pending_trip = None, [], {}
    observed_timing_receipts = 0

    def new_match(stamp):
        return dict(match=len(matches) + 1, countdownUtc=stamp, endUtc=None,
                    finale=None, deadline=None, result=None, skills={},
                    events=collections.Counter(), attackers=collections.Counter(), lossSources=collections.Counter(),
                    economy=[collections.Counter(), collections.Counter()],
                    miners=[collections.Counter(), collections.Counter()],
                    developments=[{}, {}], purchases=[collections.Counter(), collections.Counter()],
                    initialHulls=[collections.Counter(), collections.Counter()],
                    trips=[collections.defaultdict(list), collections.defaultdict(list)],
                    launchBounds=[collections.defaultdict(list), collections.defaultdict(list)],
                    commute=[collections.defaultdict(list), collections.defaultdict(list)],
                    entries=[collections.Counter(), collections.Counter()],
                    stationKills=[], firstSpawns=[[], []], firstUndocks=[None, None],
                    firstClashEntries=[None, None], maxSim=None, lastUtc=stamp,
                    unmatchedKillPackets=0)

    def phase():
        return "finale" if current["finale"] else "pre"

    def record_loss(ship, side, sector, at, stamp):
        flags = pending_flags.pop(ship, None)
        if flags is None:
            return  # explicit ejections without a damage event are counted below
        credit, death, killer = flags
        sh = ships.get(ship, {})
        hull = sh.get("hull", "unlogged")
        kind = "pod" if sh.get("pod") else "miner" if hull == "Miner" else "bomber" if "bomber" in hull.lower() else "other"
        current["events"][(side, phase(), str(sector), hull, kind, credit, death)] += 1
        if credit:
            attacker = ships.get(killer, {})
            current["attackers"][(1-side, phase(), attacker.get("hull", "unlogged"),
                                  bool(attacker.get("pod")), kind)] += 1
        current["lossSources"]["player-ship" if killer is not None else "no-player-killer-log"] += 1
        current["maxSim"] = at
        if kind == "miner":
            current["miners"][side]["lost"] += 1
            current["miners"][side]["creditedLosses"] += int(credit)
        if not death:
            sh["pod"] = True
        sh["sector"] = sector
        sh["launch"] = None
        sh["lastWarp"] = None

    for stamp, line in log_lines(server, stop_after):
        if line.startswith("[gameover-timing] mission #-1"):
            observed_timing_receipts += 1
        m = re.search(r"\[spawn\] ship (\d+) seated on side (\d+).*\(id (\d+)\)", line)
        if m:
            ship, side, sector = map(int, m.groups())
            sh = ships.setdefault(ship, {})
            sh.update(side=side, sector=sector, pod=False)
            if current and current["endUtc"] is None and len(current["firstSpawns"][side]) < 2:
                current["firstSpawns"][side].append(dict(ship=ship, utc=stamp))
        m = re.search(r"\[initial-miner\] side (\d+).*ship (\d+).*station (\d+).*in '(.*?)'", line)
        if m:
            side, ship, station = map(int, m.groups()[:3])
            # The named homes are specific to this two-side spider-map audit.
            sector = {"Ganymede":1050, "Organia":1051}.get(m[4], "unlogged")
            stations[station] = sector
            ships[ship] = dict(side=side, hull="Miner", sector=sector, pod=False, initialMiner=True)
        if line.startswith("[start]") and "countdown begun" in line:
            current = new_match(stamp)
            matches.append(current)
            pending_flags.clear(); pending_trip.clear(); pending_launch.clear()
            pending_killer = None
            for sh in ships.values():
                sh.pop("launch", None); sh.pop("lastWarp", None)
                sh.pop("purchased", None)
                if sh.get("initialMiner"):
                    current["miners"][sh["side"]]["initial"] += 1
            continue
        if not current or current["endUtc"] is not None:
            continue
        current["lastUtc"] = stamp
        if line.startswith("[start]") and "countdown elapsed" in line:
            current["startedUtc"] = stamp
        m = re.search(r"\[ping\].*, server (\d+)\)", line)
        if m:
            ping = int(m[1]) / 1000
            for side, outcome, end, lower in pending_launch:
                if ping >= max(lower, end):
                    current["launchBounds"][side][outcome].append((max(0, lower-end), ping-end))
            pending_launch.clear()
        m = re.search(r"\[loadout\].*ship (\d+).*swapping base hull .* -> (\d+) \('(.*?)'\)", line)
        if m:
            ship, _, hull = m.groups(); sh = ships.setdefault(int(ship), {})
            sh.update(hull=hull, pod=False)
            if "side" in sh:
                current["purchases"][sh["side"]][hull] += 1
                if not sh.get("purchased"):
                    current["initialHulls"][sh["side"]][hull] += 1
                    sh["purchased"] = True
        m = re.search(r"\[drone\].*complete on side (\d+).*spawned drone ship (\d+).*hull '(.*?)'", line)
        if m:
            side, ship, hull = int(m[1]), int(m[2]), m[3]
            ships[ship] = dict(side=side, hull=hull, sector=1050+side, pod=False)
            if hull == "Miner": current["miners"][side]["built"] += 1
        m = re.search(r"\[scoring\] killer ship (\d+) kill \+1", line)
        if m: pending_killer = int(m[1])
        m = re.search(r"\[combat\] ship (\d+).*KILL_SHIP\(105\) bKillCredit=(True|False) bDeathCredit=(True|False)", line)
        if m:
            pending_flags[int(m[1])] = (m[2] == "True", m[3] == "True", pending_killer)
            pending_killer = None
        m = re.search(r"\[pod-trip\] eject ship=(\d+) side=(\d+) sector=(\d+) at=([\d.]+)", line)
        if m:
            ship, side, sector, at = int(m[1]), int(m[2]), int(m[3]), float(m[4])
            if ship not in pending_flags:
                current["lossSources"]["ejection-without-kill-packet"] += 1
            record_loss(ship, side, sector, at, stamp)
            ships.setdefault(ship, {}).update(side=side, sector=sector, pod=True, launch=None, lastWarp=None)
        m = re.search(r"\[combat\] ship (\d+) killed by .* at sim-t ([\d.]+)", line)
        if m:
            ship, at = int(m[1]), float(m[2]); sh = ships.get(ship, {})
            if sh.get("side") in (0, 1):
                record_loss(ship, sh["side"], sh.get("sector", "unlogged"), at, stamp)
            else:
                current["lossSources"]["unmapped-true-death"] += 1
                pending_flags.pop(ship, None)
        if line.startswith("[pod-trip] {"):
            t = json.loads(line[len("[pod-trip] "):]); side = t["Side"]
            if side in (0, 1):
                # This precedes KILL_SHIP for pod deaths and is more precise
                # than a last warp (rescue/ripcord need not produce a warp).
                ships.setdefault(t["Ship"], {})["sector"] = t["EndSector"]
                current["trips"][side][t["Outcome"]].append(t["Seconds"])
                if t["EjectSector"] == 1052:
                    current["trips"][side]["clash:"+t["Outcome"]].append(t["Seconds"])
                if t["Outcome"] in ("docked", "rescued", "station-rescued", "probe-rescued", "oxygen"):
                    pending_trip[t["Ship"]] = (side, t["Outcome"], t["EndAt"])
        m = re.search(r"\[undock\] ship (\d+) undocked from station (\d+)", line)
        if m:
            ship, station = int(m[1]), int(m[2]); sh = ships.get(ship, {})
            if sh.get("side") in (0, 1):
                side = sh["side"]
                sh["sector"] = stations.get(station, "unlogged")
                current["firstUndocks"][side] = current["firstUndocks"][side] or stamp
                sh.update(pod=False, launch=(ping, stamp, station), lastWarp=None, reachedClash=False)
                if ship in pending_trip and ping is not None:
                    s, outcome, end = pending_trip.pop(ship)
                    pending_launch.append((s, outcome, end, ping))
        m = re.search(r"\[warp\] ship (\d+) crossed from .*?\((\d+)\) to .*?\((\d+)\).* t=([\d.]+)", line)
        if m:
            ship, origin, target, at = int(m[1]), int(m[2]), int(m[3]), float(m[4])
            sh = ships.setdefault(ship, {}); previous = sh.get("lastWarp")
            sh["sector"] = target; sh["lastWarp"] = (target, at)
            current["maxSim"] = at
            if sh.get("side") in (0, 1) and not sh.get("pod"):
                side = sh["side"]; hull = sh.get("hull", "unlogged")
                current["entries"][side][f"{phase()}:{origin}->{target}:{'bomber' if 'bomber' in hull.lower() else 'other'}"] += 1
                if target == 1052 and not sh.get("reachedClash"):
                    sh["reachedClash"] = True
                    current["firstClashEntries"][side] = current["firstClashEntries"][side] or stamp
                    launch = sh.get("launch")
                    if launch and launch[0] is not None and at >= launch[0]:
                        key = f"{phase()}:{hull}"
                        current["commute"][side][key].append(at-launch[0])
                        if stations.get(launch[2]) == 1050+side:
                            current["commute"][side]["home:"+key].append(at-launch[0])
                if previous and previous[0] == origin and at >= previous[1] and "bomber" in hull.lower():
                    current["commute"][side][f"bomberResidence:{phase()}:{origin}"].append(at-previous[1])
        m = re.search(r"\[mine-sell\].*sold ([\d.]+) ore.*side (\d+)", line)
        if m:
            current["economy"][int(m[2])]["oreSold"] += float(m[1])
            current["economy"][int(m[2])]["sales"] += 1
        m = re.search(r"\[devcomplete\] side (\d+): completed development (\d+) '(.*?)'", line)
        if m:
            current["developments"][int(m[1])].setdefault(m[2]+":"+m[3], stamp)
        if line.startswith("[finale] research"):
            m = re.search(r"armedBy=(\d+).*deadline=([\d.]+); timeout=([\d.]+)", line)
            if m:
                current["finale"] = dict(utc=stamp, armedBy=int(m[1]), armSim=float(m[2])-float(m[3]), deadlineSim=float(m[2]))
        if line.startswith("[finale] deadline"):
            current["deadline"] = dict(winner=int(re.search(r"winner side=(\d+)", line)[1]),
                scores=[dict(side=int(s), kills=int(k), deaths=int(d)) for s,k,d in re.findall(r"(\d+):kills=(\d+)/deaths=(\d+)", line)])
        m = re.match(r"\[skills\] match \d+ side (\d+): (.*)", line)
        if m:
            current["skills"][m[1]] = {k:int(v) for k,v in re.findall(r"([\w-]+) (\d+)", m[2])}
        if line.startswith("[station-damage]") and "killed=True" in line:
            current["stationKills"].append(dict(utc=stamp, receipt=line.strip()))
        if line.startswith("[gameover] mission") and "Stage Over" in line:
            current["endUtc"] = stamp
            current["result"] = line.strip()
            for sh in ships.values(): sh.pop("initialMiner", None)

    if current:
        current["unmatchedKillPackets"] = len(pending_flags)

    for m in matches:
        m["eventColumns"] = ["victimSide", "phase", "sector", "hull", "kind", "killCredit", "trueDeath", "count"]
        m["events"] = [list(k)+[v] for k,v in sorted(m["events"].items())]
        m["attackerColumns"] = ["side", "phase", "lastPurchasedHull", "currentlyPod", "victimKind", "count"]
        m["attackers"] = [list(k)+[v] for k,v in sorted(m["attackers"].items())]
        m["status"] = "deadline" if m["deadline"] and m["endUtc"] else "ended-without-deadline" if m["endUtc"] else "censored"
        m["trips"] = [{k:quantiles(v) for k,v in s.items()} for s in m["trips"]]
        m["launchBounds"] = [{k:dict(lower=quantiles([x[0] for x in v]),upper=quantiles([x[1] for x in v])) for k,v in s.items()} for s in m["launchBounds"]]
        m["commute"] = [{k:quantiles(v) for k,v in s.items()} for s in m["commute"]]
        kills = [sum(r[-1] for r in m["events"] if r[0] == 1-s and r[5]) for s in (0,1)]
        deaths = [sum(r[-1] for r in m["events"] if r[0] == s and r[6]) for s in (0,1)]
        m["reconciled"] = dict(kills=kills,deaths=deaths)
        if m["deadline"]:
            m["reconciled"]["matchesDeadline"] = all(kills[s] == sc["kills"] and deaths[s] == sc["deaths"] for s,sc in enumerate(m["deadline"]["scores"]))
    with server.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    return dict(schema=2, source=server.parent.name,
                input=dict(file=server.name, bytes=server.stat().st_size, sha256=digest, stopAfter=stop_after),
                matches=matches, timingReceipts=observed_timing_receipts,
                workers=position_workers(bots, ships, matches), limits=[
                    "Credit is inferred to the opposing side only when KILL_SHIP says true; valid for these two-side captures.",
                    "Sector for ejections is authoritative pod-trip eject; pod deaths use trip EndSector; other true deaths use last warp/home. Unknown remains explicit.",
                    "Attacker hull is last purchased hull plus currentlyPod flag, not weapon launch hull: delayed missiles/mines can score after an ejection.",
                    "Initial hulls are first logged in-place swaps per ship per countdown, not initial role intent or fleet exposure.",
                    "Commute is successful undock to first non-pod Alderan entry in a life, measured from the preceding ping (upper duration estimate); home: restricts to original home station launches. Combat, diversion, exploration included; failures censored. Not a pure travel-time sample.",
                    "Phase boundary is server finale arm, not each worker's slightly later trigger. No per-bomber pose or damage-weapon attribution."])


def position_workers(paths, ships, matches):
    workers = []
    for path in paths:
        # Per-name reseating episodes are more reliable than one global reset:
        # workers re-seat their bots in an interleaved order.
        episodes, names, last_step = {}, {}, [None, None]
        # Initial purchase receipts precede the first re-ready receipt.
        for _, line in log_lines(path):
            m = re.search(r"\[p598\] re-readied bot '(.*?)' ship (\d+) after rotation", line)
            if m and int(m[2]) in ships:
                names[m[1]] = ships[int(m[2])].get("side")
        research_episode = [0, 0]
        clock = 0.
        records = {}
        def record(ep):
            if ep not in records:
                records[ep] = dict(match=ep, initialRoles=[collections.Counter(),collections.Counter()],
                    requestedRoles=[collections.Counter(),collections.Counter()], wallets=[[],[]],
                    research=[{},{}], triggers=[], commits=[[],[]], promotions=[0,0],
                    promotionNames=[set(),set()], stagingPeak=[0,0], warningCounts=[0,0], rearmed=0,
                    lastWorkerClock=0.)
            return records[ep]
        for _, line in log_lines(path):
            m = re.match(r"\[(?:intel|scout|opening)\] t=([\d.]+)s", line)
            if m: clock = max(clock, float(m[1]))
            m = re.search(r"\[p598\] re-readied bot '(.*?)' ship (\d+) after rotation", line)
            if m:
                name, ship = m[1], int(m[2]); episodes[name] = episodes.get(name,0)+1
                if ship in ships: names[name] = ships[ship].get("side")
            ep = max(episodes.values(), default=1)
            r = record(ep); r["lastWorkerClock"] = clock
            m = re.search(r"\[loadout\] (\S+) initial: (.*)", line)
            if m and names.get(m[1]) in (0,1):
                # Initial loadout precedes THIS bot's re-ready, including after
                # rotation. Do not use another bot's already advanced episode.
                r = record(episodes.get(m[1], 0)+1)
                side = names[m[1]]; labels = re.findall(r"\brole=(\w+)",m[2]); temporary = re.search(r"temporary (\w+)",m[2])
                if labels:
                    r["requestedRoles"][side][labels[0]] += 1
                    r["initialRoles"][side][temporary[1] if temporary else labels[-1]] += 1
            m = re.search(r"\[research\] side=(\d+).*step=(\d+)/(\d+).*wallet=(\d+)", line)
            if m:
                side, step, total, wallet = map(int,m.groups())
                if last_step[side] is None or step < last_step[side]: research_episode[side] += 1
                last_step[side] = step
                rr = record(research_episode[side]); rr["wallets"][side].append(wallet)
                research = rr["research"][side]
                research.setdefault("firstClock",clock); research["lastClock"] = clock
                research["lastStep"] = step; research["totalSteps"] = total
                # First observed >= milestone avoids silently dropping skipped steps.
                for milestone in (25,50,75,90):
                    if step >= milestone: research.setdefault(str(milestone),clock-research["firstClock"])
            if line.startswith("[finale] trigger"):
                r["triggers"].append(dict(workerClock=clock,receipt=line.strip()))
            if line.startswith("[finale] re-armed"): r["rearmed"] += 1
            m = re.search(r"\[strike\] side=(\d+) finale (commit|staging).*assembled=(\d+)", line)
            if m:
                side, kind, count = int(m[1]),m[2],int(m[3]); r["stagingPeak"][side] = max(r["stagingPeak"][side],count)
                if kind == "commit": r["commits"][side].append(dict(workerClock=clock,receipt=line.strip()))
            m = re.search(r"\[finale-duty\] (\S+) ship=(\d+).*accepted strike", line)
            if m and names.get(m[1]) in (0,1):
                side = names[m[1]]; r["promotions"][side] += 1; r["promotionNames"][side].add(m[1])
            m = re.search(r"\[finale\] side (\d+).*no buyable bomber", line)
            if m: r["warningCounts"][int(m[1])] += 1
        for r in records.values():
            r["wallets"] = [quantiles(v) for v in r["wallets"]]
            r["uniquePromotedNames"] = [len(v) for v in r.pop("promotionNames")]
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        workers.append(dict(file=path.name, bytes=path.stat().st_size,
                            sha256=digest, matches=list(records.values())))
    return workers


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--bots", type=Path, nargs="*", default=[])
    parser.add_argument("--per-match", action="store_true", help="P826 scoring, phases, sectors and lives")
    parser.add_argument("--stop-after", type=int, help="Stop after N mission #-1 timing receipts")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = position_audit(args.server, args.bots, args.stop_after) if args.per_match else audit(args.server, args.bots)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    if args.per_match:
        counts = collections.Counter(m['status'] for m in result['matches'])
        print(f"{dict(counts)}; audit: {args.output}")
    else:
        print(f"{len(result['matches'])} completed matches; audit: {args.output}")
