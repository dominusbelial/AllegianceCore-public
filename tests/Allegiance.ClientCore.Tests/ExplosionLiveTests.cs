using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P263 — live proof that a real server-side destruction event reaches the
/// MonoGame client as a real <see cref="ExplosionEvent"/>(207): a real ship
/// death (lifepod-hulled peer suicide riding the combat kill path) makes the
/// server broadcast EXPLOSION_EVENT to the same-sector audience, and the
/// suiciding peer's OWN <see cref="ConnectionSession"/> ingests it into
/// <c>world.Explosions</c>. This is the end-to-end discriminator for the P263
/// server wiring: without the <c>SendExplosionEvent</c> call in
/// <c>BroadcastKillShip</c>, the world never sees an explosion.
/// </summary>
[Collection("LiveNetwork")]
public class ExplosionLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Explosion Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 922u, DwCookie = 0x922u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9300000000000000000ul, DrmHash = "explosionclient",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RealKill_BroadcastsExplosionEvent_ReceivedByPeerWorld()
    {
        Require.Artwork();
        using var server = new Srv();
        // A lifepod-hulled ship so the suicide is a real death (not an in-space
        // eject) — the P107 note; in this core the first lifepod hull is also a
        // fighter, so native's c_habmFighter split yields c_etSmallShip.
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var ship = new Ship(shipId, hull, new Vector3(750f, 760f, 940f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "EXPLODER");
        session.SendLogonRequest(Request("EXPLODER"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "exploder must be assigned a ship");

        // Press the kill button: a real death over the real transport.
        session.SendSuicide();

        // The server's BroadcastKillShip → SendExplosionEvent must deliver a
        // real EXPLOSION_EVENT(207) that the peer's own world ingests.
        int boom = PumpUntil(server, () => session.World.Explosions.Count > 0, 2000);
        Assert.True(boom < 2000, $"no EXPLOSION_EVENT arrived over the wire (boom={boom})");

        var ex = Assert.Single(session.World.Explosions);
        Assert.Equal(ExplosionType.SmallShip, ex.Type);   // fighter hull → c_etSmallShip
        Assert.Equal((short)1051, ex.ClusterId);          // XXV_simple spawn sector
        Assert.Equal(750f, ex.Position.X, 1);
        Assert.Equal(760f, ex.Position.Y, 1);
        Assert.Equal(940f, ex.Position.Z, 1);
        Console.WriteLine($"EXPLOSION-LIVE: real kill produced EXPLOSION_EVENT(207) type {ex.Type} " +
                          $"in the peer's world after {boom} ticks");
    }
}
