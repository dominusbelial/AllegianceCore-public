import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('audit', Path(__file__).with_name('p925-home-audit.py'))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class HomeAuditTests(unittest.TestCase):
    def test_zero_stage_times_are_reached(self):
        self.assertEqual('Radius', audit.stage(dict(BayAt=None, RadiusAt=0, HomeAt=0)))
        self.assertEqual('Bay', audit.stage(dict(BayAt=0)))
        self.assertEqual('Away', audit.stage({}))

    def test_empty_distributions_remain_unknown(self):
        self.assertEqual(dict(n=0, median=None, p95=None), audit.distribution([]))
        self.assertEqual(2, audit.distribution([1, 2, 8])['median'])

    def test_match_boundaries_unknown_ends_and_delayed_removal(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            server, worker = root / 'server.log', root / 'worker.log'
            match = '[matchup] match={n} home0={h0} home1={h1} side0=18:Gigacorp side1=4:Phoenix\n'
            trip = dict(Ship=11, Side=0, Outcome='oxygen', HomeSector=1050, Station=4328,
                        EndSector=1050, EndPosition=[1, 2, 3], StationPosition=[0, 0, 0],
                        StationType=15, StationModel='ss24a', StationRadius=423.5,
                        StationForward=[0, 0, -1], StationUp=[0, 1, 0], PodHull=244,
                        EndAt=300, RadiusAt=200, HomeAt=100, BayAt=None)
            server.write_text('utc ' + match.format(n=1, h0=1050, h1=1051)
                              + 'utc [pod-trip] eject ship=11\n'
                              + 'utc [pod-trip] ' + json.dumps(trip) + '\n'
                              + 'utc [gameover] Stage Over\n'
                              + 'utc ' + match.format(n=2, h0=1051, h1=1050)
                              + 'utc [pod-trip] ' + json.dumps({**trip, 'Outcome': 'removed'}) + '\n')
            worker.write_text('[strike] side=0 wave=0 end reason=unknown\n'
                              + 'ship 11\n[strike] side=0 wave=1 commit stationHull=1 stationShield=1\n'
                              + '[strike] side=0 wave=1 end arrived=0 peakOnTarget=0 stationHull=-1 stationShield=-1 reason=members-gone\n')
            result = audit.analyze(server, [worker])
            self.assertTrue(result['matches'][0]['complete'])
            self.assertFalse(result['matches'][1]['complete'])
            self.assertEqual(1, result['waves'][0]['match'])
            self.assertEqual('-1', result['waves'][0]['end']['stationHull'])
            self.assertEqual(1, len(result['unmatchedWaveEnds']))
            self.assertEqual({'Radius': 1}, result['pods'][0]['oxygenStages'])
            self.assertEqual(100, result['pods'][0]['radiusToOxygen']['median'])
            self.assertEqual({'removed': 1}, result['pods'][2]['outcomes'])

    def test_unended_wave_is_censored(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            server, worker = root / 's', root / 'w'
            server.write_text('utc [matchup] match=1 home0=1050 home1=1051 side0=18:G side1=4:P\n'
                              + 'utc [pod-trip] eject ship=11\n')
            worker.write_text('ship 11\n[strike] side=0 wave=1 commit members=3\n')
            wave = audit.analyze(server, [worker])['waves'][0]
            self.assertNotIn('end', wave)


if __name__ == '__main__':
    unittest.main()
