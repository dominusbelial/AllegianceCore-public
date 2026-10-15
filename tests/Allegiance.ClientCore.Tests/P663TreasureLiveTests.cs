using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P663TreasureLiveTests
{
    private static void Pump(Srv server, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "loopback client did not receive the expected treasure state");
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1, SteamAuthTicket = new byte[16],
        DrmHash = "p663", SteamID = 9530000000000000000ul,
    };

    [SkippableFact, Trait("Requires", "Artwork")]
    public void AmbientGeneration_AndArtifactCaptureExpiryAndDeathRespawn_ReachRealClients()
    {
        Require.Artwork();
        using var server = new Srv();
        int spawnIndex = 0;
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server,
            (Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)>)((core, m, id) =>
            {
                var neutral = m.Sectors.Where(s => !s.Cluster.HomeSector).ToArray();
                var sector = spawnIndex < neutral.Length ? neutral[spawnIndex] : m.Sectors.First(s => s.Cluster.HomeSector);
                spawnIndex++;
                var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
                var ship = new Ship(id, hull, new Vector3(3500, 3500, 500), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                m.PlaceShip(ship, sector);
                return (ship, sector);
            }));
        server.Boot(new()
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Games = [new() { Name = "P663", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
                Treasure = new() { PlayerSectorTreasureRate = 0, NeutralSectorTreasureRate = 1f / 60 },
                MapGen = new() { Type = "SingleRing", ArtifactsGame = true,
                    PlayerSectorMineableAsteroids = 0, PlayerSectorSpecialAsteroids = 0, PlayerSectorAsteroids = 0,
                    NeutralSectorMineableAsteroids = 0, NeutralSectorSpecialAsteroids = 0, NeutralSectorAsteroids = 0 } }]
        });
        var m = server.Missions[0];
        m.Stage = MissionStage.Started;
        // Map population does not opt into a zero-goal victory condition.
        Assert.False(m.ArtifactEnabled);
        m.ArtifactEnabled = true;
        m.ArtifactWinGoal = 99;
        m.ApplyStartingMoneyAndInitialPayday(0);
        using var one = new ConnectionSession();
        using var two = new ConnectionSession();
        using var actor = new ConnectionSession();
        var sessions = new[] { one, two, actor };
        for (int i = 0; i < sessions.Length; i++)
        {
            var session = sessions[i];
            session.Connect(server.BoundPort, "T663" + i);
            session.SendLogonRequest(Request("T663" + i));
            Pump(server, () => { lock (session.Gate) return session.World.MyShipId is not null; });
        }
        var neutralSectors = m.Sectors.Where(s => !s.Cluster.HomeSector).ToArray();
        Assert.Equal(2, neutralSectors.Length);
        for (int i = 0; i < 2; i++)
        {
            var session = sessions[i];
            Pump(server, () => { lock (session.Gate) return session.World.Treasures.Values.Any(t => t.Code == TreasureCode.Flag); });
        }
        // A real loaded-core payday generates in both neutral sectors, never home.
        // Anchor the measured window after joins, independent of TCP join delays.
        m.ApplyStartingMoneyAndInitialPayday(m.LastUpdate);
        var before = m.Sectors.SelectMany(s => s.Models.OfType<Treasure>()).Select(t => t.Id).ToHashSet();
        for (int i = 0; i < 1220; i++) server.TickSim();
        var ambient = m.Sectors.SelectMany(s => s.Models.OfType<Treasure>()).Where(t => !before.Contains(t.Id)).ToArray();
        Assert.Equal(2, ambient.Length);
        Assert.All(ambient, t => Assert.False(t.OwnerSector.Cluster.HomeSector));
        foreach (var t in ambient) Seen(t);

        // Capture through the production docking site callback respawns one artifact.
        var carrier = server.Ships.Single(s => s.Ship.Id == actor.World.MyShipId).Ship;
        var ownStation = carrier.Sector!.Stations.First(s => s.SideId == carrier.SideId);
        carrier.Flag = Side.ArtifactSentinelFlag;
        var oldFlags = Flags();
        Assert.True(m.Site.DockWithStationEvent(carrier, new DockStation(ownStation)));
        Assert.Null(carrier.Flag);
        Assert.Equal(1, m.Sides.Single(s => s.Id == carrier.SideId).Artifacts);
        Seen(NewFlag(oldFlags));

        // Native expiry retains the payload and respawns; pickup does not respawn.
        var expired = NewFlag(oldFlags);
        oldFlags = Flags();
        expired.Update(expired.ExpireAt);
        Assert.DoesNotContain(expired, expired.OwnerSector.Models);
        Seen(NewFlag(oldFlags));

        // The previously deferred out-of-bounds death leg reaches the same generator.
        carrier.StationId = null;
        carrier.Position = new Vector3(1000000, 0, 0);
        carrier.Flag = Side.ArtifactSentinelFlag;
        oldFlags = Flags();
        m.Site.KillShipEvent(m.LastUpdate, carrier, null, 1, carrier.Position, Vector3.Zero);
        Seen(NewFlag(oldFlags));

        HashSet<short> Flags() => m.Sectors.SelectMany(s => s.Models.OfType<Treasure>())
            .Where(t => t.Code == TreasureCode.Flag).Select(t => t.Id).ToHashSet();
        Treasure NewFlag(HashSet<short> old) => Assert.Single(m.Sectors.SelectMany(s => s.Models.OfType<Treasure>()),
            t => t.Code == TreasureCode.Flag && !old.Contains(t.Id));
        void Seen(Treasure treasure)
        {
            int i = Array.IndexOf(neutralSectors, treasure.OwnerSector);
            Assert.InRange(i, 0, 1);
            var watcher = sessions[i];
            Pump(server, () => { lock (watcher.Gate) return watcher.World.Treasures.ContainsKey(treasure.Id); });
            lock (watcher.Gate) Assert.Equal(treasure.Position, watcher.World.Treasures[treasure.Id].Position);
        }
    }
}
