using Allegiance.Core.Collision;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

// 'Allegiance.Server' is a namespace AND our type's namespace; the bare name
// 'Server' would bind to the namespace (same idiom as P853HostEditServerTests).
using Server = global::Allegiance.Server.Server;

/// <summary>
/// P1027 — MISSIONPARAMS non-finite-float hardening (R1 §4 lead).
/// Native <c>MissionParams::Invalid</c> (<c>Igc/igc.h:1339-1506</c>) checks its
/// floats only with <c>&lt;</c>/<c>&gt;</c> comparisons: NaN fails every
/// comparison, so it passes all of them, and ±Infinity passes wherever the
/// bound is open (<c>dtGameLength</c>, <c>fStartCountdown</c>,
/// <c>fRestartCountdown</c> have no check at all). A mission owner could
/// therefore store non-finite goals via FM_CS_MISSIONPARAMS(113); the server
/// re-broadcasts them in MISSIONDEF(38) and writes them into the lobby result
/// report (<c>Server.Lobby.cs</c> GoalCountdownLength/GoalProsperityMoney).
/// Rejecting non-finite floats on the client leg is a deliberate port
/// hardening — these tests pin it, plus the <c>DtGameLength</c> range (host UI
/// 0..360 minutes, <c>MissionSettingsForm.cs:72,193</c> = native
/// <c>CountdownTimeValues</c>, <c>artwork/newgamescreen.mdl:329</c>; longest
/// match the port itself hosts is a day, <c>ServerConfig.cs:35</c>), and that
/// every preset/default the port actually produces stays valid.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P1027MissionParamsNonFiniteTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    /// <summary>The eight <c>float</c> fields the 252-byte MissionParams struct
    /// carries (<c>Igc/igc.h:1109-1251</c>; there are no doubles).</summary>
    private static readonly (string Name, Action<MissionParamsMsg, float> Set)[] FloatFields =
    [
        ("FGoalTeamMoney",              (p, v) => p.FGoalTeamMoney = v),              // igc.h:1158
        ("NPlayerSectorTreasureRate",   (p, v) => p.NPlayerSectorTreasureRate = v),   // igc.h:1160
        ("NNeutralSectorTreasureRate",  (p, v) => p.NNeutralSectorTreasureRate = v),  // igc.h:1161
        ("DtGameLength",                (p, v) => p.DtGameLength = v),                // igc.h:1162
        ("FHe3Density",                 (p, v) => p.FHe3Density = v),                 // igc.h:1163
        ("FStartCountdown",             (p, v) => p.FStartCountdown = v),             // igc.h:1170
        ("FRestartCountdown",           (p, v) => p.FRestartCountdown = v),           // igc.h:1171
        ("FStartingMoney",              (p, v) => p.FStartingMoney = v),              // igc.h:1184
    ];

    /// <summary>A params struct that passes <see cref="MissionParamsMsg.Invalid"/>(true)
    /// with every float finite (the P853 Sample() shape).</summary>
    private static MissionParamsMsg Baseline() => new()
    {
        StrGameName = "P1027 NonFinite",
        SzIgcStaticFile = "PCore001",
        Bits = { [MissionParamsMsg.BitAllowDevelopments] = true },   // prosperity leg (igc.h:1496-1501)
        FGoalTeamMoney = 1.5f,
        DtGameLength = 1800f,
        FHe3Density = 2f,
        FStartingMoney = 2f,
        IGoalConquestPercentage = 100,
        IGoalTerritoryPercentage = 100,
        MmMapType = 8,          // c_mmHiLo — native Reset default (igc.h:1293)
        NTems = 2,
        NMinPlayersPerTeam = 1,
        NMaxPlayersPerTeam = 30,
        NTotalMaxPlayersPerGame = 200,
    };

    public static TheoryData<string, float> NonFinitePerField()
    {
        var data = new TheoryData<string, float>();
        foreach (var (name, _) in FloatFields)
        {
            data.Add(name, float.NaN);
            data.Add(name, float.PositiveInfinity);
            data.Add(name, float.NegativeInfinity);
        }
        return data;
    }

    [Fact]
    public void Baseline_is_valid_before_anything_is_poisoned()
    {
        Assert.Null(Baseline().Invalid(bFromClient: true));
    }

    /// <summary>The core P1027 assertion: every non-finite value in every
    /// float field is refused on the client leg. Pre-fix, NaN passed in all
    /// eight fields (it fails every <c>&lt;</c>/<c>&gt;</c> comparison);
    /// −Infinity also passed in the five fields with no lower bound (both
    /// treasure rates, dtGameLength, both countdowns) and +Infinity in the
    /// three fields with no upper bound (dtGameLength, both countdowns).</summary>
    [Theory]
    [MemberData(nameof(NonFinitePerField))]
    public void Client_leg_rejects_every_non_finite_float(string field, float value)
    {
        var p = Baseline();
        FloatFields.First(f => f.Name == field).Set(p, value);
        string? why = p.Invalid(bFromClient: true);
        Assert.NotNull(why);
        Assert.Contains("finite", why);
    }

    [Fact]
    public void Non_finite_rejections_name_the_offending_field()
    {
        var p = Baseline();
        p.FGoalTeamMoney = float.NaN;
        Assert.Equal("GoalTeamMoney must be a finite number.", p.Invalid(bFromClient: true));
        p = Baseline();
        p.NPlayerSectorTreasureRate = float.NegativeInfinity;
        Assert.Equal("PlayerSectorTreasureRate must be a finite number.", p.Invalid(bFromClient: true));
        p = Baseline();
        p.DtGameLength = float.PositiveInfinity;
        Assert.Equal("GameLength must be a finite number.", p.Invalid(bFromClient: true));
        p = Baseline();
        p.FStartCountdown = float.NaN;
        Assert.Equal("StartCountdown must be a finite number.", p.Invalid(bFromClient: true));
        p = Baseline();
        p.FRestartCountdown = float.NaN;
        Assert.Equal("RestartCountdown must be a finite number.", p.Invalid(bFromClient: true));
        p = Baseline();
        p.FHe3Density = float.NaN;
        Assert.Equal("He3Density must be a finite number.", p.Invalid(bFromClient: true));
        p = Baseline();
        p.FStartingMoney = float.NaN;
        Assert.Equal("StartingMoney must be a finite number.", p.Invalid(bFromClient: true));
        p = Baseline();
        p.NNeutralSectorTreasureRate = float.NaN;
        Assert.Equal("NeutralSectorTreasureRate must be a finite number.", p.Invalid(bFromClient: true));
    }

    // ---- DtGameLength range (port hardening — native checks none) ----------

    /// <summary>Every value a real producer makes stays valid: 0 disables the
    /// countdown (igc.h:1162), 600 is the training default
    /// (clintlib.cpp:755), 1800 the port's countdown preset
    /// (MissionGoals.Preset), 3600 a pig game (Pig.cpp:2759), 21600 the
    /// host-edit maximum (360 min) and 86400 the longest match the port's own
    /// showcase validator allows (ServerConfig.cs:35).</summary>
    [Theory]
    [InlineData(0f)] [InlineData(600f)] [InlineData(1800f)] [InlineData(3600f)]
    [InlineData(21600f)] [InlineData(86400f)]
    public void GameLength_values_real_producers_use_stay_valid(float seconds)
    {
        var p = Baseline();
        p.DtGameLength = seconds;
        Assert.Null(p.Invalid(bFromClient: true));
    }

    [Fact]
    public void GameLength_outside_zero_to_one_day_is_rejected()
    {
        var p = Baseline();
        p.DtGameLength = -60f;
        Assert.Equal("GameLength must be between 0 and 86400.", p.Invalid(bFromClient: true));
        p.DtGameLength = 86460f;
        Assert.Equal("GameLength must be between 0 and 86400.", p.Invalid(bFromClient: true));
    }

    [Fact]
    public void Every_host_edit_countdown_preset_stays_valid()
    {
        // MissionSettingsForm.cs:72,193 steps dtGameLength through
        // CountdownMinutesValues * 60 — the native CountdownTimeValues list
        // (artwork/newgamescreen.mdl:329, newgamescreen.cpp:1010).
        short[] countdownMinutes = [0, 10, 20, 30, 60, 120, 180, 240, 300, 360];
        foreach (short minutes in countdownMinutes)
        {
            var p = Baseline();
            p.DtGameLength = minutes * 60f;
            Assert.Null(p.Invalid(bFromClient: true));
        }
    }

    // ---- existing presets / defaults must not regress -----------------------

    private static MissionParamsMsg PresetParams(MissionGoals goals) => new()
    {
        StrGameName = "P1027 preset",
        Bits = { [MissionParamsMsg.BitAllowDevelopments] = true },   // the prosperity preset needs it
        IGoalConquestPercentage = goals.ConquestPercentage,
        IGoalTerritoryPercentage = goals.TerritoryPercentage,
        NGoalTeamKills = goals.TeamKills,
        NGoalFlagsCount = goals.Flags,
        NGoalArtifactsCount = goals.Artifacts,
        FGoalTeamMoney = goals.TeamMoney,
        DtGameLength = goals.GameLengthSeconds,
        MmMapType = 8,          // c_mmHiLo — native Reset default (igc.h:1293)
        NTems = 2,
    };

    [Fact]
    public void Every_MissionGoals_preset_stays_valid()
    {
        foreach (string name in new[] { "conquest", "territory", "kills", "flags", "artifacts", "prosperity", "countdown" })
        {
            var p = PresetParams(MissionGoals.Preset(name));
            Assert.Null(p.Invalid(bFromClient: true));
        }
        // "none" carries no winning condition — the native client leg refuses
        // that (igc.h:1349-1352); nothing to do with floats.
        Assert.Equal("You must choose one or more winning conditions.",
            PresetParams(MissionGoals.Preset("none")).Invalid(bFromClient: true));
    }

    [Fact]
    public void Native_Reset_defaults_stay_valid()
    {
        // MissionParams::Reset (igc.h:1255-1337): the port's field defaults
        // plus the Reset values the port does not default (floats shown with
        // their native citation).
        var p = new MissionParamsMsg
        {
            NTems = 2,                              // igc.h:1299
            IGoalConquestPercentage = 100,          // igc.h:1307
            IGoalTerritoryPercentage = 100,         // igc.h:1308
            MmMapType = 8,                          // c_mmHiLo, igc.h:1293
            NPlayerSectorTreasureRate = 0.2f / 60f, // igc.h:1321
            NNeutralSectorTreasureRate = 0.5f / 60f,// igc.h:1322
            FStartCountdown = 15f,                  // igc.h:1273
            FRestartCountdown = 60f,                // igc.h:1274
        };
        Assert.Null(p.Invalid(bFromClient: true));
    }

    // ---- server level: refusal + nothing re-broadcast -----------------------

    private static ServerConfig Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new GameConfig
            {
                Name = "P1027 NonFinite", Map = "XXV_simple.igc", Teams = 2,
                CivIds = new[] { 1, 3 },
                SideNames = new[] { "Side0", "Side1" },
            },
        ],
    };

    private static JoinIdentity Identity() => new(
        Iss: "https://lobby.test", Aud: "game", Sub: "00000000000000000000000000000001", Callsign: "Pilot",
        Iat: 1700000000, Exp: 1700086400, Jti: "11111111111111111111111111111111");

    private static Ship Seat(Mission mission, short id, short side)
    {
        var s = new Ship(id, mission.Core.HullTypes.Values.First(), new Vector3(0, 10000, 0),
            Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(s, mission.Sectors[0]);
        return s;
    }

    private static List<(ConnectionId Peer, byte[] Msg)> CaptureSends(Server server)
    {
        var sent = new List<(ConnectionId, byte[])>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        { foreach (var m in FedPacket.Unpack(bytes)) sent.Add((peer, m)); };
        return sent;
    }

    private static bool IsMissionDef(byte[] msg) => (msg[2] | msg[3] << 8) == MissionDef.Fmid;

    private static (ConnectionId Owner, ConnectionId Mate) SeatOwnerAndMate(Server server, Mission mission, short sideId = 0)
    {
        var ownerConn = new ConnectionId(1027);
        var mateConn = new ConnectionId(1028);
        var ownerShip = Seat(mission, 19400, sideId);
        var mateShip = Seat(mission, 19401, sideId);
        server.TestSeedPeers([(ownerConn, ownerShip, mission.Sectors[0]), (mateConn, mateShip, mission.Sectors[0])]);
        server.TestSetIdentity(ownerConn, Identity());
        server.TestSetIdentity(mateConn, Identity());
        mission.MissionOwnerSideId = sideId;
        mission.Sides.First(s => s.Id == sideId).LeaderShipId = ownerShip.Id;
        return (ownerConn, mateConn);
    }

    /// <summary>The P853 host-edit fixture shape: a legal owner edit.</summary>
    private static MissionParamsMsg Edit() => new()
    {
        StrGameName = "P1027 NonFinite",
        NTems = 2,
        ILives = 30,
        IMinRank = -1,
        IMaxRank = 40,
        NMinPlayersPerTeam = 1,
        NMaxPlayersPerTeam = 100,
        NTotalMaxPlayersPerGame = unchecked((short)0x7FFF),
        IGoalConquestPercentage = 100,          // a winning condition must exist
        IGoalTerritoryPercentage = 100,         // native's territory DISABLED sentinel (igc.h:1516)
    };

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owner_NaN_missionparams_is_refused_and_nothing_is_rebroadcast()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var (owner, mate) = SeatOwnerAndMate(server, mission);

        // Positive control: a legal countdown edit is adopted and relayed.
        var sent = CaptureSends(server);
        var ok = Edit();
        ok.DtGameLength = 1800f;
        server.TestProcessDatagram(owner, FedPacket.Pack([ok.Encode()]));
        Assert.Contains(sent, s => IsMissionDef(s.Msg));
        Assert.Contains(sent, s => s.Peer == mate && IsMissionDef(s.Msg));
        Assert.Equal(1800f, server.TestParamsOf(mission).DtGameLength);

        // The P1027 case: pre-fix the NaN sailed through Invalid(true)
        // (igc.h:1339-1506 compares only with </>), was stored and
        // re-broadcast in MISSIONDEF(38). Now it must TrapHackBoot-refuse
        // (FedSrv.CPP:7534) with no re-broadcast and no stored NaN.
        sent.Clear();
        var bad = Edit();
        bad.DtGameLength = float.NaN;
        server.TestProcessDatagram(owner, FedPacket.Pack([bad.Encode()]));
        Assert.DoesNotContain(sent, s => IsMissionDef(s.Msg));
        Assert.Equal(1800f, server.TestParamsOf(mission).DtGameLength);
        Assert.False(float.IsNaN(mission.Goals.GameLengthSeconds));
        Assert.Equal(1800d, mission.CountdownDuration);
    }
}
