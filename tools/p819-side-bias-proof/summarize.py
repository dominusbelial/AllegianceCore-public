#!/usr/bin/env python3
"""Summarize loopback receipts and 30-second own-client samples into review JSON."""
import argparse
import collections
import json
import re
import hashlib
import math
import struct
from pathlib import Path


def position_geometry(path):
    """Read the small native map, using the fixed Data*IGC layouts.

    Offsets mirror Core/Data/Data{Cluster,Warp,Station}IGC.Decode. No map
    modifications, simulation, fog bypass or gameplay route selection here.
    """
    raw = path.read_bytes()
    size, = struct.unpack_from('<i', raw)
    if size != len(raw)-4:
        raise ValueError('Expected exact DataSize map wrapper')
    sectors, warps, stations = {}, {}, []
    offset = 4
    while offset < len(raw):
        kind, size = struct.unpack_from('<hi', raw, offset)
        offset += 6
        b = raw[offset:offset+size]
        if len(b) != size: raise ValueError('Truncated map record')
        offset += size
        if kind == 13:
            if size != 92: raise ValueError('Cluster size')
            sector, = struct.unpack_from('<h', b, 28)
            sectors[sector] = dict(Id=sector, Name=b[34:59].split(b'\0')[0].decode(), Warps=[], Stations=[])
        elif kind == 7:
            if size != 108: raise ValueError('Warp size')
            wid, partner = struct.unpack_from('<hh', b)
            warps[wid] = dict(WarpId=wid, Partner=partner,
                             Position=list(struct.unpack_from('<fff', b, 60)),
                             Sector=struct.unpack_from('<h', b, 104)[0])
        elif kind == 1:
            if size != 88: raise ValueError('Station size')
            sector, side, station = struct.unpack_from('<hhh', b, 52)
            stations.append(dict(Sector=sector, SideId=side, StationId=station,
                                 Position=list(struct.unpack_from('<fff', b))))
    for w in warps.values():
        w['To'] = warps[w['Partner']]['Sector']
        sectors[w['Sector']]['Warps'].append(w)
    for st in stations: sectors[st['Sector']]['Stations'].append(st)

    def route(home, ids, final=None):
        position = home['Position']; legs = []; crossings = []
        for origin, target in zip(ids, ids[1:]):
            w, = [w for w in sectors[origin]['Warps'] if w['To'] == target]
            legs.append(math.dist(position, w['Position']))
            crossings.append(w['WarpId'])
            position = warps[w['Partner']]['Position']
        if final is not None: legs.append(math.dist(position, final))
        return dict(sectors=ids, departures=crossings, flightLegs=legs,
                    distance=sum(legs), arrivalPosition=position)

    homes = sorted(stations, key=lambda s:s['SideId'])
    clash, siege = [], []
    for home in homes:
        paths = []
        for w in sectors[home['Sector']]['Warps']:
            if any(x['To'] == 1052 for x in sectors[w['To']]['Warps']):
                r = route(home, [home['Sector'],w['To'],1052])
                r['distanceToClashOrigin'] = r['distance']+math.dist(r['arrivalPosition'],[0,0,0])
                paths.append(r)
        clash.append(dict(side=home['SideId'], routes=paths))
        enemy, = [s for s in homes if s['SideId'] != home['SideId']]
        ids = [1050,1054,1055,1051] if home['SideId']==0 else [1051,1055,1054,1050]
        r = route(home, ids, enemy['Position'])
        r['oldHoldToStation'] = r['flightLegs'][-1]+750
        r['stationToOldHold'] = sum(r['flightLegs'][:-1])-750
        r['approximateTo3500Standoff'] = sum(r['flightLegs'][:-1])+max(0,r['flightLegs'][-1]-3500)
        siege.append(dict(side=home['SideId'], **r))
    return dict(file=path.name, sha256=hashlib.sha256(raw).hexdigest(), sectors=list(sectors.values()),
                clash=clash, siege=siege,
                limits='Straight distances; no turning, obstacles, warp exit clearance or combat. Alderan origin is a reference point, not a measured battle centre. All two-hop home-to-Alderan routes shown; bots choose using their own visible graph.')


def position_summary(paths):
    """Aggregate the reconciled P826 event cubes without pooling medians."""
    results=[]
    for path in paths:
        data=json.loads(path.read_text())
        for m in data['matches']:
            phases={}
            for phase in ('pre','finale'):
                groups = {k:collections.defaultdict(lambda:[0,0]) for k in ('killsBySector','deathsBySector','killsByVictimKind','killsByVictimHull')}
                for s,p,sector,hull,kind,credit,death,count in m['events']:
                    if p!=phase: continue
                    if credit:
                        groups['killsBySector'][sector][1-s]+=count
                        groups['killsByVictimKind'][kind][1-s]+=count
                        groups['killsByVictimHull'][hull][1-s]+=count
                    if death: groups['deathsBySector'][sector][s]+=count
                phases[phase]=dict(groups)
                phases[phase]['kills']=[sum(v[s] for v in groups['killsBySector'].values()) for s in (0,1)]
                phases[phase]['deaths']=[sum(v[s] for v in groups['deathsBySector'].values()) for s in (0,1)]
            bomber_entries = [sum(v for k,v in m['entries'][s].items()
                                 if k.startswith('finale:') and k.endswith(f'->{1051-s}:bomber')) for s in (0,1)]
            results.append(dict(batch=data['source'], **{k:m.get(k) for k in ('match','status','countdownUtc','startedUtc','endUtc','lastUtc','finale','deadline','reconciled','economy','miners','initialHulls','skills','trips','launchBounds','stationKills','firstUndocks','firstClashEntries')}, phases=phases, finaleBomberTargetEntries=bomber_entries))
    return dict(matches=results, limits='Side pairs always 0/1; kills attributed to opposite victim side only on true KILL_SHIP kill flag, not pod deaths. Per-match medians retained, never pooled from medians. Shutdown and censored rounds excluded from winner totals.')


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
    parser.add_argument("receipts", type=Path, nargs="*")
    parser.add_argument("--position", action="store_true", help="Summarize --per-match audit cubes")
    parser.add_argument("--map", type=Path, help="Extract P826 map geometry only")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.map:
        result=position_geometry(args.map)
    elif args.position:
        result=position_summary(args.receipts)
    else:
        if not args.receipts: parser.error('receipts required')
        result=[summarize(p) for p in args.receipts]
    args.output.write_text(json.dumps(result, indent=2) + "\n")
