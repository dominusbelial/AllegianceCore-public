#!/usr/bin/env python3
"""Summarize P898 per-run JSON and ss samples; quantiles are medians of run quantiles."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import re
import statistics
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('directory', type=Path)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
rows = json.loads((a.directory / 'results.json').read_text())
clock_offset = time.time() - time.monotonic()

for row in rows:
    name = row['Name']
    state = a.directory / name
    port = (state / 'port.txt').read_text().strip()
    start = (state / 'measure').stat().st_mtime - clock_offset
    stop = start + row['Seconds']
    server_samples, client_samples = [], []
    counters = defaultdict(list)
    stamp = 0
    lines = (a.directory / (name + '-ss.txt')).read_text().splitlines()
    for i, line in enumerate(lines):
        if line.startswith('TIME '):
            stamp = float(line.split()[1])
        if not start <= stamp <= stop or not line.startswith('ESTAB '):
            continue
        fields = line.split()
        if len(fields) < 5:
            continue
        details = lines[i + 1] if i + 1 < len(lines) else ''
        def number(pattern):
            match = re.search(pattern, details)
            return int(match[1]) if match else 0
        if fields[3].endswith(':' + port):
            sample = dict(RecvQ=int(fields[1]), SendQ=int(fields[2]),
                          Wmem=number(r',w(\d+)'), Sndbuf=number(r',tb(\d+)'),
                          Unacked=number(r'\bunacked:(\d+)'), Notsent=number(r'\bnotsent:(\d+)'))
            server_samples.append(sample)
            counters[(fields[3], fields[4])].append(dict(
                BytesSent=number(r'\bbytes_sent:(\d+)'), Segments=number(r'\bdata_segs_out:(\d+)'),
                RetransBytes=number(r'\bbytes_retrans:(\d+)')))
        elif fields[4].endswith(':' + port):
            client_samples.append(int(fields[1]))
    if server_samples:
        memory = sorted(s['Wmem'] for s in server_samples)
        row['Kernel'] = dict(Samples=len(server_samples),
            SendBufferBytes=sorted({s['Sndbuf'] for s in server_samples}),
            WmemP50Bytes=statistics.median(memory), WmemP95Bytes=memory[int(.95 * (len(memory)-1))],
            MaxSendQBytes=max(s['SendQ'] for s in server_samples),
            MaxNotsentBytes=max(s['Notsent'] for s in server_samples),
            MaxUnackedSegments=max(s['Unacked'] for s in server_samples),
            MaxServerRecvQBytes=max(s['RecvQ'] for s in server_samples),
            MaxClientRecvQBytes=max(client_samples, default=0))
        sent = sum(max(s['BytesSent'] for s in samples) - min(s['BytesSent'] for s in samples)
                   for samples in counters.values())
        segments = sum(max(s['Segments'] for s in samples) - min(s['Segments'] for s in samples)
                       for samples in counters.values())
        row['Kernel']['MeanPayloadBytesPerSegment'] = sent / max(1, segments)
        row['Kernel']['RetransmittedBytes'] = sum(max(s['RetransBytes'] for s in samples) - min(s['RetransBytes'] for s in samples)
                                                for samples in counters.values())

groups = defaultdict(list)
for row in rows:
    groups[(row['Protocol'], row['Bots'], row['DelayMs'], row['Mtu'], row['TcpBatchWrites'])].append(row)
summary = []
for key, cases in sorted(groups.items()):
    proto, bots, delay, mtu, enabled = key
    summary.append(dict(Protocol=proto, Bots=bots, DelayMs=delay, Mtu=mtu, TcpBatchWrites=enabled,
        Repetitions=len(cases),
        Median={field: statistics.median(c[field] for c in cases if field in c)
                for field in ('PoseP50Ms', 'PoseP95Ms', 'PoseSamples', 'BytesPerSecond', 'FlowP95Ms',
                              'ServerTickP95Ms', 'ServerCpuCores', 'ClientProxyCpuCores', 'Superseded', 'PollBlocked', 'WriteCalls')
                if any(field in c for c in cases)},
        WorstGapMs=max(c['LongestPoseGapMs'] for c in cases),
        WorstWriteMs=max(c.get('LongestWriteMs', 0) for c in cases),
        AllConnected=all(c['ConnectedClients'] == bots and c['ConnectedServer'] == bots for c in cases)))
a.output.parent.mkdir(parents=True, exist_ok=True)
a.output.write_text(json.dumps(dict(Runs=rows, Summary=summary), indent=2) + '\n')
print('| delay | batch | pose p50/p95 ms | worst gap ms | KB/s | poses | superseded |')
print('|---:|:---:|---:|---:|---:|---:|---:|')
for row in summary:
    m = row['Median']
    print(f'| {row["DelayMs"]} | {"on" if row["TcpBatchWrites"] else "off"} '
          f'| {m["PoseP50Ms"]:.1f}/{m["PoseP95Ms"]:.1f} | {row["WorstGapMs"]:.1f} '
          f'| {m["BytesPerSecond"]/1000:.2f} | {m["PoseSamples"]:.0f} | {m.get("Superseded", 0):.0f} |')
