using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P469 — the quick-chat-to-drone command proof: an Individual-family chat that
/// names a <b>same-side NPC</b> with a legal <c>CommandID</c> makes that drone
/// OBEY immediately (the port of native <c>CommandToDrone</c>,
/// FedSrv.CPP:3175-3325, called from <c>ForwardChatMessage</c>'s
/// CHAT_INDIVIDUAL branch when the recipient is a non-player,
/// FedSrv.CPP:3557-3573). The drone maps the command onto its sim arm and
/// replies <c>"Affirmative."</c> to the commander. Contrast the P457 queued-order
/// flow, which targets same-side <b>real players</b> (free will → accept/reject);
/// P469 is the direct immediate obedience of a drone.
/// <list type="bullet">
/// <item><b>Primary leg</b> — a commander on side A sends an Individual
/// <c>cidAttack</c> chat at a same-side Wingman NPC, targeting an enemy ship on
/// side B. The Wingman's <see cref="Ship.AttackTarget"/> is set to the enemy AND
/// the commander's client receives the drone's <c>"Affirmative."</c> chat.</item>
/// <item><b>Illegal leg</b> — an Individual <c>cidMine</c> chat to the same
/// Wingman is refused (a Wingman cannot mine; native <c>LegalCommand</c> Mine is
/// Miner-only, shipIGC.h:1711): no slot change, no <c>"Affirmative."</c>.</item>
/// <item><b>Enemy-side sender leg</b> — a sender on the <b>other</b> side
/// sending an Individual attack chat at the drone is ignored (native same-side
/// gate, FedSrv.CPP:3190-3191): no slot change, no <c>"Affirmative."</c>.</item>
/// </list>
/// Sides are DERIVED from each bot's real <c>SideId</c> after boot (P457's
/// lesson: never assume the round-robin <c>ShipSpawner</c> matches connect
/// order under load), so every test is robust to which bot lands on which side.
/// </summary>
[Collection("LiveNetwork")]
public sealed class LiveQuickChatToDroneCommandTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P469 QuickChat To Drone", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, string drm) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 769u, DwCookie = 0x769u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 76500000000000000ul, DrmHash = drm,
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short? SideOf(Srv server, FltBot bot) =>
        server.Ships.First(l => l.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship.SideId;

    private static short ShipId(FltBot bot) => bot.ShipAssignment!.Ship.ShipId;

    private static bool GotAffirmative(FltBot bot, short droneId) =>
        bot.Chats.Any(c => c.Message == "Affirmative." && c.SidSender == droneId && c.VoiceOver == 1178);

    /// <summary>Boot an in-process server; seats <paramref name="peerCount"/>
    /// bots round-robin onto the two sides (peer k → <c>Sides[k % 2]</c>). Returns
    /// the server, mission, first sector, both side ids, the Wingman drone-type
    /// id, and the seated bots.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, short Side0, short Side1, short WingDroneId, List<FltBot> Bots)
        Boot(int peerCount)
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short side0 = 0, side1 = 0, wing = 0;
        int placed = 0;
        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            side0 = m.Sides[0].Id;
            side1 = m.Sides[1].Id;
            wing = m.Core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeWingman).Key;
            short side = (placed % 2 == 0) ? side0 : side1;
            placed++;
            var hull = m.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(980f, 634f, 619f), Vector3.Zero, Orientation.Identity) { SideId = side };
            m.PlaceShip(ship, m.Sectors[0]);
            return (ship, m.Sectors[0]);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180)

        var bots = new List<FltBot>();
        for (int i = 0; i < peerCount; i++)
        {
            var b = new FltBot();
            b.Connect(server.BoundPort, $"p469_{i}");
            b.SendLogonRequest(Request($"P469P{i}", $"p469drm{i}"));
            bots.Add(b);
        }
        Pump(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        return (server, mission!, sector!, side0, side1, wing, bots);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void P676_Individual_repair_orders_ship_or_station_and_affirms_over_tcp(bool stationTarget)
    {
        Require.Artwork();
        var (server, mission, sector, _, _, wing, bots) = Boot(1);
        using (server)
        using (var commander = bots[0])
        {
            short side = SideOf(server, commander)!.Value;
            var wingman = server.SpawnNpcShip(mission, sector, side, new Vector3(710, 605, 542), wing);
            var charge = server.Ships.Single(p => p.Ship.Id == ShipId(commander)).Ship;
            var type = mission.Core.StationTypes.Values.First();
            var data = DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 31000, StationTypeId = type.StationTypeId, ClusterId = sector.Id,
                SideId = side, Position = [500, 500, 500] };
            var station = new Station(data, type); mission.PlaceStation(station, sector);
            commander.SendChat("repair", ChatTarget.Individual, oidRecipient: wingman.Id,
                commandId: OrderChange.CidRepair,
                oidTarget: stationTarget ? station.StationId : charge.Id,
                otTarget: (short)(stationTarget ? IgcObjectType.Station : IgcObjectType.Ship));
            Pump(server, () => GotAffirmative(commander, wingman.Id), 3000);
            Assert.True(GotAffirmative(commander, wingman.Id));
            if (stationTarget) Assert.Same(station, wingman.RepairStationTarget);
            else Assert.Same(charge, wingman.RepairTarget);
            Assert.Null(wingman.AttackTarget);
        }
    }

    /// <summary>⭐ Primary leg: a commander on side A orders a same-side Wingman
    /// NPC (Individual <c>cidAttack</c>, targeting an enemy ship on side B). The
    /// Wingman's <see cref="Ship.AttackTarget"/> becomes that enemy and the
    /// commander's client receives the drone's <c>"Affirmative."</c> reply. The
    /// wingman and enemy sit in the SAME sector so the Wingman's per-tick combat
    /// validation keeps <see cref="Ship.AttackTarget"/> set (native pursuit is
    /// same-sector-only, so a cross-sector target is immediately dropped). The
    /// discriminating P469 proof is the drone's "Affirmative." reply — it can
    /// ONLY be produced by the order path (proved red when the wiring is
    /// removed), whereas an idle Wingman could autonomously pick the nearest
    /// in-sector enemy (P162); the <c>AttackTarget</c> assertion corroborates.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Individual_Attack_OrdersSameSideWingman_AndAffirms()
    {
        Require.Artwork();
        var (server, mission, sector, side0, side1, wing, bots) = Boot(1);
        using (server)
        {
            var commander = bots[0];
            short commanderSide = SideOf(server, commander)!.Value;
            short enemySide = commanderSide == side0 ? side1 : side0;

            // The Wingman drone on the commander's OWN side, same sector.
            var wingman = server.SpawnNpcShip(mission, sector, commanderSide, new Vector3(710f, 605f, 542f), wing);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);

            // An enemy ship on the OTHER side, in the same sector (so the set
            // AttackTarget is not immediately dropped by the same-sector pursuit).
            var enemyHull = mission.Core.HullTypes.Values.OrderByDescending(h => h.Length).First();
            var enemy = new Ship((short)5001, enemyHull, new Vector3(838f, 623f, 554f), Vector3.Zero, Orientation.Identity) { SideId = enemySide };
            mission.PlaceShip(enemy, sector);

            // Commander issues an Individual cidAttack at the same-side wingman,
            // targeting the enemy ship.
            commander.SendChat("attack target", ChatTarget.Individual,
                oidRecipient: wingman.Id, commandId: OrderChange.CidAttack,
                oidTarget: enemy.Id, otTarget: (short)IgcObjectType.Ship);

            // Wait on the drone's "Affirmative." reply reaching the commander's
            // client. The reply is sent only AFTER the sim arm is set, so its
            // arrival guarantees the whole order path ran (AttackTarget is set
            // first). Pumping on the bot's accumulator alone (not the server
            // state) avoids exiting on the server-side set before the async
            // receive thread has delivered the chat (P186 lesson).
            Pump(server, () => GotAffirmative(commander, wingman.Id), 3000);
            Assert.Same(enemy, wingman.AttackTarget);
            Assert.True(GotAffirmative(commander, wingman.Id),
                "the commanding player's client must receive the drone's \"Affirmative.\"");

            Console.WriteLine($"P469: commander {ShipId(commander)} (side {commanderSide}) Individual-cidAttack -> same-side wingman {wingman.Id} at enemy {enemy.Id} (side {enemySide}): AttackTarget set + Affirmative");
        }
    }

    /// <summary>Illegal leg: an Individual <c>cidMine</c> chat at the same
    /// Wingman is refused (Mine is Miner-only per native LegalCommand,
    /// shipIGC.h:1711) — no slot change and no <c>"Affirmative."</c>. The
    /// Wingman sits in the last sector (no enemies) so it stays idle and its
    /// own default order cannot set any target.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mine_ToWingman_IsIllegal_NoChange_NoAffirmative()
    {
        Require.Artwork();
        var (server, mission, sector, _, _, wing, bots) = Boot(1);
        using (server)
        {
            var commander = bots[0];
            short commanderSide = SideOf(server, commander)!.Value;
            var lastSector = mission.Sectors[mission.Sectors.Count - 1];
            var wingman = server.SpawnNpcShip(mission, lastSector, commanderSide, new Vector3(823f, 633f, 491f), wing);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);

            short rockId = sector.Mineable.Count > 0 ? sector.Mineable[0].Data.AsteroidId : (short)1;

            commander.SendChat("mine", ChatTarget.Individual,
                oidRecipient: wingman.Id, commandId: OrderChange.CidMine,
                oidTarget: rockId, otTarget: (short)IgcObjectType.Asteroid);

            Pump(server, () => false, 60);   // let any (wrong) effect land

            Assert.Null(wingman.MiningAsteroidTarget);
            Assert.Null(wingman.AttackTarget);
            Assert.False(GotAffirmative(commander, wingman.Id),
                "an illegal command must not earn an \"Affirmative.\"");
            Console.WriteLine($"P469: commander {ShipId(commander)} (side {commanderSide}) Individual-cidMine -> wingman {wingman.Id} refused (Mine is Miner-only): no change, no Affirmative");
        }
    }

    /// <summary>Enemy-side sender leg: a sender on the OTHER side issuing an
    /// Individual <c>cidAttack</c> at the drone is ignored (native same-side
    /// gate, FedSrv.CPP:3190-3191) — no slot change, no <c>"Affirmative."</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EnemySide_Sender_IndividualAttack_IsIgnored()
    {
        Require.Artwork();
        var (server, mission, sector, _, _, wing, bots) = Boot(2);
        using (server)
        {
            var commander = bots[0];
            short commanderSide = SideOf(server, commander)!.Value;

            // The enemy-side sender: whichever of the two bots is on the OTHER
            // side (exactly one, since two bots round-robin onto two sides).
            var enemyCommander = bots.First(b => SideOf(server, b)!.Value != commanderSide);

            var lastSector = mission.Sectors[mission.Sectors.Count - 1];
            var wingman = server.SpawnNpcShip(mission, lastSector, commanderSide, new Vector3(759f, 649f, 483f), wing);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);

            enemyCommander.SendChat("attack", ChatTarget.Individual,
                oidRecipient: wingman.Id, commandId: OrderChange.CidAttack,
                oidTarget: ShipId(commander), otTarget: (short)IgcObjectType.Ship);

            Pump(server, () => false, 60);   // let any (wrong) effect land

            Assert.Null(wingman.AttackTarget);
            Assert.False(GotAffirmative(enemyCommander, wingman.Id),
                "an enemy-side sender must not be obeyed (nor affirmed)");
            Console.WriteLine($"P469: enemy-side sender {ShipId(enemyCommander)} (side {SideOf(server, enemyCommander)}) Individual-cidAttack -> wingman {wingman.Id} (side {commanderSide}) ignored (same-side gate)");
        }
    }
}
