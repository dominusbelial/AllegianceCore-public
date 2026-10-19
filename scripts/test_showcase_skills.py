import csv
import importlib.util
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('skills', ROOT/'scripts/showcase-skills.py')
skills = importlib.util.module_from_spec(spec)
spec.loader.exec_module(skills)


class SkillsReportTests(unittest.TestCase):
    def test_catalogue_matches_server_schema_and_uses_lf(self):
        raw = skills.CATALOGUE.read_bytes()
        self.assertNotIn(b'\r', raw)
        with skills.CATALOGUE.open() as stream:
            catalogue = list(csv.DictReader(stream))
        source = (ROOT/'src/Allegiance.Core/Model/SkillTelemetry.cs').read_text()
        pairs = re.findall(r'\("([a-z-]+)", (\d+)\)', source)
        self.assertEqual(pairs, [(c['id'], c['minimum']) for c in catalogue])
        self.assertTrue(all(c['native_source'] and c['detection'] for c in catalogue))

    def test_final_wins_without_summing_snapshots(self):
        log = ['time [skills] match 1 side 0 snapshot', '[skills] match 1 side 0: guns 1',
               '[skills] match 1 side 0 final', '[skills] match 1 side 0: guns 3',
               '[skills] match 1 side 0 snapshot', '[skills] match 1 side 0: guns 999']
        row, = skills.parse(log, [dict(id='guns', minimum='3')])
        self.assertEqual(('final', 3, 1), (row['state'], row['counts']['guns'], row['covered']))

    def test_sides_and_matches_are_separate_and_zero_under_minimum_are_listed(self):
        rows = skills.parse(['[skills] match 1 side 0: guns 0', '[skills] match 1 side 1: guns 2',
                             '[skills] match 2 side 0: guns 4'], [dict(id='guns', minimum='3')])
        self.assertEqual([0, 0, 1], [r['covered'] for r in rows])
        self.assertIn('guns 0/3', skills.markdown(rows))
        self.assertIn('guns 2/3', skills.markdown(rows))

    def test_incomplete_and_malformed_blocks_are_visible(self):
        row, = skills.parse(['[skills] match 2 side 0: guns 3'], [dict(id='guns', minimum='3'), dict(id='cloak', minimum='1')])
        self.assertEqual(['cloak'], row['missing'])
        self.assertEqual(2, row['total'])
        self.assertIn('incomplete', skills.markdown([row]))
        with self.assertRaises(ValueError):
            skills.parse(['[skills] match 2 side 0: guns'], [])


if __name__ == '__main__':
    unittest.main()
