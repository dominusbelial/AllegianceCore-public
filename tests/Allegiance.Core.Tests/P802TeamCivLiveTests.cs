using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P802 — the team-lobby civilization picker's SERVER half, over real loopback
/// TCP with <see cref="FltBot"/> peers (the P186 harness in
/// <c>LiveTeamLobbyToggleTests</c>). Native rules under test:
/// <list type="bullet">
/// <item><c>FM_CS_CHANGE_TEAM_CIV(51)</c> shape and permission
/// (<c>Inc/Messages.h:407-411</c>; <c>FedSrv/FedSrv.CPP:7344-7366</c>);</item>
/// <item>random factions — native resolves the random pick with
/// <c>randomInt</c> over the legal civ list (<c>teamscreen.cpp:1062-1075</c>,
/// <c>:2683-2685</c>) from the pool that excludes special civs
/// (<c>:2667</c>, <c>civID &lt; 300</c>); this port resolves it on the
/// server and relays the concrete civ;</item>
/// <item>the <c>SetSideCiv</c> side effects: per-ship persist-score reset
/// (<c>fsmission.cpp:5274-5283</c>) and <c>IsideIGC::SetCivilization</c>'s
/// dev-tech reset + station retype (<c>Igc/sideigc.h:76-102</c>);</item>
/// <item>the side's research plan follows the new civ (the plan is keyed by
/// civ, <c>data/showcase-plans/&lt;core&gt;/civ&lt;id&gt;-*.json</c>).</item>
/// </list>
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class P802TeamCivLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root absent");
        }
    }

    private static Cfg Config(string? researchPlanDirectory = null) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P802 Civ", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
                ResearchPlanDirectory = researchPlanDirectory },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 802u, DwCookie = 0x802u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8020000000000000000ul, DrmHash = "p802",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static Side Side(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    private static void LeadEmptySide(Srv server, FltBot bot, short shipId, short sideId)
        => Assert.True(server.TryJoinSide(ShipOf(server, shipId), sideId));

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    private static DataCivilizationIGC TargetCiv(Srv server, short currentCivId) =>
        server.Missions[0].Core.Civilizations.Values
            .Where(c => c.CivilizationId != currentCivId && c.CivilizationId is >= 0 and < 300)
            .OrderBy(c => c.CivilizationId).First();

    /// <summary>Reflect the P802 test seam (server internals are not visible to
    /// this assembly — the same reflection idiom the P438/P602 live tests use).</summary>
    private static object? PlayerScoreOf(Srv server, short shipId) =>
        typeof(Srv).GetMethod("TestPlayerScore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [shipId]);

    /// <summary>⭐ A <c>random=true</c> request resolves on the server to a real
    /// civilization from the legal lobby pool (native random pick,
    /// teamscreen.cpp:1062-1075/2667) and the whole-mission relay carries
    /// that RESOLVED civ with the random flag set. Master ignores the flag and
    /// applies the placeholder civ id (9999), which it refuses because no such
    /// civ exists — so this fails without the fix.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Random_change_resolves_to_a_real_legal_civ_and_relays_it()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "RR1");
        short bId = Seat(server, b, "RR2");
        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), server.Missions[0].Sides[1].Id));
        Flush(server);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        a.ResetLeadershipSignals();
        b.ResetLeadershipSignals();

        // Native RANDOM_ID (igc.h:501) is a placeholder, not a civ.
        a.SendChangeTeamCiv(side0, TeamCivPickerRandomId, random: true);

        // Native random may legitimately select the side's current civ again.
        int got = PumpUntil(server, () => a.ChangeTeamCivs.Any(x => x.SideId == side0 && x.Random)
            && b.ChangeTeamCivs.Any(x => x.SideId == side0 && x.Random), 800);
        Assert.True(got < 800, "a random CHANGE_TEAM_CIV must resolve, apply and relay");

        short resolved = Side(server, side0).Civilization.CivilizationId;
        Assert.InRange(resolved, (short)0, (short)299);   // legal lobby pool, never a special civ
        Assert.True(server.Missions[0].Core.Civilizations.ContainsKey(resolved));
        Assert.NotEqual(TeamCivPickerRandomId, resolved);
        Assert.All(new[] { a, b }, bot =>
        {
            var relay = Assert.Single(bot.ChangeTeamCivs, x => x.SideId == side0);
            Assert.True(relay.Random);
            Assert.Equal(resolved, relay.CivId);   // the relay carries the resolved civ
        });
    }

    private const short TeamCivPickerRandomId = 9999;   // Igc/igc.h:501 RANDOM_ID

    /// <summary>⭐ The native <c>IsideIGC::SetCivilization</c> effects apply: the
    /// side's development techs become the new civilization's base techs
    /// (sideigc.h:88) and every station of the side is retyped to that civ's
    /// initial station type (sideigc.h:92-100). Also pins the native
    /// persist-score reset (fsmission.cpp:5274-5283) on a real peer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Change_applies_new_civ_base_techs_stations_and_score_reset()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "ST1");
        short bId = Seat(server, b, "ST2");
        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));   // same-side non-leader
        Flush(server);

        short before = Side(server, side0).Civilization.CivilizationId;
        var target = TargetCiv(server, before);
        Assert.NotEqual(target.CivilizationId, before);

        // A non-zero live score proves the reset really ran (the native persist
        // copy has no per-civ store here, P193/P583).
        var leaderScore = PlayerScoreOf(server, aId)!;
        leaderScore.GetType().GetProperty("Kills")!.SetValue(leaderScore, 7);

        a.ResetLeadershipSignals();
        a.SendChangeTeamCiv(side0, target.CivilizationId);
        int got = PumpUntil(server, () => Side(server, side0).Civilization.CivilizationId == target.CivilizationId
            && a.ChangeTeamCivs.Any(x => x.SideId == side0 && x.CivId == target.CivilizationId), 800);
        Assert.True(got < 800, "the direct civ change must apply and relay");

        var side = Side(server, side0);
        Assert.Equal(TechTreeBitMask.FromBytes(target.TtbmBaseTechs), side.DevelopmentTechs);
        var stations = server.Missions[0].Sectors.SelectMany(s => s.StationRuntimes)
            .Where(st => st.SideId == side0).ToArray();
        Assert.NotEmpty(stations);
        Assert.All(stations, st => Assert.Equal(target.InitialStationTypeId, st.Type.StationTypeId));

        leaderScore = PlayerScoreOf(server, aId)!;
        Assert.Equal(0, (int)leaderScore.GetType().GetProperty("Kills")!.GetValue(leaderScore)!);
        var followerScore = PlayerScoreOf(server, bId)!;
        Assert.Equal(0, (int)followerScore.GetType().GetProperty("Kills")!.GetValue(followerScore)!);
    }

    /// <summary>⭐ The side's research plan follows the new civ: plans are keyed
    /// by civ, and the lobby change picks the new civ's plan (the P703 research
    /// commander and the briefing execute this plan). Master keeps the boot-time
    /// civ's plan, so a leader changing civ would have the bots research the old
    /// civilization.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Research_plan_follows_the_new_civilization()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(Path.Combine(Root, "data/showcase-plans")));
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "RP1");
        short bId = Seat(server, b, "RP2");
        short side0 = server.Missions[0].Sides[0].Id;
        LeadEmptySide(server, a, aId, side0);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), server.Missions[0].Sides[1].Id));
        Flush(server);

        var mission = server.Missions[0];
        Assert.True(mission.ResearchPlans.ContainsKey(side0), "the research game selects a plan per side at assembly");
        short beforeCiv = Side(server, side0).Civilization.CivilizationId;
        Assert.Equal(beforeCiv, mission.ResearchPlans[side0].CivId);

        // Civ 25 is one of the core's authored plan civilizations (data/showcase-plans/PCore001).
        a.ResetLeadershipSignals();
        a.SendChangeTeamCiv(side0, 25);
        int got = PumpUntil(server, () => Side(server, side0).Civilization.CivilizationId == 25, 800);
        Assert.True(got < 800, "the civ change must apply");

        Assert.Equal(25, mission.ResearchPlans[side0].CivId);
        Assert.NotEqual(beforeCiv, mission.ResearchPlans[side0].CivId);
    }
}
