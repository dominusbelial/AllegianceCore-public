#!/usr/bin/env python3
"""P426 — stdio harness that drives the allegiance-mcp server through its own
MCP protocol (newline-delimited JSON-RPC 2.0), the same way an MCP client /
Claude Code would. This is NOT a manual human test.

Usage:
    python3 verify_stdio.py <host> <port> [path-to-allegiance-mcp]

Prints each exchange, then a PASS/FAIL summary. Exit code 0 iff all live checks
pass:
  1. tools/list returns the 4 tools
  2. list_hulls / list_parts return real entries from PCore001.igc
  3. spawn_bot(real hull, behavior=weave) returns a real confirmation
  4. list_active_bots shows the ship seated, then its position CHANGES over a
     few seconds (proving the weave loop is really running)
  5. spawn_bot(garbage hull) returns a real isError with a specific message
"""
import json, os, select, subprocess, sys, time

def choose_server(argv):
    host = argv[0] if len(argv) > 0 else "127.0.0.1"
    port = argv[1] if len(argv) > 1 else "6073"
    exe = argv[2] if len(argv) > 2 else (
        os.path.join(os.path.dirname(os.path.abspath(__file__)),
                     "bin", "Release", "net10.0", "allegiance-mcp"))
    return host, port, exe

host, port, exe = choose_server(sys.argv[1:])

proc = subprocess.Popen(
    [exe, host, port],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    bufsize=0,
)

_decoder = json.JSONDecoder()

def read_message(timeout=25):
    """Read exactly one complete JSON-RPC message from stdout (MCP stdio is
    newline-delimited JSON; tolerant of leading whitespace and multi-line JSON)."""
    buf = b""
    deadline = time.time() + timeout
    while time.time() < deadline:
        while True:
            r, _, _ = select.select([proc.stdout], [], [], 0)
            if not r:
                break
            chunk = proc.stdout.read(65536)
            if not chunk:
                break
            buf += chunk
            if buf[:15].strip().lower().startswith(b"content-length"):
                raise RuntimeError(
                    "server used Content-Length framing, not newline-delimited JSON "
                    "(MCP stdio spec violation)")
        try:
            s = buf.decode(errors="replace")
            i = 0
            while i < len(s) and s[i] in " \r\n\t":
                i += 1
            if i < len(s):
                obj, _ = _decoder.raw_decode(s, i)
                return obj
        except ValueError:
            pass
        time.sleep(0.05)
    raise TimeoutError("timed out waiting for a message from the MCP server")

def send(obj):
    line = json.dumps(obj, separators=(",", ":")) + "\n"
    proc.stdin.write(line.encode())
    proc.stdin.flush()

def stderr_snippet():
    try:
        data = proc.stderr.read(65536)
        return data.decode(errors="replace").rstrip() if data else ""
    except Exception:
        return ""

def call(name, arguments=None, rid=None):
    rid = rid if rid is not None else call.counter
    call.counter += 1
    params = {"name": name}
    if arguments is not None:
        params["arguments"] = arguments
    send({"jsonrpc": "2.0", "id": rid, "method": "tools/call", "params": params})
    return read_message()

call.counter = 100

def result_of(resp):
    if "error" in resp:
        return None, resp["error"]
    return resp.get("result"), None

def text_of(result):
    if not result:
        return ""
    content = result.get("content") or []
    return "\n".join(b.get("text", "") for b in content if b.get("type") == "text")

