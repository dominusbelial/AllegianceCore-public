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
/// P303 — live loopback proof of the <b>flags and kills win conditions</b> over
/// a real in-process server + real TCP bots:
///   (1) <b>flags</b>: a ship possesses an ENEMY flag treasure (carried value ==
///       a real enemy side's id) and returns it to its own side's station; the
///       resulting secure reaches <see cref="Mission.FlagsWinGoal"/> → mission
///       Over, GAME_OVER(62) winner = the securing side (native
///       <c>fsmission.cpp:3883-3887</c>, FedSrv.CPP:13149);
///   (2) the same flag secure with <see cref="Mission.FlagsEnabled"/> off is a
///       non-event — <see cref="Side.Flags"/> still increments but no win;
///   (3) <b>kills</b>: a peer-owned killer destroys an enemy ship and the killer's
///       side reaches <see cref="Mission.KillsWinGoal"/> → mission Over,
///       GAME_OVER winner = the killing side (native
///       <c>fsmission.cpp:3749-3760</c>, FedSrv.CPP:11872);
///   (4) the same kill with <see cref="Mission.KillsEnabled"/> off is a
///       non-event — <see cref="Side.Kills"/> still increments but no win.
/// Companion pure-domain proofs live in <see cref="FlagsKillsTests"/>.
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public sealed class FlagsKillsLiveTests
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
            new Gm { Goals = goals ?? new(), Name = "Flags/Kills Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 977u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9920000000000000000ul, DrmHash = "flagsnkills",
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

    // ── (1)+(2) flags: real enemy-flag secure win / disabled no-op ──────────

    /// <summary>Spawn a single ship on its own side's station sector, outside
    /// dock radius; the returned ship carries nothing yet so an enemy flag
    /// treasure can be dropped on it for pickup.</summary>
    private sealed class FlagHarness : IDisposable
    {
        public Srv Server = null!;
        public Ship Carrier = null!;
        public short OwnSide, EnemySide;
        public FltBot Bot = null!;
        public void Dispose() { Bot?.Dispose(); Server?.Dispose(); }
    }

    private static FlagHarness SeatFlagCarrier(MissionGoals? goals = null)
    {
        var h = new FlagHarness();
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
            var ship = new Ship(shipId, hull, dock + new Vector3(300f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = own;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        h.Server.Boot(Config(goals: goals));
        foreach (var __m in h.Server.Missions) __m.Stage = MissionStage.Started;
        var port = h.Server.BoundPort;
        h.Bot = new FltBot();
        h.Bot.Connect(port, "flcr");
        h.Bot.SendLogonRequest(Request("flcr"));
        int seat = PumpUntil(h.Server, () => h.Bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "flag carrier bot did not seat");
        h.OwnSide = own;
        h.EnemySide = enemy;
        h.Carrier = h.Server.Ships.First().Ship;
        Assert.True(h.OwnSide != h.EnemySide, "harness needs two distinct sides");
        return h;
    }

    /// <summary>⭐ Real enemy-flag-secure win: the carrier picks up an enemy flag
    /// treasure (carried value == the enemy side's id), flies it to its OWN
    /// side's station where the secure sets <see cref="Side.Flags"/> to the
    /// <see cref="Mission.FlagsWinGoal"/>, and the mission flips Over with that
    /// side as GAME_OVER winner — the port of <c>fsmission.cpp:3883-3887</c>
    /// driven end-to-end through the real pickup → dock-secure pipeline.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Securing_enemy_flag_to_the_goal_wins_the_game_for_that_side()
    {
        Require.Artwork();
        using var h = SeatFlagCarrier(new() { Flags = 1 });
        var mission = h.Server.Missions[0];
        var winningSide = mission.Sides.Single(s => s.Id == h.OwnSide);
        Assert.Equal((short)1, mission.Goals.Flags);

        // The own base (where the carrier secures) is the injected CapLand home,
        // which lives in Sectors[0] alongside the spawned carrier.
        var fly = mission.Sectors[0];
        // Place an ENEMY flag treasure (TreasureId == a real enemy side's id) at
        // the carrier's position → the pickup pass hands it to the ship
        // (ship.Flag = enemy id, a real captured-enemy-flag possession).
        var data = new DataTreasureIGC(
            P0: [h.Carrier.Position.X, h.Carrier.Position.Y, h.Carrier.Position.Z],
            V0: [0f, 0f, 0f], Lifespan: 9999f, Time0: 0u, ObjectId: 9801,
            TreasureId: h.EnemySide,   // a real enemy flag
            ClusterId: fly.Id, Amount: 0,
            TreasureCode: TreasureCode.Flag, CreateNow: true);
        var treasure = new Treasure(9801, fly, data, 0.0);
        fly.Add(treasure);

        // Phase 1: the carrier picks up the enemy flag (possesses it).
        int picked = PumpUntil(h.Server, () => h.Carrier.Flag is { } f && f == h.EnemySide, 3000);
        Assert.True(picked < 3000, "carrier never picked up the enemy flag");
        Assert.Equal(h.EnemySide, h.Carrier.Flag!.Value);

        h.Bot.ResetGameOvers();

        // Phase 2: drive it into its own station's dock radius → dock → secure → win.
        var ownStation = fly.Stations.First(st => st.SideId == h.OwnSide && st.Name is not null && st.Name.StartsWith("CAPHOME"));
        h.Carrier.Position = StationPos(ownStation) + new Vector3(15f, 0f, 0f);

        int won = PumpUntil(h.Server, () => mission.Stage == MissionStage.Over, 6000);
        Assert.True(won < 6000, "securing the enemy flag never ended the mission");
        Assert.Equal(MissionStage.Over, mission.Stage);
        Assert.Equal((short)1, winningSide.Flags);

        PumpUntil(h.Server, () => h.Bot.GameOvers.Count >= 1, 4000);
        var go = Assert.Single(h.Bot.GameOvers);
        Assert.Equal(h.OwnSide, go.SideWinner);
        Assert.Contains(" wins with the flag recovered by ", go.GameoverMessage);

        Console.WriteLine($"FLAGS-LIVE: side {h.OwnSide} ship {h.Carrier.Id} picked up + secured enemy (side {h.EnemySide}) flag at own station -> " +
                          $"flags=1/1, mission Over, GAME_OVER(62) winner={go.SideWinner}");
    }

    /// <summary>⭐ Selection wiring regression: with <see cref="Mission.FlagsEnabled"/>
    /// false (the default), securing an enemy flag increments
    /// <see cref="Side.Flags"/> but does NOT end the mission — no GAME_OVER
    /// (existing non-flags missions are unaffected).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flags_disabled_secure_increments_counter_but_does_not_end_game()
    {
        Require.Artwork();
        using var h = SeatFlagCarrier();
        var mission = h.Server.Missions[0];
        mission.Goals.Flags = 0;

        // Direct possession (the P201 secure-test shape): carry the enemy flag
        // into the own station.
        h.Carrier.Flag = h.EnemySide;
        var fly = mission.Sectors[0];
        var ownStation = fly.Stations.First(st => st.SideId == h.OwnSide && st.Name is not null && st.Name.StartsWith("CAPHOME"));
        h.Carrier.Position = StationPos(ownStation) + new Vector3(15f, 0f, 0f);

        h.Bot.ResetGameOvers();
        int done = PumpUntil(h.Server, () => h.Carrier.StationId is not null && h.Carrier.Flag is null, 4000);
        Assert.True(done < 4000, "carrier never docked/secured the enemy flag");
        Assert.Equal((short)1, mission.Sides.Single(s => s.Id == h.OwnSide).Flags);  // counter really incremented

        // ...but with flags disabled the mission does NOT end and no GAME_OVER fires.
        Assert.Equal(MissionStage.Started, mission.Stage);
        int neg = PumpUntil(h.Server, () => h.Bot.GameOvers.Count >= 1, 1500);
        Assert.True(neg == 1500, "flags-disabled secure must not fire GAME_OVER");
        Assert.Empty(h.Bot.GameOvers);

        Console.WriteLine($"FLAGS-LIVE-DISABLED: side {h.OwnSide} secured an enemy flag (flags=1) with FlagsEnabled=false -> " +
                          $"stage {mission.Stage}, no GAME_OVER (default unaffected)");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P832c_Below_goal_secure_respawns_real_flag_and_sends_native_team_cue()
    {
        Require.Artwork();
        using var h = SeatFlagCarrier(new() { Flags = 2 });
        var mission = h.Server.Missions[0];
        var flags = mission.Sectors.SelectMany(s => s.Models.OfType<Treasure>())
            .Where(t => t.Code == TreasureCode.Flag && t.Data.TreasureId == h.EnemySide).ToArray();
        Assert.Equal(2, flags.Length);
        var flag = flags[0];
        // Fixture places the ship; the server's ordinary collision/pickup and
        // docking paths still perform the secure. The bot game flies unaided.
        mission.PlaceShip(h.Carrier, flag.OwnerSector);
        h.Carrier.Position = flag.Position;
        h.Carrier.Velocity = Vector3.Zero;
        Assert.True(PumpUntil(h.Server, () => h.Carrier.Flag == h.EnemySide, 3000) < 3000);
        h.Bot.ResetChatReceived();
        var fly = mission.Sectors[0];
        mission.PlaceShip(h.Carrier, fly);
        var home = fly.Stations.Single(s => s.Name is not null && s.Name.StartsWith("CAPHOME"));
        h.Carrier.Position = StationPos(home) + new Vector3(15, 0, 0);
        Assert.True(PumpUntil(h.Server, () => h.Carrier.StationId is not null && h.Carrier.Flag is null, 4000) < 4000);
        Assert.Equal(MissionStage.Started, mission.Stage);
        Assert.Equal(1, mission.Sides.Single(s => s.Id == h.OwnSide).Flags);
        var respawned = mission.Sectors.SelectMany(s => s.Models.OfType<Treasure>())
            .Where(t => t.Code == TreasureCode.Flag && t.Data.TreasureId == h.EnemySide).ToArray();
        Assert.Equal(2, respawned.Length);
        Assert.DoesNotContain(respawned, t => t.Id == flag.Id);
        Assert.True(PumpUntil(h.Server, () => h.Bot.Chats.Any(c => c.VoiceOver == 1360), 3000) < 3000);
        var cue = Assert.Single(h.Bot.Chats, c => c.VoiceOver == 1360);
        Assert.Equal((int)ChatTarget.Team, cue.ChatTarget);
        Assert.Equal(h.OwnSide, cue.OidRecipient);
        Assert.Contains("has secured", cue.Message);
        Assert.DoesNotContain(h.Bot.Chats, c => c.VoiceOver == 1358);
        Assert.Empty(h.Bot.GameOvers);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void P832c_No_eligible_pedestal_after_secure_uses_native_capturer_victory()
    {
        Require.Artwork();
        using var h = SeatFlagCarrier(new() { Flags = 2 });
        var mission = h.Server.Missions[0];
        var winningSide = mission.Sides.Single(s => s.Id == h.OwnSide);
        // Both pedestals already have flags: no eligible respawn position.
        // Native tests eligible-pedestal count, even though its text says bases.
        h.Carrier.Flag = h.EnemySide;
        var fly = mission.Sectors[0];
        var home = fly.Stations.Single(s => s.Name is not null && s.Name.StartsWith("CAPHOME"));
        h.Carrier.Position = StationPos(home) + new Vector3(15, 0, 0);
        Assert.True(PumpUntil(h.Server, () => mission.Stage == MissionStage.Over, 4000) < 4000);
        Assert.True(PumpUntil(h.Server, () => h.Bot.GameOvers.Count > 0, 3000) < 3000);
        var result = Assert.Single(h.Bot.GameOvers);
        Assert.Equal(h.OwnSide, result.SideWinner);
        Assert.Equal($"{winningSide.Name} wins. Team owning captured flag has no stations left.", result.GameoverMessage);
        Assert.Equal(1, winningSide.Flags);
    }

    // ── (3)+(4) kills: real deathmatch kill-threshold win / disabled no-op ──

    /// <summary>Seat a single peer-owned killer bot; returns the mission, the
    /// killer's side, the enemy side id, and the killer's peer-owned ship.</summary>
    private sealed class KillHarness : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Ship Killer = null!;
        public Sector Sector = null!;
        public short KillerSide, EnemySide;
        public FltBot Bot = null!;
        public void Dispose() { Bot?.Dispose(); Server?.Dispose(); }
    }

    private static KillHarness SeatKiller(MissionGoals? goals = null)
    {
        var h = new KillHarness();
        h.Server = new Srv();
        h.Server.Boot(Config(goals: goals));
        foreach (var __m in h.Server.Missions) __m.Stage = MissionStage.Started;
        h.Mission = h.Server.Missions[0];
        var port = h.Server.BoundPort;
        h.Bot = new FltBot();
        h.Bot.Connect(port, "klr");
        h.Bot.SendLogonRequest(Request("klr"));
        int seat = PumpUntil(h.Server, () => h.Bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "killer bot did not seat");
        h.KillerSide = SideOf(h.Server, h.Bot);
        h.EnemySide = h.Mission.Sides.Select(s => s.Id).First(i => i != h.KillerSide);
        h.Killer = h.Server.Ships.First().Ship;    // the peer-owned killer ship
        h.Sector = h.Killer.Sector!;
        Assert.True(h.KillerSide != h.EnemySide, "harness needs two distinct sides");
        return h;
    }

    /// <summary>Create a real enemy victim ship (non-lifepod hull) in the
    /// killer's sector, so a cross-side credited kill can be driven.</summary>
    private static Ship SpawnEnemyVictim(KillHarness h)
    {
        var hull = h.Mission.Core.HullTypes.Values.OrderBy(x => x.HullId)
            .First(x => !x.HasCapability(DataHullTypeIGC.HabmLifepod));
        var victim = new Ship(5231, hull, h.Killer.Position + new Vector3(50f, 0f, 0f),
            Vector3.Zero, Orientation.Identity);
        victim.SideId = h.EnemySide;
        h.Mission.PlaceShip(victim, h.Sector);
        return victim;
    }

    /// <summary>⭐ Real deathmatch kill-threshold win: a peer-owned killer
    /// destroys an enemy ship and the killer's side reaches
    /// <see cref="Mission.KillsWinGoal"/> → mission Over, GAME_OVER winner = the
    /// killing side — the port of <c>fsmission.cpp:3749-3760</c> driven
    /// end-to-end through the real credited-kill path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Scoring_kill_to_the_team_goal_wins_the_game_for_that_side()
    {
        Require.Artwork();
        using var h = SeatKiller(new() { TeamKills = 1 });
        Assert.Equal((short)1, h.Mission.Goals.TeamKills);

        var winningSide = h.Mission.Sides.Single(s => s.Id == h.KillerSide);
        var victim = SpawnEnemyVictim(h);
        h.Bot.ResetGameOvers();

        // Real credited cross-side kill: the peer-owned killer destroys the
        // enemy victim (native FedSrv.CPP:11868 AddKill → CheckForVictoryByKills).
        h.Mission.Site.KillShipEvent(h.Mission.LastUpdate, victim, h.Killer, 1.0f, victim.Position, Vector3.Zero);

        int won = PumpUntil(h.Server, () => h.Mission.Stage == MissionStage.Over, 8000);
        Assert.True(won < 8000, "scoring the goal kill never ended the mission");
        Assert.Equal(MissionStage.Over, h.Mission.Stage);
        Assert.Equal((short)1, winningSide.Kills);

        PumpUntil(h.Server, () => h.Bot.GameOvers.Count >= 1, 4000);
        var go = Assert.Single(h.Bot.GameOvers);
        Assert.Equal(h.KillerSide, go.SideWinner);
        Assert.Equal($"{winningSide.Name} won by killing enough of their opponents", go.GameoverMessage);

        Console.WriteLine($"KILLS-LIVE: side {h.KillerSide} ship {h.Killer.Id} killed enemy ship {victim.Id} (side {h.EnemySide}) -> " +
                          $"kills=1/1, mission Over, GAME_OVER(62) winner={go.SideWinner}");
    }

    /// <summary>⭐ Selection wiring regression: with <see cref="Mission.KillsEnabled"/>
    /// false (the default), a credited kill increments <see cref="Side.Kills"/>
    /// but does NOT end the mission — no GAME_OVER (existing non-deathmatch
    /// missions are unaffected).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kills_disabled_credited_kill_increments_counter_but_does_not_end_game()
    {
        Require.Artwork();
        using var h = SeatKiller();
        h.Mission.Goals.TeamKills = 0;

        var victim = SpawnEnemyVictim(h);
        h.Bot.ResetGameOvers();
        h.Mission.Site.KillShipEvent(h.Mission.LastUpdate, victim, h.Killer, 1.0f, victim.Position, Vector3.Zero);

        // The credited kill still bumps the side counter (the real kill-credit
        // path ran)…
        int counted = PumpUntil(h.Server, () =>
            h.Mission.Sides.Single(s => s.Id == h.KillerSide).Kills == 1, 3000);
        Assert.True(counted < 3000, "the credited kill never bumped the side kill counter");
        Assert.Equal((short)1, h.Mission.Sides.Single(s => s.Id == h.KillerSide).Kills);

        // …but with kills disabled the mission does NOT end and no GAME_OVER fires.
        Assert.Equal(MissionStage.Started, h.Mission.Stage);
        int neg = PumpUntil(h.Server, () => h.Bot.GameOvers.Count >= 1, 1500);
        Assert.True(neg == 1500, "kills-disabled credited kill must not fire GAME_OVER");
        Assert.Empty(h.Bot.GameOvers);

        Console.WriteLine($"KILLS-LIVE-DISABLED: side {h.KillerSide} scored 1 kill with KillsEnabled=false -> " +
                          $"stage {h.Mission.Stage}, no GAME_OVER (default unaffected)");
    }
}
