using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P803 (SIM-0470) — the SERVER half of the HEAVY_SHIPS_UPDATE(176)
/// active-turret relay, pinned against native
/// <c>FedSrv.CPP:4049-4071</c> (the fill) and <c>:3783-3790</c> (the
/// ±1.5 s freshness gate that filled <c>rgActiveTurrets</c>): every boarded
/// gunner of the recipient's cluster with a stored, still-fresh
/// ACTIVE_TURRET_UPDATE(99) is relayed INTACT (controls + orientation, CompactTime
/// delta from the message's timeReference) to every OTHER peer — never the
/// gunner's own connection (FedSrv.CPP:4058). Fails on master (the relay was
/// the literal empty array).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P803TurretRelayBroadcastTests
{
    private static ServerConfig Config() => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Games = [new() { Name = "P803 turret relay", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Fresh_boarded_gunners_are_relayed_to_other_peers_never_to_themselves()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var sector = server.TestSectors[0];
        var mission = server.TestMission!;
        var hull = mission.Core.HullTypes.Values.First();

        // The carrier P (a plain ship), its gunner G, and an unboarded viewer V.
        var carrier = new Ship((short)10001, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var gunner = new Ship((short)10002, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var viewer = new Ship((short)10003, hull, new Vector3(90, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        gunner.ParentShip = carrier;                       // boarded gunner on mount 2
        gunner.TurretId = 2;
        mission.PlaceShip(carrier, sector);
        mission.PlaceShip(gunner, sector);
        mission.PlaceShip(viewer, sector);

        var connG = new ConnectionId(700u);
        var connV = new ConnectionId(701u);
        server.TestSeedPeers([(connG, gunner, sector), (connV, viewer, sector)]);

        // The stored P109 update: fresh (dt = 0). Native keeps m_atu until a
        // fresher one replaces it (fsship.cpp:1215) and gates the relay on
        // |atu.time − g.timeNow| ≤ 1.5 (FedSrv.CPP:3786-3789).
        var facing = new Orientation();
        facing.Set(new Vector3(0f, 0f, -1f), new Vector3(0f, 1f, 0f));
        var controls = new ControlData(0.25f, -0.5f, 0f, 1f);
        server._lastTurretUpdates[gunner.Id] =
            ((uint)Math.Round((mission.LastUpdate + 0.1) * 1000.0), controls, facing);

        var packets = server.TestCollectBroadcast(10, parallel: false, "shipsUpdate");
        var heavyFor = (ConnectionId c) => packets[c]
            .SelectMany(p => FedPacket.Unpack(p))
            .Where(m => (ushort)(m[2] | m[3] << 8) == HeavyShipsUpdate.Fmid)
            .Select(m => HeavyShipsUpdate.Decode(m)).Single();

        // The GUNNER's own connection: native skips pfsShip == pfsPlayer
        // (FedSrv.CPP:4058) — no self-relay.
        Assert.Empty(heavyFor(connG).ActiveTurretUpdates);

        // The VIEWER's connection: the gunner relayed intact.
        var acts = heavyFor(connV).ActiveTurretUpdates;
        Assert.Single(acts);
        Assert.Equal(gunner.Id, acts[0].ShipId);
        Assert.Equal(0.25f, acts[0].Controls.Yaw, 2);
        Assert.Equal(-0.5f, acts[0].Controls.Pitch, 2);
        // The orientation survives the CompactTime/CompactOrientation round trip.
        Assert.True(facing.GetForward() * acts[0].Orientation.GetForward() > 0.99f);
        // The recovered instant decodes against the message's timeReference.
        Assert.Equal((uint)Math.Round((mission.LastUpdate + 0.1) * 1000.0 * 20 / 1000.0),
            acts[0].Time(heavyFor(connV).TimeReference));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Stale_turret_updates_age_out_of_the_relay()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var sector = server.TestSectors[0];
        var mission = server.TestMission!;
        var hull = mission.Core.HullTypes.Values.First();

        var carrier = new Ship((short)10011, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var gunner = new Ship((short)10012, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var viewer = new Ship((short)10013, hull, new Vector3(90, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        gunner.ParentShip = carrier;
        gunner.TurretId = 2;
        mission.PlaceShip(carrier, sector);
        mission.PlaceShip(gunner, sector);
        mission.PlaceShip(viewer, sector);
        server.TestSeedPeers([(new ConnectionId(710u), viewer, sector)]);

        // Two seconds ahead of the mission clock — outside native's ±1.5 s
        // window (FedSrv.CPP:3786-3789): the gunner simply drops out.
        server._lastTurretUpdates[gunner.Id] =
            ((uint)Math.Round((mission.LastUpdate + 2.0) * 1000.0),
                new ControlData(0f, 0f, 0f, 1f), new Orientation());

        var packets = server.TestCollectBroadcast(10, parallel: false, "shipsUpdate");
        var heavy = packets[new ConnectionId(710u)]
            .SelectMany(p => FedPacket.Unpack(p))
            .Where(m => (ushort)(m[2] | m[3] << 8) == HeavyShipsUpdate.Fmid)
            .Select(m => HeavyShipsUpdate.Decode(m)).Single();
        Assert.Empty(heavy.ActiveTurretUpdates);
    }
}
