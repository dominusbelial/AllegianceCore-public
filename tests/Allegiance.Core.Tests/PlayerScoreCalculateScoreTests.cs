using PlayerScore = global::Allegiance.Server.PlayerScore;

namespace Allegiance.Core.Tests;

/// <summary>
/// P583 (native player scoring, Stage 2) — pure, server-free unit tests of the
/// full <c>PlayerScoreObject</c> port: the complete <c>CalculateScore</c>
/// formula (<c>Igc/common.cpp:2667-2695</c>) against hand-computed native
/// values (including the <c>kMax</c> diminishing-returns terms and the win ×2),
/// and one test per counter-feeding method (rescue → +1 rescue → +5 points,
/// and so on). Each test FAILS on the pre-P583 code (which had none of these
/// counters) and PASSES after.
/// </summary>
public class PlayerScoreCalculateScoreTests
{
    /// <summary>PCore15's shipped <c>c_points*</c> values (missionigc.cpp:
    /// 2704-2716) placed at their <c>c_fcidPoints*</c> indices
    /// (<c>igc.h:77-93</c>) in a 30-element <c>FloatConstants</c> array, so the
    /// <c>CalculateScore</c> math reads exactly what a real loaded core hands
    /// the server.</summary>
    private static readonly float[] Fc = BuildFloatConstants();

    private static float[] BuildFloatConstants()
    {
        var fc = new float[30];
        fc[PlayerScore.PointsWarpIndex] = 2.0f;        // c_pointsWarp
        fc[PlayerScore.PointsAsteroidIndex] = 1.0f;    // c_pointsAsteroid
        fc[PlayerScore.PointsTechIndex] = 5.0f;        // c_pointsTech
        fc[PlayerScore.PointsMinerIndex] = 10.0f;      // c_pointsMiner
        fc[PlayerScore.PointsBuilderIndex] = 20.0f;    // c_pointsBuilder
        fc[PlayerScore.PointsLayerIndex] = 10.0f;      // c_pointsLayer
        fc[PlayerScore.PointsCarrierIndex] = 10.0f;    // c_pointsCarrier
        fc[PlayerScore.PointsPlayerIndex] = 10.0f;     // c_pointsPlayer
        fc[PlayerScore.PointsBaseKillIndex] = 25.0f;   // c_pointsBaseKill
        fc[PlayerScore.PointsBaseCaptureIndex] = 25.0f;// c_pointsBaseCapture
        fc[PlayerScore.PointsFlagsIndex] = 10.0f;      // c_pointsFlags
        fc[PlayerScore.PointsArtifactsIndex] = 10.0f;  // c_pointsArtifacts
        fc[PlayerScore.PointsRescuesIndex] = 5.0f;     // c_pointsRescues
        fc[PlayerScore.CombatRatingAddIndex] = 1.25f;  // c_crAdd
        fc[PlayerScore.CombatRatingDivideIndex] = 100f;// c_crDivide
        return fc;
    }

    [Fact]
    public void Calculate_score_sums_the_linear_terms()
    {
        // kMax = 900/900 = 1.0; only the purely-linear terms are non-zero:
        // 3 warps (2) + 4 asteroids (1) = 6 + 4 = 10. No kMax class-kill terms.
        var ps = new PlayerScore
        {
            WarpsSpotted = 3,
            AsteroidsSpotted = 4,
            TimePlayed = 900f,     // exactly one 15-minute block → kMax = 1
        };
        ps.CalculateScore(Fc);
        Assert.Equal(10f, ps.Score, 5);
    }

    /// <summary>⭐ The <c>kMax</c> diminishing-returns term: a single class's
    /// kill score is <c>(c·kMax)·Points/(c+kMax)</c>, not <c>c·Points</c>. With
    /// kMax=1 and 10 miner kills at 10 pts: (10·1)·10/(10+1) = 100/11 ≈ 9.09 —
    /// less than the naive 100, exactly the diminishing shape native intends.</summary>
    [Fact]
    public void Calculate_score_applies_the_kmax_diminishing_term_for_class_kills()
    {
        var ps = new PlayerScore { MinerKills = 10f, TimePlayed = 900f };
        ps.CalculateScore(Fc);
        Assert.Equal(100f / 11f, ps.Score, 5);   // ≈ 9.0909
    }

