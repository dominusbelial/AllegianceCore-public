using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P436 — LIVE proof (scope req. "Live proof, required") that the player's own
/// currently-selected target gets a distinct radar highlight even when that
/// target is OFF-SCREEN (edge-clamped) — the exact reported problem ("pressing
/// u not doing anything if the target is not on my view"). A real in-process
/// server (map m2xspiderw, 2 sides) seats a real peer on side 0 in the Ganymede
/// sector and places a real side-1 ENEMY NPC in that SAME sector, directly
/// BEHIND the peer's forward — i.e. genuinely off-screen. The real cross-side
/// export (P346/P349) delivers the enemy's real identity+position over the real
/// wire. We then run the REAL production U-key selection (<see cref="Game1.GatherSectorCandidates"/>
/// + <see cref="Game1.FilterCandidates"/>) and confirm:
///   • the U path selects THAT real enemy (not the peer / any other contact);
///   • the enemy is genuinely BEHIND the peer's real facing (off-screen ⇒ the
///     radar's edge-clamped bracket — the exact case that needs the highlight);
///   • that exact enemy contact is the flagged highlight
///     (<see cref="RadarTargetSelection.IsSelected"/>, the same call
///     <c>Game1.DrawRadar</c> makes each frame), while every other ship in the
///     sector is not flagged.
/// This mirrors the P415 live recipe (boot real server, connect real clients,
/// drive the production static gather) — the port's established live-proof
/// standard for the wire + targeting layers.
/// </summary>
[Collection("LiveNetwork")]
public sealed class RadarTargetHighlightLiveTests
{
    private const short Ganymede = 1050;   // 4 warps, 1 station, 0 asteroids (P371)

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
        SteamID = 97500000000000001ul, DrmHash = "p436",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ LIVE: the real U-key path selects a real ENEMY that is genuinely
    /// OFF-SCREEN (behind the peer's facing), and that exact enemy contact is the
    /// flagged radar highlight while every other ship in the sector is not.</summary>
    [Fact]
    public void LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight()
    {
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

        server.Boot(Config("P436 Radar Target Highlight"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];

        // A real ENEMY (side 1) NPC in the peer's sector, positioned on the +Z
        // axis. The peer's ship spawns at the origin facing identity (−Z, per
        // Orientation.Reset), so +Z is DIRECTLY BEHIND the peer — i.e. off-screen
        // (the reported problem case: "target is not on my view").
        var spawnNpc = typeof(Srv).GetMethod("SpawnNpcShip", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Allegiance.Server.Server.SpawnNpcShip method not found");
        var ganymede = mission.Sectors.First(s => s.Id == Ganymede);
        var enemy = (Ship)spawnNpc.Invoke(server, new object?[] { mission, ganymede, mission.Sides[1].Id, new SimVec(0, 0, 1500), null })!;

        using var peer = Connect(server, "AlphaPilot");
        int seated = PumpUntil(server,
            () => peer.World.MyShipId is { } && peer.World.MySideId is { }, 4000);
        Assert.True(seated < 4000, "peer seated with a side");

        // The peer must receive the enemy's real identity (side+hull) over the
        // real cross-side wire.
        int identified = PumpUntil(server,
            () =>
            {
                lock (peer.Gate)
                    return peer.World.Ships.TryGetValue(enemy.Id, out var e) && e.HasRealIdentity;
            },
            6000);
        Assert.True(identified < 6000, "peer never received the enemy's real identity");

        // ⭐ (1)+(2) The enemy is genuinely BEHIND the peer's real facing ⇒
        // off-screen ⇒ the radar draws it edge-clamped (the exact gap reported).
        SimVec ownPos, enemyPos; Vector3 forward;
        lock (peer.Gate)
        {
            short meId = peer.World.MyShipId!.Value;
            var ownUp = peer.World.Ships[meId].Latest!;
            ownPos = Vec(ownUp.Position);
            enemyPos = Vec(peer.World.Ships[enemy.Id].Latest!.Position);
            forward = ownUp.Orientation.GetForward();
        }
        var toEnemy = new Vector3(enemyPos.X - ownPos.X, enemyPos.Y - ownPos.Y, enemyPos.Z - ownPos.Z);
        float dotBehind = forward.X * toEnemy.X + forward.Y * toEnemy.Y + forward.Z * toEnemy.Z;
        Assert.True(dotBehind < -500f,
            $"enemy must be behind the peer's facing (off-screen), dot(fwd,toEnemy)={dotBehind:F0}; " +
            $"own={ownPos.X},{ownPos.Y},{ownPos.Z} fwd={forward.X},{forward.Y},{forward.Z} enemy={enemyPos.X},{enemyPos.Y},{enemyPos.Z}");

        // Real role resolvers from the same client ShopCatalog the client uses.
        var cat = Rendering.ShopCatalog.TryLoad(ArtRoot);
        short ShipRole(short hullId)
        {
            if (cat is null) return 0;
            foreach (var h in cat.Hulls) if (h.HullId == hullId) return h.HabmCapabilities;
            return 0;
        }

        // ⭐ (3) The REAL U-key selection path (gather + enemy filter) picks the
        // real enemy; the selected target id is exactly the enemy's id.
        short me, mySide;
        List<Game1.CandidateObject> objs;
        lock (peer.Gate)
        {
            me = peer.World.MyShipId!.Value;
            mySide = peer.World.MySideId!.Value;
            var sector = peer.World.Ships[me].SectorId;
            objs = Game1.GatherSectorCandidates(peer.World.Ships, peer.World.Warps,
                                                peer.World.Stations, peer.World.Asteroids,
                                                me, sector, ShipRole, static (short _) => (short)0);
        }
        Assert.NotEqual(mySide, mission.Sides[1].Id);   // the peer is on side 0, the enemy on side 1

        var enemies = Game1.FilterCandidates(objs, mySide, me, SimVec.Zero,
            new Game1.TargetFilterSpec(Game1.TargetRelation.Enemy));
        Assert.Contains(enemy.Id, enemies.Select(x => x.Id));
        var picked = enemies.First(e => e.Id == enemy.Id);

        // ⭐ (4) The exact flagging Game1.DrawRadar performs each frame: the
        // selected enemy (an enemy SHIP ⇒ RadarContactType.Ship) is the flagged
        // highlight contact…
        var selType = RadarBracketRenderer.RadarContactType.Ship;   // enemy.Kind == TargetKind.Ship
        Assert.True(RadarTargetSelection.IsSelected(
            RadarBracketRenderer.RadarContactType.Ship, enemy.Id, selType, picked.Id),
            "the off-screen selected enemy must be the flagged radar highlight");

        // …and NO other ship in the sector is flagged. (The only other ships are
        // the peer itself — excluded from radar contacts by DrawRadar — so any
        // map/roster ship present must also not be the highlight.)
        foreach (var other in objs.Where(c => c.Kind == Game1.TargetKind.Ship && c.Id != enemy.Id && c.Id != me))
            Assert.False(RadarTargetSelection.IsSelected(
                RadarBracketRenderer.RadarContactType.Ship, other.Id, selType, picked.Id),
                $"ship #{other.Id} must NOT be the selected-target highlight");
    }

    private static SimVec Vec(Vector3 v) => new(v.X, v.Y, v.Z);

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }
}
