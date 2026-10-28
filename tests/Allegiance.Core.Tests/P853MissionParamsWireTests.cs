using Allegiance.Core;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P853 — the live host-editing wire + model layer:
/// <list type="bullet">
/// <item>the <c>FM_CS_MISSIONPARAMS(113)</c> codec is wire-exact to the native
/// 252-byte <c>MissionParams</c> struct (<c>Igc/igc.h:1109-1251</c>; header 4 +
/// 252 = 256 bytes) — the field placements the P29 MSVC probe confirmed on the
/// wire (szIGCStaticFile +65, verIGCcore +120, timeStart +156, rgCivID +168,
/// iLives +204, nTeams +218) must round-trip;</item>
/// <item>the port of <c>MissionParams::Invalid(bool)</c>
/// (<c>igc.h:1339-1506</c>) refuses exactly what native refuses;</item>
/// <item>the MISSIONDEF(38) advertisement now carries the params tail the
/// C→S echo guards compare against (iMinRank/iMaxRank/nMin/MaxPlayersPerTeam/
/// nTotalMaxPlayersPerGame);</item>
/// <item>SIM-0491 close-out: a squad-game invitation renames the side to the
/// invited squad (native CFSSide::AddInvitation → SetSideName,
/// fsside.cpp:78), and a non-squad game never renames.</item>
/// </list>
/// </summary>
public sealed class P853MissionParamsWireTests
{
    private static MissionParamsMsg Sample() => new()
    {
        StrGameName = "P853 Host Edit",
        Bits = { [MissionParamsMsg.BitAllowDevelopments] = true },   // Sample() is a prosperity game (TeamMoney 1.5)
        SzIgcStaticFile = "PCore001",
        StrGamePassword = "pw",
        KbLevel = 2,
        FGoalTeamMoney = 1.5f,
        VerIgcCore = 27,
        DtGameLength = 1800f,
        FHe3Density = 2f,
        TimeStart = 0x5353,
        RgCivId = [1, 3, 0, 0, 0, 0],
        IGoalConquestPercentage = 100,
        IGoalTerritoryPercentage = 70,
        NGoalFlagsCount = 10,
        NGoalArtifactsCount = 5,
        NGoalTeamKills = 20,
        FStartingMoney = 2f,
        ILives = 3,
        MmMapType = 8,
        NTems = 3,
        IMinRank = 2,
        IMaxRank = 40,
        NInvitationListId = 42,
        IMaxImbalance = 3,
        NMinPlayersPerTeam = 2,
        NMaxPlayersPerTeam = 30,
        NInitialMinersPerTeam = 2,
        NMaxDronesPerTeam = 6,
        NTotalMaxPlayersPerGame = 200,
    };

