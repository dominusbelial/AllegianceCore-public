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
/// P438 — LIVE proof that a side switch reaches the client's friend/enemy
/// classification. The client previously threw away <c>JOIN_SIDE(125)</c> — the
/// only whole-mission announcement the server broadcasts whenever
/// <c>Server.TryJoinSide</c> moves a ship onto a side — so any ship that changed
/// side after a viewer had already latched its <c>EXPORT</c> kept its old side
/// on that viewer. The P376 target box then painted a *friendly* ship in the
/// P376 *enemy* salmon-orange (P436's live-test finding).
/// <para>
/// Two in-process scenarios over a real server + real TCP
/// <see cref="ConnectionSession"/> peers (the same data plane the MonoGame
/// client uses):
/// <list type="number">
/// <item>The FAITHFUL real-world sequence — the one that is provably stale on
/// unfixed code: B starts as A's same-side teammate (A latches B's teammate
/// EXPORT, consuming the teammate-export latch), B switches to the enemy side
/// (A latches B's cross-side EXPORT, consuming the cross-export latch), then B
/// switches BACK to A's side via the real <c>TryJoinSide</c> path. Neither
/// export path re-fires (both one-shot latches are spent), so A's cached
/// <c>Ships[B].SideId</c> can only follow via <c>JOIN_SIDE</c> — which unfixed
/// code drops. A still thinks B is the old (enemy) side ⇒ friendly B rendered
/// orange.</item>
/// <item>The brief's literal single switch: A on side 0, B seated on side 1, A
/// learns B cross-side, then B switches to side 0. Included because it is the
/// task's stated shape; the fresh teammate EXPORT that fires on that first
/// same-side join tends to paper over the staleness (see report §2), which is
/// precisely why the two-phase scenario above is the deterministic one.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class ClientJoinSideLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static readonly MethodInfo TryJoinSideMethod =
        typeof(Srv).GetMethod("TryJoinSide", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.TryJoinSide method not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static void SwitchSide(Srv server, Ship ship, short sideId)
    {
        bool ok = (bool)(TryJoinSideMethod.Invoke(server, new object[] { ship, sideId })
                         ?? throw new InvalidOperationException("TryJoinSide returned null"));
        Assert.True(ok, $"server.TryJoinSide(ship {ship.Id} -> side {sideId}) should succeed");
    }

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

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 904u, DwCookie = 0x904u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = steam, DrmHash = "p438",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship? ServerShip(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s && s.Id == id)
                        return s;
        return null;
    }

    private static ConnectionSession Connect(Srv server, string name, ulong steam)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name, steam));
        return s;
    }

    private sealed record View(ConnectionSession S, Dictionary<short, ShipEntity> Ships, short Me)
    {
        public static View Of(ConnectionSession s)
        {
            lock (s.Gate)
            {
                return new View(s,
                    s.World.Ships.ToDictionary(kv => kv.Key, kv => kv.Value),
                    s.World.MyShipId ?? -1);
            }
        }
    }

    /// <summary>⭐ The deterministic real-world staleness. B is A's same-side
    /// teammate, spends a cross-side stint (A latches B as enemy), then comes
    /// home. Both EXPORT one-shot latches are spent, so A's view of B's side
    /// can only update through the JOIN_SIDE(125) the server broadcasts on
    /// TryJoinSide — which the fixed client now honours.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teammate_side_change_updates_viewer_after_cross_side_stint()
    {
        Require.Artwork();
        using var server = new Srv();
        short side0 = 0, side1 = 0; Sector? s0 = null; int placed = 0;
        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            s0 = mission.Sectors[0];
            side0 = mission.Sides[0].Id;
            side1 = mission.Sides[1].Id;
            var pos = new Vector3(placed * 200f, 0f, 0f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity)
            { SideId = side0 };   // BOTH start as Alpha teammates
            mission.PlaceShip(ship, s0);
            placed++;
            return (ship, s0);
        });

        server.Boot(Config("P438 Homecoming Stint"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up lobby

        using var alpha = Connect(server, "AlphaHome", 93000000000000001ul);
        using var bravo = Connect(server, "BravoHome", 93000000000000002ul);
        int seated = PumpUntil(server, () => alpha.World.MyShipId is { } && bravo.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");
        short aId = alpha.World.MyShipId!.Value;
        short bId = bravo.World.MyShipId!.Value;

        // Phase 1 — same-side teammates: A learns B via the one-shot teammate
        // EXPORT (consumes the (A,B) teammate latch).
        int asTeammate = PumpUntil(server,
            () => View.Of(alpha).Ships.TryGetValue(bId, out var e) && e.SideId == side0, 4000);
        Assert.True(asTeammate < 4000, "A learned B as a same-side teammate (EXPECTED 0)");

        // Phase 2 — B goes cross-side: A latches B as the enemy side (consumes
        // the (A,B) cross-side latch).
        SwitchSide(server, ServerShip(server, bId)!, side1);
        int asEnemy = PumpUntil(server,
            () => View.Of(alpha).Ships.TryGetValue(bId, out var e) && e.SideId == side1, 4000);
        Assert.True(asEnemy < 4000, "A latched B as the enemy side while B was cross-side (EXPECTED 1)");

        // Phase 3 — B comes HOME to side 0 via the REAL TryJoinSide path. Both
        // export latches are now spent (teammate in phase 1, cross-side in
        // phase 2), so A's view of B's side can only be corrected by the
        // JOIN_SIDE(125) that TryJoinSide broadcasts to the whole mission.
        SwitchSide(server, ServerShip(server, bId)!, side0);
        int corrected = PumpUntil(server,
            () => View.Of(alpha).Ships.TryGetValue(bId, out var e) && e.SideId == side0, 4000);
        Assert.True(corrected < 4000,
            $"A's view of B must follow the JOIN_SIDE homecoming (got {View.Of(alpha).Ships[bId].SideId}, expected {side0}); " +
            "without the P438 JOIN_SIDE handler A keeps B's stale enemy side and paints a friendly ship orange");

        Console.WriteLine($"P438-HOME: A {aId} sees teammate B {bId} side {side0} after homecoming " +
                          $"(fix: JOIN_SIDE honoured; was stale-enemy {side1}) after {corrected} ticks");
    }

    /// <summary>⭐ Step 3 of the brief — the LOCAL player's own side. After the
    /// own-ship EXPORT seats <c>MySideId</c>, a <c>JOIN_SIDE</c> naming the
    /// local ship (a lobby side switch of the player themself) must update both
    /// <c>ClientWorld.MySideId</c> and the own <c>ShipEntity.SideId</c> — the
    /// two inputs to the <c>isEnemy</c> classification on <c>Game1.Camera</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Own_ship_join_side_updates_MySideId_and_own_entity()
    {
        Require.Artwork();
        using var server = new Srv();
        short side0 = 0; Sector? s0 = null;
        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            s0 = mission.Sectors[0];
            side0 = mission.Sides[0].Id;
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side0 };
            mission.PlaceShip(ship, s0);
            return (ship, s0);
        });

        server.Boot(Config("P438 Own Side Switch"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var player = Connect(server, "SelfSwitch", 95000000000000001ul);
        int seated = PumpUntil(server, () => player.World.MyShipId is { } && player.World.MySideId is { }, 4000);
        Assert.True(seated < 4000, "player seated with own ship + side");
        short me = player.World.MyShipId!.Value;
        short myInitial = player.World.MySideId!.Value;
        Assert.Equal(side0, myInitial);

        // Move the local ship to the OTHER side via the real TryJoinSide path.
        short other = server.Missions[0].Sides[1].Id;
        SwitchSide(server, ServerShip(server, me)!, other);

        // The JOIN_SIDE(125) broadcast must refresh both the local ship entity's
        // SideId and MySideId.
        int updated = PumpUntil(server,
            () => View.Of(player).Me == me
                  && View.Of(player).Ships.TryGetValue(me, out var e) && e.SideId == other
                  && player.World.MySideId == other, 4000);
        Assert.True(updated < 4000,
            $"own-ship JOIN_SIDE must update MySideId ({player.World.MySideId}) and the own ShipEntity.SideId " +
            $"({View.Of(player).Ships[me].SideId}) to {other}");

        Console.WriteLine($"P438-OWN: local ship {me} side {myInitial} -> {other}; MySideId={player.World.MySideId}, " +
                          $"ShipEntity.SideId={View.Of(player).Ships[me].SideId}");
    }
}
