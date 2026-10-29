using Allegiance.Core.Collision;
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
/// P302 — live loopback proof of the <b>prosperity and artifact win conditions</b>
/// over a real in-process server + real TCP bots:
///   (1) <b>prosperity</b>: completing the real <c>c_didTeamMoney</c> (id 1)
///       development bucket through the real funding+build-time pipeline flips
///       the mission <see cref="MissionStage.Over"/> and broadcasts
///       GAME_OVER(62) with the completing side as winner (native
///       <c>FedSrv.CPP:11274-11279</c>);
///   (2) the same completion with <see cref="Mission.ProsperityEnabled"/> off is
///       a non-event — the mission stays Started, no GAME_OVER (the selection
///       wiring);
///   (3) <b>artifact</b>: a rescue-capable ship possesses a neutral artifact
///       (carried value == <see cref="Side.ArtifactSentinelFlag"/>), returns it
///       to its own side's station, and the resulting secure reaches
///       <see cref="Mission.ArtifactWinGoal"/> → mission Over, GAME_OVER winner =
///       the securing side (native <c>fsmission.cpp:3888-3894</c>);
///   (4) the same artifact secure with <see cref="Mission.ArtifactEnabled"/> off
///       is a non-event — artifacts still increment but no win.
/// Companion pure-domain proofs live in <see cref="ProsperityArtifactTests"/>.
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public sealed partial class ProsperityArtifactLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static readonly Vector3 StationSpot = new(0f, 0f, -950f);   // at the friendly station (dock radius)
    private static readonly Vector3 Outside = new(0f, 850f, 920f);      // well outside DockRadius

    private static Cfg Config(int ups = 20, MissionGoals? goals = null) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Goals = goals ?? new(), Name = "Prosperity/Artifact Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 913u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9910000000000000000ul, DrmHash = "prosartifact",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    // ── (1)+(2) prosperity: real team-money dev completion win / disabled no-op ─

    /// <summary>⭐ Real economic-threshold prosperity win: the completing side's
    /// <c>c_didTeamMoney</c> development bucket is funded (donation) and accrues
    /// the full real build time, then the mission flips Over and every peer gets
    /// GAME_OVER(62) with that side as winner — the port of
    /// <c>FedSrv.CPP:11274-11279</c> driven end-to-end through the real
    /// bucket-completion pipeline.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_team_money_development_wins_the_game_for_that_side()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(ups: 2, goals: new() { TeamMoney = 2 }));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        var mission = server.Missions[0];
        Assert.Equal(2, mission.Goals.TeamMoney);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "rich");
        bot.SendLogonRequest(Request("rich"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "donor bot did not seat");
        short sideId = SideOf(server, bot);

        var side = mission.Sides.Single(s => s.Id == sideId);
        var tm = side.Buckets.Single(b =>
            b.ItemType == BucketItemType.BitDevelopment && b.Development is { } d && d.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        Assert.False(tm.Completed, "team-money bucket must start unbuilt");

        // P198: fund the donor's PERSONAL wallet, then donate the full price.
        short donorId = bot.ShipAssignment!.Ship.ShipId;
        server.Ships.First(s => s.Ship.Id == donorId).Wallet.Money = 10_000_000;
        bot.ResetGameOvers();
        bot.SendDonate(tm.Id, tm.Price);
        int funded = PumpUntil(server, () => tm.InvestedMoney >= tm.Price, 3000);
        Assert.True(funded < 3000, "team-money bucket did not reach full funding");
        Assert.Equal(tm.Price, tm.InvestedMoney);

        // Accrue the real build time (600s @ 2 upd = 0.5s/tick → 1200 ticks).
        long ticksNeeded = 2L * tm.TimeToBuild + 50;
        int done = PumpUntil(server, () => mission.Stage == MissionStage.Over, (int)ticksNeeded);
        Assert.True(done < ticksNeeded, $"team-money dev bucket never completed the mission within {ticksNeeded} ticks");
        Assert.True(tm.Completed, "the bucket must have completed for the win to fire");

        // The mission ended with the completing side as the winner.
        Assert.Equal(MissionStage.Over, mission.Stage);
        PumpUntil(server, () => bot.GameOvers.Count >= 1, 4000);
        var go = Assert.Single(bot.GameOvers);
        Assert.Equal(sideId, go.SideWinner);
        Assert.Equal(sideId, side.Id);

        Console.WriteLine($"PROSP-LIVE: side {sideId} fully funded + built team-money dev '{tm.Name}' ({tm.Price}c/{tm.TimeToBuild}s) -> " +
                          $"mission Over, GAME_OVER(62) winner={go.SideWinner} -> {bot.GameOvers.Count} peer(s)");
    }

    /// <summary>⭐ Selection wiring regression: with <see cref="Mission.ProsperityEnabled"/>
    /// false (the default), completing the team-money development is NOT a win —
    /// the mission stays Started and no GAME_OVER escapes (existing non-prosperity
    /// missions are unaffected).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Prosperity_disabled_team_money_completion_does_not_end_game()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(ups: 2));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var mission = server.Missions[0];
        mission.Goals.TeamMoney = 0;              // the default

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "modest");
        bot.SendLogonRequest(Request("modest"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "donor bot did not seat");
        short sideId = SideOf(server, bot);

        var side = mission.Sides.Single(s => s.Id == sideId);
        var tm = side.Buckets.Single(b =>
            b.ItemType == BucketItemType.BitDevelopment && b.Development is { } d && d.DevelopmentId == Mission.TeamMoneyDevelopmentId);

        short donorId = bot.ShipAssignment!.Ship.ShipId;
        server.Ships.First(s => s.Ship.Id == donorId).Wallet.Money = 10_000_000;
        bot.ResetGameOvers();
        bot.SendDonate(tm.Id, tm.Price);
        int funded = PumpUntil(server, () => tm.InvestedMoney >= tm.Price, 3000);
        Assert.True(funded < 3000, "team-money bucket did not reach full funding");

        long ticksNeeded = 2L * tm.TimeToBuild + 100;
        int done = PumpUntil(server, () => tm.Completed, (int)ticksNeeded);
        Assert.True(done < ticksNeeded, "team-money bucket never completed");
        Assert.True(tm.Completed, "the completion must still happen (the DEV_COMPLETED broadcast)");

        // ...but with prosperity disabled the mission does NOT end and no GAME_OVER fires.
        Assert.Equal(MissionStage.Started, mission.Stage);
        int neg = PumpUntil(server, () => bot.GameOvers.Count >= 1, 1500);
        Assert.True(neg == 1500, "prosperity-disabled completion must not fire GAME_OVER");
        Assert.Empty(bot.GameOvers);

        Console.WriteLine($"PROSP-LIVE-DISABLED: side {sideId} completed team-money dev '{tm.Name}' with ProsperityEnabled=false -> " +
                          $"stage {mission.Stage}, no GAME_OVER (default unaffected)");
    }

    // ── (3)+(4) artifact: real neutral-artifact secure win / disabled no-op ─

    /// <summary>Spawn a single rescue-capable ship on its own side's station
    /// sector, outside dock radius; the returned ship carries the neutral
    /// artifact sentinel (possessing an artifact) so it can be driven into the
    /// station to secure it.</summary>
    private sealed class ArtifactHarness : IDisposable
    {
        public Srv Server = null!;
        public Ship Carrier = null!;
        public short OwnSide, EnemySide;
        public FltBot Bot = null!;
        public void Dispose() { Bot?.Dispose(); Server?.Dispose(); }
    }

    private static ArtifactHarness SeatArtifactCarrier(MissionGoals? goals = null)
    {
        var h = new ArtifactHarness();
        short own = 0, enemy = 0;
        Vector3 dock = default;
        h.Server = new Srv();
        h.Server.ShipSpawner = (core, mission, shipId) =>
        {
            // P550: the carrier secures by docking at its OWN base, but the map
            // garrison lacks SabmCapLand for a capital — make the own base the
            // injected SabmLand|SabmCapLand home (Sectors[0]).
            var friendlySide = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0).SideId;
            own = friendlySide;
            enemy = (short)(own == 0 ? 1 : 0);
            var friendly = P550TestDockHome.CapLandHome(mission, own);
            dock = StationPos(friendly);
            var sector = mission.Sectors[0];
            var hull = core.HullTypes.Values.OrderBy(x => x.HullId).First(hh => hh.HasCapability(DataHullTypeIGC.HabmRescue));
            // Spawn on own side, OUTSIDE the dock radius, carrying nothing yet —
            // the artifact is picked up / set after boot.
            var ship = new Ship(shipId, hull, dock + new Vector3(300f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = own;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        h.Server.Boot(Config(goals: goals));
        foreach (var __m in h.Server.Missions) __m.Stage = MissionStage.Started;
        var port = h.Server.BoundPort;
        h.Bot = new FltBot();
        h.Bot.Connect(port, "acrt");
        h.Bot.SendLogonRequest(Request("acrt"));
        int seat = PumpUntil(h.Server, () => h.Bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "artifact carrier bot did not seat");
        h.OwnSide = own;
        h.EnemySide = enemy;
        h.Carrier = h.Server.Ships.First().Ship;
        Assert.True(h.OwnSide != h.EnemySide, "harness needs two distinct sides");
        return h;
    }

    /// <summary>⭐ Real artifact-possession win: the carrier picks up a neutral
    /// artifact treasure (carried value == <see cref="Side.ArtifactSentinelFlag"/>),
    /// flies it to its OWN side's station where the secure sets
    /// <see cref="Side.Artifacts"/> to the <see cref="Mission.ArtifactWinGoal"/>,
    /// and the mission flips Over with that side as GAME_OVER winner — the port of
    /// <c>fsmission.cpp:3888-3894</c> driven end-to-end through the real
    /// pickup → dock-secure pipeline.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Possessing_and_returning_an_artifact_wins_the_game_for_that_side()
    {
        Require.Artwork();
        using var h = SeatArtifactCarrier(new() { Artifacts = 1 });
        var mission = h.Server.Missions[0];
        Assert.Equal((short)1, mission.Goals.Artifacts);
        var recoveredSide = mission.Sides.Single(s => s.Id == h.OwnSide); // survives registry retirement for inspection

        // The own base (where the carrier secures) is the injected CapLand home,
        // which lives in Sectors[0] alongside the spawned carrier.
        var fly = mission.Sectors[0];
        // Place a NEUTRAL artifact treasure (TreasureId == ArtifactSentinelFlag)
        // at the carrier's position → the pickup pass hands it to the ship
        // (ship.Flag = -2, a real neutral-artifact possession).
        var data = new DataTreasureIGC(
            P0: [h.Carrier.Position.X, h.Carrier.Position.Y, h.Carrier.Position.Z],
            V0: [0f, 0f, 0f], Lifespan: 9999f, Time0: 0u, ObjectId: 9101,
            TreasureId: Side.ArtifactSentinelFlag,   // neutral artifact
            ClusterId: fly.Id, Amount: 0,
            TreasureCode: TreasureCode.Flag, CreateNow: true);
        var treasure = new Treasure(9101, fly, data, 0.0);
        fly.Add(treasure);

        // Phase 1: the carrier picks up the artifact (possesses it).
        int picked = PumpUntil(h.Server, () => h.Carrier.Flag is { } f && f == Side.ArtifactSentinelFlag, 3000);
        Assert.True(picked < 3000, "carrier never picked up the neutral artifact");
        Assert.Equal(Side.ArtifactSentinelFlag, h.Carrier.Flag!.Value);

        h.Bot.ResetGameOvers();

        // Phase 2: drive it into its own station's dock radius → dock → secure → win.
        var ownStation = fly.Stations.First(st => st.SideId == h.OwnSide && st.Name is not null && st.Name.StartsWith("CAPHOME"));
        h.Carrier.Position = StationPos(ownStation) + new Vector3(15f, 0f, 0f);

        int won = PumpUntil(h.Server, () => mission.Stage == MissionStage.Over, 6000);
        Assert.True(won < 6000, "returning the artifact never ended the mission");
        Assert.Equal(MissionStage.Over, mission.Stage);
        Assert.Equal((short)1, recoveredSide.Artifacts);

        PumpUntil(h.Server, () => h.Bot.GameOvers.Count >= 1, 4000);
        var go = Assert.Single(h.Bot.GameOvers);
        Assert.Equal(h.OwnSide, go.SideWinner);
        Assert.Equal($"{recoveredSide.Name} wins with the artifact recovered by acrt", go.GameoverMessage);
        Assert.True(PumpUntil(h.Server, () => h.Bot.Chats.Any(c => c.VoiceOver == 1362), 3000) < 3000);
        var cue = Assert.Single(h.Bot.Chats, c => c.VoiceOver == 1362);
        Assert.Equal((int)ChatTarget.Team, cue.ChatTarget);
        Assert.Equal(h.OwnSide, cue.OidRecipient);
        Assert.Equal("acrt has secured an artifact.", cue.Message);
        Assert.Null(h.Carrier.Flag);
        Assert.Contains(h.Bot.GainFlags, f => f.SideidFlag == GainFlag.NA);

        Console.WriteLine($"ARTIFACT-LIVE: side {h.OwnSide} ship {h.Carrier.Id} picked up + returned a neutral artifact to own station -> " +
                          $"artifacts=1/1, mission Over, GAME_OVER(62) winner={go.SideWinner}");
    }

    /// <summary>⭐ Selection wiring regression: with <see cref="Mission.ArtifactEnabled"/>
    /// false (the default), securing a neutral artifact increments
    /// <see cref="Side.Artifacts"/> but does NOT end the mission — no GAME_OVER
    /// (existing non-artifact missions are unaffected).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Artifact_disabled_secure_increments_counter_but_does_not_end_game()
    {
        Require.Artwork();
        using var h = SeatArtifactCarrier();
        var mission = h.Server.Missions[0];
        mission.Goals.Artifacts = 0;

        // Direct possession (the P201 secure-test shape): carry the neutral
        // artifact sentinel into the own station.
        h.Carrier.Flag = Side.ArtifactSentinelFlag;
        var fly = mission.Sectors[0];
        var ownStation = fly.Stations.First(st => st.SideId == h.OwnSide && st.Name is not null && st.Name.StartsWith("CAPHOME"));
        h.Carrier.Position = StationPos(ownStation) + new Vector3(15f, 0f, 0f);

        h.Bot.ResetGameOvers();
        int done = PumpUntil(h.Server, () => h.Carrier.StationId is not null && h.Carrier.Flag is null, 4000);
        Assert.True(done < 4000, "carrier never docked/secured the artifact");
        Assert.Equal((short)1, mission.Sides.Single(s => s.Id == h.OwnSide).Artifacts);  // counter really incremented

        // ...but with artifacts disabled the mission does NOT end and no GAME_OVER fires.
        Assert.Equal(MissionStage.Started, mission.Stage);
        int neg = PumpUntil(h.Server, () => h.Bot.GameOvers.Count >= 1, 1500);
        Assert.True(neg == 1500, "artifact-disabled secure must not fire GAME_OVER");
        Assert.Empty(h.Bot.GameOvers);

        Console.WriteLine($"ARTIFACT-LIVE-DISABLED: side {h.OwnSide} secured an artifact (artifacts=1) with ArtifactEnabled=false -> " +
                          $"stage {mission.Stage}, no GAME_OVER (default unaffected)");
    }
}
