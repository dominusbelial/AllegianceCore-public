#!/usr/bin/env python3
"""Run one local SC1 research showcase. Raw output stays outside the repository."""
import argparse
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[1]


def run(directory, port, seconds):
    directory.mkdir(parents=True, exist_ok=True)
    art = os.environ['ALLEGIANCE_ARTWORK_PATH']
    config = dict(port=port, artPath=art, staticCore='PCore001.igc',
                  showcase=dict(techMode='research', researchTimeScale=16, incomeMultiplier=5000, finaleTimeoutSeconds=180),
                  liveSettings=dict(timeDilation='off', autoNextMatch=dict(enabled=False)),
                  games=[dict(name='SC1 local 15v15', teams=2, civIds=[25, 3], sideNames=['Alpha', 'Bravo'],
                              maxTeamImbalance=32767, gameLengthSeconds=seconds,
                              mapGen=dict(type='SingleRing', playerSectorAsteroids=20, playerSectorSpecialAsteroids=8))])
    config_path = directory / 'server.json'
    config_path.write_text(json.dumps(config, indent=2))
    with (directory / 'server.log').open('w') as server_log, (directory / 'mcp-stderr.log').open('w') as mcp_err, (directory / 'mcp.jsonl').open('w') as mcp_log:
        server = subprocess.Popen(['dotnet', str(ROOT/'src/Allegiance.Server/bin/Debug/net10.0/Allegiance.Server.dll'), '--config', str(config_path)],
                                  cwd=ROOT, stdin=subprocess.PIPE, stdout=server_log, stderr=subprocess.STDOUT, text=True)
        mcp = None
        try:
            for _ in range(120):
                if server.poll() is not None:
                    raise RuntimeError('server failed; see server.log')
                try:
                    with socket.create_connection(('127.0.0.1', port), timeout=.2):
                        break
                except OSError:
                    time.sleep(.25)
            else:
                raise TimeoutError('server start')
            mcp = subprocess.Popen(['dotnet', str(ROOT/'tools/allegiance-mcp/bin/Debug/net10.0/allegiance-mcp.dll'), '127.0.0.1', str(port)],
                                   cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=mcp_err, text=True)
            messages = queue.Queue()
            def reader():
                for line in mcp.stdout:
                    mcp_log.write(line); mcp_log.flush()
                    try:
                        messages.put(json.loads(line))
                    except json.JSONDecodeError:
                        pass
            threading.Thread(target=reader, daemon=True).start()
            seq = 0
            def rpc(method, params):
                nonlocal seq
                seq += 1
                mcp.stdin.write(json.dumps(dict(jsonrpc='2.0', id=seq, method=method, params=params))+'\n'); mcp.stdin.flush()
                deadline = time.monotonic()+180
                while time.monotonic() < deadline:
                    msg = messages.get(timeout=max(.01, deadline-time.monotonic()))
                    if msg.get('id') == seq:
                        if 'error' in msg or msg.get('result', {}).get('isError'):
                            raise RuntimeError(msg)
                        return msg
                raise TimeoutError(method)
            rpc('initialize', dict(protocolVersion='2024-11-05', capabilities={}, clientInfo=dict(name='sc1-local', version='1')))
            mcp.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); mcp.stdin.flush()
            rpc('tools/call', dict(name='spawn_scenario', arguments=dict(preset='showcase', size_scale=1,
                target_sector='1', staging=True, finale_after_minutes=2, finale_side='1',
                finale_min_relaunch_share=0, finale_min_kills_last_5_minutes=1, finale_bombers=8)))
            deadline = time.monotonic()+seconds+180
            snapshot_at = time.monotonic()+60
            while time.monotonic() < deadline:
                if server.poll() is not None or mcp.poll() is not None:
                    raise RuntimeError('process exited before game over')
                content = (directory/'server.log').read_text(errors='replace')
                final_side = '[skills] match 1 side 1 final'
                if '[skills] match 1 side 0 final' in content and final_side in content and '[skills] match 1 side 1 coverage' in content.split(final_side)[-1]:
                    print('Match complete: '+str(directory/'server.log'), flush=True)
                    return
                if time.monotonic() >= snapshot_at:
                    server.stdin.write('skills\n'); server.stdin.flush(); snapshot_at += 60
                    print('Local showcase running; requested skills snapshot', flush=True)
                time.sleep(2)
            raise TimeoutError('match completion')
        finally:
            for process in (mcp, server):
                if process is not None and process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill(); process.wait()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path('/tmp/allegiance-evidence/SC1/local'))
    parser.add_argument('--port', type=int, default=18471)
    parser.add_argument('--seconds', type=int, default=600)
    args = parser.parse_args()
    run(args.output.resolve(), args.port, args.seconds)
