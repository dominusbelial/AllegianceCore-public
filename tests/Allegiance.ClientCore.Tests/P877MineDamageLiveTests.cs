using System.Reflection;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;
using Gm = Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>P877 live proof: a CHASED bot mines its wake and the mine damages
/// its pursuer on the real in-process server. The pursued scout flies with an
/// astern pursuer chasing it plus a wingman making it locally outmatched; the
/// dispenser law drops the prox mine at the ship's position (a dispenser drops
/// at the ship — Igc/dispenserigc.cpp), the pursuer flies through it, and the
/// mine blast damages the pursuer (Sector.ResolveMineDetonations →
/// Ship.ReceiveDamage with the mine's launcher). Also proves the P877 coverage
/// counters light up from real wire traffic (the pursuer's own VictimDamaged
/// unicast attributes the blast to the mine's launcher).</summary>
[Collection("P877Coverage")]
public sealed class P877MineDamageLiveTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, Time = 877,
        DwCookie = 877, CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8770000000000000000ul, DrmHash = "p877",
    };

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Chased_bot_wake_mine_damages_pursuer()
    {
        Require.Artwork();
        using var server = new Srv();
        var core0 = StaticCore.Load(IgcReader.ParseFile(Path.Combine(
            ExternalPaths.Resolve(ExternalResourceKind.Artwork), "PCore001.igc")));
        short scoutHull = core0.HullTypes.Values.First(h => h.Name == "Scout").HullId;

            // First spawned ship = the pursued scout (side 0, prox mines mounted,
        // FreeSpawn — no purchase path involved), nose −Z, in open space; the
        // second = its pursuer (side 1), 400 u dead astern (+Z) facing the same
        // way; the third = an unflown pursuer wingman that only makes the bot
        // locally outmatched (two enemies vs one friend).
        int spawned = 0;
        Ship botShip = null!, pursuerShip = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First(h => h.HullId == scoutHull);
            Vector3 basePos = new(750f, 760f, 940f);
            var ship = spawned switch
            {
                0 => botShip = new Ship(shipId, hull, basePos, Vector3.Zero, Orientation.Identity)
                { SideId = mission.Sides[0].Id },
                _ => pursuerShip = new Ship(shipId, hull, basePos + new Vector3(0, 0, 400), Vector3.Zero, Orientation.Identity)
                { SideId = mission.Sides[1].Id },
            };
            spawned++;
            // The pursuers carry guns. The scout's prox-mine dispenser is
            // mounted AFTER its client joined (below) so the real same-side
            // LOADOUT_CHANGE broadcast populates the client's Loadout — at
            // spawn time the mission site is not wired yet.
            if (spawned >= 2)
            {
                var gun = core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon && p.GroupId == 0);
                ship.MountPart(gun, 1, 0, ExpandedPartData.FullyMounted, freeSpawn: true);
            }
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
        server.Boot(new()
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            SideSelectAutoSeatSeconds = 30,
            Games = [new Gm { Name = "P877 wake mines", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var botCs = new ConnectionSession();
        using var pursuerCs = new ConnectionSession();
        botCs.Connect(server.BoundPort, "p877-bot");
        botCs.SendLogonRequest(Request("p877-bot"));
        pursuerCs.Connect(server.BoundPort, "p877-pursuer");
        pursuerCs.SendLogonRequest(Request("p877-pursuer"));
        Pump(server, () => botCs.World.MyShipId is not null && pursuerCs.World.MyShipId is not null);
        short botId = botCs.World.MyShipId!.Value, pursuerId = pursuerCs.World.MyShipId!.Value;

        // The dispenser law reads the real mounted inventory.
        botCs.World.PartTypeResolver = id => Catalog.RepresentativeCore().TryBuildExpendableLauncher(id, out var p) ? p : null;
        var botType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
        var bot = Activator.CreateInstance(botType, Fields, null,
            ["p877-bot", scoutHull, "fighter", (short)0, (short)0, "combat", 0.0], null)!;
        botType.GetField("Cs", Fields)!.SetValue(bot, botCs);
        botType.GetField("ShipId", Fields)!.SetValue(bot, botId);
        botType.GetField("WingmanBehaviour", Fields)!.SetValue(bot, Showcase.WbbmUseMines);
        botType.GetField("SpawnPending", Fields)!.SetValue(bot, false);

        var mission = server.Missions[0];
        // The scout's prox-mine dispenser: mounted server-side (FreeSpawn — no
        // purchase path involved); the real same-side LOADOUT_CHANGE broadcast
        // carries it to the bot's own ClientWorld.
        Assert.True(core0.TryBuildExpendableLauncher(59, out var mineType), "prox mine launcher missing from the core");
        botShip.MountPart(mineType, 5, 0, ExpandedPartData.FullyMounted, freeSpawn: true);
        Pump(server, () => botCs.World.Ships.TryGetValue(botId, out var me2) && (me2.Loadout?.Count ?? 0) > 0);
        // The bot is genuinely in danger: chew its hull down server-side so the
        // dispenser law's low-hull danger gate reflects real server state.
        botShip.ReceiveDamage(16, botShip.EffectiveMaxHitPoints() * 0.7f, 1.0,
            botShip.Position, botShip.Position, null);

        // Fly: the bot flees full-throttle along its nose (−Z); the pursuer
        // chases it through the bot's wake; the wingman sits still. Identical
        // hulls + identical throttle hold the gap (closing-or-holding gate).
        long drops = 0, hits = 0;
        int ticks = 0;
        float pursuerHullAtStart = 1f;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(45) && ticks < 20000)
        {
            server.TickSim();
            ticks++;
            lock (botCs.Gate)
            {
                if (botCs.World.Ships.TryGetValue(botId, out var me) && me.Latest is not null
                    && me.StationId < 0)
                    botCs.CommandControls(new ControlData(0f, 0f, 0f, 1f), 0);
            }
            lock (pursuerCs.Gate)
            {
                if (pursuerCs.World.Ships.TryGetValue(pursuerId, out var me) && me.Latest is not null
                    && me.StationId < 0
                    && botCs.World.Ships.TryGetValue(botId, out var prey) && prey.Latest is not null)
                {
                    pursuerHullAtStart = Math.Min(pursuerHullAtStart, me.Latest.HullFraction);
                    pursuerCs.CommandControls(AutopilotSteering.Pursue(
                        prey.Latest.Position, me.Latest.Orientation, prey.Latest.Position), 0);
                }
            }
            // The dispenser law runs on the harness cadence (the manager loop's
            // ~1 Hz decision tick in production).
            if (ticks % 20 == 0)
            {
                MaybeUseDispensers(bot, botCs, ticks / 20.0);
                lock (botCs.Gate)
                {
                    if (botCs.World.Mines.Values.Any(mn => mn.LauncherShipId == botId))
                        drops++;
                }
            }
            // The pursuer's own VictimDamaged unicast is what attributes mine
            // blast damage to the mine's launcher (this bot).
            if (ticks % 40 == 0)
                WeaponCoverage.Global.SweepBot(botCs);   // the LAUNCHER's sweep watches its victims
            hits = WeaponCoverage.Global.EscapeMineHits;
            if (hits > 0 && pursuerHullAtStart < 1f) break;
        }

        Assert.True(drops > 0, "the chased bot never dropped a wake mine");
        lock (pursuerCs.Gate)
        {
            var pursuer = pursuerCs.World.Ships[pursuerId];
            Assert.True(pursuer.Latest is { } lu && lu.HullFraction < 1f,
                $"the pursuer never took wake-mine damage (hull={pursuer.Latest?.HullFraction})");
        }
        Assert.True(hits > 0, "escape-mine coverage counters never lit up");
        foreach (var line in WeaponCoverage.Global.P877Lines()) Console.WriteLine($"P877-COUNTERS {line}");
    }

    private static void MaybeUseDispensers(object bot, ConnectionSession cs, double now)
    {
        var m = typeof(BotManager).GetMethod("MaybeUseDispensers", BindingFlags.Static | BindingFlags.NonPublic)!;
        m.Invoke(null, [bot, cs, now]);
    }

    private static void Pump(Srv server, Func<bool> done, int maxTicks = 8000)
    {
        int n = 0;
        while (!done() && n < maxTicks) { server.TickSim(); n++; Thread.Sleep(1); }
        Assert.True(done(), "P877 loopback join condition timed out");
    }
}
