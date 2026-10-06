#!/usr/bin/env python3
"""P558 arena run driver: speak MCP stdio (NDJSON) to the standalone
allegiance-mcp attached to the live networked server, spawn an arena (both
sides relocated into Rigel), let the bots fight for `seconds`, then pull
get_perf_stats and parse the [p539d] DEATH log for per-side gunfire/collision
deaths and per-bot shots/kills. Prints a JSON summary; writes per-run logs.

Usage: driver.py <label> <tech_a> <tech_b> <seconds>
"""
import json, subprocess, sys, threading, time, os, re

HOST, PORT = "127.0.0.1", "6073"
MCP = "/home/domingo/Development/AllegianceCore/tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp.dll"
DOTNET = "/home/domingo/.dotnet/dotnet"

label = sys.argv[1]; tech_a = int(sys.argv[2]); tech_b = int(sys.argv[3]); seconds = float(sys.argv[4])
outdir = "/tmp/p558"; os.makedirs(outdir, exist_ok=True)

proc = subprocess.Popen([DOTNET, MCP, HOST, PORT],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE, bufsize=1, text=True)

responses = {}
def reader():
    for line in proc.stdout:
        line = line.strip()
        if not line: continue
        try: msg = json.loads(line)
        except Exception: continue
        rid = msg.get("id")
        responses.setdefault(rid, []).append(msg)
threading.Thread(target=reader, daemon=True).start()

errbuf = []
def errreader():
    for line in proc.stderr:
        errbuf.append(line)
threading.Thread(target=errreader, daemon=True).start()

def send(o):
    proc.stdin.write(json.dumps(o) + "\n"); proc.stdin.flush()

def wait_id(rid, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if rid in responses and responses[rid]:
            return responses[rid][-1]
        time.sleep(0.05)
    return None

send({"jsonrpc":"2.0","id":1,"method":"initialize",
      "params":{"protocolVersion":"2024-11-05","capabilities":{},
                "clientInfo":{"name":"p558driver","version":"1.0"}}})
init = wait_id(1, 25)
send({"jsonrpc":"2.0","method":"notifications/initialized","params":{}})
time.sleep(1.5)

spawn_args = {"size":5, "target_sector":"Rigel", "arena":True, "tech_a":tech_a, "tech_b":tech_b}
send({"jsonrpc":"2.0","id":2,"method":"tools/call",
      "params":{"name":"spawn_scenario","arguments":spawn_args}})
spawn = wait_id(2, 90)

t0 = time.time()
time.sleep(seconds)   # the fight window

send({"jsonrpc":"2.0","id":3,"method":"tools/call",
      "params":{"name":"get_perf_stats","arguments":{}}})
perf = wait_id(3, 40)

proc.terminate()
time.sleep(1)
try: proc.kill()
except Exception: pass

# ── parse ────────────────────────────────────────────────────────────────
stderr_text = "".join(errbuf)
open(os.path.join(outdir, f"{label}_stderr.log"), "w").write(stderr_text)
open(os.path.join(outdir, f"{label}_responses.json"), "w").write(
    json.dumps({"spawn": spawn, "perf": perf}, indent=2))

# Per-bot stats from get_perf_stats: shots/kills by side (sideA=0, sideB=1).
def bots_from(perf):
    out = []
    if not perf: return out
    try:
        res = perf["result"]
        txt = res.get("content", [{}])[0].get("text", "") if isinstance(res, dict) else ""
    except Exception:
        txt = ""
    return txt

perf_text = bots_from(perf)
shots_by_side = {0:0, 1:0}; kills_by_side = {0:0, 1:0}
# get_perf_stats text has per-bot JSON; try to extract via regex for side + shots/kills.
# Fall back to DEATH-line-independent numbers below. We primarily rely on the
# DEATH log for deaths and the perf text for shots/kills if parseable.
print_perf = perf_text[:4000]

# DEATH lines: [p539d] DEATH bot B ship S side N cause=... gunfire=G collision=C
deaths = {0:{"gunfire":0,"collision":0}, 1:{"gunfire":0,"collision":0}}
death_last = {}
for m in re.finditer(r"\[p539d\] DEATH .*? side (\d+) cause=(\w+) .*? gunfire=(\d+) collision=(\d+)", stderr_text):
    side = int(m.group(1)); g = int(m.group(3)); c = int(m.group(4))
    death_last[(m.group(0)[:0], side)] = (g, c)  # marker
# aggregate by reading each bot's final gunfire/collision totals
bot_totals = {}
for m in re.finditer(r"\[p539d\] DEATH .*? ship (\d+) side (\d+) cause=\w+ .*? gunfire=(\d+) collision=(\d+)", stderr_text):
    sid = int(m.group(1)); side = int(m.group(2)); g = int(m.group(3)); c = int(m.group(4))
    bot_totals[sid] = (side, g, c)
for sid,(side,g,c) in bot_totals.items():
    deaths[side]["gunfire"] = g; deaths[side]["collision"] = c

print(json.dumps({
    "label": label, "tech_a": tech_a, "tech_b": tech_b, "window_s": seconds,
    "spawn_error": (spawn or {}).get("error"),
    "deaths": deaths,
    "perf_text_head": print_perf,
}, indent=2, default=str))
