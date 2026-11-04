"""Evidence integrity: delayed identities, censored windows, no fabricated rounds."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('p924', Path(__file__).with_name('p924-side-bias-audit.py'))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class EvidenceTests(unittest.TestCase):
    def test_partial_opening_rebuild_and_partial_tail(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'log'
            path.write_text('\n'.join([
                '2026-11-03T00:01:00Z [scoring] killer ship 10 kill +1 (player peer)',
                '2026-11-03T00:01:01Z [pod-trip] eject ship=11 side=1 sector=1052',
                '2026-11-03T00:01:02Z [scoring] victim ship 11 death +1 (player peer)',
                '2026-11-03T00:01:03Z [pod-trip] {"Side":1,"Outcome":"death","EndSector":1052}',
                '2026-11-03T00:02:00Z [scoring] ship 10 (side 0) final score 1 kills=1 deaths=0',
                '2026-11-03T00:02:00Z [gameover] mission #0 -> Stage Over, iSideWinner=0 match=1',
                '2026-11-03T00:02:01Z [research-map] side=0 homeType=15->715',
                '2026-11-03T00:02:02Z [matchup] mission #0 match=2 seed=924',
                '2026-11-03T00:03:00Z [scoring] killer ship 999 kill +1 (NPC)',
            ]))
            result = audit.analyze(path)['matches']
            self.assertEqual([1, 2], [m['match'] for m in result])
            self.assertTrue(result[0]['openingCensored'])
            self.assertEqual(1, result[0]['sides'][0]['kills'])
            self.assertEqual(1, result[0]['sides'][1]['deaths'])
            self.assertEqual({'death': 1}, result[0]['sides'][1]['podOutcomes'])
            self.assertEqual({'1052': 1}, result[0]['sides'][1]['ejectionSectors'])
            self.assertNotIn('winner', result[1])
            self.assertEqual({'kills': 1}, result[1]['unknownScoring'])

    def test_repair_is_not_the_first_station_attack(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'log'
            path.write_text('2026-11-03T00:00:00Z [chat] 4 ship 1 side 0 hello\n'
                            '2026-11-03T00:00:01Z [queuedcmd] queued order commandID=6 on ship 1 (target wire #2 Station)\n'
                            '2026-11-03T00:00:02Z [queuedcmd] queued order commandID=1 on ship 1 (target wire #3 Station)\n')
            result = audit.analyze(path)['matches'][0]
            self.assertEqual('2026-11-03T00:00:02+00:00', result['sides'][0]['firstStrikeOrderUtc'])

    def test_reused_identity_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'log'
            path.write_text('2026-11-03T00:00:00Z [pod-trip] eject ship=1 side=0\n'
                            '2026-11-03T00:00:01Z [pod-trip] eject ship=1 side=1\n')
            with self.assertRaises(ValueError):
                audit.analyze(path)


if __name__ == '__main__':
    unittest.main()
