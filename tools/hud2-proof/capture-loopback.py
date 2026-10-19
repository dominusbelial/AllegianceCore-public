#!/usr/bin/env python3
"""HUD2 local real-window review and 30v30 ABBA benchmark (no rig required).
Build AllegianceCore.slnx -c Release first. Set ALLEGIANCE_ARTWORK_PATH.
Raw logs are gzip compressed and stay outside the repository by default.
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
parser.add_argument('--out', type=Path, default=Path('/tmp/HUD2/reproduce'))
parser.add_argument('--port', type=int, default=17126)
parser.add_argument('--sector', default='Fejfar')
parser.add_argument('--benchmark-only', action='store_true')
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
out = args.out.resolve()
if out == root or root in out.parents:
    parser.error('--out must be outside the repository (raw evidence policy)')
art = os.environ.get('ALLEGIANCE_ARTWORK_PATH')
if not art:
    parser.error('set ALLEGIANCE_ARTWORK_PATH to the native artwork directory')
out.mkdir(parents=True, exist_ok=True)
env = os.environ.copy()
env.update(XDG_CONFIG_HOME=str(out / 'config'), XDG_DATA_HOME=str(out / 'data'), ALSOFT_DRIVERS='null')
processes = []
threads = []
results = []


def launch(argv, log, process_env=env):
    process = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=process_env)
    processes.append(process)

    def copy_log():
        with gzip.open(str(log) + '.gz', 'wb') as stream:
            while block := process.stdout.read(16384):
                stream.write(block)
    thread = threading.Thread(target=copy_log, daemon=True)
    thread.start()
    threads.append(thread)
    return process


def client(size, scene, style, extra, seconds=12, benchmark=False):
    dest = out / f'{size}-{scene}-{style}'
    dest.mkdir(exist_ok=True)
    settings = dest / 'hud.json'
    settings.write_text(json.dumps(dict(Style=style, Size=1, ColorBlindSafe=False)))
    client_env = env.copy()
    client_env['ALLEGIANCE_HUD_SETTINGS'] = str(settings)
    if benchmark:
        client_env.update(ALLEGIANCE_NOVSYNC='1', ALLEGIANCE_CLIENT_PERF='1',
                          ALLEGIANCE_CLIENT_PERF_CSV=str(dest / 'frames.csv'))
    argv = [str(root / 'src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame'),
            '127.0.0.1', str(args.port), 'proofHUD2', '--auto-side', '1', '--auto-ready',
            '--camera', 'cockpit', '--window', size, '--fx', 'enhanced', '--soft', 'on',
            '--autoshot-dir', str(dest), '--exit-after', str(seconds)] + extra
    if not benchmark:
        argv += ['--autoshots', '9']
    (dest / 'argv.json').write_text(json.dumps(argv, indent=2))
    process = launch(argv, dest / 'client.log', client_env)
    try:
        code = process.wait(timeout=seconds + 65)
    except subprocess.TimeoutExpired:
        process.terminate()
        code = process.wait(timeout=10)
    results.append(dict(size=size, scene=scene, style=style, exit=code, path=str(dest)))
    (out / 'runs.json').write_text(json.dumps(results, indent=2))
    if code:
        raise RuntimeError(f'{scene} {style} exited {code}; see {dest}')
    if not benchmark:
        pngs = list(dest.glob('*.png'))
        if not pngs or any(not png.read_bytes().endswith(b'\x00\x00\x00\x00IEND\xaeB`\x82') for png in pngs):
            raise RuntimeError(f'incomplete PNG capture (check disk quota): {dest}')
    if benchmark:
        csv = dest / 'frames.csv'
        with csv.open('rb') as source, gzip.open(str(csv) + '.gz', 'wb') as target:
            while block := source.read(16384):
                target.write(block)
        csv.unlink()
    print(results[-1], flush=True)


config = dict(artPath=art, staticCore='PCore001.igc', port=args.port, updatesPerSecond=20,
              games=[dict(name='HUD2 local review', map='m2scatter.igc', teams=2,
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
    if not args.benchmark_only:
        for size in ['1920x1080', '2560x1440']:
            for style in ['Classic', 'Modern']:
                client(size, 'flight', style, ['--auto-launch', 'Adv Fighter'])
                client(size, 'docked', style, [])
                client(size, 'lifepod', style, ['--auto-launch', 'Adv Fighter', '--auto-suicide-at', '5'])
    commands = out / 'commands.jsonl'
    commands.write_text('')
    launch(['python3', str(root / 'scripts/mcp_driver.py'),
            str(root / 'tools/allegiance-mcp/bin/Release/net10.0/allegiance-mcp'),
            '127.0.0.1', str(args.port), str(commands), str(out / 'mcp.log'), str(out / 'mcp-err.log')], out / 'driver.log')
    time.sleep(4)
    with commands.open('a') as stream:
        stream.write(json.dumps(dict(tool='spawn_scenario', args=dict(size=30, arena=True, target_sector=args.sector))) + '\n')
    for attempt in range(180):
        if (out / 'mcp.log').exists() and 'RESULT spawn_scenario isError=False' in (out / 'mcp.log').read_text():
            break
        time.sleep(1)
    else:
        raise RuntimeError('30v30 spawn failed; inspect mcp.log')
    if not args.benchmark_only:
        for size in ['1920x1080', '2560x1440']:
            for style in ['Classic', 'Modern']:
                client(size, 'combat', style, ['--auto-launch', 'Adv Fighter', '--auto-target-class', 'enemy:fighter'], 18)
    for index, style in enumerate(['Classic', 'Modern', 'Modern', 'Classic']):
        client('2560x1440', 'perf-' + str(index), style,
               ['--auto-launch', 'Adv Fighter', '--auto-target-class', 'enemy:fighter'], 32, True)
finally:
    for process in reversed(processes):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
    for thread in threads:
        thread.join(timeout=5)
