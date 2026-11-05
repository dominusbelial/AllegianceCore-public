#!/usr/bin/env python3
"""Capture a local-only P928 showcase against saved NativeAOT server binaries.

Usage: showcase.py before|after [seconds=150] [run=1]
Run arms serially. Build server-before/server-after under .scratch/p928 and the
Debug allegiance-mcp host first; source local.env before invoking this script.
"""
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import sys
import threading
import time

arm = sys.argv[1]
if arm not in ('before', 'after'):
    raise ValueError('arm must be before or after')
seconds = int(sys.argv[2]) if len(sys.argv) > 2 else 150
label = arm + '-' + (sys.argv[3] if len(sys.argv) > 3 else '1')
out = Path('.scratch/p928')
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
config = {
    'port': port, 'consoleVerbose': False,
    'broadcast': {'immutableSnapshot': True, 'encodeOnce': True,
                  'parallelBuild': True, 'shipUpdateModel': 'native'},
    'transport': {'mode': 'pool', 'maxConnectionsPerIp': 512},
    'sim': {'nativeGotoPlan': True},
    'liveSettings': {'autoNextMatch': {'enabled': False}},
    'showcase': {'techMode': 'research', 'incomeMultiplier': 5000, 'researchTimeScale': 8},
    'games': [{
        'name': 'P928 local', 'map': 'm2xspiderw.igc', 'teams': 2,
        'sideNames': ['Alpha', 'Bravo'], 'civIds': [25, 3],
        'maxTeamImbalance': 32767, 'maxPlayersPerTeam': 100,
        'gameLengthSeconds': 1800,
        'researchPlanDirectory': str(Path('data/showcase-plans').resolve()),
        'researchPlans': {'0': 'plan_a', '1': 'plan_a'},
    }],
}
config_path = out / f'showcase-{label}.json'
config_path.write_text(json.dumps(config))
env = dict(os.environ, ALLEGIANCE_TICK_PROFILER='1',
           ALLEGIANCE_TICK_PROFILER_INTERVAL='5', ALLEGIANCE_SIM_NATIVE_GOTOPLAN='1')
log = open(out / f'showcase-{label}.log', 'w')
mcp_log = open(out / f'showcase-{label}-mcp.log', 'w')
server = subprocess.Popen([str(out / f'server-{arm}' / 'Allegiance.Server'),
                           '--config', str(config_path)],
                          stdout=log, stderr=subprocess.STDOUT, env=env)
mcp = None
try:
    time.sleep(3)
    if server.poll() is not None:
        raise RuntimeError('Server exited')
    mcp = subprocess.Popen([
        str(Path.home() / '.dotnet/dotnet'),
        'tools/allegiance-mcp/bin/Debug/net10.0/allegiance-mcp.dll',
        '127.0.0.1', str(port),
    ], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=mcp_log, text=True, env=env)
    messages = queue.Queue()

    def read_messages():
        for line in mcp.stdout:
            messages.put(json.loads(line))

    threading.Thread(target=read_messages, daemon=True).start()

    def send(data):
        mcp.stdin.write(json.dumps(data) + '\n')
        mcp.stdin.flush()

    def receive(identifier, timeout=120):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                data = messages.get(timeout=1)
            except queue.Empty:
                if mcp.poll() is not None:
                    raise RuntimeError('MCP exited')
                continue
            if data.get('id') == identifier:
                return data
        raise TimeoutError('MCP reply')

    send({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {
        'protocolVersion': '2024-11-05', 'capabilities': {},
        'clientInfo': {'name': 'p928-proof', 'version': '1'},
    }})
    print(receive(1), flush=True)
    send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
    send({'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call', 'params': {
        'name': 'spawn_scenario', 'arguments': {
            'preset': 'showcase', 'size_scale': 4, 'staging': True, 'finale_after_minutes': 0,
        },
    }})
    spawned = receive(2)
    print(spawned, flush=True)
    result = spawned['result']
    if result.get('isError'):
        raise RuntimeError('Showcase spawn failed')
    fleet = json.loads(result['content'][0]['text'])
    if fleet['sideA']['spawned'] != 60 or fleet['sideB']['spawned'] != 60:
        raise RuntimeError('Incomplete showcase fleet')
    time.sleep(seconds)
    send({'jsonrpc': '2.0', 'id': 3, 'method': 'tools/call',
          'params': {'name': 'get_perf_stats', 'arguments': {}}})
    print(receive(3), flush=True)
finally:
    for process in (mcp, server):
        if process is None:
            continue
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
    log.close()
    mcp_log.close()
    worker = Path('err.worker-1.log')
    if worker.exists():
        worker.rename(out / f'showcase-{label}-worker.log')
