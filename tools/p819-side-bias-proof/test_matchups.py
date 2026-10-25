import unittest
from matchups import tally


class MatchupTallyTests(unittest.TestCase):
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

    def test_negative_seed_replays_and_empty_log_is_empty(self):
        result = tally(["[matchup] mission #0 match=1 seed=-825 side0=25:Iron Coalition(plan_a)\n"])
        self.assertEqual(-825, result["assembled"][0]["seed"])
        self.assertEqual(0, result["completedMatches"])
        self.assertEqual({}, tally([])["byCiv"])


if __name__ == "__main__":
    unittest.main()
