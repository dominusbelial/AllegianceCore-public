import unittest
from coverage_receipts import PREFIX, read_server, read_worker
import json


class CoverageTests(unittest.TestCase):
    def window(self, sequence=1, final=False, events=None, overflow=0):
        return PREFIX + json.dumps(dict(match=1, cookie=2, sequence=sequence, final=final,
            events=events or [], reachable=[], overflowEvents=overflow))

    def selection(self, selected=7, covered=1, money=10, strategy=100):
        return PREFIX + (f'cookie=2 epoch=e side=0 pilot=7 stage=selected duty=Fighter hull=1 parts=2 '
            f'coverage=true selected={selected} covered={covered} coverageMoney={money} strategyMoney={strategy} share=0.15')

    def test_stages_and_overflow_remain_distinct(self):
        events = [dict(side=0, stage=s, mechanism="hull", id=1, count=n)
                  for s, n in [("granted", 3), ("launched", 2), ("hit", 1)]]
        result = read_server([self.window(events=events, overflow=4), self.selection(), self.window(2, True)])[0]
        self.assertEqual(result["overflow"], 4)
        self.assertEqual(result["stages"]["0/granted/hull/1"], 3)
        self.assertNotIn("0/completed/hull/1", result["stages"])
        self.assertTrue(result["final"])

    def test_worker_slot_and_resource_bounds(self):
        prefix = [self.selection(i, 0, 0) for i in range(1, 7)]
        self.assertEqual(len(read_worker(prefix + [self.selection()])), 7)
        with self.assertRaises(ValueError): read_worker([self.selection(1, 1)])
        with self.assertRaises(ValueError): read_worker(prefix + [self.selection(money=100)])
        with self.assertRaises(ValueError): read_worker(prefix + [self.selection(), self.selection()])

    def test_windows_fail_closed(self):
        for lines in [[self.window(2)], [self.window(final=True), self.window(2)],
                      [self.window(), self.window()]]:
            with self.assertRaises(ValueError): read_server(lines)

    def test_absence_is_not_failure_or_completion(self):
        self.assertEqual(read_server([]), [])
        result = read_server([self.window(final=True)])[0]
        self.assertEqual(result["stages"], {})
        self.assertEqual(result["reachable"], {})


if __name__ == "__main__": unittest.main()
