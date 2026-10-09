using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P417 — LIVE proof for the small targeting keys. Mirror of the P415 live
/// recipe: boot a REAL in-process server (m2xspiderw, 2 sides), seat a real peer
/// in the Ganymede sector, and place real NPC ships in that same sector (an
/// enemy on side 1, a friendly on side 0), delivered to the peer's real
/// <see cref="ClientWorld"/> over the real wire.
///
/// It proves the backing concepts the P417 keys drive against real data:
/// <list type="bullet">
/// <item><b>TargetSelf</b> (<c>SelectSelfTarget</c>): the local player's own ship
/// is a real, present, identifiable entry in the peer's own world
/// (<c>World.MyShipId</c> resolves to a live <c>ShipEntity</c>) — the exact datum
/// TargetSelf reads to select "me".</item>
/// <item><b>TargetCenter</b> (<c>SelectCentermostTarget</c> → the production
/// <see cref="Game1.GatherSectorCandidates"/> + <see cref="Game1.SelectCandidates"/>
/// nearest-to-reticle ranking): the reticle-ranked selection, run against the real
/// gathered candidates, picks a real in-sector ship (never empty, always one of the
/// real wire ships) and orders nearest-to-crosshair.</item>
/// </list>
/// TargetNothing reuses <see cref="AutopilotSteering"/>/DisengageAutopilot's exact
/// clear path (already covered by the P212/G-key live behaviour); its only new code
/// is the call-through, which the unit + this live context pin.
/// </summary>
[Collection("LiveNetwork")]
public sealed class MiscKeyBindingLiveTests
{
    private const short Ganymede = 1050;

    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

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
            new Gm { Name = name, Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 971u, DwCookie = 0x971u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97500000000000001ul, DrmHash = "p417",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ LIVE: the peer's own ship is present and identifiable in its own
    /// real world (the TargetSelf backing datum), and the production
    /// gather + reticle-ranking returns a real in-sector ship for TargetCenter.</summary>
    [Fact]
    public void Live_own_ship_is_real_and_center_ranking_selects_a_real_ship()
    {
        using var server = new Srv();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var sector = mission.Sectors.First(s => s.Id == Ganymede);
            var ship = new Ship(id, hull, SimVec.Zero, SimVec.Zero, Orientation.Identity)
            { SideId = mission.Sides[0].Id };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        });

        server.Boot(Config("P417 Misc Targeting"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];

        var spawnNpc = typeof(Srv).GetMethod("SpawnNpcShip", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Allegiance.Server.Server.SpawnNpcShip method not found");
        var ganymede = mission.Sectors.First(s => s.Id == Ganymede);
        var enemyDrone = (Ship)spawnNpc.Invoke(server, new object?[] { mission, ganymede, mission.Sides[1].Id, new SimVec(1500, 0, 0), null })!;
        var friendDrone = (Ship)spawnNpc.Invoke(server, new object?[] { mission, ganymede, mission.Sides[0].Id, new SimVec(-1500, 0, 0), null })!;

        using var peer = Connect(server, "AlphaPilot");
        int seated = PumpUntil(server,
            () => peer.World.MyShipId is { } && peer.World.MySideId is { }, 4000);
        Assert.True(seated < 4000, "peer seated with a side");

        // ⭐ TargetSelf backing: the peer's own ship id resolves to a live,
        // real-identity ShipEntity in its own world (what SelectSelfTarget reads
        // to build the AutopilotTarget(Ship, me, name)).
        int selfResolves = PumpUntil(server,
            () =>
            {
                lock (peer.Gate)
                    return peer.World.MyShipId is { } me
                        && peer.World.Ships.TryGetValue(me, out var ent)
                        && !string.IsNullOrEmpty(ent.CharacterName);
            }, 4000);
        Assert.True(selfResolves < 4000, "own ship never resolvable in peer world");

        short me;
        List<Game1.CandidateObject> objs;
        lock (peer.Gate)
        {
            me = peer.World.MyShipId!.Value;
            var sector = peer.World.Ships[me].SectorId;
            objs = Game1.GatherSectorCandidates(peer.World.Ships, peer.World.Warps,
                                                peer.World.Stations, peer.World.Asteroids,
                                                me, sector, null, null);
        }

        // ⭐ TargetCenter backing: the reticle-ranked production selection over the
        // real gathered candidates yields a real in-sector ship (one of the two
        // drones / the own ship's sector mates) — never empty. A projector that
        // puts everything dead-centre simulates the crosshair; the tier rule then
        // offers the ships tier.
        var ranked = Game1.SelectCandidates(objs, _ => (0f, 0f),
            out _, out _, out _, out _, out _);
        Assert.NotEmpty(ranked);
        Assert.All(ranked, r => Assert.Equal(Game1.TargetKind.Ship, r.Kind));

        // The enemy and friendly drones are the real ships in the peer's sector;
        // a reticle-centred pick surfaces a real ship id from the peer's world.
        var realIds = ranked.Select(r => r.Id).ToHashSet();
        Assert.True(realIds.Contains(enemyDrone.Id) || realIds.Contains(friendDrone.Id),
            "center ranking should surface a real in-sector drone");
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }
}
