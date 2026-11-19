#!/usr/bin/env python3
"""Ownership, original-byte verification and incomplete-recording regressions."""
import hashlib
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('sector_analysis', Path(__file__).with_name('p1054-sector-analysis.py'))
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)


class SectorAnalysisTests(unittest.TestCase):
    def test_before_launch_or_reused_assets_are_common(self):
        self.assertEqual('common', analysis.ownership({None, 1}, {1}))
        self.assertEqual('common', analysis.ownership({1, 2}, {1, 2}))
        self.assertEqual('common', analysis.ownership({1, 2}, set()))

    def test_prewarmed_asset_is_attributed_to_its_consuming_sector(self):
        self.assertEqual('2', analysis.ownership({1}, {2}))
        self.assertEqual('1', analysis.ownership({1}, set()))

    def test_original_hashes_and_dependency_reuse_are_verified(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            texture = b'original surface'
            # A real MDL namespace header: model + surface, dependency strings
            # padded to DWORD boundaries.
            model = struct.pack('<6I', 0xDEBADF00, 65536, 2, 0, 0, 0) + b'model\0\0\0surface\0'
            (root / 'rock.mdl').write_bytes(model)
            (root / 'surface.mdl').write_bytes(texture)
            opens = [dict(Source=name, Origin='artwork', SectorId=1, ByteSize=len(data), Sha256=hashlib.sha256(data).hexdigest())
                     for name, data in [('rock.mdl', model), ('surface.mdl', texture)]]
            trace = dict(map='fixture.igc', seed=1, Sectors=[dict(ClusterId=1, Name='One'), dict(ClusterId=2, Name='Two')],
                         Visits=[dict(SectorId=1, Seconds=30), dict(SectorId=2, Seconds=30)],
                         SectorRequired={'1': ['rock.mdl'], '2': ['rock.mdl']}, Opens=opens)
            traces = [dict(trace, style=style) for style in ('Classic', 'Modern')]
            result = analysis.analyse(traces, root, root)
            self.assertEqual(2, result['sectors']['common']['files'])
            self.assertEqual(len(model) + len(texture), result['sectors']['common']['rawBytes'])
            with self.assertRaisesRegex(ValueError, 'map sector count'):
                analysis.analyse(traces, root, root, expected_sectors=3)
            (root / 'surface.mdl').write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'Recorded bytes changed'):
                analysis.analyse(traces, root, root)

    def test_incomplete_visits_and_wrong_styles_are_rejected(self):
        trace = dict(map='fixture.igc', seed=1, Sectors=[dict(ClusterId=1, Name='One')],
                     Visits=[dict(SectorId=1, Seconds=29.9)], SectorRequired={}, Opens=[])
        with self.assertRaisesRegex(ValueError, 'Both styles'):
            analysis.analyse([dict(trace, style='Classic')], Path('.'), Path('.'))
        with self.assertRaisesRegex(ValueError, '30-second'):
            analysis.analyse([dict(trace, style=style) for style in ('Classic', 'Modern')], Path('.'), Path('.'))


if __name__ == '__main__':
    unittest.main()
