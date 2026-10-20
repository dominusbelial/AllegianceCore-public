#!/usr/bin/env python3
"""HUD7 — real-window inventory-pane captures (no rig required).
Build AllegianceCore.slnx -c Release first. Raw output stays under --out.
"""
import argparse
import gzip
import json
import os
from pathlib import Path
import socket
import subprocess
import threading
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--out', type=Path, default=Path('.scratch/HUD7'))
parser.add_argument('--port', type=int, default=17127)
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
out = args.out.resolve()
art = os.environ.get('ALLEGIANCE_ARTWORK_PATH')
if not art:
    parser.error('set ALLEGIANCE_ARTWORK_PATH to the native artwork directory')
out.mkdir(parents=True, exist_ok=True)
env = os.environ.copy()
env.update(XDG_CONFIG_HOME=str(out / 'config'), XDG_DATA_HOME=str(out / 'data'), ALSOFT_DRIVERS='null')
processes = []
threads = []


def launch(argv, log, process_env=env):
    process = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=process_env)
    processes.append(process)

    def copy_log():
        with gzip.open(str(log) + '.gz', 'wb') as stream:
            while block := process.stdout.read(16384):
                stream.write(block)
    t = threading.Thread(target=copy_log, daemon=True)
    t.start()
    threads.append(t)
    return process


def client(size, style):
    dest = out / f'{size}-inventory-{style}'
    dest.mkdir(exist_ok=True)
    settings = dest / 'hud.json'
    settings.write_text(json.dumps(dict(Style=style, Size=1, ColorBlindSafe=False)))
    client_env = env.copy()
    client_env['ALLEGIANCE_HUD_SETTINGS'] = str(settings)
    argv = [str(root / 'src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame'),
            '127.0.0.1', str(args.port), 'proofHUD7', '--auto-side', '1', '--auto-ready',
            '--camera', 'cockpit', '--window', size, '--fx', 'enhanced', '--soft', 'on',
            '--auto-launch', 'Adv Fighter', '--auto-inventory',
            '--autoshots', '6,7,8', '--autoshot-dir', str(dest), '--exit-after', '11']
    (dest / 'argv.json').write_text(json.dumps(argv, indent=2))
    p = launch(argv, dest / 'client.log', client_env)
    try:
        code = p.wait(timeout=75)
    except subprocess.TimeoutExpired:
        p.terminate()
        code = p.wait(timeout=10)
    (out / 'runs.json').write_text(json.dumps(dict(size=size, style=style, exit=code, path=str(dest)), indent=2))
    if code:
        raise RuntimeError(f'{size} {style} exited {code}; see {dest}')
    pngs = sorted(dest.glob('*.png'))
    if not pngs:
        raise RuntimeError(f'no PNG captured for {size} {style}')
    print(f'{size} {style}: {len(pngs)} png -> {dest}', flush=True)


config = dict(artPath=art, staticCore='PCore001.igc', port=args.port, updatesPerSecond=20,
              games=[dict(name='HUD7 inventory', map='m2scatter.igc', teams=2,
                          civIds=[1, 3], sideNames=['Alpha', 'Bravo'])],
              liveSettings=dict(testHarness=dict(enabled=True, characterNamePrefix='mcp-', money=500000, ignoreTechGate=True),
                                testUnlock=dict(enabled=True, allTech=True, money=500000),
                                adminCheats=dict(enabled=True, allowNames=['mcp-*', 'proof*']), debugMode=True))
(out / 'server.json').write_text(json.dumps(config, indent=2))
try:
    server = launch([str(root / 'src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server'),
                     '--config', str(out / 'server.json')], out / 'server.log')
    for attempt in range(100):
        if server.poll() is not None:
            raise RuntimeError('server exited')
        try:
            with socket.create_connection(('127.0.0.1', args.port), .2):
                break
        except OSError:
            time.sleep(.2)
    else:
        raise TimeoutError('server did not listen')
    for size in ['1920x1080', '2560x1440']:
        for style in ['Classic', 'Modern']:
            client(size, style)
finally:
    for process in reversed(processes):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
    for t in threads:
        t.join(timeout=5)
