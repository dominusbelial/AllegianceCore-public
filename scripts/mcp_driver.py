#!/usr/bin/env python3
"""Persistent stdio driver for tools/allegiance-mcp (P572 live-proof).

Self-contained copy of the proven stdio driver (Claude's
~/.local/share/allegiance-mcp-driver/mcp_driver.py) so scripts/live-proof.sh
does not depend on a machine-local path. It starts the MCP server, performs the
MCP initialize handshake, then executes tool calls appended (one JSON object
per line: {"tool": "...", "args": {...}}) to CMD_FILE. Each request/response is
appended to OUT_FILE. {"tool": "quit"} stops the server (which despawns its
bots). The bots live only as long as this process, so live-proof.sh keeps this
driver alive across the whole client run and quits it afterwards.

Optional command result_file writes the full MCP result atomically. CMD_FILE.ready
contains the exact MCP PID once the initial handshake and command cursor are ready.
SIGTERM/INT always terminate and wait for the owned child.

Usage: mcp_driver.py <mcp-exe> <host> <port> <cmd-file> <out-file> <err-file> [mcp-options...]
"""
import json, os, select, signal, subprocess, sys, time
from pathlib import Path

EXE, HOST, PORT, CMD_FILE, OUT_FILE, ERR_FILE = sys.argv[1:7]
out = open(OUT_FILE, "a", buffering=1)
def log(*a): out.write(time.strftime("%H:%M:%S ") + " ".join(str(x) for x in a) + "\n")

proc = subprocess.Popen([EXE, HOST, PORT, *sys.argv[7:]], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=open(ERR_FILE, "ab"), bufsize=0)
dec = json.JSONDecoder(); buf = ""
def read_msg(want_id, timeout):
    global buf
    end = time.time() + timeout
    while time.time() < end:
        r, _, _ = select.select([proc.stdout], [], [], 1.0)
        if r:
            chunk = proc.stdout.read(65536)
            if not chunk: raise RuntimeError("mcp server exited")
            buf += chunk.decode(errors="replace")
        while True:
            s = buf.lstrip()
            if not s: buf = ""; break
            try: obj, i = dec.raw_decode(s)
            except ValueError: break
            buf = s[i:]
            if obj.get("id") == want_id: return obj
    raise TimeoutError(f"no response to id {want_id} in {timeout}s")

nid = 0
def call(method, params, timeout=60):
    global nid; nid += 1
    proc.stdin.write((json.dumps({"jsonrpc": "2.0", "id": nid, "method": method, "params": params}) + "\n").encode())
    return read_msg(nid, timeout)

def stop_signal(signum, frame):
    raise SystemExit(128 + signum)

signal.signal(signal.SIGTERM, stop_signal)
signal.signal(signal.SIGINT, stop_signal)
try:
    init = call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                               "clientInfo": {"name": "live-proof-driver", "version": "0.1"}}, 120)
    log("INIT", json.dumps(init.get("result", init))[:300])
    proc.stdin.write((json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n").encode())

    pos = os.path.getsize(CMD_FILE) if os.path.exists(CMD_FILE) else 0
    Path(CMD_FILE + ".ready").write_text(str(proc.pid))
    while proc.poll() is None:
        time.sleep(1)
        if not os.path.exists(CMD_FILE): continue
        with open(CMD_FILE) as f:
            f.seek(pos); lines = f.readlines(); pos = f.tell()
        for line in lines:
            line = line.strip()
            if not line: continue
            cmd = json.loads(line)
            if cmd["tool"] == "quit":
                log("QUIT"); proc.terminate(); proc.wait(30); sys.exit(0)
            log("CALL", cmd["tool"], json.dumps(cmd.get("args", {})))
            try:
                r = call("tools/call", {"name": cmd["tool"], "arguments": cmd.get("args", {})}, cmd.get("timeout", 900))
                res = r.get("result", r)
                if cmd.get("result_file"):
                    target = Path(cmd["result_file"])
                    target.with_suffix(target.suffix + ".tmp").write_text(json.dumps(res))
                    target.with_suffix(target.suffix + ".tmp").replace(target)
                text = "\n".join(c.get("text", "") for c in res.get("content", [])) if isinstance(res, dict) else str(res)
                log("RESULT", cmd["tool"], "isError=" + str(res.get("isError") if isinstance(res, dict) else None), "\n" + text[:400000])
            except Exception as e:
                log("ERROR", cmd["tool"], repr(e))
    log("SERVER EXITED code", proc.returncode)
finally:
    # Exact owned PID. MCP's signal handler disposes its tracked worker PIDs.
    if proc.poll() is None:
        proc.terminate()
        try: proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill(); proc.wait()
    out.close()