def main():
    time.sleep(1.0)  # let the server connect its probe
    err = stderr_snippet()
    if err:
        print("[stderr] " + err.replace("\n", " | "))
    results = []

    def check(name, cond, detail=""):
        results.append((name, bool(cond)))
        print(("  PASS  " if cond else "  FAIL  ") + name + (f"  — {detail}" if detail else ""))

    # 0. initialize + initialized
    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                     "clientInfo": {"name": "p426-harness", "version": "1.0"}}})
    init = read_message()
    print("initialize ->", json.dumps(init)[:200])
    check("server answered initialize", init.get("result") is not None)
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    # 1. tools/list
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    tl = read_message()
    tools = [t.get("name") for t in (tl.get("result") or {}).get("tools", [])]
    print("tools/list ->", tools)
    for want in ("spawn_bot", "list_hulls", "list_parts", "list_active_bots"):
        check(f"tools/list includes {want}", want in tools)

    # 2. list_hulls / list_parts
    lh = result_of(call("list_hulls"))[0]
    hulls = json.loads(text_of(lh)) if lh else []
    print(f"list_hulls -> {len(hulls)} hulls; first: {hulls[0] if hulls else None}")
    check("list_hulls returns real entries", isinstance(hulls, list) and len(hulls) > 0)

    lp = result_of(call("list_parts"))[0]
    parts = json.loads(text_of(lp)) if lp else []
    cats = {p.get("category") for p in parts}
    print(f"list_parts -> {len(parts)} parts; categories: {sorted(cats)}")
    check("list_parts returns real entries w/ categories",
          isinstance(parts, list) and len(parts) > 0 and len(cats) > 0)

    # 3. spawn_bot with a real hull + weave
    chosen = hulls[0]["name"] if hulls else "Patrol Craft"
    spawn = result_of(call("spawn_bot", {"hull": chosen, "behavior": "weave"}))
    sres, serr = spawn
    stxt = text_of(sres)
    print(f"spawn_bot({chosen}, weave) -> ok={sres and not sres.get('isError')}  {stxt[:160]}")
    if serr is None and sres and not sres.get("isError"):
        sp = json.loads(stxt)
        check("spawn_bot returns real confirmation",
              sp.get("ok") is True and sp.get("shipId") is not None and sp.get("hull"))
        spawned_id = sp.get("shipId")
    else:
        spawned_id = None
        check("spawn_bot returns real confirmation", False, stxt)

    # 4. list_active_bots: seated now, position changes over ~4s
    time.sleep(2.0)
    b1 = result_of(call("list_active_bots"))[0]
    bots1 = json.loads(text_of(b1)) if b1 else []
    print(f"list_active_bots #1 -> {len(bots1)} bot(s): "
          f"[{[{ 'name': b['name'], 'shipId': b['shipId'], 'side': b['side'], 'hull': b['hull'], 'sector': b['sector'], 'pos': b['pos'] } for b in bots1]}")
    my1 = next((b for b in bots1 if spawned_id is None or b["shipId"] == spawned_id), None)
    check("list_active_bots shows the spawned ship seated",
          my1 is not None and my1.get("shipId") is not None and my1.get("side") is not None
          and my1.get("sector", -1) != -1 and my1.get("hull"))

    time.sleep(4.0)
    b2 = result_of(call("list_active_bots"))[0]
    bots2 = json.loads(text_of(b2)) if b2 else []
    my2 = next((b for b in bots2 if spawned_id is None or b["shipId"] == spawned_id), None)
    moved = my1 is not None and my2 is not None and tuple(my1["pos"]) != tuple(my2["pos"])
    print(f"list_active_bots #2 pos={my2['pos'] if my2 else None}  (prev {my1['pos'] if my1 else None})")
    check("weave loop really moving the ship (position changed)", moved)

    # 5. spawn_bot with garbage hull -> real isError
    bad = result_of(call("spawn_bot", {"hull": "definitely-not-a-real-hull-xyzzy"}))
    br, berr = bad
    btxt = text_of(br)
    is_err = br is not None and br.get("isError") is True
    print(f"spawn_bot(garbage) -> isError={is_err}  {btxt[:160]}")
    check("garbage hull returns a real isError", is_err)
    check("error message identifies the hull problem",
          "no hull matches" in btxt.lower() or "not a real" in btxt.lower() or berr is not None)

    # also confirm parts spawn works (real parts)
    pp = result_of(call("spawn_bot", {"hull": chosen, "behavior": "idle",
                                      "parts": [parts[0]["name"], parts[1]["name"]]}))
    pr, perr = pp
    ptxt = text_of(pr)
    print(f"spawn_bot(real hull + 2 real parts, idle) -> ok={not (pr and pr.get('isError'))}  {ptxt[:120]}")
    check("spawn_bot with explicit real parts succeeds", pr is not None and not pr.get("isError"), ptxt)

    print("\n=== SUMMARY ===")
    ok = all(c for _, c in results)
    for name, cond in results:
        print(("  PASS  " if cond else "  FAIL  ") + name)
    proc.terminate()
    proc.wait(timeout=10)
    sys.exit(0 if ok else 1)

if __name__ == "__main__":
    main()
