#!/usr/bin/env python3
"""P873 authoring behavior and matrix validation regression checks."""
import csv
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def module(path, name):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


tools = module('tools/p873-training-tools.py', 'training_tools')
scorecard = module('scripts/parity-scorecard.py', 'scorecard')


class TrainingToolsTests(unittest.TestCase):
    def test_native_grid_rounds_count_and_preserves_rectangular_y_bug(self):
        rows = [line.split('\t') for line in tools.asteroids(11, 873)]
        self.assertEqual(9, len(rows))
        self.assertEqual(list(range(9)), [int(r[2]) for r in rows])
        self.assertEqual(rows, [line.split('\t') for line in tools.asteroids(11, 873)])
        # count 3 => width 1 height 3; native Y uses width rather than height.
        rectangular = [line.split('\t') for line in tools.asteroids(3, 873)]
        self.assertGreater(float(rectangular[2][13]), 15000)
        for row in rows:
            self.assertTrue(150 <= int(row[6]) <= 350)
            self.assertTrue(-1000 <= float(row[14]) <= 1000)
            self.assertEqual(26, len(row))

    def test_invalid_native_count_fails_instead_of_dividing_by_zero(self):
        with self.assertRaises(ValueError):
            list(tools.asteroids(0, 873))

    def test_native_comment_format_keeps_comma_in_description(self):
        self.assertEqual(['\t\t// tm_2_01', '\t\t// "hello, cadet"', ''],
                         list(tools.format_comments([' tm_2_01 ,"hello, cadet"\n'])))
        self.assertEqual(3, len(list(tools.format_comments(['id,' + 'x' * 70]))))

    def test_registered_matrix_is_valid_and_partial_gets_no_credit(self):
        self.assertIn('training-matrix.csv', scorecard.MATRICES)
        rows = scorecard.read_matrix(ROOT / 'docs/parity/training-matrix.csv')
        credit = scorecard.score(rows)
        self.assertGreater(credit[1], credit[0])
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'training-matrix.csv'
            header = list(rows[0])
            for key, value in [('weight', '6'), ('row_id', 'SIM-1'), ('native_rule', '')]:
                row = rows[0] | {key: value}
                with target.open('w', newline='') as out:
                    writer = csv.DictWriter(out, fieldnames=header)
                    writer.writeheader()
                    writer.writerow(row)
                with self.assertRaises(ValueError):
                    scorecard.read_matrix(target)
            with target.open('w', newline='') as out:
                csv.writer(out).writerow(header[:-1])
            with self.assertRaises(ValueError):
                scorecard.read_matrix(target)


if __name__ == '__main__':
    unittest.main()
