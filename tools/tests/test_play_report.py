import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('play_report', ROOT / 'play-report.py')
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)
FIXTURE = Path(__file__).parent / 'fixtures/p1071-plays.jsonl'


class PlayReportTests(unittest.TestCase):
    def test_fixture_totals_and_iso_year(self):
        data = report.aggregate(report.read_records([FIXTURE]))
        total = data['total']
        self.assertEqual((2, 1, 1, 3), tuple(total[k] for k in ('unique_callsigns', 'guests', 'accounts', 'sessions')))
        self.assertEqual((10, 20, 1), tuple(total[k] for k in ('median_minutes', 'p90_minutes', 'returning_players')))
        self.assertEqual((2, 1, 2, 1, 1), tuple(total[k] for k in ('browser', 'desktop', 'launched', 'only_joined', 'matches_with_humans')))
        self.assertEqual(['2026-W01'], list(data['weeks']))
        self.assertEqual(1, data['weeks']['2026-W01']['returning_players'])
        self.assertEqual(0, data['days']['2025-12-29']['returning_players'])
        self.assertEqual(1, data['days']['2025-12-30']['returning_players'])
        self.assertEqual(0, data['days']['2026-01-02']['sessions'])

    def test_json_cli_deduplicates_files_and_text_has_fields(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, report.main([str(FIXTURE), str(FIXTURE), '--json']))
        self.assertEqual(3, json.loads(out.getvalue())['total']['sessions'])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, report.main([str(FIXTURE)]))
        self.assertIn('matches_with_humans=1', out.getvalue())
        self.assertIn('returning_players=', out.getvalue())

    def test_invalid_line_reports_location_and_empty_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'bad.jsonl'
            path.write_text('not json\n')
            with self.assertRaisesRegex(ValueError, 'bad.jsonl:1:'):
                list(report.read_records([path]))
            path.write_text('')
            self.assertEqual(0, report.aggregate(report.read_records([path]))['total']['sessions'])


if __name__ == '__main__':
    unittest.main()