    /// <summary>⭐ A full mix exercising every term at once, compared against
    /// the hand-derived rational 5426/33 ≈ 164.4242 (see the P583 report's
    /// worked example). Every class counter, base kill/capture, rescue,
    /// artifact, flag, high-value spot and repair contributes.</summary>
    [Fact]
    public void Calculate_score_full_mix_matches_hand_computed_native_value()
    {
        var ps = new PlayerScore
        {
            WarpsSpotted = 3,
            AsteroidsSpotted = 4,
            TechsRecovered = 1,
            MinerKills = 10f,
            BuilderKills = 5f,
            PlayerKills = 2f,
            BaseKills = 4f,
            BaseCaptures = 2f,
            Rescues = 2,
            Artifacts = 1,
            Flags = 1,
            HighValueTargetsSpotted = 3,
            Repair = 5f,
            TimePlayed = 900f,
        };
        ps.CalculateScore(Fc);
        Assert.Equal(5426f / 33f, ps.Score, 4);   // ≈ 164.4242
    }

    /// <summary>⭐ Winning doubles the score (<c>common.cpp:2691-2692</c>):
    /// the 10-point linear case becomes 20.</summary>
    [Fact]
    public void Calculate_score_doubles_when_the_side_wins()
    {
        var ps = new PlayerScore
        {
            WarpsSpotted = 3,
            AsteroidsSpotted = 4,
            TimePlayed = 900f,
            Win = true,
        };
        ps.CalculateScore(Fc);
        Assert.Equal(20f, ps.Score, 5);
    }

    /// <summary>⭐ The rank ratio multiplies the (already ×2) score
    /// (<c>common.cpp:2694</c>): 10 → ×2 (win) → ×1.5 (ratio) = 30.</summary>
    [Fact]
    public void Calculate_score_multiplies_by_rank_ratio_after_win_double()
    {
        var ps = new PlayerScore
        {
            WarpsSpotted = 3,
            AsteroidsSpotted = 4,
            TimePlayed = 900f,
            Win = true,
        };
        ps.SetRankRatio(1.5f);
        ps.CalculateScore(Fc);
        Assert.Equal(30f, ps.Score, 5);
    }

    /// <summary>⭐ Native returns early (leaving the score untouched) when
    /// <c>m_dtPlayed == 0</c> (<c>common.cpp:2669-2670</c>).</summary>
    [Fact]
    public void Calculate_score_leaves_score_untouched_when_no_time_played()
    {
        var ps = new PlayerScore { WarpsSpotted = 3, TimePlayed = 0f };
        ps.CalculateScore(Fc);
        Assert.Equal(0f, ps.Score);
    }

    /// <summary>⭐ A rescue feeds the counter and is worth exactly
    /// <c>c_fcidPointsRescues</c> (5 on PCore15): <c>AddRescue()</c> → 1 rescue →
    /// 5 points (with kMax=1).</summary>
    [Fact]
    public void Rescue_feeds_counter_and_worth_five_points()
    {
        var ps = new PlayerScore();
        ps.AddRescue();
        Assert.Equal(1, ps.Rescues);
        ps.TimePlayed = 900f;
        ps.CalculateScore(Fc);
        Assert.Equal(5f, ps.Score, 5);
    }

    [Fact]
    public void Add_ejection_increments_the_counter()
    {
        var ps = new PlayerScore();
        ps.AddEjection(); ps.AddEjection();
        Assert.Equal(2, ps.Ejections);
    }

    [Fact]
    public void Add_techs_recovered_increments_the_counter()
    {
        var ps = new PlayerScore();
        ps.AddTechsRecovered();
        Assert.Equal(1, ps.TechsRecovered);
    }

