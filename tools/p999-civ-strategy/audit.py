#!/usr/bin/env python3
"""Audit saved P999 evidence offline. Inputs stay read-only; exports contain no paths.

usage: audit.py LOG_DIRECTORY CORE_JSON OUTPUT_DIRECTORY
Only finalized [gameover] matches enter win-rate/normalization denominators.
Worker t= values are process-clock bounds, NOT server simulation timestamps.
"""
import argparse
import bisect
from collections import Counter, defaultdict
import csv
import hashlib
import json
from pathlib import Path
import re

NAMES = {18: "Gigacorp", 4: "Phoenix", 5: "Omicron", 25: "Iron Coalition"}
BOT = r"mcp-w\d+-b\d+-\d+"


def wilson(wins, count):
    if not count:
        return None
    z = 1.959963984540054
    p = wins / count
    mid = (p + z*z/(2*count)) / (1+z*z/count)
    half = z * ((p*(1-p)/count+z*z/(4*count*count)) ** .5) / (1+z*z/count)
    return [mid-half, mid+half]


def parse_summary(lines, batch):
    rosters, matches = {}, {}
    active = None
    final = {}
    skills, causes = {}, {}
    for line in lines:
        if line.startswith("[matchup]"):
            active = int(re.search(r"match=(\d+)", line)[1])
            sides = {int(s): int(c) for s, c in re.findall(r"side(\d+)=(\d+):", line)}
            rosters[active] = dict(sides=sides, seed=int(re.search(r"seed=(\d+)", line)[1]),
                homes={int(s): int(h) for s, h in re.findall(r"home(\d+)=(\d+)", line)},
                map=(re.search(r" map=(\S+)", line)[1] if " map=" in line else None))
        if line.startswith("[finale] deadline winner"):
            final[active] = dict(criterion=re.search(r" by=(\S+)", line)[1],
                scores={int(s): dict(kills=int(k), deaths=int(d), stations=int(st) if st else None)
                    for s, st, k, d in re.findall(r"(\d+):(?:stations=(\d+)/)?kills=(\d+)/deaths=(\d+)", line)})
        if line.startswith("[finale] cap winner"):
            final.setdefault(active, {})["duration"] = float(re.search(r"elapsed=([\d.]+)", line)[1])
        if line.startswith("[gameover]"):
            match = int(re.search(r"match=(\d+)", line)[1])
            if match in matches:
                raise ValueError("duplicate finalized match")
            matches[match] = dict(batch=batch, match=match, winner=int(re.search(r"iSideWinner=(-?\d+)", line)[1]),
                peers=int(re.search(r"(\d+) peer\(s\)", line)[1]), **rosters[match], **final.get(match, {}))
        m = re.match(r"match (\d+) side (\d+) station-kills (\d+) research (\d+) ship-kills (\d+)", line)
        if m:
            match, side, stations, research, kills = map(int, m.groups())
            skills[match, side] = dict(station_kills=stations, research=research, skill_ship_kills=kills)
        m = re.match(r"\[kills\] match (\d+) side (\d+) .*: podded (.*); killed (.*)", line)
        if m:
            match, side, pod, killed = m.groups()
            pairs = lambda s: {k: int(v) for k, v in re.findall(r"(\w+) (\d+)", s)}
            causes[int(match), int(side)] = dict(podded=pairs(pod), killed=pairs(killed))
    rows = []
    for match, m in sorted(matches.items()):
        for side, civ in sorted(m["sides"].items()):
            score = m.get("scores", {}).get(side, {})
            cause = causes.get((match, side), {})
            rows.append(dict(batch=batch, match=match, side=side, civ=civ, name=NAMES.get(civ, str(civ)),
                sides=len(m["sides"]), win=int(side == m["winner"]), winner_civ=m["sides"].get(m["winner"]),
                opponents="/".join(map(str, sorted(c for s, c in m["sides"].items() if s != side))),
                home_sector=m["homes"].get(side), seed=m["seed"], map=m["map"], peers=m["peers"],
                criterion=m.get("criterion"), duration=m.get("duration"), kills=score.get("kills"), deaths=score.get("deaths"),
                **skills.get((match, side), {}), podded=sum(cause.get("podded", {}).values()),
                oxygen=cause.get("killed", {}).get("oxygen", 0),
                **{f"podded_{k}": v for k, v in cause.get("podded", {}).items()},
                **{f"killed_{k}": v for k, v in cause.get("killed", {}).items()}))
    return rows, rosters


