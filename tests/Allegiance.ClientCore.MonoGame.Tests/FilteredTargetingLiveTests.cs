using System.Linq;
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
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P415 — LIVE proof (scope req. "Live proof, required") that the friend/foe
/// filtered-nearest targeting selects a real, correctly-sided ship. A real
/// in-process server (map m2xspiderw, 2 sides) seats a real peer on side 0 in
/// the Ganymede sector, and two real NPC ships are placed in that SAME sector:
/// one on the OTHER side (enemy) and one on the peer's OWN side (friendly).
/// The server's real cross-side export (P346/P349 SweepCrossSideExports) and
/// same-side NPC export (P366) deliver both ships' real identity (SideId +
/// HullId, HasRealIdentity=true) to the peer over the real wire. We then run
/// the REAL production gather (<see cref="Game1.GatherSectorCandidates"/>) +
/// filter (<see cref="Game1.FilterCandidates"/>) against the peer's real world
/// and assert the enemy key picks the other-side ship and the friendly key
/// picks the same-side ship — never each other's — plus the same-by-side base
/// filter against the real in-sector station.
///
/// This mirrors the P371 live recipe (boot real server, connect real clients,
/// drive the production static gather). The selection path it drives is the
/// exact code the U / Shift+U / Z / Shift+Z keys invoke.
/// </summary>
[Collection("LiveNetwork")]
public sealed class FilteredTargetingLiveTests
{
    private const short Ganymede = 1050;   // 4 warps, 1 station, 0 asteroids (P371)

    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = name, Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 971u, DwCookie = 0x971u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97500000000000001ul, DrmHash = "p415",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ LIVE: with a real side-1 enemy ship and a real side-0 friendly
    /// ship in the peer's own sector, the enemy filter selects ONLY the enemy and
    /// the friendly filter selects ONLY the friendly; the same-by-side base filter
    /// selects the real in-sector station by its side. This is the exact selection
    /// the U / Shift+U / Z / Shift+Z keys drive.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LiveNearestEnemyAndFriendly_SelectTheCorrectlySidedRealShip()
    {
        Require.Artwork();
        using var server = new Srv();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var sector = mission.Sectors.First(s => s.Id == Ganymede);
            var ship = new Ship(id, hull, SimVec.Zero, SimVec.Zero, Orientation.Identity)
            { SideId = mission.Sides[0].Id };   // the PEER lands on side 0
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        });

        server.Boot(Config("P415 Friend/Foe Targeting"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];

        // Two real NPC drones in the peer's sector: one enemy (side 1), one friendly (side 0).
        // SpawnNpcShip is internal to Allegiance.Server (not InternalsVisibleTo this
        // assembly) — reach it by reflection, exactly like ShipSpawner above.
        var spawnNpc = typeof(Srv).GetMethod("SpawnNpcShip", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Allegiance.Server.Server.SpawnNpcShip method not found");
        var ganymede = mission.Sectors.First(s => s.Id == Ganymede);
        var enemyDrone = (Ship)spawnNpc.Invoke(server, new object?[] { mission, ganymede, mission.Sides[1].Id, new SimVec(1500, 0, 0), null })!;
        var friendDrone = (Ship)spawnNpc.Invoke(server, new object?[] { mission, ganymede, mission.Sides[0].Id, new SimVec(-1500, 0, 0), null })!;

        using var peer = Connect(server, "AlphaPilot");
        int seated = PumpUntil(server,
            () => peer.World.MyShipId is { } && peer.World.MySideId is { }, 4000);
        Assert.True(seated < 4000, "peer seated with a side");

        // The peer must receive BOTH drones' real identity (side+hull) over the wire:
        // the enemy via the cross-side export sweep (P346/P349), the friendly via
        // the same-side NPC export (P366).
        int identified = PumpUntil(server,
            () =>
            {
                lock (peer.Gate)
                    return peer.World.Ships.TryGetValue(enemyDrone.Id, out var e) && e.HasRealIdentity && e.Latest is not null
                        && peer.World.Ships.TryGetValue(friendDrone.Id, out var f) && f.HasRealIdentity && f.Latest is not null
                        && peer.World.MyShipId is { } ownId && peer.World.Ships.TryGetValue(ownId, out var own)
                        && own.Latest is not null;
            },
            6000);
        Assert.True(identified < 6000,
            $"peer never received both drones' identity (enemy {enemyDrone.Id}, friend {friendDrone.Id})");

        // Real role resolvers from the same client ShopCatalog the running client uses.
        var cat = Rendering.ShopCatalog.TryLoad(ArtRoot);
        short ShipRole(short hullId)
        {
            if (cat is null) return 0;
            foreach (var h in cat.Hulls) if (h.HullId == hullId) return h.HabmCapabilities;
            return 0;
        }
        short StRole(short tid)
        {
            if (cat?.StationTypes is not { } sts) return 0;
            return sts.TryGetValue(tid, out var s) ? s.SabmCapabilities : (short)0;
        }

        short me, mySide;
        List<Game1.CandidateObject> objs;
        lock (peer.Gate)
        {
            me = peer.World.MyShipId!.Value;
            mySide = peer.World.MySideId!.Value;
            var sector = peer.World.Ships[me].SectorId;
            objs = Game1.GatherSectorCandidates(peer.World.Ships, peer.World.Warps,
                                                peer.World.Stations, peer.World.Asteroids,
                                                me, sector, ShipRole, StRole);
        }

        Assert.NotEqual(mySide, mission.Sides[1].Id);   // the peer is on side 0, the enemy drone on side 1
        Assert.Equal(mySide, mission.Sides[0].Id);

        // ⭐ nearest ENEMY selects the real other-side ship, never the friendly.
        var enemies = Game1.FilterCandidates(objs, mySide, me, SimVec.Zero,
            new Game1.TargetFilterSpec(Game1.TargetRelation.Enemy));
        Assert.Contains(enemyDrone.Id, enemies.Select(x => x.Id));
        Assert.DoesNotContain(friendDrone.Id, enemies.Select(x => x.Id));

        // ⭐ nearest FRIENDLY selects the real same-side ship, never the enemy.
        var friends = Game1.FilterCandidates(objs, mySide, me, SimVec.Zero,
            new Game1.TargetFilterSpec(Game1.TargetRelation.Friendly));
        Assert.Contains(friendDrone.Id, friends.Select(x => x.Id));
        Assert.DoesNotContain(enemyDrone.Id, friends.Select(x => x.Id));

        // ⭐ base-by-side: the real in-sector station is selected by whichever
        // side relation matches its real wire SideId (adaptive — the test map's
        // Ganymede garrison sits on a real side; the filter must agree).
        foreach (var st in objs.Where(c => c.Kind == Game1.TargetKind.Station))
        {
            var stSide = peer.World.Stations[st.Id].SideId;
            var fBase = Game1.FilterCandidates(objs, mySide, me, SimVec.Zero,
                new Game1.TargetFilterSpec(Game1.TargetRelation.Friendly, StationOnly: true));
            var eBase = Game1.FilterCandidates(objs, mySide, me, SimVec.Zero,
                new Game1.TargetFilterSpec(Game1.TargetRelation.Enemy, StationOnly: true));
            if (stSide == mySide)
                Assert.Contains(st.Id, fBase.Select(x => x.Id));
            else
                Assert.Contains(st.Id, eBase.Select(x => x.Id));
        }
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }
}
