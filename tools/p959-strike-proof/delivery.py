#!/usr/bin/env python3
"""Measure delivery from --delivery-proof logs and a --delivery-catalog JSON file."""

import argparse
import json
import re
from pathlib import Path


def fields(line):
    return dict(re.findall(r"\b([\w]+)=([^\s]+)", line))


def measure(path, catalog, review_observers=False):
    label = path.stem
    summary = json.loads(path.with_name(f"{label}-summary.json").read_text())
    observations = json.loads(path.with_name(f"{label}-observations.json").read_text())
    parts = {p["PartId"]: p for p in catalog["missiles"] + catalog["guns"]}
    # Native damage type 8 is anti-base in the proof core. Keep a separate
    # all-weapons counter for losses so a self-defense gun round is visible.
    anti_base = {("gun" if p["Kind"] == "gun" else "missile", p["Type"])
                 for p in parts.values() if p["DamageType"] == 8 and p["Power"] > 0}
    waves = []
    current = {}
    owners = {}
    lives = {}
    armed_lives = losses = before_any = before_strike = 0
    arrivals = {}
    first_arrival = first_damage = first_arrival_hit = None
    idle_events = []
    released_lives = set()
    match_hits = 0
    credited = winner = seed = deadline_at = uncredited = None
    pilot_base_kills = {}
    civs = homes = None
    sectors = json.loads(path.with_name(f"{label}-geometry.json").read_text())["Sectors"]
    deadline = over = False
    with path.open() as receipt:
        for line in receipt:
            # Damage callbacks are drained after TickSim, including on the
            # winning tick, when the host has already posted GAME_OVER.
            if over and not line.startswith("[p961-hit]"):
                continue
            if line.startswith("[strike-arrival]"):
                f = fields(line)
                key = int(f["side"]), int(f["station"])
                arrivals.setdefault(key, float(f["sim"]))
                if first_arrival is None:
                    first_arrival = (*key, float(f["sim"]))
            elif line.startswith("[strike-idle]"):
                ship = int(fields(line)["ship"])
                if ship in lives:
                    idle_events.append(dict(ship=ship, at=float(fields(line)["t"]), released=ship in released_lives))
            elif line.startswith("[strike]") and " finale commit " in line:
                f = fields(line)
                side = int(f["side"])
                time = float(f["sim"]) if "sim" in f else observations[len(waves)]["SimAt"]
                wave = dict(side=side, target=int(f["target"].split("#")[1]), required=int(f["required"]),
                            go=round(time + float(f["goIn"].rstrip("s")), 2), reason=f["reason"],
                            members=[int(i) for i in f["memberIds"].split(",")], owners=[],
                            accepted=0, on_target=0, shooters=set(), hits=0, hit_ships=set(),
                            first=None, kills=0, target_death_at=None)
                wave["commit"] = time
                wave["arrival"] = arrivals.pop((side, wave["target"]), None)
                # Another target can be selected before a qualifying commit.
                arrivals = {k: v for k, v in arrivals.items() if k[0] != side}
                current[side] = wave
                waves.append(wave)
                owners = {ship: data for ship, data in owners.items() if data[0]["side"] != side}
            elif line.startswith("[strike-owner]"):
                f = fields(line)
                ship = int(f["ship"])
                wave = next(w for w in current.values() if ship in w["members"])
                # Older baseline logs predate the explicit selected part field.
                mounted = [(int(p), int(mount), int(q))
                           for p, mount, q in (entry.split("/") for entry in f["parts"].split(","))]
                part_id = f.get("weapon", "")
                if part_id == "Weapon":
                    part_id = re.search(r" Part = (\d+)", line)[1]
                if not part_id:
                    candidates = [p for p, mount, qty in mounted if qty > 0 and mount == 0 and p in parts
                                  and parts[p]["DamageType"] == 8 and parts[p]["Kind"] != "gun"]
                    part_id = candidates[0] if len(candidates) == 1 else None
                part = parts.get(int(part_id)) if part_id is not None else None
                owner = dict(ship=ship, sim=float(f["sim"]) if "sim" in f else None, distance=float(f["range"]), target=f["target"], current=f.get("current"),
                             lock=float(f["lock"]), ready=f["ready"] == "True",
                             part=part["PartId"] if part else None,
                             quantity=next((q for p, mount, q in mounted if part and p == part["PartId"]), None),
                             accepted=0, on_target=0, first_shot=None)
                wave["owners"].append(owner)
                if part:
                    owners[ship] = (wave, ("gun" if part["Kind"] == "gun" else "missile", part["Type"]), owner)
            elif line.startswith("[strike]") and " finale release ship=" in line:
                f = fields(line)
                wave = current[int(f["side"])]
                released_lives.add(int(f["ship"]))
                for owner in wave["owners"]:
                    if owner["ship"] == int(f["ship"]):
                        owner["role"] = f["role"]
                        break
            elif line.startswith("[strike-life]"):
                ship = int(fields(line)["ship"])
                if ship not in lives:  # a retarget can name the same armed life again
                    lives[ship] = [False, False]
                    armed_lives += 1
            elif line.startswith("[strike-shot]"):
                f = fields(line)
                ship = int(f["ship"])
                shot = (f["kind"], int(f["type"]))
                if ship in lives:
                    lives[ship][0] = True
                    if shot in anti_base:
                        lives[ship][1] = True
                if ship in owners:
                    wave, weapon, owner = owners[ship]
                    if weapon == shot and wave["target_death_at"] is None:
                        wave["accepted"] += 1
                        wave["shooters"].add(ship)
                        on_target = f["target"] == f"Station/{wave['target']}"
                        wave["on_target"] += on_target
                        owner["accepted"] += 1
                        owner["on_target"] += on_target
                        if owner["first_shot"] is None:
                            owner["first_shot"] = float(f["t"])
            elif line.startswith("[eject] player ship ") or line.startswith("[p539d] DEATH bot "):
                ship = int(re.search(r"\bship (\d+)", line)[1])
                released_lives.discard(ship)
                owners.pop(ship, None)  # the next life cannot fire for this release
                if ship in lives:
                    any_shot, strike_shot = lives.pop(ship)
                    losses += 1
                    before_any += not any_shot
                    before_strike += not strike_shot
            elif line.startswith("[p961-hit]"):
                f = fields(line)
                side, ship = int(f["attackerSide"]), int(f["ship"])
                station = int(f["station"].split(":")[0])
                shields = [float(v) for v in f["shield"].split("->")]
                hull = [float(v) for v in f["hull"].split("->")]
                if shields[0] <= shields[1] and hull[0] <= hull[1] and f["killed"] != "True":
                    continue
                match_hits += 1
                at = float(f["t"])
                if first_damage is None:
                    first_damage = at
                if first_arrival and first_arrival_hit is None and (side, station) == first_arrival[:2] and at >= first_arrival[2]:
                    first_arrival_hit = at
                wave = current.get(side)
                if wave and station == wave["target"] and f["killed"] == "True":
                    wave["target_death_at"] = float(f["t"])
                if (wave is None or station != wave["target"]
                        or not any(owner["ship"] == ship for owner in wave["owners"])):
                    continue
                wave["hits"] += 1
                wave["hit_ships"].add(ship)
                if wave["first"] is None:
                    wave["first"] = round(float(f["t"]) - wave["go"], 2)
                if f["killed"] == "True":
                    wave["kills"] = 1  # simultaneous post-tick samples share the lethal pool
            elif line.startswith("[finale] research showcase") and "bounded finale armed" in line:
                deadline_at = float(fields(line)["deadline"].rstrip(";"))
            elif line.startswith("[finale] deadline winner"):
                deadline = True
            elif line.startswith("[finale] Station kills: "):
                credited = {s: int(n) for s, n in re.findall(r"(\w+)=(\d+)", line)}
            elif line.startswith("[finale] Enemy base shattered:"):
                uncredited = int(fields(line)["uncredited"])
            elif line.startswith("[scoring] ship ") and " final score " in line:
                side = int(re.search(r"\(side (-?\d+)\)", line)[1])
                if side >= 0:
                    pilot_base_kills[side] = pilot_base_kills.get(side, 0) + int(fields(line)["baseKills"])
            elif line.startswith("[gameover]") and "match=1 " in line:
                f = fields(line)
                winner, seed = int(f["iSideWinner"].rstrip(",")), int(f["seed"])
                civs = [int(n) for n in re.findall(r"side\d+=(\d+):", line)]
                homes = [int(n) for n in re.findall(r"home\d+=(\d+)", line)]
                over = True
    if not over or credited is None or summary["Stage"] != "Over":
        raise ValueError(f"{label} lacks a completed, scored first match")
    for wave, fixture in zip(waves, summary["Waves"], strict=True):
        if sorted(o["ship"] for o in wave["owners"]) != sorted(fixture["ReleasedShips"]):
            raise ValueError(f"{label} lacks release observations")
        for key in ("shooters", "hit_ships"):
            wave[key] = sorted(wave[key])
        wave["first_damage"] = round(wave["go"] + wave["first"], 2) if wave["first"] is not None else None
        wave["arrival_to_damage"] = (round(wave["first_damage"] - wave["arrival"], 2)
                                     if wave["first_damage"] is not None and wave["arrival"] is not None else None)
    # Old delivery-proof logs cannot distinguish zero idle events from an absent observer.
    observed_idle = review_observers or first_arrival is not None or bool(idle_events)
    return dict(label=label, assembly=summary["BotAssemblyHash"], seed=seed, civs=civs, homes=homes,
                sectors=len(sectors), tick_ms=summary["TickMilliseconds"], deadline_at=deadline_at, credited=credited,
                pilot_base_kills=pilot_base_kills, uncredited=uncredited, match_hits=match_hits, winner=winner, pre_deadline=winner >= 0 and not deadline, waves=waves,
                first_arrival=first_arrival, first_damage=first_damage, first_arrival_target_damage=first_arrival_hit,
                idle_pilots=sorted({e["ship"] for e in idle_events}) if observed_idle else None,
                idle_events=idle_events if observed_idle else None,
                armed_lives=armed_lives, armed_losses=losses, losses_before_any=before_any,
                losses_before_anti_base=before_strike)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--review-observers", action="store_true",
                        help="logs were captured with the P1105b arrival and idle observers enabled")
    parser.add_argument("catalog", type=Path)
    parser.add_argument("logs", nargs="+", type=Path)
    args = parser.parse_args()
    catalog = json.loads(args.catalog.read_text())
    print(json.dumps([measure(path, catalog, args.review_observers) for path in args.logs], indent=2))