    [Fact]
    public void MissionParams_round_trips_wire_exact()
    {
        var p = Sample();
        p[MissionParamsMsg.BitEjectPods] = true;
        p[MissionParamsMsg.BitAllowEmptyTeams] = true;
        p[MissionParamsMsg.BitSquadGame] = true;
        p[MissionParamsMsg.BitAllowFriendlyFire] = true;

        var buf = p.Encode();
        Assert.Equal(MissionParamsMsg.FixedSize, buf.Length);       // header(4) + MissionParams(252)
        Assert.Equal(113, buf[2] | buf[3] << 8);                    // fmid
        Assert.Equal(MissionParamsMsg.FixedSize, buf[0] | buf[1] << 8); // cbmsg

        var back = MissionParamsMsg.Decode(buf);
        Assert.Equal("P853 Host Edit", back.StrGameName);
        Assert.Equal("PCore001", back.SzIgcStaticFile);
        Assert.Equal("", back.SzCustomMapFile);
        Assert.Equal("pw", back.StrGamePassword);
        Assert.Equal(2, back.KbLevel);
        Assert.Equal(1.5f, back.FGoalTeamMoney);
        Assert.Equal(27, back.VerIgcCore);
        Assert.Equal(1800f, back.DtGameLength);
        Assert.Equal(2f, back.FHe3Density);
        Assert.Equal(0x5353u, back.TimeStart);
        Assert.Equal(1, back.RgCivId[0]);
        Assert.Equal(3, back.RgCivId[1]);
        Assert.Equal(70, back.IGoalTerritoryPercentage);
        Assert.Equal(20, back.NGoalTeamKills);
        Assert.Equal(2f, back.FStartingMoney);
        Assert.Equal(3, back.ILives);
        Assert.Equal(8, back.MmMapType);
        Assert.Equal(3, back.NTems);
        Assert.Equal(2, back.IMinRank);
        Assert.Equal(40, back.IMaxRank);
        Assert.Equal(42, back.NInvitationListId);
        Assert.Equal(3, back.IMaxImbalance);
        Assert.Equal((byte)2, back.NMinPlayersPerTeam);
        Assert.Equal((byte)30, back.NMaxPlayersPerTeam);
        Assert.Equal((sbyte)2, back.NInitialMinersPerTeam);
        Assert.Equal((sbyte)6, back.NMaxDronesPerTeam);
        Assert.Equal(200, back.NTotalMaxPlayersPerGame);
        Assert.True(back[MissionParamsMsg.BitEjectPods]);
        Assert.True(back[MissionParamsMsg.BitAllowEmptyTeams]);
        Assert.True(back[MissionParamsMsg.BitSquadGame]);
        Assert.True(back[MissionParamsMsg.BitAllowFriendlyFire]);
        Assert.False(back[MissionParamsMsg.BitLockGameOpen]);
        Assert.False(back[MissionParamsMsg.BitClubGame]);
    }

    /// <summary>The P29 MSVC-probe placements must hold in this codec too —
    /// the whole point of the MISSIONPARAMS payload is byte-for-byte native
    /// compatibility with what the native client sends.</summary>
    [Fact]
    public void MissionParams_field_placements_match_the_P29_probe()
    {
        var msg = Sample();
        msg[MissionParamsMsg.BitSquadGame] = true;
        msg[MissionParamsMsg.BitAllowFriendlyFire] = true;
        var buf = msg.Encode();
        // probe: szIGCStaticFile at params+65, verIGCcore at +120,
        // timeStart at +156, rgCivID at +168, iLives at +204, nTeams at +218.
        var bytes = System.Text.Encoding.Latin1.GetBytes("PCore001");
        foreach (var b in bytes) Assert.Contains(b, buf.AsSpan(MissionParamsMsg.SzIgcStaticFileOffset, 13).ToArray());
        Assert.Equal(27, BitConverter.ToInt32(buf.AsSpan(MissionParamsMsg.VerIgcCoreOffset)));
        Assert.Equal(0x5353u, BitConverter.ToUInt32(buf.AsSpan(MissionParamsMsg.TimeStartOffset)));
        Assert.Equal(1, BitConverter.ToInt16(buf.AsSpan(MissionParamsMsg.RgCivIdOffset)));
        Assert.Equal(3, BitConverter.ToInt16(buf.AsSpan(MissionParamsMsg.ILivesOffset)));
        Assert.Equal(3, BitConverter.ToInt16(buf.AsSpan(MissionParamsMsg.NTemsOffset)));
        // The bool:1 block sits at params+108 (five bytes); bSquadGame is bit 21
        // (igc.h:1142 declaration order) and bAllowFriendlyFire bit 28 — the
        // same mapping the P761/P856 MissionDef codec established.
        Assert.True((buf[MissionParamsMsg.BitfieldsOffset + 21 / 8] & (1 << (21 % 8))) != 0);
        Assert.True((buf[MissionParamsMsg.BitfieldsOffset + 28 / 8] & (1 << (28 % 8))) != 0);
        Assert.Equal(200, BitConverter.ToInt16(buf.AsSpan(MissionParamsMsg.NTotalMaxPlayersPerGameOffset)));
    }

    // ---- Invalid(bool) — the port of Igc/igc.h:1339-1506 -------------------