def worker_evidence(lines, batch, roster, core):
    # Explicit side-bearing lines establish stable bot -> side membership, with
    # starting core hull identity as a cross-check/fallback. Names are not exported.
    membership = defaultdict(set)
    hull_civs = defaultdict(set)
    for c in core["civs"]:
        for u in c["initial"]:
            if u["kind"] == "hull":
                hull_civs[u["id"]].add(c["civ"]["civilizationId"])
    for line in lines:
        names = re.findall(BOT, line)
        s = re.search(r"\bside[ =]+(\d+)", line)
        if names and s and line.startswith(("[opening]", "[scout]", "[cloak-policy]", "[p539d]", "[finale]", "[support")):
            for name in names:
                membership[name].add(int(s[1]))
        if line.startswith("[loadout]") and " initial:" in line and names:
            h = re.search(r"(?:human hull |hull=)(\d+)", line)
            if h:
                civs = hull_civs[int(h[1])]
                sides = [s for s, c in roster[1]["sides"].items() if c in civs]
                if len(sides) == 1:
                    membership[names[0]].add(sides[0])
    conflicts = sum(len(v) > 1 for v in membership.values())
    side_of = {k: next(iter(v)) for k, v in membership.items() if len(v) == 1}
    generation, t = 1, None
    indexed, anchors = [], defaultdict(list)
    for i, line in enumerate(lines):
        g = re.search(r"\[battle-sector\].*generation=(\d+)", line)
        if g:
            generation = int(g[1])
        tm = re.search(r"\bt=([\d.]+)s?\b", line) if line.startswith(("[opening]", "[intel]", "[scout]", "[gallery]")) else None
        if tm:
            t = float(tm[1]); anchors[generation].append((i, t))
        indexed.append((i, generation, line))

    def bounds(i, gen):
        a = anchors[gen]
        j = bisect.bisect_left(a, (i, -1))
        lo = a[j-1][1] if j else None
        hi = a[j][1] if j < len(a) else None
        if lo is None or hi is None or hi < lo or hi-lo > 60:
            return None, None
        return lo, hi

    initial_bots, hulls, research, raw_research = defaultdict(set), Counter(), [], []
    observations = Counter()
    roles, loadouts, unmapped, receipts = Counter(), Counter(), 0, 0
    for i, gen, line in indexed:
        if gen not in roster:
            continue
        lo, hi = bounds(i, gen)
        m = re.match(r"\[loadout\] (" + BOT + r")(.*)", line)
        if m:
            bot, tail = m.groups()
            h = re.search(r"(?:human hull |hull=)(\d+) '([^']*)'", tail)
            if not h:
                continue
            receipts += 1
            side = side_of.get(bot)
            if side is None:
                unmapped += 1
                continue
            civ = roster[gen]["sides"].get(side)
            if civ is None:
                continue
            phase = "initial" if " initial:" in tail else "refit"
            if phase == "initial":
                initial_bots[side].add(bot)
            hull = int(h[1])
            # Worker-clock window measured from the generation's first timed
            # diagnostic, not a claimed server start. Preserve unknown/boundary
            # cases instead of assigning an interpolated combat timestamp.
            window = "opening" if phase == "initial" else "unknown"
            if phase != "initial" and lo is not None:
                start = anchors[gen][0][1]
                lower, upper = int(max(0,lo-start)//300), int(max(0,hi-start)//300)
                window = f"{lower*5}-{(lower+1)*5}m" if lower==upper else "boundary"
            hulls[gen, side, civ, hull, h[2], phase, window] += 1
            role = re.search(r"\brole=(\w+)", tail)
            roles[gen, side, civ, role[1] if role else "unknown", phase] += 1
            for eq, pid, pname, qty in re.findall(r"(\w+)@-?\d+=(\d+) '([^']*)' x(\d+)", tail):
                loadouts[gen, side, civ, eq, int(pid), pname, phase] += 1
        m = re.search(r"\[p682\] bot '(" + BOT + r")'.*bought and launched next-match hull (\d+)", line)
        if m and m[1] in side_of:
            side = side_of[m[1]]
            observations[gen, side, roster[gen]["sides"].get(side), int(m[2]), "rotation_launch_ack"] += 1
        m = re.search(r"\[cloak-policy\] .*?side=(\d+) hull=(\d+)", line)
        if m:
            side, hull = map(int, m.groups())
            observations[gen, side, roster[gen]["sides"].get(side), hull, "cloak_policy_hull"] += 1
        m = re.match(r"\[research\] side=(\d+) plan=(\S+) step=(\d+)/(\d+) (\w+) '([^']*)'.*wallet=(\d+).*techs=\[(.*?)\] stations=\[(.*?)\] status=(.*)", line)
        if m:
            side, plan, step, total, action, name, wallet, techs, stations, status = m.groups()
            side = int(side)
            civ = roster[gen]["sides"].get(side)
            if civ is None:
                continue
            # Exclude stale commander snapshots at a rotation boundary when the
            # old civ's generated total differs from the new one.
            c = next(c for c in core["civs"] if c["civ"]["civilizationId"] == civ)
            if plan == "generated" and int(total) != len(c["baseline"]["steps"]):
                continue
            raw_research.append(dict(batch=batch, match=gen, side=side, civ=civ, step=int(step), total=int(total),
                action=action, name=name, wallet=int(wallet), techs=techs.split(",") if techs else [],
                stations=stations.split(",") if stations else [], status=status, lo=lo, hi=hi))
    seen_tech, seen_stations, seen_steps = defaultdict(set), defaultdict(set), defaultdict(set)
    for r in raw_research:
        key = r["match"], r["side"]
        if r["status"] == "waiting for mission start":
            continue
        additions = [("tech", n) for n in r["techs"] if n not in seen_tech[key]]
        additions += [("station", n) for n in r["stations"] if n not in seen_stations[key]]
        if (r["step"], r["name"]) not in seen_steps[key]:
            additions.append(("step", r["name"]))
        for kind, name in additions:
            research.append({k: r[k] for k in ("batch", "match", "side", "civ", "step", "total", "lo", "hi")} | dict(kind=kind, name=name))
        seen_tech[key].update(r["techs"]); seen_stations[key].update(r["stations"])
        seen_steps[key].add((r["step"], r["name"]))
    return dict(initial_bots={s: len(b) for s, b in initial_bots.items()}, hulls=hulls, roles=roles,
        loadouts=loadouts, research=research, raw_research=raw_research,
        observations=observations, conflicts=conflicts, unmapped=unmapped, receipts=receipts)


def export_csv(path, rows):
    fields = list(dict.fromkeys(k for r in rows for k in r))
    with path.open("w", newline="") as out:
        writer = csv.DictWriter(out, fields)
        writer.writeheader(); writer.writerows(rows)


def aggregate(rows, keys):
    groups = defaultdict(list)
    for r in rows:
        groups[tuple(r[k] for k in keys)].append(r)
    result = []
    for key, rs in sorted(groups.items()):
        n, w = len(rs), sum(r["win"] for r in rs)
        complete = [r for r in rs if r.get("kills") is not None and r.get("deaths") is not None]
        k, d = sum(r["kills"] for r in complete), sum(r["deaths"] for r in complete)
        slots = sum(r.get("bot_slots", 0) for r in rs)
        scored_slots = sum(r.get("bot_slots", 0) for r in complete)
        result.append(dict(zip(keys, key)) | dict(n=n, wins=w, win_rate=w/n, interval=wilson(w,n),
            score_n=len(complete), kills=k, deaths=d, kd=k/d if d else None,
            research=sum(r.get("research", 0) for r in rs)/n,
            station_kills=sum(r.get("station_kills", 0) for r in rs)/n,
            oxygen=sum(r.get("oxygen", 0) for r in rs)/n,
            bot_slots=slots, scored_bot_slots=scored_slots,
            kills_per_bot=k/scored_slots if scored_slots else None,
            deaths_per_bot=d/scored_slots if scored_slots else None,
            research_per_bot=sum(r.get("research",0) for r in rs)/slots if slots else None,
            oxygen_per_bot=sum(r.get("oxygen",0) for r in rs)/slots if slots else None,
            stations_per_bot=sum(r.get("station_kills",0) for r in rs)/slots if slots else None,
            podded=sum(r.get("podded", 0) for r in rs)/n))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path); parser.add_argument("core", type=Path); parser.add_argument("output", type=Path)
    args = parser.parse_args()
    core = json.loads(args.core.read_text())
    args.output.mkdir(parents=True, exist_ok=True)
    rows, inventory, research, hulls, loadouts, roles, diagnostics, raw, observations = [], [], [], [], [], [], [], [], []
    missing = []
    for batch in range(76,113):
        summary = args.logs / f"b{batch}-server-summary.txt"
        if not summary.exists():
            missing.append(batch); continue
        data = summary.read_bytes()
        inventory.append(dict(batch=batch, source=summary.name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
        batch_rows, roster = parse_summary(data.decode(errors="replace").splitlines(), batch)
        bot_counts = Counter()
        for worker in sorted((args.logs / f"b{batch}-bots").glob("err.worker-*.log")):
            data = worker.read_bytes()
            inventory.append(dict(batch=batch, source=worker.name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
            e = worker_evidence(data.decode(errors="replace").splitlines(), batch, roster, core)
            bot_counts.update(e["initial_bots"])
            research += e["research"]; raw += e["raw_research"]
            for key, count in e["hulls"].items():
                match, side, civ, hull, name, phase, window = key
                hulls.append(dict(batch=batch, match=match, side=side, civ=civ, hull=hull, name=name, phase=phase, window=window, selections=count))
            for key, count in e["observations"].items():
                match, side, civ, hull, source = key
                observations.append(dict(batch=batch,match=match,side=side,civ=civ,hull=hull,source=source,receipts=count))
            for key, count in e["loadouts"].items():
                match, side, civ, equipment, part, name, phase = key
                loadouts.append(dict(batch=batch, match=match, side=side, civ=civ, equipment=equipment, part=part, name=name, phase=phase, selections=count))
            for key, count in e["roles"].items():
                match, side, civ, role, phase = key
                roles.append(dict(batch=batch, match=match, side=side, civ=civ, role=role, phase=phase, selections=count))
            diagnostics.append(dict(batch=batch, worker=worker.name, conflicts=e["conflicts"], unmapped=e["unmapped"], receipts=e["receipts"]))
        for r in batch_rows:
            r["bot_slots"] = bot_counts[r["side"]]
        rows += batch_rows
    completed = {(r["batch"],r["match"]) for r in rows}
    for name, data in [("matches",rows),("hulls",hulls),("loadouts",loadouts),("roles",roles),("research",research),("research-snapshots",raw),("observations",observations)]:
        filtered = [r for r in data if (r["batch"],r["match"]) in completed]
        if name == "research-snapshots":
            for r in filtered:
                r["techs"] = ";".join(r["techs"]); r["stations"] = ";".join(r["stations"])
        export_csv(args.output / f"{name}.csv", filtered)
    result = dict(missing=missing, completed_matches=len(completed), civilization_opportunities=len(rows),
        by_civ=aggregate(rows,["civ"]), by_size=aggregate(rows,["sides","civ"]),
        by_slot=aggregate(rows,["sides","civ","side"]), by_matchup=aggregate(rows,["civ","opponents"]),
        recent=aggregate([r for r in rows if r["batch"]>=103],["sides","civ"]),
        inventory=inventory, worker_diagnostics=diagnostics)
    (args.output/"audit.json").write_text(json.dumps(result,indent=2)+"\n")
    print(json.dumps({k:result[k] for k in ("missing","completed_matches","by_civ","by_size","recent")},indent=2))


if __name__ == "__main__":
    main()
