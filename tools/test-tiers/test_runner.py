import unittest
from pathlib import Path
import tempfile
import argparse
from contextlib import redirect_stdout
import io
from runner import check_coverage, duration, timing_offenders, read_trx, Runner, PROJECTS


class GuardTests(unittest.TestCase):
    Tier = "Fast"  # Infrastructure checks; outside the four game assemblies.
    def test_duration_including_hours(self):
        self.assertEqual(duration("01:02:03.500"), 3723.5)

    def test_scaled_fast_limit_and_live_perf_exemption(self):
        inventory = [dict(name="unit", tier="Fast"), dict(name="match", tier="Live"), dict(name="budget", tier="Perf")]
        rows = [dict(name="unit", seconds=12), dict(name="match", seconds=200), dict(name="budget", seconds=40)]
        self.assertEqual(timing_offenders(rows, inventory, 10), [rows[0]])
        self.assertEqual(timing_offenders(rows, inventory, 15), [])

    def test_guard_includes_skipped_and_failed_test_time(self):
        for outcome in ("Passed", "Failed", "NotExecuted"):
            row = dict(name="unit", seconds=11, outcome=outcome)
            self.assertEqual(timing_offenders([row], [dict(name="unit", tier="Fast")], 10), [row])

    def test_unknown_test_fails_closed(self):
        with self.assertRaises(ValueError):
            timing_offenders([dict(name="unknown", seconds=1)], [], 10)

    def test_ambiguous_tiers_fail_closed(self):
        inventory = [dict(name="same", tier="Fast"), dict(name="same", tier="Live")]
        with self.assertRaises(ValueError):
            timing_offenders([], inventory, 10)

    def test_trx_keeps_distinct_rows_with_the_same_adapter_test_id(self):
        xml = '''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <TestDefinitions><UnitTest id="shared"><TestMethod className="Example" name="Theory" /></UnitTest></TestDefinitions>
          <Results>
            <UnitTestResult testId="shared" executionId="one" testName="truncated" outcome="Passed" duration="00:00:01" />
            <UnitTestResult testId="shared" executionId="two" testName="truncated" outcome="Passed" duration="00:00:02" />
          </Results></TestRun>'''
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.trx"
            path.write_text(xml)
            self.assertEqual([r["seconds"] for r in read_trx(path)], [1, 2])
            path.write_text(xml.replace('executionId="two"', 'executionId="one"'))
            with self.assertRaises(ValueError):
                read_trx(path)

    def test_slow_fast_case_fails_the_tier_and_is_listed_in_its_summary(self):
        with tempfile.TemporaryDirectory() as directory:
            runner = Runner(argparse.Namespace(results_dir=str(Path(directory) / "results"), parallel=4))
            case = dict(name="slow", tier="Fast", traits={})
            result = dict(name="slow", seconds=11, outcome="Passed", method="Fixture.Slow")
            runner.inventory = {name: [case] if name == PROJECTS[0] else [] for name in PROJECTS}
            runner.expected = runner.inventory
            runner.calibrate = lambda path: setattr(runner, "calibration", dict(fastLimitSeconds=10))
            runner.run_project = lambda name, tier, lane, path: ([result] if name == PROJECTS[0] and lane == "main" else [], 0)
            with redirect_stdout(io.StringIO()):
                self.assertFalse(runner.run_tier("fast"))
            summary = (runner.output / "fast/summary.txt").read_text()
            self.assertIn("SLOW FAST: 11.000s slow", summary)
            self.assertIn("FAIL", summary)

    def test_quarantine_retry_cannot_bypass_the_timing_guard(self):
        with tempfile.TemporaryDirectory() as directory:
            runner = Runner(argparse.Namespace(results_dir=str(Path(directory) / "results"), parallel=4))
            case = dict(name="flaky", tier="Fast", traits={"Lane": ["LiveTiming"]})
            failed = dict(name="flaky", seconds=1, outcome="Failed", method="Fixture.Flaky")
            retry = dict(name="flaky", seconds=11, outcome="Passed", method="Fixture.Flaky")
            runner.inventory = {name: [case] if name == PROJECTS[0] else [] for name in PROJECTS}
            runner.expected = runner.inventory
            runner.calibrate = lambda path: setattr(runner, "calibration", dict(fastLimitSeconds=10))
            def project(name, tier, lane, path, selection=None):
                if name != PROJECTS[0] or lane == "main":
                    return [], 0
                return ([retry], 0) if selection else ([failed], 1)
            runner.run_project = project
            with redirect_stdout(io.StringIO()):
                self.assertFalse(runner.run_tier("fast"))
            summary = runner.summaries["fast"]
            self.assertEqual(summary["quarantineFlakes"], ["Fixture.Flaky"])
            self.assertEqual(summary["timingOffenders"], [retry])

    def test_union_must_preserve_counts_and_have_no_duplicates(self):
        check_coverage(["a", "b"], ["b", "a"])
        # xUnit truncates long theory arguments; distinct rows can share a name.
        check_coverage(["truncated", "truncated"], ["truncated", "truncated"])
        for actual in (["a"], ["a", "b", "b"], ["a", "other"]):
            with self.assertRaises(ValueError):
                check_coverage(["a", "b"], actual)


if __name__ == "__main__":
    unittest.main()