    [Fact]
    public void Invalid_client_rejects_out_of_range_max_players_per_team()
    {
        var p = Sample();
        p.IGoalConquestPercentage = 100;   // a winning condition must exist
        p.NMaxPlayersPerTeam = 0;
        Assert.NotNull(p.Invalid(bFromClient: true));     // igc.h:1343-1348 (client leg: 1..100)
        p.NMaxPlayersPerTeam = 101;
        Assert.NotNull(p.Invalid(bFromClient: true));
        p.NMaxPlayersPerTeam = 100;
        Assert.Null(p.Invalid(bFromClient: true));
    }

    [Fact]
    public void Invalid_client_rejects_no_winning_conditions_and_admin_flags()
    {
        var p = Sample();
        p.IGoalConquestPercentage = 0;
        p.IGoalTerritoryPercentage = 100;
        p.NGoalTeamKills = 0;
        p.NGoalFlagsCount = 0;
        p.NGoalArtifactsCount = 0;
        p.FGoalTeamMoney = 0;
        p.DtGameLength = 0;
        Assert.Equal("You must choose one or more winning conditions.", p.Invalid(bFromClient: true));  // igc.h:1349-1352
        p.IGoalConquestPercentage = 100;               // a legal settings screen always has one goal set
        p[MissionParamsMsg.BitAutoRestart] = true;     // igc.h:1353-1356
        Assert.Equal("HACK: one or more admin-only flags are set.", p.Invalid(bFromClient: true));
        p[MissionParamsMsg.BitAutoRestart] = false;
        p[MissionParamsMsg.BitObjectModelCreated] = true;
        p[MissionParamsMsg.BitClubGame] = true;
        Assert.NotNull(p.Invalid(bFromClient: true));
        // The non-client leg does NOT run the admin-flag check (igc.h:1353 sits
        // inside `if (bFromClient)`).
        p[MissionParamsMsg.BitClubGame] = false;
        Assert.Null(p.Invalid(bFromClient: false));
    }

    [Fact]
    public void Invalid_common_leg_matches_native_rules()
    {
        var p = Sample();
        p.NTems = 7;                                                    // igc.h:1462-1465
        Assert.Equal("Number of teams must be between 2 and 6.", p.Invalid());
        p.NTems = 3;
        p.NTotalMaxPlayersPerGame = 0;                                  // igc.h:1466-1469
        Assert.Equal("Number of players per game must be greater than 0.", p.Invalid());
        p.NTotalMaxPlayersPerGame = 200;
        p.NMinPlayersPerTeam = 0;                                       // igc.h:1474-1477
        Assert.Equal("Minimum number of players must be 1 or greater.", p.Invalid());
        p.NMinPlayersPerTeam = 50;
        Assert.Equal("Minimum number of players must not be greater than the maximum number of players.", p.Invalid());  // igc.h:1478-1481
        p.NMinPlayersPerTeam = 2;
        p.IGoalConquestPercentage = 100;                                // igc.h:1490-1495
        p[MissionParamsMsg.BitInvulnerableStations] = true;
        Assert.NotNull(p.Invalid());
        p[MissionParamsMsg.BitInvulnerableStations] = false;
        p.FGoalTeamMoney = 1;                                           // igc.h:1496-1501
        p[MissionParamsMsg.BitAllowDevelopments] = false;
        Assert.NotNull(p.Invalid());
        p[MissionParamsMsg.BitAllowDevelopments] = true;
        p.MmMapType = 3; p.NTems = 2;                                   // DiamondRing needs 3 teams (mapmakerigc.cpp:1930-1937)
        Assert.NotNull(p.Invalid());
        p.MmMapType = 13; p.NTems = 4;                                  // EastWest needs exactly 2 (mapmakerigc.cpp:2416-2427)
        Assert.NotNull(p.Invalid());
        p.MmMapType = 13; p.NTems = 2;
        Assert.Null(p.Invalid());
        p.MmMapType = 99;                                               // mapmakerigc.cpp:164-166
        Assert.Equal("Invalid map type", p.Invalid());
    }

    // ---- MISSIONDEF params tail --------------------------------------------

