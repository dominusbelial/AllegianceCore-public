#!/usr/bin/env python3
"""Aggregate a P558 arena run's responses.json (perf) + stderr.log (DEATH)
into per-side numbers: shots, gunfire kills, collision deaths, TTK, hits."""
import json, sys, re, os

label = sys.argv[1]
outdir = "/tmp/p558"
resp = json.load(open(os.path.join(outdir, f"{label}_responses.json")))
perf = resp.get("perf")
txt = ""
if perf and "result" in perf:
    res = perf["result"]
    content = res.get("content") if isinstance(res, dict) else None
    if content:
        txt = content[0].get("text", "") if isinstance(content, list) else ""
try:
    data = json.loads(txt)
except Exception:
    data = {}

shots = {0:0, 1:0}; kills = {0:0, 1:0}; hits = {0:0, 1:0}
hitrates = {0:[], 1:[]}
combat = (data.get("combat") or {})
per_bot = combat.get("perBot") or []
for b in per_bot:
    side = b.get("side"); s=b.get("shotsFired",0); k=b.get("kills",0); h=b.get("shipHits",0)
    shots.setdefault(side,0); shots[side]+=s
    kills.setdefault(side,0); kills[side]+=k
    hits.setdefault(side,0); hits[side]+=h
    hitrates.setdefault(side,[]).append(b.get("hitRate"))
ttk = combat.get("timeToKill")

stderr = open(os.path.join(outdir, f"{label}_stderr.log")).read()
deaths = {0:{"gunfire":0,"collision":0}, 1:{"gunfire":0,"collision":0}}
bt = {}
for m in re.finditer(r"\[p539d\] DEATH .*? ship (\d+) side (\d+) cause=\w+ .*? gunfire=(\d+) collision=(\d+)", stderr):
    bt[int(m.group(1))] = (int(m.group(2)), int(m.group(3)), int(m.group(4)))
for sid,(side,g,c) in bt.items():
    deaths.setdefault(side, {"gunfire":0,"collision":0})
    deaths[side]["gunfire"] += g; deaths[side]["collision"] += c

print(json.dumps({
  "label": label,
  "shots": shots, "kills": kills, "hits": hits,
  "avg_hitrate": {k: (round(sum(v)/len(v),3) if v else None) for k,v in hitrates.items()},
  "deaths": deaths,
  "ttk": ttk,
}, indent=2))
