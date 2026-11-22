import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest

spec = importlib.util.spec_from_file_location('ws_load_report', Path(__file__).with_name('ws-load-report.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
FIXTURE = Path(__file__).with_name('fixtures') / 'p1072-ws-load.log'


class WsLoadReportTests(unittest.TestCase):
    def test_mixed_fixture_and_histogram_merge(self):
        result = module.report(FIXTURE.read_text().splitlines())
        tcp, ws = (result['transports'][p] for p in ('tcp', 'ws'))
        self.assertEqual((30, 3000, 2, 500), (tcp['sends'], tcp['bytes'], tcp['drops'], tcp['queueBytesHighWater']))
        self.assertEqual((5, 700, 1), (ws['sends'], ws['bytes'], ws['bots']))
        self.assertEqual({'FragmentTimeout': 1, 'IdleTimeout': 2}, ws['disconnects'])
        self.assertEqual(1, ws['reconnects'])
        self.assertEqual(40, ws['updateAgeMs']['p95'])
        self.assertEqual({'p50': 1, 'p95': 5, 'p99': 5}, result['tickMs'])
        self.assertEqual(20, result['tickSamples'])
        self.assertIn('| Metric | TCP | WebSocket |', module.table(result))

    def test_empty_input_reports_missing_samples(self):
        result = module.report(['unrelated log line'])
        self.assertIsNone(result['tickMs']['p50'])
        self.assertIsNone(result['transports']['tcp']['updateAgeMs']['p95'])
        self.assertIn('n/a', module.table(result))

    def test_window_selection_retains_initial_logon_and_excludes_warmup_counts(self):
        lines = FIXTURE.read_text().splitlines()
        result = module.report([*lines, '[comparison-window] start', *lines,
                                '[comparison-window] end', *lines], window_only=True)
        self.assertEqual(3000, result['transports']['tcp']['bytes'])
        self.assertEqual(2, result['transports']['ws']['updateSamples'])
        self.assertEqual(50, result['transports']['ws']['logonMs']['p50'])
        with self.assertRaises(ValueError):
            module.report([], window_only=True)

    def test_cli_json(self):
        result = subprocess.run([sys.executable, str(Path(module.__file__)), '--json', str(FIXTURE)],
                                check=True, text=True, capture_output=True)
        self.assertEqual(3, json.loads(result.stdout)['serverRecords'])

    def test_truncated_or_nonfinite_records_fail_explicitly(self):
        for line in ['[transport-egress] protocol=ws sends=3',
                     '[bot-transport] {"name":"a","protocol":"ws","updateAgeMs":NaN}']:
            with self.assertRaises(ValueError):
                module.report([line])


if __name__ == '__main__':
    unittest.main()