    [Fact]
    public void Add_flag_and_add_artifact_increment_their_counters()
    {
        var ps = new PlayerScore();
        ps.AddFlag(); ps.AddArtifact();
        Assert.Equal(1, ps.Flags);
        Assert.Equal(1, ps.Artifacts);
    }

    [Fact]
    public void Spot_warp_and_special_asteroid_increment_their_counters()
    {
        var ps = new PlayerScore();
        ps.SpotWarp(); ps.SpotSpecialAsteroid();
        Assert.Equal(1, ps.WarpsSpotted);
        Assert.Equal(1, ps.AsteroidsSpotted);
    }

    [Fact]
    public void Add_target_spot_counts_high_value_targets()
    {
        var ps = new PlayerScore();
        ps.AddTargetSpot(); ps.AddTargetSpot(); ps.AddTargetSpot();
        Assert.Equal(3, ps.HighValueTargetsSpotted);
    }

    /// <summary>⭐ <c>KillShip</c> buckets a kill into the victim's class
    /// counter (<c>igc.h:5675-5706</c>): a miner victim raises MinerKills and
    /// feeds the kMax miner term, worth 10 pts each (here 1 kill → 100/11·1
    /// with kMax=1 … i.e. the single-kill diminishing value ≈ 4.545).</summary>
    [Fact]
    public void Kill_ship_buckets_miner_kills_into_the_miner_counter()
    {
        var ps = new PlayerScore();
        ps.KillShip(PlayerScore.VictimClass.Miner, 1.0f);
        Assert.Equal(1f, ps.MinerKills);
        ps.TimePlayed = 900f;
        ps.CalculateScore(Fc);
        Assert.Equal(10f * 1f / 2f, ps.Score, 5);   // (1·1)·10/(1+1) = 5
    }

    [Fact]
    public void Kill_ship_buckets_builder_layer_carrier_and_player()
    {
        var ps = new PlayerScore();
        ps.KillShip(PlayerScore.VictimClass.Builder, 1.0f);
        ps.KillShip(PlayerScore.VictimClass.Layer, 0.5f);
        ps.KillShip(PlayerScore.VictimClass.Carrier, 1.0f);
        ps.KillShip(PlayerScore.VictimClass.Player, 2.0f);
        Assert.Equal(1f, ps.BuilderKills);
        Assert.Equal(0.5f, ps.LayerKills);
        Assert.Equal(1f, ps.CarrierKills);
        Assert.Equal(2f, ps.PlayerKills);
    }

    /// <summary>⭐ A pilot's base kill counts 1.0, a child's 0.5
    /// (<c>igc.h:5708-5717</c>); base kills feed the kMax base-kill term.</summary>
    [Fact]
    public void Kill_base_and_capture_base_credit_pilot_vs_child_differently()
    {
        var ps = new PlayerScore();
        ps.KillBase(bPilot: true);
        ps.CaptureBase(bPilot: true);
        ps.KillBase(bPilot: false);
        Assert.Equal(1.5f, ps.BaseKills);
        Assert.Equal(1f, ps.BaseCaptures);
    }

    /// <summary>⭐ SetRepair records the ship's repair credit and is worth ·5
    /// points (<c>common.cpp:2689</c>, <c>igc.h:5664-5667</c>).</summary>
    [Fact]
    public void Set_repair_feeds_repair_points()
    {
        var ps = new PlayerScore();
        ps.SetRepair(3f);
        Assert.Equal(3f, ps.Repair);
        ps.TimePlayed = 900f;
        ps.CalculateScore(Fc);
        Assert.Equal(15f, ps.Score, 5);
    }

    /// <summary>⭐ Assists default to 0 and a fresh score defaults to the native
    /// neutral seeds (<c>igc.h:5510,5571</c>).</summary>
    [Fact]
    public void Fresh_score_has_native_neutral_seeds()
    {
        var ps = new PlayerScore();
        Assert.Equal(1.0f, ps.RankRatio);
        Assert.False(ps.Win);
        Assert.Equal(0, ps.Rescues);
        Assert.Equal(0, ps.Ejections);
        Assert.Equal(0f, ps.TimePlayed);
    }
}
