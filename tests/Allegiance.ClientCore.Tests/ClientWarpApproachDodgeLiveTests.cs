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
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P489 — the P484 differential, INVERTED, as the LIVE proof that the client
/// bots now steer with native's shared dodge law. P484 (§5) measured that the
/// SAME 2-hop MCP flight with REAL asteroids in Alderan(1052) parks or is
/// destroyed before the hop-2 aleph (min warp distance ~7000+ u — the obstacle
/// park), while with asteroids STRIPPED it transited both hops cleanly
/// (min warp distance ~107-113 u). P489 wires the shared
/// <see cref="ShipDodgeLaw"/> (the same pure module the server NPC autopilot's
/// <c>Ship.TryComputeDodgeGoal</c> calls byte-identically) into the client
/// warp-approach law, fed from the peer's own <c>ClientWorld</c>. This test
/// drives the REAL debug arena with the asteroid field PRESENT and asserts the
/// bot now transits BOTH alephs — recording the minimum distance to each
/// hop-warp and the hull damage taken.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ClientWarpApproachDodgeLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg DebugConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { DebugMode = true },
        Games =
        [
            new Gm { Name = "P489 Warp Approach Dodge", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 531u, DwCookie = 0x2D3u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 53300000000000000ul, DrmHash = "p489dodge",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ── The MCP combat routing law + the P489 shared-dodge addition ──────────
    private static short CsSector(ConnectionSession cs)
    {
        lock (cs.Gate)
            if (cs.World.MyShipId is { } me && cs.World.Ships.TryGetValue(me, out var e))
                return e.SectorId;
        return -1;
    }

    private static bool TryCsPose(ConnectionSession cs, short shipId, out Vector3 pos, out Orientation orient)
    {
        lock (cs.Gate)
        {
            if (cs.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
            {
                pos = lu.Position;
                orient = lu.Orientation;
                return true;
            }
        }
        pos = Vector3.Zero;
        orient = Orientation.Identity;
        return false;
    }

    private static Vector3 Vec3(float[] v) => v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    private static bool TryBuildWarpDestMap(ConnectionSession cs, out Dictionary<short, short> map)
    {
        map = new Dictionary<short, short>();
        lock (cs.Gate)
            foreach (var w in cs.World.Warps.Values)
                if (cs.World.Warps.TryGetValue(w.DestinationId, out var partner))
                    map[w.WarpId] = partner.ClusterId;
        return map.Count > 0;
    }

    private static bool TryFindWarpTo(ConnectionSession cs, short cur, short next, IReadOnlyDictionary<short, short> warpDest, out Vector3 pos)
    {
        lock (cs.Gate)
        {
            foreach (var w in cs.World.Warps.Values)
            {
                if (w.ClusterId != cur) continue;
                if (warpDest.TryGetValue(w.WarpId, out var d) && d == next)
                {
                    pos = Vec3(w.Position);
                    return true;
                }
            }
        }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>P489/P524 — the MCP bot's shared-dodge feed IS
    /// <see cref="Allegiance.ClientCore.Flight.ClientAutopilotDodge"/>, the ONE
    /// canonical client feed P524 extracted (it previously lived inline in
    /// <c>BotManager</c> and in this test's private copy): feed the pure
    /// <see cref="ShipDodgeLaw"/> from the peer's <c>ClientWorld</c> (asteroids,
    /// stations, known same-side ships in its cluster) and the peer's real hull
    /// stats from the authoritative <c>StaticCore</c> the server booted. Routing
    /// this live test through the shared feed proves the P489 bot behaviour is
    /// byte-identical after the P524 extraction.</summary>
    private static bool TryDodgeGoal(ConnectionSession cs, short shipId, short hullId, short mySide,
                                     Vector3 myPos, StaticCore core, out Vector3 goal)
    {
        goal = default;
        short cur = CsSector(cs);
        if (cur < 0) return false;
        Vector3 myVel;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(shipId, out var me) || me.Latest is not { } mu) return false;
            myVel = mu.Velocity;
        }
        if (core.HullTypes.TryGetValue(hullId, out var hull) && hull.Mass > 0f && hull.Thrust > 0f)
        {
            lock (cs.Gate)
            {
                return Allegiance.ClientCore.Flight.ClientAutopilotDodge.TryComputeDodgeGoal(
                    cs.World, shipId, mySide, myPos, myVel,
                    new Allegiance.ClientCore.Flight.ClientAutopilotDodge.MoverHull(hull.Length, hull.Thrust, hull.Mass),
                    dodgeStations: true,
                    id => core.HullTypes.TryGetValue(id, out var h) ? h.Length : (float?)null,
                    tid => core.StationTypes.TryGetValue(tid, out var t) && t.Radius > 0f ? t.Radius : (float?)null,
                    skipGoal: null,
                    out goal);
            }
        }
        return false;
    }

    /// <summary>P489 — the MCP <c>CombatFlyTo</c> with the shared-dodge pre-empt:
    /// when an imminent collision is found in the peer's own cluster it flies the
    /// dodge goal instead of the obstacle-blind straight line (server-autopilot
    /// behaviour); otherwise the law is unchanged.</summary>
    private static void CombatFlyToDodge(ConnectionSession cs, short shipId, short hullId, short mySide,
                                         StaticCore core, Vector3 target, float arrival, float brake, int stateM)
    {
        if (!TryCsPose(cs, shipId, out var pos, out var orient)) return;
        if (TryDodgeGoal(cs, shipId, hullId, mySide, pos, core, out var dodgeGoal))
            target = dodgeGoal;
        var toTarget = target - pos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f) { cs.CommandControls(ControlData.Neutral, stateM); return; }
        var dir = toTarget / dist;
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float yaw = Math.Clamp(2.0f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2.0f * -body.X, -1f, 1f);
        float distFactor = dist >= brake ? 1f : Math.Clamp((dist - arrival) / (brake - arrival), 0f, 1f);
        float align = Math.Clamp(-orient.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * align - 1f, -1f, 1f);
        cs.CommandControls(new ControlData(yaw, pitch, 0f, throttle), stateM);
    }

    /// <summary>Drive a seated peer toward <paramref name="goal"/> with the MCP
    /// routing law + the shared-dodge pre-empt, tracking the evidence metrics:
    /// the minimum distance from the server-authoritative bot pose to each
    /// hop-warp the bot targets (must dip inside the ~110 u transit volume to
    /// prove it REACHED the aleph, versus P484's ~7000 u park), and the minimum
    /// hull fraction of the server ship (hull damage taken = 1 − min).</summary>
    private static (short finalSector, float minWarpDist, float minHullFrac, List<string> log) FlyToward(
        Srv server, ConnectionSession cs, StaticCore core, short goal, float arrival, float brake, int maxTicks)
    {
        var shipId = cs.World.MyShipId!.Value;
        short mySide = cs.World.MySideId ?? -1;
        short hullId;
        lock (cs.Gate) hullId = cs.World.Ships.TryGetValue(shipId, out var me) ? me.HullId : (short)-1;

        var log = new List<string>();
        short lastSector = -1;
        float minWarpDist = float.MaxValue;
        float minHullFrac = 1f;
        var serverShip = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models).OfType<Ship>()
            .FirstOrDefault(s => s.Id == shipId);

        for (int i = 0; i < maxTicks; i++)
        {
            server.TickSim();
            short cur = CsSector(cs);
            if (cur < 0) continue;

            var g = cs.World.Graph;
            if (g is not null)
            {
                var path = g.FindShortestPath(cur, goal);
                if (path.Count > 0 && cur != goal)
                {
                    short next = cur;
                    for (int j = 1; j < path.Count; j++)
                        if (path[j - 1] == cur) { next = path[j]; break; }
                    if (next != cur && TryBuildWarpDestMap(cs, out var wd) && TryFindWarpTo(cs, cur, next, wd, out var warpPos))
                    {
                        CombatFlyToDodge(cs, shipId, hullId, mySide, core, warpPos, arrival, brake, 0);
                        // Evidence: how close does the REAL server ship get to the hop-warp it is targeting?
                        if (serverShip is not null)
                            minWarpDist = MathF.Min(minWarpDist, (serverShip.Position - warpPos).Length());
                    }
                    else
                        cs.CommandControls(ControlData.Neutral, 0);
                }
            }

            if (serverShip is not null && serverShip.EffectiveMaxHitPoints() > 0f)
                minHullFrac = MathF.Min(minHullFrac, serverShip.HitPoints / serverShip.EffectiveMaxHitPoints());

            if (cur != lastSector)
            {
                lastSector = cur;
                log.Add($"reached sector {cur} at tick {i}");
            }
            if (cur == goal) { log.Add($"ARRIVED goal sector {cur} at tick {i}"); break; }
        }
        return (CsSector(cs), minWarpDist, minHullFrac, log);
    }

    /// <summary>⭐ P489 LIVE: the P484 differential INVERTED. With REAL asteroids
    /// in Alderan (nothing stripped), a client bot that steers with the SHARED
    /// native dodge law now transits BOTH alephs to the 2-hop target — where
    /// P484 §5 measured the same flight (without dodge) parked ~7000 u short of
    /// the hop-2 aleph and was destroyed in the field.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Client_bot_with_shared_dodge_transits_two_hops_through_real_alderan_field()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(DebugConfig());
        var mission = server.Missions[0];
        Assert.NotEqual(MissionStage.NotStarted, mission.Stage);   // P387 debug auto-start
        var core = mission.Core;

        using var bot = new ConnectionSession();
        bot.Connect(server.BoundPort, "p489_bot");
        bot.SendLogonRequest(Request("p489_bot"));
        int seated = PumpUntil(server, () => bot.World.MyShipId is not null, 4000);
        Assert.True(seated < 4000, "peer must be seated");
        Assert.Equal((short)1053, CsSector(bot));                  // debug spawner seats side 0 in Omega
        Assert.True(bot.World.Graph is not null, "peer must have received CLUSTERINFO");

        bot.SendPlayerReady(fReady: true, shipId: bot.World.MyShipId!.Value);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");
        bot.SendDocked();
        Assert.True(PumpUntil(server, () => server.Ships.Single(l => l.Ship.Id == bot.World.MyShipId).Ship.StationId is null, 4000) < 4000,
            "the flight fixture must launch from the debug hangar");

        // Sanity: Alderan genuinely has its mining-hub rock field present
        // (nothing stripped) — this is the very field P484 showed blocks a
        // straight line.
        var alderan = mission.Sectors.First(s => s.Id == 1052);
        int alderanRocks;
        lock (bot.Gate) alderanRocks = bot.World.Asteroids.Values.Count(a => a.ClusterId == 1052);
        Assert.True(alderanRocks > 0, $"Alderan must retain its asteroid field for the test to be the P484 differential: client sees {alderanRocks}");
        Console.WriteLine($"P489-ARENA: Alderan client-known rocks = {alderanRocks}");

        short goal = 1060;                                         // Plato — 2 hops from Omega via Alderan
        var (finalSector, minWarpDist, minHullFrac, log) = FlyToward(server, bot, core, goal, arrival: 200f, brake: 450f, maxTicks: 60000);

        Console.WriteLine("P489-DODGE:\n  " + string.Join("\n  ", log));
        Console.WriteLine($"P489-DODGE: final sector {finalSector} vs goal {goal}; minDist-to-hop-warp = {minWarpDist:F1} u; min hull fraction = {minHullFrac:F3} (damage {(1 - minHullFrac) * 100f:F1}%)");

        Assert.Equal(goal, finalSector);
        // The whole point of the fix: the bot must get INSIDE the aleph's transit
        // volume (~110 u) — nowhere near P484's ~7000 u park — on both hops.
        Assert.True(minWarpDist < 200f,
            $"must reach the aleph transit volume, not park ~7000 u short (P484): minDist-to-hop-warp = {minWarpDist:F1}");
    }
}
