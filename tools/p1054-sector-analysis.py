#!/usr/bin/env python3
"""Audit real P1054 sector recordings without copying artwork into the repository.

Outputs only names, hashes, sizes and observed sector ownership. This analysis
does not choose or relax the browser's base residency or transfer policy.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re
import struct


def namespace_dependencies(data):
    if len(data) < 24 or struct.unpack_from('<I', data)[0] != 0xDEBADF00:
        return []
    count = struct.unpack_from('<I', data, 8)[0]
    if count > 1024:
        raise ValueError('Unbounded namespace table')
    cursor, names = 24, []
    for _ in range(count):
        end = data.index(0, cursor)
        names.append(data[cursor:end].decode('ascii').lower() + '.mdl')
        cursor = (end + 4) & ~3
    return names


def ownership(observed_sectors, required_sectors):
    """An open before launch is common; reused files are common too.

    Recorded dependency roots attribute speculative desktop prewarms to their
    actual consuming sector instead of the sector the player was leaving.
    """
    if None in observed_sectors:
        return 'common'
    owners = required_sectors or observed_sectors
    return str(next(iter(owners))) if len(owners) == 1 else 'common'


def analyse(traces, artwork, content, expected_sectors=None):
    if {trace['style'] for trace in traces} != {'Classic', 'Modern'}:
        raise ValueError('Both styles are required')
    if any(trace.get('map') != traces[0].get('map') or trace.get('seed') != traces[0].get('seed') for trace in traces):
        raise ValueError('Recordings must use the same map and seed')
    sectors = {sector['ClusterId']: sector['Name'] for trace in traces for sector in trace['Sectors']}
    if expected_sectors is not None and len(sectors) != expected_sectors:
        raise ValueError('Recording does not cover the map sector count')
    for trace in traces:
        visited = {visit['SectorId'] for visit in trace['Visits'] if visit['Seconds'] >= 30}
        if visited != sectors.keys():
            raise ValueError('Every sector needs a 30-second visit in each style')
    files = {}
    source_paths = {p.relative_to(artwork).as_posix().lower(): p for p in artwork.rglob('*') if p.is_file()}
    for trace in traces:
        for opened in trace['Opens']:
            name = opened['Source'].lower()
            if not re.fullmatch(r'[a-z0-9_@./-]+', name) or any(p in ('', '.', '..') for p in name.split('/')):
                raise ValueError('Invalid recorded source')
            if name not in files:
                path = source_paths[name] if opened['Origin'] == 'artwork' else content / opened['Source'].removeprefix('Content/')
                data = path.read_bytes()
                digest = hashlib.sha256(data).hexdigest()
                files[name] = dict(source=name, rawBytes=len(data), gzipBytes=len(gzip.compress(data, 9, mtime=0)), sha256=digest,
                                   observed=set(), required=set(), dependencies=namespace_dependencies(data))
            entry = files[name]
            if opened['ByteSize'] != entry['rawBytes'] or opened['Sha256'] != entry['sha256']:
                raise ValueError('Recorded bytes changed: ' + name)
            entry['observed'].add(opened.get('SectorId'))
    for trace in traces:
        for sector, roots in trace['SectorRequired'].items():
            pending, visited = list(name.lower() for name in roots), set()
            while pending:
                name = pending.pop()
                if name in visited or name not in files:
                    continue  # built-in namespaces have no artwork file
                visited.add(name)
                files[name]['required'].add(int(sector))
                pending.extend(files[name]['dependencies'])
    table = {str(sector): dict(name=name, files=0, rawBytes=0, gzipBytes=0) for sector, name in sorted(sectors.items())}
    table['common'] = dict(name='common', files=0, rawBytes=0, gzipBytes=0)
    for entry in files.values():
        entry['owner'] = ownership(entry['observed'], entry['required'])
        row = table[entry['owner']]
        row['files'] += 1
        row['rawBytes'] += entry['rawBytes']
        row['gzipBytes'] += entry['gzipBytes']
        entry['observed'] = sorted(entry['observed'], key=lambda value: -1 if value is None else value)
        entry['required'] = sorted(entry['required'])
    return dict(map=traces[0]['map'], seed=traces[0]['seed'], styles=[trace['style'] for trace in traces],
                visits={trace['style']: trace['Visits'] for trace in traces}, sectors=table, files=[files[name] for name in sorted(files)])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--traces', type=Path, nargs=2, required=True)
    parser.add_argument('--artwork', type=Path, required=True)
    parser.add_argument('--content', type=Path, required=True)
    parser.add_argument('--expected-sectors', type=int, required=True, help='Actual sector count from the recorded server boot')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = analyse([json.loads(path.read_text()) for path in args.traces], args.artwork, args.content, args.expected_sectors)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result['sectors'], indent=2))


if __name__ == '__main__':
    main()
