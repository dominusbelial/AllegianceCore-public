import contextlib
import importlib.util
import io
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('bench_compare', Path(__file__).with_name('bench-compare.py'))
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class CompareTests(unittest.TestCase):
    def result(self, fps=60, tick=10, mode='skirmish'):
        return dict(SchemaVersion=1, Mode=mode, Preset='Small', Seed=729, Map='map', Civs=[25, 3],
                    Median=dict(Rendered=True, FpsAverage=fps, ServerTickMs=dict(P95=tick)))

    def test_regression_direction_and_threshold(self):
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, bench.compare(self.result(), self.result(70, 9), 5))
            self.assertEqual(1, bench.compare(self.result(), self.result(50), 5))
            self.assertEqual(1, bench.compare(self.result(), self.result(tick=15), 5))

    def test_missing_gpu_is_unavailable(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(0, bench.compare(self.result(), self.result(), 0))
        self.assertIn('GPU ms                   unavailable', output.getvalue())

    def test_incompatible_workload_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'Mode'):
            bench.compare(self.result(), self.result(mode='replay'))
