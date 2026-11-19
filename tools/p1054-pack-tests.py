#!/usr/bin/env python3
"""Exercise the actual recorded-pack splitter with small, original-byte fixtures."""
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('packs', Path(__file__).with_name('p1054-browser-packs.py'))
packs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packs)


def mdl(deps=(), surface=False):
    data = struct.pack('<6I', 0xDEBADF00, 1, len(deps), 0, 0, 1)
    for dep in deps:
        word = dep.encode() + b'\0'
        data += word + b'\0' * (-len(word) % 4)
    if surface:
        data += bytes(16) + struct.pack('<9I', 512, 512, 1024, 16, 0xf800, 0x7e0, 0x1f, 0, 0) + bytes(range(256)) * 2048
    return data


class PackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'publish'
        self.art = Path(self.temp.name) / 'art'
        (self.root / 'packs').mkdir(parents=True)
        (self.root / '_framework').mkdir()
        self.art.mkdir()
        sources = {'bgrnd03.mdl': mdl(['bgrnd03bmp']), 'bgrnd03bmp.mdl': mdl(surface=True),
                   'bgrnd05.mdl': mdl(['bgrnd05bmp']), 'bgrnd05bmp.mdl': mdl(surface=True) + b'\0',
                   'hull.mdl': mdl(['weaponbmp']), 'weaponbmp.mdl': mdl(surface=True), 'hull.cvh': b'collision', 'core.igc': b'core'}
        observed, entries = [], []
        for name, data in sources.items():
            (self.art / name).write_bytes(data)
            digest = hashlib.sha256(data).hexdigest()
            (self.root / 'packs' / digest).write_bytes(data)
            (self.root / 'packs' / (digest + '.gz')).write_bytes(gzip.compress(data, 9, mtime=0))
            observed.append(dict(Source=name, Origin='artwork', ByteSize=len(data), Sha256=digest))
            entries.append(dict(source=name, path='packs/' + digest, sha256=digest, byteSize=len(data), contentType='application/octet-stream', provenance='fixture', dependencies=[]))
        (self.root / 'join-manifest.json').write_text(json.dumps(dict(version=2, assets=entries)))
        (self.root / 'starter-manifest.json').write_text(json.dumps(dict(version=2, assets=[entries[-1]])))
        self.traces = [dict(style=style, map='map.igc', seed=1, FirstSector=dict(ClusterId=1),
                            Sectors=[dict(ClusterId=1, Name='Entry'), dict(ClusterId=2, Name='Next')],
                            Visits=[dict(SectorId=1, Seconds=30), dict(SectorId=2, Seconds=30)], Opens=observed,
                            SectorRequired={'1': ['bgrnd03.mdl', 'hull.mdl', 'hull.cvh'], '2': ['bgrnd05.mdl', 'hull.mdl']}) for style in ['Classic', 'Modern']]

    def test_only_entry_caps_and_all_streamed_files_are_original(self):
        receipt = packs.split(self.root, self.traces, self.art)
        base = {a['source']: a for a in json.loads((self.root / 'join-manifest.json').read_text())['assets']}
        index = json.loads((self.root / 'sector-manifest.json').read_text())
        self.assertEqual(['bgrnd03bmp.mdl'], receipt['entryFirstFrameCopies'])
        self.assertNotIn('bgrnd05bmp.mdl', base)
        for name in ['core.igc', 'hull.cvh', 'hull.mdl', 'weaponbmp.mdl', 'bgrnd03.mdl', 'bgrnd05.mdl']:
            self.assertEqual((self.art / name).read_bytes(), (self.root / base[name]['path']).read_bytes())
        for pack in index['sectors']:
            for asset in pack['assets']:
                data = (self.root / asset['path']).read_bytes()
                self.assertEqual((self.art / asset['source']).read_bytes(), data)
                self.assertEqual(asset['sha256'], hashlib.sha256(data).hexdigest())
        self.assertLess(receipt['totalTransferBytes'], 14 * 1024 * 1024)
        self.assertEqual(14 * 1024 * 1024, receipt['budgetBytes'])

    def test_changed_recorded_bytes_are_rejected(self):
        (self.art / 'bgrnd03bmp.mdl').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'Unverified desktop dependency'):
            packs.split(self.root, self.traces, self.art)

    def test_missing_sector_dwell_and_style_are_rejected(self):
        self.traces[0]['Visits'][1]['Seconds'] = 29
        with self.assertRaisesRegex(ValueError, 'Incomplete sector'):
            packs.split(self.root, self.traces, self.art)
        with self.assertRaisesRegex(ValueError, 'Both sector'):
            packs.split(self.root, self.traces[:1], self.art)

    def test_unrecorded_dependency_cannot_enter_base_or_stream(self):
        (self.art / 'bgrnd05bmp.mdl').write_bytes(mdl(['unrecorded']))
        data = (self.art / 'bgrnd05bmp.mdl').read_bytes()
        for t in self.traces:
            for opened in t['Opens']:
                if opened['Source'] == 'bgrnd05bmp.mdl': opened.update(ByteSize=len(data), Sha256=hashlib.sha256(data).hexdigest())
        (self.art / 'unrecorded.mdl').write_bytes(mdl())
        with self.assertRaisesRegex(ValueError, 'Unverified desktop dependency'):
            packs.split(self.root, self.traces, self.art)


if __name__ == '__main__': unittest.main()
