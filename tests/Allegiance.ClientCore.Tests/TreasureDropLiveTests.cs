using System.Reflection;
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

/// <summary>
/// P293 — the real outbound + inbound round trip that makes a treasure appear
/// in the <em>client's</em> world: a real <see cref="ConnectionSession"/>
/// sends <see cref="ConnectionSession.SendDropPart"/> (FM_CS_DROP_PART(33)) for
/// a ship carrying a mounted weapon; the server's <c>HandleDropPart</c>
/// (<c>Server.cs:6961</c>) removes the part, creates a real part-treasure
/// (<c>Ship.CreateDropTreasure</c>) and broadcasts <c>FM_S_TREASURE(28)</c>
/// sector-wide (<c>Server.cs:7014</c>); the client decodes it and upserts a
/// <see cref="TreasureEntity"/> into <see cref="ClientWorld.Treasures"/>. This
/// is the P293 double-wiring proof — it reds if the ConnectionSession
/// <c>case TreasureSpawn.Fmid:</c> or the <c>ClientWorld.Apply(TreasureSpawn)</c>
/// arm is removed.
/// </summary>
[Collection("LiveNetwork")]
public class TreasureDropLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    // XXV_simple (measured, P111 report): 1052 = no-station fly sector.
    private const short FlySector = 1052;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "Treasure Drop Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 951u, DwCookie = 0x951u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9530000000000000000ul, DrmHash = "treasuredropclient",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ShipSpawner is `internal` and this assembly isn't friend-granted — reach
    // it through reflection, the same recipe ChaffDropLiveTests uses.
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    /// <summary>⭐ P293 — the real wire round trip: a <see cref="ConnectionSession"/>
    /// drops a mounted weapon via FM_CS_DROP_PART(33); the server creates a real
    /// part-treasure and broadcasts FM_S_TREASURE(28) sector-wide; the client
    /// world hydrates <see cref="ClientWorld.Treasures"/> at the treasure's real
    /// position. This is the exact path that puts a treasure in the 3D scene.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SendDropPart_ClientWorldReceivesTreasureSpawn_OverTheWire()
    {
        Require.Artwork();
        using var server = new Srv();

        Ship? placedShip = null;
        short? weaponId = null;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First();
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var ship = new Ship(shipId, hull, new Vector3(810f, 820f, 930f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, fly);
            // Mount a real core weapon at mount 0 so DROP_PART has something to drop.
            var wp = core.PartTypes.Values
                .Where(p => p.EquipmentType == EquipmentType.Weapon)
                .OrderBy(p => p.PartId)
                .First();
            ship.MountPart(wp, amount: 20, mountId: 0, fractionMounted: 255);
            placedShip = ship;
            weaponId = wp.PartId;
            return (ship, fly);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "TRESDROP");
        session.SendLogonRequest(Request("TRESDROP"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        Assert.NotNull(placedShip);
        Assert.NotNull(weaponId);

        // Client has not yet seen any treasure.
        lock (session.Gate) Assert.Empty(session.World.Treasures);

        // Press the drop key equivalent: one DROP_PART(33) over the real transport.
        session.SendDropPart(EquipmentType.Weapon, mount: 0);

        // The CLIENT's own world model must now receive the TREASURE(28) broadcast
        // and track the dropped part-treasure at its real position.
        int received = PumpUntil(server, () =>
        {
            lock (session.Gate) return session.World.Treasures.Count > 0;
        }, 2000);
        Assert.True(received < 2000, "client world never received TREASURE(28) — P293 tracking not wired");

        TreasureEntity? te;
        lock (session.Gate)
        {
            Assert.Single(session.World.Treasures);
            te = session.World.Treasures.Values.First();
        }
        Assert.NotNull(te);
        Assert.Equal(TreasureCode.Part, te!.Code);           // c_tcPart (FedSrv.CPP:4367)
        Assert.Equal(weaponId.Value, te.PayloadId);          // part's PartId
        Assert.Equal((short)20, te.Amount);                  // dropped part's Amount
        Assert.Equal(FlySector, te.ClusterId);               // pcluster id (FedSrv.CPP:4335)
        // The treasure P0 sits offset from the ship (~radius + 10), so it is NOT
        // exactly at the ship's spawn point — it is a distinct real position.
        Assert.True((te.Position - placedShip!.Position).LengthSquared() > 4f,
            $"treasure P0 {te.Position} should be offset from the ship {placedShip.Position}");

        // P663: the real DROP_PART trajectory must move on both sides, including
        // its server-ms creation epoch. Compare at an explicitly synchronized
        // instant so transport latency does not affect the analytical assertion.
        var dropped = placedShip!.Sector!.Models.OfType<Treasure>().Single(t => t.Id == te.TreasureId);
        var origin = dropped.Position;
        double target = Math.Max(server.Missions[0].LastUpdate, dropped.CreatedAt + 1);
        while (server.Missions[0].LastUpdate < target) server.TickSim();
        target = server.Missions[0].LastUpdate;
        lock (session.Gate)
        {
            session.World.NowSeconds = () => 0;
            session.World.ObserveServerTime((uint)(target * 1000), 0);
            Assert.True((dropped.Position - origin).Length() > 1);
            Assert.InRange((te.Position - dropped.Position).Length(), 0, 0.1f);
            Assert.InRange((te.Velocity - dropped.Velocity).Length(), 0, 0.1f);
        }

        // A late sector entry exports the ORIGINAL trajectory plus a clock
        // sample, rather than restarting motion from P0 at receipt time.
        using var late = new ConnectionSession();
        late.World.NowSeconds = () => 0;
        late.Connect(server.BoundPort, "TRESDROP-LATE");
        late.SendLogonRequest(Request("TRESDROP-LATE"));
        PumpUntil(server, () =>
        {
            lock (late.Gate) return late.World.Treasures.ContainsKey(dropped.Id);
        }, 4000);
        lock (late.Gate)
        {
            Assert.True(late.World.Treasures.ContainsKey(dropped.Id), "late joiner missed the existing treasure export");
            var samples = late.TakePingEchoes();
            Assert.NotEmpty(samples);
            double age = samples[^1].ServerMs / 1000.0 - dropped.CreatedAt;
            Assert.True(age >= 1, "late export must retain at least one second of prior motion");
            var expected = TreasureMotion.PositionAt(TreasureMotion.Vector(dropped.Data.P0),
                TreasureMotion.Vector(dropped.Data.V0), age);
            Assert.InRange((late.World.Treasures[dropped.Id].Position - expected).Length(), 0, 0.01f);
        }

        Console.WriteLine($"TREASUREDROP-CLIENT-LIVE(P293): client world.Treasures hydrated from TREASURE(28): code={te.Code} part={te.PayloadId} amt={te.Amount} at ({te.Position.X:F1},{te.Position.Y:F1},{te.Position.Z:F1})");
    }
}
