#!/usr/bin/env python3
"""Render sanitized P999 appendices from core.json and the local offline audit."""
import argparse
import base64
from collections import defaultdict
import csv
import json
from pathlib import Path
import random
import statistics

GA = ["speed","thrust","turn rate","turn torque","station armor","station armor regen",
      "station shield","station shield regen","ship armor","ship shield","ship shield regen",
      "scan","signature resistance","energy","ammo speed","energy lifespan","missile turn",
      "mining rate","mining yield","mining capacity","ripcord speed","gun damage","missile damage",
      "development cost","development time"]


def table(head, rows):
    def cell(v):
        if v is None: return "unknown"
        if isinstance(v,float): return f"{v:.3f}".rstrip("0").rstrip(".")
        return str(v).replace("|","/").replace("\n"," ")
    return "\n".join(["| " + " | ".join(map(cell,head)) + " |", "|"+"---|"*len(head)]
        + ["| " + " | ".join(map(cell,row)) + " |" for row in rows])+"\n"


def bits(mask):
    return ",".join(str(i) for i in range(400) if base64.b64decode(mask)[i//8] & (128 >> (i%8)))


def fit(h,p,mount=0):
    masks = [x["partMask"] for x in h["hardpoints"]] if p["equipment"]=="Weapon" else [h["pmEquipment"][["ChaffLauncher","Weapon","Magazine","Dispenser","Shield","Cloak","Pack","Afterburner"].index(p["equipment"])]]
    return mount < len(masks) and p["partMask"] & ~masks[mount] == 0


def human(h):
    return h["groupId"]>=0 and not (h["habmCapabilities"] & (4|0x4000|0x8000))


def timeline(civ,plan):
    nodes={(n["kind"],n["id"]):n for n in civ["nodes"]}
    seconds=price=0
    result={}
    for i,s in enumerate(plan["steps"],1):
        n=nodes[s["action"],s["id"]];seconds+=n["seconds"];price+=n["price"]
        result[s["action"],s["id"]]=(i,seconds,price,s["name"])
    return result


def core_specs(c):
    hulls={h["hullId"]:h for h in c["hulls"]}; parts={p["partId"]:p for p in c["parts"]}
    stations={s["stationTypeId"]:s for s in c["stations"]}
    projectiles={p["projectileTypeId"]:p for p in c["projectiles"]}
    missile={p["expendableTypeId"]:p for p in c["missiles"]}
    out=["# P999 — reproducible PCore001 specification census", "",
        f"Core version {c['version']}; SHA-256 `{c['sha256']}`. All {len(c['civs'])} loaded civilizations are below the lobby's special-civ cutoff; none is omitted.", "",
        "Raw hull values are separated from civilization GA multipliers. Shield is a mounted part, not a hull field. Initial shield capacity below is the largest compatible initially buyable shield multiplied by the civ GA; mounted masses and research are excluded. All human hulls have the engine's five cargo mounts (-5 through -1); magazine/dispenser/chaff capacities are distinct size budgets, not cargo slots. Equipment masks are listed in engine order (chaff, gun, magazine, dispenser, shield, cloak, pack, afterburner).", "",
        "Times are serial bucket seconds at researchTimeScale=1 with unlimited funding and legal rocks; station travel/building effects, losses, and concurrent funding are excluded. These are not measured match times. Base tech bit numbers use the engine's MSB-first bit convention."]
    for x in c["civs"]:
        cv=x["civ"];cid=cv["civilizationId"];ga=cv["gasBaseAttributes"];home=stations[cv["initialStationTypeId"]]
        initial_hulls=[hulls[u["id"]] for u in x["initial"] if u["kind"]=="hull"]
        final_hulls=[hulls[u["id"]] for u in x["final"] if u["kind"]=="hull" and human(hulls[u["id"]])]
        initial_parts=[parts[u["id"]] for u in x["initial"] if u["kind"]=="part"]
        out += ["",f"## {cid} — {cv['name']}", "",
            f"Starting tech bits: {bits(cv['ttbmBaseTechs'])}. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: {bits(cv['ttbmNoDevTechs'])}.","",
            f"Home {home['stationTypeId']} {home['name']}: armor {home['maxArmorHitPoints']}, shield {home['maxShieldHitPoints']}, income {home['income']}; grants [{bits(home['ttbmEffects'])}], local [{bits(home['ttbmLocal'])}]. Base payday fraction +{cv['incomeMoney']}; starting-money addition +{cv['bonusMoney']}; pod {cv['lifepod']}. Negative additions are retained.", "",
            "Nonidentity base GA multipliers:","",table(["Index","Attribute","Factor"],[(i,GA[i],v) for i,v in enumerate(ga) if v!=1]),
            "Starting hulls (including autonomous types):", "", table(["ID","Hull","Human","HP raw/effective","Shield max initial","Speed raw/effective","Thrust","Mass","Signature","Guns fixed/total","Ammo","Magazine/dispenser/chaff"],
                [(h["hullId"],h["name"],human(h),f"{h['hitPoints']:g}/{h['hitPoints']*ga[8]:g}",
                  max([p["stats"]["maxStrength"]*ga[9] for p in initial_parts if p["equipment"]=="Shield" and fit(h,p)] or [0]),
                  f"{h['speed']:g}/{h['speed']*ga[0]:g}",h["thrust"],h["mass"],h["signature"],f"{h['maxFixedWeapons']}/{h['maxWeapons']}",h["maxAmmo"],
                  f"{h['capacityMagazine']}/{h['capacityDispenser']}/{h['capacityChaffLauncher']}") for h in initial_hulls]),
            "Starting human hardpoints/equipment and purchasable weapons:","",table(["Hull","Hardpoints (mask; fixed)","Equipment masks","Initial fitting guns","Initial fitting afterburners"],
                [(h["hullId"],"; ".join(f"{p['partMask']};{p['fixed']}" for p in h["hardpoints"]), ",".join(map(str,h["pmEquipment"])),
                  "; ".join(f"{p['partId']} {p['name']}" for p in initial_parts if p["equipment"]=="Weapon" and any(fit(h,p,i) for i in range(len(h["hardpoints"])))),
                  "; ".join(f"{p['partId']} {p['name']}" for p in initial_parts if p["equipment"]=="Afterburner" and fit(h,p))) for h in initial_hulls if human(h)]),
            "Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):","",table(["ID","Hull","HP","Speed","Thrust/mass","Signature","Fixed/total","Magazine/cargo slots","Equipment masks"],
                [(h["hullId"],h["name"],h["hitPoints"],h["speed"],f"{h['thrust']:g}/{h['mass']:g}",h["signature"],f"{h['maxFixedWeapons']}/{h['maxWeapons']}",f"{h['capacityMagazine']}/5",",".join(map(str,h["pmEquipment"]))) for h in final_hulls]),
            "Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:","",table(["Part","Weapon","Initially buyable","Raw DPS / missile cycle","Damage type","Energy/ammo per shot"],
                [(p["partId"],p["name"],p in initial_parts,
                  projectiles[p["stats"]["projectileTypeId"]]["power"]/max(.01,p["stats"]["dtimeBurst"]) if p["equipment"]=="Weapon" else missile[p["stats"]["expendableTypeId"]]["readyTime"]+missile[p["stats"]["expendableTypeId"]]["lockTime"],
                  projectiles[p["stats"]["projectileTypeId"]]["damageType"] if p["equipment"]=="Weapon" else missile[p["stats"]["expendableTypeId"]]["damageType"],
                  f"{p['stats']['energyPerShot']:g}/{p['stats']['cAmmoPerShot']}" if p["equipment"]=="Weapon" else "magazine")
                 for p in [parts[u["id"]] for u in x["final"] if u["kind"]=="part"] if p["equipment"]=="Weapon" or "expendableTypeId" in p["stats"] and p["stats"]["expendableTypeId"] in missile]),
            "Station branches and baseline serial position (constructor bucket only, excluding travel):",""]
        times=timeline(x,x["baseline"])
        out.append(table(["Station","Required/effect/local bits","Direct cost/seconds","Baseline step/seconds/cost"],
            [(s["id"],s["name"]+f" [{bits(n['required'])}]/[{bits(n['effects'])}]/[{bits(n['local'])}]",f"{n['price']}/{n['seconds']}","/".join(map(str,times[s['action'],s['id']][:3])))
             for s in x["baseline"]["steps"] if s["action"]=="station" for n in [next(n for n in x["nodes"] if n["kind"]=="station" and n["id"]==s["id"])]]))
        out += ["", "Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):", "", table(["Development","Cost","Seconds","Factors"],
            [(n["name"],n["price"],n["seconds"],"; ".join(f"{GA[i]} x{v:g}" for i,v in enumerate(n["attributes"]) if v!=1))
             for n in x["nodes"] if n["kind"]=="develop" and (n["kind"],n["id"]) in times and any(v!=1 for v in n["attributes"])])]
    return "\n".join(out)+"\n"


def plan_comparison(c):
    out=["# P999 — offline plans versus today's actual generated schedule", "",
        f"Seed {c['seed']}; opponents {c.get('opponentCivs') or 'all other core civs'}. Baseline is Generate(prioritizeResearch: true), the P942 MCP execution order; the serialized server fallback still uses canonical Generate(). No live behavior is changed.", "",
        "Times below are cumulative serial bucket seconds at scale 1, prices are cumulative bucket funding. Unlimited funding/rocks, no travel/building effect, no rebuy/losses. A station bucket's completion does not imply its station is already built in a match. Candidate slices are exact-replayed; conservative unsliced prefixes are labeled in the tool JSON. Offline scores are dimensionless hypotheses, not expected win rates."]
    for cid in [18,4,5]:
        x=next((x for x in c["civs"] if x["civ"]["civilizationId"]==cid),None)
        if x is None:continue
        plans=[x["baseline"]]+[s["plan"] for s in x["strategies"]]
        out += ["",f"## {cid} — {x['civ']['name']}", "", "First 40 steps:","",table(["Step","Today research-first","Skirmish","Siege","Economy"],
            [(i+1,*[f"{p['steps'][i]['action']} {p['steps'][i]['id']} {p['steps'][i]['name']}" if i<len(p["steps"]) else "—" for p in plans]) for i in range(40)]), "",
            "Opening opportunity ranking (top six per profile):", "",table(["Profile","Target","Score","Route steps","Cost","Seconds","Exact-valid slice"],
                [(s["profile"],o["name"],o["score"],o["steps"],o["price"],o["seconds"],o["sliced"]) for s in x["strategies"] for o in s["opportunities"][:6]]), ""]
        timelines=[timeline(x,p) for p in plans]
        keys=[(s["action"],s["id"]) for s in x["baseline"]["steps"] if s["action"]=="station" or any(s["name"]==n for n in ["Patroller","Bomber","Hvy Bomber","Enh Fighter","Adv Fighter","Interceptor","Stealth Fighter","Upgrade Medium Class","Upgrade Supremacy","PW Gattling Gun 2","EW Disruptor 2","GA: Ship Hull 1","GA: PW&EW Damage 1"])]
        out += ["Key completions: step / serial seconds / cumulative cost:","",table(["Key","Today","Skirmish","Siege","Economy"],
            [(timelines[0][k][3],*[" / ".join(map(str,t[k][:3])) if k in t else "not scheduled" for t in timelines]) for k in keys]), ""]
        # Newly buyable hull/part timestamps, preserving starting items at t=0.
        unlocks=[]
        for label,p,t in zip(["today","skirmish","siege","economy"],plans,timelines):
            for u in x["initial"]:unlocks.append((label,u["kind"],u["id"],u["name"],0,0,0))
            for step in p["steps"]:
                i,seconds,price,_=t[step["action"],step["id"]]
                for u in step["expectedUnlocks"]:unlocks.append((label,u["kind"],u["id"],u["name"],i,seconds,price))
        out += ["All hull unlocks (including autonomous hulls), for reproducible timing:","",table(["Plan","Kind","ID","Name","Step","Seconds","Funding"],[r for r in unlocks if r[1]=="hull"])]
    return "\n".join(out)+"\n"


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("core",type=Path);parser.add_argument("audit",type=Path);parser.add_argument("output",type=Path)
    args=parser.parse_args();c=json.loads(args.core.read_text());a=json.loads((args.audit/"audit.json").read_text())
    args.output.mkdir(parents=True,exist_ok=True)
    (args.output/"core-specs.md").write_text(core_specs(c))
    (args.output/"plan-comparison.md").write_text(plan_comparison(c))
    for name in ["matches","research"]:
        # Only key first-observation events are published; raw snapshots remain scratch.
        rows=list(csv.DictReader((args.audit/f"{name}.csv").open()))
        if name=="research":
            rows=[r for r in rows if r["kind"]=="station" or r["kind"]=="tech" and r["name"] in ["Bomber","Patroller","Enh Fighter","Adv Fighter","Upgrade Medium Class","Upgrade Supremacy","GA: Ship Hull 1","GA: PW&EW Damage 1","PW Gattling Gun 2","EW Disruptor 2"]]
        write_csv(args.output/f"{name}.csv",rows)
    write_csv(args.output/"inventory.csv",a["inventory"])
    for name,group in [("hulls",["civ","hull","name","phase","window"]),("loadouts",["civ","equipment","part","name","phase"]),("roles",["civ","role","phase"])]:
        counts=defaultdict(int)
        for r in csv.DictReader((args.audit/f"{name}.csv").open()):counts[tuple(r[k] for k in group)]+=int(r["selections"])
        write_csv(args.output/f"{name}.csv",[dict(zip(group,k))|dict(selections=n) for k,n in sorted(counts.items())])
    write_csv(args.output/"observations.csv",list(csv.DictReader((args.audit/"observations.csv").open())))
    matches=list(csv.DictReader((args.audit/"matches.csv").open()))
    from audit import wilson
    populations=defaultdict(int)
    for row in matches:populations[row["batch"],row["match"]]+=int(row["bot_slots"])
    cohorts=defaultdict(list)
    for row in matches:cohorts[int(row["sides"]),populations[row["batch"],row["match"]],int(row["civ"])].append(row)
    cohort_rows=[]
    for (sides,population,civ),rs in sorted(cohorts.items()):
        wins=sum(int(r["win"]) for r in rs)
        scored=[r for r in rs if r["kills"] and r["deaths"]]
        kills=sum(int(r["kills"]) for r in scored);deaths=sum(int(r["deaths"]) for r in scored)
        slots=sum(int(r["bot_slots"]) for r in rs);scored_slots=sum(int(r["bot_slots"]) for r in scored)
        low,high=wilson(wins,len(rs))
        cohort_rows.append(dict(sides=sides,bots=population,civ=civ,matches=len(rs),wins=wins,
            wilson_low=low,wilson_high=high,scored_matches=len(scored),kd=kills/deaths if deaths else None,
            kills_per_bot=kills/scored_slots if scored_slots else None,
            research_per_match=sum(int(r["research"]) for r in rs)/len(rs),
            stations_per_match=sum(int(r["station_kills"]) for r in rs)/len(rs),
            oxygen_per_bot=sum(int(r["oxygen"]) for r in rs)/slots))
    write_csv(args.output/"population-cohorts.csv",cohort_rows)
    first={(r["batch"],r["match"],r["side"]) for r in matches if r["sides"]=="3" and r["match"]=="1"}
    snapshots=list(csv.DictReader((args.audit/"research-snapshots.csv").open()))
    events=list(csv.DictReader((args.audit/"research.csv").open()))
    summaries=[]
    for civ in ["18","4","5"]:
        for horizon in [60,120,180,240,300,600,900,1200,1800,2700]:
            latest={}
            for r in snapshots:
                key=r["batch"],r["match"],r["side"]
                if key in first and r["civ"]==civ and r["hi"] and float(r["hi"])<=horizon:latest[key]=r
            counts=[len(r["techs"].split(";")) if r["techs"] else 0 for r in latest.values()]
            wallets=[int(r["wallet"]) for r in latest.values()]
            summaries.append(dict(civ=civ,kind="snapshot",name=str(horizon),observed=len(latest),timed=len(latest),
                median=statistics.median(counts) if counts else None,wallet_median=statistics.median(wallets) if wallets else None))
        for name,kind in [("Patroller","tech"),("Bomber","tech"),("Supremacy","station"),("Shipyard","station"),
                          ("Upgrade Supremacy","tech"),("PW Gattling Gun 2","tech"),("GA: Ship Hull 1","tech"),("GA: PW&EW Damage 1","tech")]:
            rs=[r for r in events if (r["batch"],r["match"],r["side"]) in first and r["civ"]==civ and r["kind"]==kind and r["name"]==name]
            values=[(float(r["lo"])+float(r["hi"]))/2 for r in rs if r["lo"] and r["hi"]]
            summaries.append(dict(civ=civ,kind=kind,name=name,observed=len(rs),timed=len(values),
                median=statistics.median(values) if values else None))
    write_csv(args.output/"research-summary.csv",summaries)
    cluster=[]
    rng=random.Random(999)
    for civ in ["18","4","5"]:
        rows=[r for r in matches if r["civ"]==civ and r["sides"]=="3"]
        batches=sorted({r["batch"] for r in rows})
        by={b:[r for r in rows if r["batch"]==b] for b in batches}
        values=[]
        for _ in range(10000):
            sample=[r for _ in batches for r in by[rng.choice(batches)]]
            values.append(sum(int(r["win"]) for r in sample)/len(sample))
        values.sort()
        cluster.append(dict(civ=civ,batches=len(batches),replicates=10000,seed=999,
            low=values[249],high=values[9749]))
    write_csv(args.output/"cluster-intervals.csv",cluster)
    win_rows=[]
    for grouping in ["by_civ","by_size","by_slot","by_matchup","recent"]:
        for r in a[grouping]:
            out=dict(grouping=grouping,**{k:v for k,v in r.items() if k!="interval"})
            out["wilson_low"],out["wilson_high"]=r["interval"]
            win_rows.append(out)
    write_csv(args.output/"win-rates.csv",win_rows)
    print(f"Rendered core census, plan comparisons, and sanitized evidence for {a['completed_matches']} completed matches.")


def write_csv(path,rows):
    keys=list(dict.fromkeys(k for r in rows for k in r))
    with path.open("w",newline="") as f:
        writer=csv.DictWriter(f,keys);writer.writeheader();writer.writerows(rows)


if __name__=="__main__":main()
