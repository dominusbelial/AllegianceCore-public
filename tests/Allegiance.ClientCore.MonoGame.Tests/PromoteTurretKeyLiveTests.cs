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
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P416 — LIVE proof that the new Promote-Turret seat keys work over the real
/// wire: the PRODUCTION client sender (<see cref="ConnectionSession.SendPromote"/>,
/// the exact method the Game1 D1-D4 key handlers call) drives a real
/// in-process server's <c>HandlePromote</c>, and the P140 role-swap lands on
/// the server — the boarded gunner becomes the commander, the old commander is
/// demoted into the vacated turret mount. The server-side promote was already
/// live-tested (<see cref="global::Allegiance.Core.Tests.LivePromoteTests"/>);
/// this proves the previously-missing <b>client</b> path that a real MonoGame
/// player uses.
///
/// The [Collection("LiveNetwork")] tag keeps this sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public sealed class PromoteTurretKeyLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    // From PCore001.igc (same ids the P108/P140 live suites measure):
    //   792 = turret-capable carrier, slots [2,6); 504 = plain gunner hull.
    private const short CarrierHullId = 792;
    private const short GunnerHullId = 504;

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 981u, DwCookie = 0x981u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97700000000000001ul, DrmHash = "p416",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }

    /// <summary>⭐ LIVE: a real commander ship with a real boarded turret gunner —
    /// the commander's PRODUCTION <c>SendPromote</c> (the exact method the D1-4
    /// keys call, mount resolved the same way <see cref="Flight.PromoteTurretKey"/>
    /// does) promotes the gunner over the wire. Server state reflects it: the
    /// gunner is now free on the carrier's hull, the old commander is demoted
    /// into the vacated turret mount.</summary>
    [Fact]
    public void Live_promote_turret_key_over_production_send_promotes_the_gunner()
    {
        using var server = new Srv();
        Ship carrierShip = null!, gunnerShip = null!;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var ship = new Ship(id, core.HullTypes[carrierShip is null ? CarrierHullId : GunnerHullId],
                StationPos(station) + new SimVec(25f + (carrierShip is null ? 0f : 10f), 0f, 0f), SimVec.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);

            if (carrierShip is null)
            {
                // Equip the carrier's FIRST turret slot (MaxFixedWeapons=2) so a
                // boarder becomes a real turret gunner at a known mount.
                var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
                ship.MountPart(weapon, 1, (byte)ship.HullType.MaxFixedWeapons, 255);
                carrierShip = ship;
            }
            else
            {
                gunnerShip = ship;
            }
            return (ship, sector);
        });

        server.Boot(Config("P416 Promote-Turret Keys"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;  // bypass ready-up lobby

        using var a = Connect(server, "COMMANDER");
        using var b = Connect(server, "GUNNER");
        int seated = PumpUntil(server,
            () => a.World.MyShipId is { } && b.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");

        // Match each peer to its role by spawned id (robust to socket order).
        ConnectionSession commander, gunner;
        if (a.World.MyShipId!.Value == carrierShip.Id) { commander = a; gunner = b; }
        else { commander = b; gunner = a; }
        Assert.Equal(carrierShip.Id, commander.World.MyShipId!.Value);
        Assert.Equal(gunnerShip.Id, gunner.World.MyShipId!.Value);
        short commanderId = carrierShip.Id, gunnerId = gunnerShip.Id;

        // Wait both docked (boarding requires it).
        int docked = PumpUntil(server,
            () => carrierShip.StationId is not null && gunnerShip.StationId is not null, 400);
        Assert.True(docked < 400, "ships did not dock");

        // The gunner boards the commander via the production SendBoardShip.
        gunner.SendBoardShip(commanderId);
        int boarded = PumpUntil(server, () => gunnerShip.ParentShip is not null, 1200);
        Assert.True(boarded < 1200, "gunner did not board the commander");
        Assert.Same(carrierShip, gunnerShip.ParentShip);
        short slot = gunnerShip.TurretId!.Value;
        Assert.Equal((short)2, slot);   // the equipped first turret slot

        // The commander presses its Promote-Turret key → PRODUCTION SendPromote
        // with the same mount PromoteTurretKey.MountForSeat(1, MaxFixed, MaxWeapons)
        // resolves (seat 1 → the first turret hardpoint = MaxFixedWeapons).
        short resolvedMount = Flight.PromoteTurretKey.MountForSeat(1, carrierShip.HullType.MaxFixedWeapons, carrierShip.HullType.MaxWeapons)!.Value;
        Assert.Equal(slot, resolvedMount);
        commander.SendPromote(resolvedMount);

        // Server state reflects the role swap: gunner free on the carrier's hull,
        // old commander demoted into the vacated mount.
        int promoted = PumpUntil(server,
            () => gunnerShip.ParentShip is null && carrierShip.ParentShip is not null, 1200);
        Assert.True(promoted < 1200, "promote did not swap parent/child roles");

        Assert.Null(gunnerShip.ParentShip);
        Assert.Equal(CarrierHullId, gunnerShip.HullType.HullId);   // gunner now flies the carrier's hull
        Assert.Same(gunnerShip, carrierShip.ParentShip);            // old commander demoted onto the gunner
        Assert.Equal(slot, carrierShip.TurretId!.Value);            // at the exact vacated mount
        Assert.Contains(gunnerShip.ChildShips, s => ReferenceEquals(s, carrierShip));

        Console.WriteLine($"PROMOTE-KEY-LIVE: commander {commanderId} pressed PromoteTurret seat 1 (mount {slot}) -> gunner {gunnerId} promoted to command on hull 792; commander demoted to mount {slot}");
    }

    private static SimVec StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);
}
