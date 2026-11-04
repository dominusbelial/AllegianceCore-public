import unittest
from matchups import tally


class MatchupTallyTests(unittest.TestCase):
    def test_home_trailer_preserves_last_civ_and_tallies_winning_home(self):
        result = tally([
            "[matchup] mission #0 match=2 seed=924 side0=18:Gigacorp(generated) side1=4:Phoenix(generated) homeSwap=alternate homeMatch=2 homes=1,0 home0=1051 home1=1050",
            "[gameover] mission #0 -> Stage Over, iSideWinner=1; match=2 seed=924 side0=18:Gigacorp(generated) side1=4:Phoenix(generated) homeSwap=alternate homeMatch=2 homes=1,0 home0=1051 home1=1050",
        ])
        self.assertEqual(2, len(result['assembled'][0]['sides']))
        self.assertEqual({'18,4': 1}, result['completedMatchups'])
        self.assertEqual(1, result['byCiv']['4']['wins'])
        self.assertEqual(1, result['byAuthoredHome']['0']['wins'])
        self.assertEqual(1, result['byAuthoredHome']['1']['losses'])
        self.assertEqual({'0': '1051', '1': '1050'}, result['completed'][0]['homeSectors'])
        self.assertEqual({}, tally(["[gameover] mission #0 -> Stage Over, iSideWinner=1; match=1 seed=1 side0=18:Gigacorp(generated) side1=4:Phoenix(generated)"])['byAuthoredHome'])

    def test_interleaved_missions_actual_civs_draws_and_three_sides(self):
        result = tally([
            "[matchup] mission #0 match=1 seed=825 side0=25:Iron Coalition(plan_a) side1=3:Ga'Taraan(plan_a)",
            "[matchup] mission #1 match=2 seed=7 side0=1:One(generated) side1=2:Two(generated) side2=4:Four(generated)",
            # Civs can change in the lobby: winner attribution uses GAME_OVER.
            "[gameover] mission #0 -> Stage Over, iSideWinner=1, 0 peer(s), result packet 50 bytes; match=1 seed=825 side0=25:Iron Coalition(plan_a) side1=2:Two(generated)",
            "[gameover] mission #1 -> Stage Over, iSideWinner=NA(draw), 0 peer(s); match=2 seed=7 side0=1:One(generated) side1=2:Two(generated) side2=4:Four(generated)",
            "[gameover] P439: mission #0 finished -> rebuilt fresh STAGE_NOTSTARTED",
            "[gameover] mission #0 -> Stage Over, iSideWinner=0, 0 peer(s)",
        ])
        self.assertEqual(2, result["completedMatches"])
        self.assertEqual(2, len(result["assembled"]))
        self.assertNotIn("3", result["byCiv"])
        self.assertEqual({"name": "Two", "appearances": 2, "wins": 1, "losses": 0, "draws": 1}, result["byCiv"]["2"])
        self.assertEqual(1, result["byPosition"]["1"]["wins"])
        self.assertEqual(1, result["byPosition"]["2"]["draws"])
        self.assertEqual(1, result["byCiv"]["25"]["losses"])
        self.assertEqual({"25,2": 1, "1,2,4": 1}, result["completedMatchups"])

    def test_map_receipts_keep_last_civ_and_group_completed_results_by_map(self):
        lines = [
            "[matchup] mission #0 match=1 seed=926 side0=25:Iron Coalition(plan_a) side1=3:Ga'Taraan(plan_a) homeSwap=off homes=0,1 map=gen:SingleRing mapSeed=123 rotation=cycle#1",
            "[gameover] mission #0 -> Stage Over, iSideWinner=1; match=1 seed=926 side0=25:Iron Coalition(plan_a) side1=3:Ga'Taraan(plan_a) homeSwap=off homes=0,1 map=gen:SingleRing mapSeed=123 rotation=cycle#1",
            "[gameover] mission #0 -> Stage Over, iSideWinner=NA(draw); match=2 seed=8 side0=25:Iron Coalition(plan_a) side1=3:Ga'Taraan(plan_a) homeSwap=off homes=0,1 map=m2xspiderw.igc mapSeed=9 rotation=random#0",
        ]
        result = tally(lines)
        self.assertEqual(2, len(result['assembled'][0]['sides']))
        self.assertEqual(123, result['completed'][0]['mapSeed'])
        self.assertEqual({'matches': 1, 'draws': 0, 'winsBySide': {'1': 1}}, result['byMap']['gen:SingleRing'])
        self.assertEqual(1, result['byMap']['m2xspiderw.igc']['draws'])
        self.assertEqual({}, tally([])['byMap'])

    def test_three_factions_rotation_winner_home_and_partial_placement(self):
        lines = [
            "[gameover] mission #0 -> Stage Over, iSideWinner=2; match=1 seed=927 side0=18:Gigacorp(generated) side1=4:Phoenix(generated) side2=5:Omicron(generated) homeSwap=off homes=0,1,2 home0=0 home1=2 home2=4 map=gen:DiamondRing mapSeed=12 rotation=cycle#0",
            "[gameover] mission #1 -> Stage Over, iSideWinner=0; match=1 seed=928 side0=18:Gigacorp(generated) side1=4:Phoenix(generated) side2=5:Omicron(generated) map=gen:SingleRing mapSeed=13 rotation=cycle#1",
        ]
        result = tally(lines)
        self.assertEqual(3, len(result['completed'][0]['sides']))
        self.assertEqual(3, len(result['completed'][1]['sides']))
        self.assertEqual(1, result['byCiv']['5']['wins'])
        self.assertEqual(1, result['byPosition']['2']['wins'])
        self.assertEqual(1, result['byAuthoredHome']['2']['wins'])
        self.assertEqual({'matches': 1, 'draws': 0, 'winsBySide': {'2': 1}}, result['byMap']['gen:DiamondRing'])
        self.assertEqual([2], result['completed'][0]['placementOrder'])
        self.assertEqual([0, 1], result['completed'][0]['unrankedSides'])

    def test_negative_seed_replays_and_empty_log_is_empty(self):
        result = tally(["[matchup] mission #0 match=1 seed=-825 side0=25:Iron Coalition(plan_a)\n"])
        self.assertEqual(-825, result["assembled"][0]["seed"])
        self.assertEqual(0, result["completedMatches"])
        self.assertEqual({}, tally([])["byCiv"])


if __name__ == "__main__":
    unittest.main()