    [Fact]
    public void MissionDef_round_trips_the_params_tail()
    {
        var md = new MissionDef
        {
            SzIGCStaticFile = "PCore001",
            VerIgcCore = 27,
            NTems = 3,
            RgCivId = [1, 3, 4],
            IMinRank = 2,
            IMaxRank = 40,
            NMinPlayersPerTeam = 2,
            NMaxPlayersPerTeam = 30,
            NTotalMaxPlayersPerGame = 200,
        };
        var back = MissionDef.Decode(md.Encode());
        Assert.Equal(2, back.IMinRank);
        Assert.Equal(40, back.IMaxRank);
        Assert.Equal((byte)2, back.NMinPlayersPerTeam);
        Assert.Equal((byte)30, back.NMaxPlayersPerTeam);
        Assert.Equal(200, back.NTotalMaxPlayersPerGame);
        Assert.Equal(MissionDef.FixedSize, md.Encode().Length);   // wire width unchanged
    }

    // ---- SIM-0491 close-out: invitation squad rename -----------------------

    private static Mission NewMission(bool squadGame)
    {
        var corePath = Corpus.Files.First(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase));
        var core = StaticCore.Load(IgcReader.ParseFile(corePath));
        var mapPath = Corpus.Files.First(p => Path.GetFileName(p).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase));
        var map = MissionMap.Load(IgcReader.ParseFile(mapPath));
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).Select(c => c.CivilizationId).ToArray();
        var m = Mission.Assemble(core, map,
            [new SideSpec(0, "Alpha", civs[0]), new SideSpec(1, "Bravo", civs[1])],
            new NullIgcSite());
        m.SquadGame = squadGame;
        return m;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SquadGame_invitation_renames_the_side_to_the_invited_squad()
    {
        Require.Artwork();
        var m = NewMission(squadGame: true);
        Assert.Equal("Alpha", m.Sides[0].Name);

        m.AddInvitation(0, "Black Masks");   // fsside.cpp:69-86: clears + renames
        Assert.Equal("Black Masks", m.Sides[0].Name);
        Assert.Equal(["Black Masks"], m.SideInvitations[0]);

        m.AddInvitation(0, "Iron Pigs");     // one invited squad per side — the last wins
        Assert.Equal("Iron Pigs", m.Sides[0].Name);
        Assert.Equal(["Iron Pigs"], m.SideInvitations[0]);

        m.AddInvitation(1, "Other");         // the other side renames independently
        Assert.Equal("Other", m.Sides[1].Name);
        Assert.Equal("Iron Pigs", m.Sides[0].Name);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NonSquadGame_invitation_keeps_the_side_name()
    {
        Require.Artwork();
        var m = NewMission(squadGame: false);
        m.AddInvitation(0, "Alice");
        Assert.Equal("Alpha", m.Sides[0].Name);          // fsside.cpp:72 IsSquadGame() gate
        Assert.Equal(["Alice"], m.SideInvitations[0]);
    }

    // ---- client state: the rename echo (P853 session/state layer) ----------

    [Fact]
    public void ClientWorld_applies_the_rename_echo_and_the_squad_relay()
    {
        var world = new Allegiance.ClientCore.State.ClientWorld();
        // A non-squad rename: squadID NA + the new real name (fsmission.cpp:1587).
        world.Apply(new SetTeamInfo { SideId = 1, SquadId = -1, SideName = "Renamed" });
        Assert.Equal("Renamed", world.SideNameOverride(1));
        Assert.Null(world.SideSquadName(1));
        // A squad relay still lands in the squad slot (P820c unchanged).
        world.Apply(new SetTeamInfo { SideId = 1, SquadId = 12345, SideName = "Squad A" });
        Assert.Equal("Squad A", world.SideSquadName(1));
        Assert.Equal("Renamed", world.SideNameOverride(1));   // the rename survives the squad relay
        // A NA relay with an empty name clears both (a squadless side's name echo).
        world.Apply(new SetTeamInfo { SideId = 1, SquadId = -1, SideName = "" });
        Assert.Null(world.SideNameOverride(1));
        Assert.Null(world.SideSquadName(1));
    }
}
