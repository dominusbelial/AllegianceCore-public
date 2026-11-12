import json
import unittest
from receipts import read_receipts


def line(kind, **values):
    return "[civ-strategy] " + json.dumps(dict(kind=kind, match=7, cookie=3, **values))


def window(sequence=1, final=False, **values):
    return line("window", sequence=sequence, sim=sequence * 60, final=final, grants=[], damage=[],
                sides=[dict(side=0, civ=418, miners=2, oreAboard=12, paydays=1, income=900,
                            ore=20, oreCredits=800, stationKills=0, stationLosses=1)],
                overflowEvents=values.get("overflowEvents", 0), overflowAmount=0)


class ReceiptTests(unittest.TestCase):
    def test_exact_research_times_windows_and_losses_survive(self):
        result = read_receipts(["other log", line("choice", side=0, civ=418, primary=None),
            line("research", side=0, tech=11, sim=17.25), window(), window(2, True)])
        self.assertEqual(len(result), 1)
        match = result[0]
        self.assertTrue(match["complete"])
        self.assertEqual(match["research"][0]["sim"], 17.25)
        self.assertEqual(match["sides"]["0"]["stationLosses"], 2)
        self.assertEqual(match["sides"]["0"]["oreCredits"], 1600)
        self.assertEqual(len(match["sides"]["0"]["samples"]), 2)

    def test_absent_receipts_do_not_invent_activity(self):
        result = read_receipts([line("choice", side=0, primary=None)])[0]
        self.assertFalse(result["complete"])
        self.assertEqual(result["sides"], {})
        self.assertEqual(result["research"], [])

    def test_duplicate_gap_and_unknown_kinds_fail_closed(self):
        for lines in ([window(), window()], [window(2)], [window(1, True), window(2)],
                      [line("choice", side=0), line("choice", side=0)],
                      [line("unexpected")], ["[civ-strategy] {broken"]):
            with self.subTest(lines=lines), self.assertRaises(ValueError):
                read_receipts(lines)

    def test_multiple_missions_and_overflow_are_preserved(self):
        result = read_receipts([window(overflowEvents=17),
            line("choice", side=1).replace('"cookie": 3', '"cookie": 4')])
        self.assertEqual(len(result), 2)
        self.assertEqual(result[0]["overflow_events"], 17)
        self.assertFalse(result[0]["complete"])


if __name__ == "__main__":
    unittest.main()
