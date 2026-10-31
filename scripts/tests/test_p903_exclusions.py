"""The supplemental source audit must preserve the original exclusion gate."""
import contextlib
import importlib.util
import io
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("na_audit", ROOT / "tools/p868-na-audit.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class SupplementalAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        target = self.root / "docs/parity"
        target.mkdir(parents=True)
        for name in (*audit.MATRICES, "p868-na-audit.csv", *audit.SUPPLEMENTAL_AUDITS):
            shutil.copyfile(ROOT / "docs/parity" / name, target / name)

    def validate(self):
        with contextlib.redirect_stdout(io.StringIO()):
            return audit.validate(self.root)

    def test_supplemental_dead_ballot_keeps_all_original_exclusions(self):
        rows = self.validate()
        self.assertEqual(200, len(rows))
        self.assertEqual(1, sum(r["row_id"] == "SIM-0821" for r in rows))

    def test_dead_ballot_requires_a_supplemental_source_verdict(self):
        path = self.root / "docs/parity/p903-exclusions.csv"
        path.write_text(path.read_text().splitlines()[0] + "\n")
        with self.assertRaisesRegex(ValueError, "invalid supplemental audit"):
            self.validate()

    def test_another_unclassified_exclusion_is_still_rejected(self):
        path = self.root / "docs/parity/sim-rules-matrix.csv"
        text = path.read_text()
        # A production GotoPlan row is outside the supplemental exclusion.
        lines = text.splitlines()
        import csv
        rows = list(csv.reader(lines))
        status = rows[0].index("port_status")
        next(r for r in rows if r[0] == "SIM-0475")[status] = "n/a"
        with path.open("w", newline="") as dest:
            csv.writer(dest, lineterminator="\n").writerows(rows)
        with self.assertRaisesRegex(ValueError, "unclassified current exclusion"):
            self.validate()


if __name__ == "__main__":
    unittest.main()
