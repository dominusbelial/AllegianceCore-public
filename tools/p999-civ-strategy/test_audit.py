import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("p999_audit", Path(__file__).with_name("audit.py"))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class AuditTests(unittest.TestCase):
    def test_only_finalized_matches_count_and_all_side_counts_survive(self):
        lines = [
            "[matchup] match=1 seed=7 side0=18:Gigacorp(generated) side1=4:Phoenix(generated) side2=5:Omicron(generated) side3=25:Iron Coalition(plan_a) home0=0 home1=2",
            "[finale] deadline winner side=3 by=station-kills values=0:stations=1/kills=4/deaths=5,1:stations=2/kills=6/deaths=7,2:stations=0/kills=1/deaths=2,3:stations=3/kills=9/deaths=1",
            "[gameover] iSideWinner=3, 220 peer(s); match=1",
            "[matchup] match=2 seed=8 side0=4:Phoenix(generated) side1=18:Gigacorp(generated)",
            "match 1 side 3 station-kills 3 research 8 ship-kills 10",
            "[kills] match 1 side 3 Delta: podded gun 2, mine 3; killed gun 1, oxygen 4",
        ]
        rows, rosters = audit.parse_summary(lines, 83)
        self.assertEqual(len(rows), 4)
        self.assertEqual(len(rosters), 2)
        winner = next(r for r in rows if r["win"])
        self.assertEqual((winner["civ"],winner["sides"],winner["oxygen"],winner["podded"]), (25,4,4,5))
        self.assertEqual(winner["opponents"], "4/5/18")
        self.assertEqual((winner["kills"],winner["deaths"],winner["research"]), (9,1,8))

    def test_draw_is_preserved_and_missing_score_is_unknown(self):
        rows, _ = audit.parse_summary([
            "[matchup] match=1 seed=4 side0=4:Phoenix(generated) side1=18:Gigacorp(generated)",
            "[gameover] iSideWinner=-1, 8 peer(s); match=1"], 1)
        self.assertEqual(sum(r["win"] for r in rows), 0)
        self.assertTrue(all(r["kills"] is None for r in rows))
        self.assertEqual(audit.aggregate(rows,["civ"])[0]["score_n"], 0)

    def test_duplicate_results_are_rejected(self):
        lines = ["[matchup] match=1 seed=4 side0=4:Phoenix(generated)", "[gameover] iSideWinner=0, 8 peer(s); match=1"]
        with self.assertRaisesRegex(ValueError, "duplicate"):
            audit.parse_summary(lines+[lines[-1]], 1)

    def test_wilson_handles_zero_all_and_empty(self):
        self.assertIsNone(audit.wilson(0,0))
        self.assertAlmostEqual(audit.wilson(0,8)[0], 0)
        self.assertAlmostEqual(audit.wilson(8,8)[1], 1)
        lo, hi = audit.wilson(25,40)
        self.assertLess(lo, .625)
        self.assertGreater(hi, .625)

    def test_research_uses_bounded_worker_clock_and_filters_stale_civ(self):
        core = dict(civs=[dict(civ=dict(civilizationId=18),initial=[dict(kind="hull",id=210)],baseline=dict(steps=[{},{}]))])
        roster = {1:dict(sides={0:18})}
        lines = [
            "[loadout] mcp-w1-b001-1 initial: role=Fighter; human hull 210 'Scout'; Weapon@0=33 'Gun' x1",
            "[intel] t=20s side 0 home=0",
            "[research] side=0 plan=generated step=1/2 develop 'Hull' progress=1.0% wallet=9 eta=1s techs=[] stations=[Garrison] status=building bucket",
            "[research] side=0 plan=generated step=2/2 develop 'Gun' progress=2.0% wallet=10 eta=1s techs=[Hull] stations=[Garrison] status=building bucket",
            "[research] side=0 plan=generated step=1/3 develop 'Wrong civ' progress=1.0% wallet=10 eta=1s techs=[Wrong] stations=[Garrison] status=building bucket",
            "[intel] t=30s side 0 home=0",
        ]
        result = audit.worker_evidence(lines,112,roster,core)
        self.assertEqual(result["initial_bots"], {0:1})
        hull = next(r for r in result["research"] if r["kind"] == "tech")
        self.assertEqual((hull["name"],hull["lo"],hull["hi"]), ("Hull",20,30))
        self.assertFalse(any(r["name"] == "Wrong" for r in result["research"]))

    def test_per_bot_combat_denominator_excludes_missing_score_rows(self):
        common=dict(civ=18,win=0,research=3,station_kills=1,oxygen=2,podded=4)
        rows=[common|dict(kills=10,deaths=5,bot_slots=10),common|dict(kills=None,deaths=None,bot_slots=90)]
        result=audit.aggregate(rows,["civ"])[0]
        self.assertEqual((result["score_n"],result["scored_bot_slots"],result["bot_slots"]),(1,10,100))
        self.assertEqual(result["kills_per_bot"],1)
        self.assertEqual(result["research_per_bot"],.06)

    def test_research_time_bounds_do_not_cross_a_rotation(self):
        core=dict(civs=[dict(civ=dict(civilizationId=18),initial=[],baseline=dict(steps=[{}]))])
        roster={1:dict(sides={0:18}),2:dict(sides={0:18})}
        lines=["[intel] t=20s side 0 home=0",
            "[research] side=0 plan=generated step=1/1 develop 'Hull' progress=1.0% wallet=9 eta=1s techs=[Hull] stations=[Garrison] status=complete",
            "[battle-sector] cookie=1 generation=2 sector=1", "[intel] t=500s side 0 home=0"]
        result=audit.worker_evidence(lines,112,roster,core)
        self.assertTrue(all(r["lo"] is None and r["hi"] is None for r in result["research"]))


if __name__ == "__main__":
    unittest.main()
