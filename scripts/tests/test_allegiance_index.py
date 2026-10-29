"""Small, generated fixture logs; no external artwork or evidence required."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'allegiance-index.py'
SPEC = importlib.util.spec_from_file_location('allegiance_index', SCRIPT)
aix = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = aix
SPEC.loader.exec_module(aix)
RIG = {'name': 'test-rig', 'server': {'physicalCores': 8}, 'botHosts': [{'bots': 30}]}


def tick(mean=10, p95=20, ticks=100, stamp=''):
    return f'{stamp}[tickprof] window 5s ticks={ticks} tick-wall mean={mean}ms p95={p95}ms max=90ms\n'


class IndexTests(unittest.TestCase):
    def setUp(self):
        # The caller sets TMPDIR to the task scratch directory for repo gates.
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def file(self, name, text):
        path = self.root / name
        path.write_text(text)
        return path

    def compute(self, log, **kwargs):
        return aix.compute([self.file('server.log', log)], [], [], RIG, players=30, **kwargs)

    def test_good_window_boundaries_and_idle_exclusion(self):
        result = self.compute(tick(4.99) + tick(5, 40, 95) + tick(10, 40.01)
                              + tick(ticks=94) + tick() + '[tidi] scale=0.949 cost=5ms\n'
                              + tick() + '[tidi] scale=0.95 cost=5ms\n')
        self.assertEqual((result['busy_windows'], result['good_windows']), (5, 2))
        self.assertEqual(result['quality'], .4)

    def test_headroom_cap(self):
        result = self.compute(tick(5, 1))
        self.assertEqual(result['headroom'], 4)
        self.assertEqual(result['aix'], 60)

    def test_server_only(self):
        result = self.compute(tick())
        self.assertEqual(result['battle']['mode'], 'server-only')
        self.assertEqual(result['battle']['factor'], 1)
        self.assertEqual(result['machines'], 2)
        self.assertAlmostEqual(result['aix_per_core'], 30 * 2 ** .5 / 8)

    def test_b54_two_piece_merge_keeps_identical_untimed_windows(self):
        first = self.file('b54-day1.log', tick() * 2)
        second = self.file('b54-day2.log', tick(p95=60))
        result = aix.compute([first, second], [], [], RIG, players=30)
        self.assertEqual(result['busy_windows'], 3)
        self.assertEqual(result['quality'], 2 / 3)
        self.assertEqual(result['median_p95_ms'], 20)

    def test_timestamped_overlap_deduplicates(self):
        line = tick(stamp='2026-10-29T10:00:05Z ')
        first = self.file('a.log', line)
        second = self.file('b.log', line)
        result = aix.compute([first, second], [], [], RIG, players=30)
        self.assertEqual(result['busy_windows'], 1)

    def test_tidi_can_follow_tick_across_midnight_file_boundary(self):
        first = self.file('day1.log', tick())
        second = self.file('day2.log', '[tidi] scale=0.90 cost=10ms\n' + tick())
        result = aix.compute([first, second], [], [], RIG, players=30)
        self.assertEqual(result['quality'], .5)

    def test_samples_matched_by_utc_not_whole_day_mean(self):
        log = self.file('server.log', tick(stamp='2026-10-29T10:00:05Z ')
                        + tick(stamp='2026-10-29T10:00:10Z '))
        samples = self.file('samples.csv', 'ts,peers\n2026-10-29T00:00:00,999\n'
                            '2026-10-29T10:00:03,30\n2026-10-29T10:00:13,40\n')
        result = aix.compute([log], [samples], [], RIG, players=999)
        self.assertEqual(result['players'], 35)
        self.assertTrue(result['players_source'].startswith('samples'))

    def test_unmatched_samples_fall_back_to_final_not_migration_or_zero_declare(self):
        log = tick() + '[gameover-timing] mission #0 part=declare-start peers=0\n'
        log += '[gameover-timing] mission #-1 winner=0 peers=42 elapsedMs=3\n'
        log += '[gameover-timing] part=migrate peers=999\n'
        result = self.compute(log)
        self.assertEqual(result['players'], 42)

    def test_missing_peers_is_error(self):
        with self.assertRaisesRegex(ValueError, 'supply --players'):
            aix.compute([self.file('server.log', tick())], [], [], RIG)

    def test_no_busy_windows_is_error(self):
        with self.assertRaisesRegex(ValueError, 'No busy'):
            self.compute(tick(4.9))

    def test_non_five_second_window_excluded(self):
        result = self.compute(tick().replace('window 5s', 'window 10s') + tick())
        self.assertEqual(result['windows'], 1)

    def test_label_dilation_and_following_tidi(self):
        line = tick().replace('ticks=100', 'ticks=100 [sim.timeScale=0.9]')
        self.assertEqual(self.compute(line)['quality'], 0)
        self.assertEqual(self.compute(line + '[tidi] scale=1 cost=1ms\n')['quality'], 1)

    def test_finale_uses_side_kills_and_completed_interval_only(self):
        log = tick() * 2 + '[finale] deadline winner side=1 values=0:kills=2/deaths=3,1:kills=3/deaths=2 epoch=a\n'
        log += '[gameover-timing] mission #-1 winner=1 peers=30\n' + tick() * 10
        result = self.compute(log, reference={'kill_rate': 1})
        battle = result['battle']
        self.assertEqual(battle['kills'], 5)
        self.assertEqual(battle['scored_bot_minutes'], 5)
        self.assertEqual(battle['metrics']['kill_rate'], 1)

    def test_bot_rotation_stalls_and_disappearance_are_excluded(self):
        bot = self.file('bot.log', '[opening] t=100s\n[stall] utc=2026-10-29T10:00:00Z\n'
                        '[opening] t=120s\n[p598] re-readied bot after rotation\n'
                        '[opening] t=300s\n[stall] utc=2026-10-29T10:03:20Z\n'
                        '[strike] target=station#1 outcome=station-killed hitsInWindow=0\n'
                        '[strike] target=station#2 outcome=station-killed hitsInWindow=2\n')
        metrics = aix.bot_metrics([bot])
        self.assertEqual(metrics, dict(stalls=1, rotation_stalls_excluded=1,
                                      disconnects=0, station_kills=1, mean_log_seconds=300))

    def test_finale_without_gameover_timing_is_battle_evidence(self):
        result = self.compute(tick() + '[finale] deadline winner side=0 values=0:kills=2/deaths=1 epoch=a\n',
                              reference={'kill_rate': 1})
        self.assertEqual(result['battle']['mode'], 'battle')
        self.assertEqual(result['battle']['kills'], 2)

    def test_incomplete_battle_is_explicitly_unverified(self):
        result = self.compute(tick() + '[finale] bounded finale armed\n')
        self.assertEqual(result['battle']['mode'], 'battle')
        self.assertTrue(any('unverified' in note for note in result['notes']))

    def test_battle_geometric_ratios_cap_and_zero_incident_reference(self):
        battle = {'mode': 'battle', 'metrics': {'kill_rate': 2, 'station_rate': .5,
                  'disconnect_rate': .001, 'stall_rate': 0}}
        factor, parts = aix.battle_factor(battle, {'kill_rate': 1, 'station_rate': 1,
                                         'disconnect_rate': 0, 'stall_rate': 0})
        self.assertEqual(parts['kill_rate'], 1)
        self.assertAlmostEqual(factor, (.5 * .5) ** .25)

    def test_zero_reference_station_rate_is_unscored(self):
        factor, parts = aix.battle_factor({'mode': 'battle', 'metrics': {'station_rate': 0}},
                                         {'station_rate': 0})
        self.assertEqual((factor, parts), (1, {}))

    def test_cli_json_and_summary(self):
        server = self.file('server.log', tick())
        rig = self.file('rig.json', json.dumps(RIG))
        output = self.root / 'out.json'
        result = subprocess.run([sys.executable, str(SCRIPT), '--server', str(server),
                                 '--rig', str(rig), '--players', '30', '--json', str(output)],
                                text=True, capture_output=True, check=True)
        self.assertIn('AIX 42', result.stdout)
        self.assertIn('server-only', result.stdout)
        self.assertEqual(json.loads(output.read_text())['players_source'], 'CLI --players')

    def test_invalid_k_rejected(self):
        for value in (0, -1, float('nan'), float('inf')):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.compute(tick(), k=value)

    def test_invalid_core_count_rejected(self):
        rig = dict(RIG, server={'physicalCores': 0})
        with self.assertRaisesRegex(ValueError, 'physicalCores'):
            aix.compute([self.file('server.log', tick())], [], [], rig, players=30)

    def test_power_fit_and_poor_fit_fallback(self):
        fit = aix.fit_exponent([(30, .9), (60, 3.6), (120, 14.4), (240, 57.6)])
        self.assertAlmostEqual(fit['a'], 2)
        self.assertAlmostEqual(fit['k'], .5)
        self.assertAlmostEqual(fit['r_squared'], 1)
        poor = aix.fit_exponent([(30, 10), (60, 1), (120, 10), (240, 1)])
        self.assertFalse(poor['accepted'])
        self.assertEqual(poor['k'], .5)

    def test_score_rows_work_without_bot_logs(self):
        result = self.compute(tick() + '[scoring] ship 1 final score 50 kills=2 deaths=9 baseKills=1.5\n',
                              reference={'kill_rate': 1, 'station_rate': 1})
        self.assertEqual(result['battle']['kills'], 2)
        self.assertEqual(result['battle']['mode'], 'battle')
        self.assertEqual(result['battle']['metrics']['station_rate'], .6)


if __name__ == '__main__':
    unittest.main()
