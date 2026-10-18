"""Resolve P734b in files; repository metadata is read only. No git subprocesses."""
import csv, hashlib, io, json, re, struct, tarfile
from collections import Counter
from merge_data import ROOT, OUT, gitdir, objects, B, O, T

data = (gitdir / 'index').read_bytes()
signature, version, count = struct.unpack_from('!4sII', data)
assert signature == b'DIRC' and version == 2
pos = 12
stages = {}
for _ in range(count):
    start = pos
    fields = struct.unpack_from('!10I20sH', data, pos)
    flags = fields[-1]
    pos += 62 + (2 if flags & 0x4000 else 0)
    end = data.index(b'\0', pos)
    name = data[pos:end].decode()
    stage = (flags >> 12) & 3
    pos = start + ((end - start + 8) // 8) * 8
    if stage:
        stages.setdefault(name, {})[str(stage)] = fields[-2].hex()
(OUT / 'index-stages.json').write_text(json.dumps(stages, indent=2) + '\n')
(OUT / 'metadata-fingerprints.json').write_text(json.dumps({
    p: hashlib.sha256((gitdir / p).read_bytes()).hexdigest()
    for p in ('index', 'HEAD', 'MERGE_HEAD', 'MERGE_MSG')
}, indent=2) + '\n')
pattern = re.compile(r'^<<<<<<<[^\n]*\n(.*?)^=======\n(.*?)^>>>>>>>[^\n]*(?:\n|$)', re.M | re.S)
conflicts = {p: [dict(ours=m[1], theirs=m[2]) for m in pattern.finditer((ROOT / p).read_text())] for p in stages}
assert all(conflicts.values()), 'One-time resolver requires the original conflicted worktree'
assert set(stages) == {'docs/TEST_FLAKES.md', 'docs/parity/sim-rules-matrix.csv',
    'src/Allegiance.Core/Sim/IIgcSite.cs', 'src/Allegiance.ClientCore/State/ClientWorld.cs',
    'src/Allegiance.ClientCore/Session/ConnectionSession.cs'}
(OUT / 'conflicts-before.json').write_text(json.dumps(conflicts, indent=2) + '\n')
with tarfile.open(OUT / 'conflict-inputs.tar.gz', 'w:gz') as archive:
    for name in stages: archive.add(ROOT / name, arcname=name)

for name in ('docs/TEST_FLAKES.md', 'src/Allegiance.Core/Sim/IIgcSite.cs'):
    path = ROOT / name
    path.write_text(pattern.sub(lambda m: m[1] + '\n' + m[2], path.read_text()), newline='\n')
path = ROOT / 'src/Allegiance.ClientCore/Session/ConnectionSession.cs'
assert len(conflicts[str(path.relative_to(ROOT))]) == 2
path.write_text(pattern.sub('        short voiceOver = -1,\n        short commandId = -1,\n', path.read_text()), newline='\n')
path = ROOT / 'src/Allegiance.ClientCore/State/ClientWorld.cs'
path.write_text(pattern.sub(lambda m: m[2], path.read_text()), newline='\n')

# Resolve every matrix by row against the common ancestor, including rows
# outside explicit conflict hunks. Preserve both evidences on shared changes.
audit = []
rank = {'missing': 0, 'partial': 1, 'done': 2, 'implemented': 2, 'na': 2, 'n/a': 2}
for name in ('docs/parity/parity-matrix.csv', 'docs/parity/sim-rules-matrix.csv', 'docs/parity/fedmsg-wiring.csv'):
    def read(blob):
        reader = csv.DictReader(io.StringIO(blob.decode()))
        rows = list(reader)
        def key(r): return r.get('row_id') or r.get('id') or (r['area'], r['native_item'])
        return reader.fieldnames, {key(r): r for r in rows}
    header, b = read(objects.read(B[name]))
    _, o = read(objects.read(O[name]))
    _, t = read(objects.read(T[name]))
    assert b.keys() == o.keys() == t.keys()
    merged = []
    for key, base in b.items():
        ours, theirs = o[key], t[key]
        if ours == theirs:
            result, decision = dict(ours), 'identical'
        elif ours == base:
            result, decision = dict(theirs), 'incoming only'
        elif theirs == base:
            result, decision = dict(ours), 'ours only'
        else:
            result = dict(ours if rank[ours['port_status']] >= rank[theirs['port_status']] else theirs)
            for field in header:
                if field in ('row_id', 'id', 'port_status', 'weight') or ours[field] == theirs[field]: continue
                if ours[field] == base[field]: result[field] = theirs[field]
                elif theirs[field] == base[field]: result[field] = ours[field]
                else: result[field] = '; '.join(dict.fromkeys(v for v in (ours[field], theirs[field]) if v))
            decision = 'both changed: more complete status and both evidences'
        merged.append(result)
        if ours != theirs:
            audit.append(dict(file=name, key=key, decision=decision, base=base, ours=ours, theirs=theirs, resolved=result))
    current = ROOT / name
    if pattern.search(current.read_text()) or read(current.read_bytes())[1] != {k: r for k, r in zip(b, merged)}:
        with current.open('w', newline='') as stream:
            writer = csv.DictWriter(stream, header, lineterminator='\n')
            writer.writeheader()
            writer.writerows(merged)
    assert b'\r' not in current.read_bytes(), name
(OUT / 'parity-row-audit.json').write_text(json.dumps(audit, indent=2) + '\n')
print(json.dumps(dict(conflicts={p: len(v) for p, v in conflicts.items()}, row_decisions=Counter(a['decision'] for a in audit)), indent=2))
