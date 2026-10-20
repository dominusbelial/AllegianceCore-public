using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public class SpectatorFrameLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly Lazy<Dictionary<short, string>> HullNames = new(() =>
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        var core = StaticCore.Load(file);
        return core.HullTypes.Values.ToDictionary(h => h.HullId, h => h.Name);
    });

    // The same fightable fighter + weapon selection as the reference
    // KillShipLiveTests (energy/ammo, fixed mount 0, real-damage projectile).
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon");
    });

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games = [new Gm { Name = "Spectator Frame", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 642u, DwCookie = 0xD0Eu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 81400000000000000ul, DrmHash = "specframe",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void InjectFightSpawn(Srv server, Vector3 shooterPos, Vector3 targetPos)
    {
        var placedShooter = false;
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            Vector3 pos = !placedShooter ? shooterPos : targetPos;
            short? sideId = !placedShooter ? mission.Sides[0].Id : null;   // target null-sided -> real death (no eject)
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = sideId };
            mission.PlaceShip(ship, mission.Sectors[0]);
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;
                ship.MountWeapon(weapon);
            }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
        field.SetValue(server, spawner);
    }

    /// <summary>
    /// P418 — prove the ClientWorld→spectator-JSON bridge against a real
    /// in-process server: a real ConnectionSession's <see cref="ClientWorld"/>
    /// is translated into the exact P44 <c>tick</c> frame the frontend renders
    /// (ship id/hull/side/pos/fwd/hp/alive all map from real wire data), and a
    /// real KILL_SHIP crossed over the wire produces a real <c>kill</c> frame.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Translates_ARealWorldAndARealKillShip()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectFightSpawn(server, new Vector3(0, 0, 0), new Vector3(0, 0, -180f));
        server.Boot(Config());

        using var shooter = new ConnectionSession();
        using var victim = new ConnectionSession();
        shooter.Connect(server.BoundPort, "spec_shooter");
        victim.Connect(server.BoundPort, "spec_victim");
        shooter.SendLogonRequest(Request("spec_shooter"));
        victim.SendLogonRequest(Request("spec_victim"));

        PumpUntil(server, () => shooter.World.MyShipId is not null && victim.World.MyShipId is not null, 4000);
        Assert.NotNull(shooter.World.MyShipId);
        Assert.NotNull(victim.World.MyShipId);

        // Resolve who is the shooter by spawn position (weapon mounted at origin).
        var shPos = shooter.World.Ships[shooter.World.MyShipId!.Value].Latest!.Position;
        var viPos = victim.World.Ships[victim.World.MyShipId!.Value].Latest!.Position;
        bool shooterAtOrigin = shPos.Z > viPos.Z;
        var gunner = shooterAtOrigin ? shooter : victim;
        var prey = shooterAtOrigin ? victim : shooter;
        short shooterId = gunner.World.MyShipId!.Value;
        short victimId = prey.World.MyShipId!.Value;
        Assert.NotEqual(shooterId, victimId);

        // Bring the mission to Started (both occupied sides ready), then fire.
        shooter.SendPlayerReady(fReady: true, shipId: shooterId);
        victim.SendPlayerReady(fReady: true, shipId: victimId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");

        // ── Tick-frame translation from the shooter's REAL world ──
        // Gather the snapshot under the session gate (the receive thread mutates
        // these tables; the translator itself is stateless).
        string tickJson;
        short shHullId;
        Vector3 shPosSnapshot;
        double simTime = 12.5;
        lock (gunner.Gate)
        {
            shHullId = gunner.World.Ships[shooterId].HullId;
            shPosSnapshot = gunner.World.Ships[shooterId].Latest!.Position;
            long counter = 0;
            tickJson = SpectatorFrame.BuildTickFrame(gunner.World, id => HullNames.Value.GetValueOrDefault(id, ""), simTime, ref counter);
        }

        using (var doc = JsonDocument.Parse(tickJson))
        {
            var root = doc.RootElement;
            Assert.Equal("tick", root.GetProperty("type").GetString());
            Assert.True(root.GetProperty("simTime").GetDouble() > 0);
            var ships = root.GetProperty("ships").EnumerateArray().ToList();
            Assert.NotEmpty(ships);
            var own = ships.First(s => s.GetProperty("id").GetInt16() == shooterId);
            Assert.Equal(HullNames.Value[shHullId], own.GetProperty("hull").GetString());
            Assert.Equal((short)0, own.GetProperty("side").GetInt16());          // shooter is side 0
            var pos = own.GetProperty("pos").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            Assert.Equal(3, pos.Length);
            Assert.Equal(MathF.Round(shPosSnapshot.X, 2), (float)pos[0]);
            Assert.Equal(3, own.GetProperty("fwd").GetArrayLength());
            Assert.True(own.GetProperty("hp").GetDouble() > 0);
            Assert.True(own.GetProperty("alive").GetBoolean());
            Assert.True(root.GetProperty("models").GetArrayLength() >= 0);
        }

        // ── Kill-frame translation from the victim's real KILL_SHIP ──
        // The gunner fights the (null-sided) victim to a real death.
        gunner.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        prey.CommandControls(ControlData.Neutral, 0);
        int died = PumpUntil(server,
            () => prey.World.Events.Kills.Any(k => k.ShipId == victimId) &&
                  prey.World.Ships.TryGetValue(victimId, out var e) && e.IsDead,
            4800);
        Assert.True(died < 4800, $"victim never died within {died} ticks");

        KillShip realKill;
        lock (prey.Gate)
            realKill = prey.World.Events.Kills.First(k => k.ShipId == victimId);

        string killJson;
        lock (prey.Gate)
            killJson = SpectatorFrame.BuildKillFrame(realKill, 13.0);

        using (var doc = JsonDocument.Parse(killJson))
        {
            var root = doc.RootElement;
            Assert.Equal("kill", root.GetProperty("type").GetString());
            Assert.Equal(victimId, root.GetProperty("victim").GetInt16());
            // The weapon's launcher is the real gunner ship, so the wire credits
            // it as the killer — exactly the real KILL_SHIP the frontend renders.
            Assert.Equal(shooterId, root.GetProperty("killer").GetInt16());
            Assert.True(root.GetProperty("simTime").GetDouble() > 0);
        }

        // ── Sector-frame translation from the shooter's real EXPORT tables ──
        string? sectorJson = null;
        lock (gunner.Gate)
        {
            short cluster = gunner.World.Ships[shooterId].SectorId;
            sectorJson = SpectatorFrame.BuildSectorFrame(gunner.World, cluster, id => $"c{id}");
        }
        Assert.NotNull(sectorJson);
        using (var doc = JsonDocument.Parse(sectorJson))
        {
            Assert.Equal("sector", doc.RootElement.GetProperty("type").GetString());
            Assert.True(doc.RootElement.GetProperty("stations").GetArrayLength() >= 0);
        }

        Console.WriteLine($"P418-SPECFRAME: shooter {shooterId} side0 maps {shHullId}->{HullNames.Value[shHullId]}; victim {victimId} kill killer={shooterId}");
    }
}
