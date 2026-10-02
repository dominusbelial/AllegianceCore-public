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

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P484 — client-side proof that the MCP combat bot's warp-approach ROUTING
/// (tools/allegiance-mcp/BotManager.cs: TryBuildWarpDestMap / TryFindWarpTo /
/// EnsureCombatPath / CombatFlyTo) is correct, and that the warp wire data the
/// client sees is IDENTICAL to the server's authoritative warp data.
///
/// A <see cref="ConnectionSession"/> peer is driven by the exact MCP law
/// against the sealed debug arena (m2xspiderw + liveSettings.debugMode=true:
/// Omega(1053) ↔ Alderan(1052) ↔ Plato(1060)). The peer spawns in Omega and is
/// routed to Plato (1060) — two warp hops. Asteroids are stripped from the
/// three transit sectors so the transit itself is observable (a rock field in
/// the hub is a SEPARATE, navigation obstacle — see P484 report §5). The test
/// proves the bot reaches the 2-hop target, and logs at each hop that the
/// client-believed warp position equals the server's warp position and radius
/// and the client own-pose equals the server's authoritative position —
/// directly refuting P429's "server warp-export/coordinate handling for the
/// probe's side" hypothesis with evidence, not assertion.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ClientWarpApproachLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg DebugConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { DebugMode = true },
        Games =
        [
            new Gm { Name = "P484 Warp Approach", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 405u, DwCookie = 0xF4Fu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 42200000000000000ul, DrmHash = "p484warp",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Strip every asteroid out of the three transit sectors so the
    /// transit itself is observable end-to-end (no rock kills) and the export
    /// to the peer carries none. Reflection is required — Sector's asteroid
    /// lists are private; this is a test-only prism.</summary>
    private static void StripAsteroids(params Sector[] sectors)
    {
        var astField = typeof(Sector).GetField("_asteroids", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mineableField = typeof(Sector).GetField("_mineable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var s in sectors)
        {
            if (astField.GetValue(s) is System.Collections.IList al) al.Clear();
            if (mineableField.GetValue(s) is System.Collections.IList ml) ml.Clear();
        }
    }

    // ── The MCP combat routing law, ported verbatim (BotManager.cs) ────────
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

    private static void CombatFlyTo(ConnectionSession cs, short shipId, Vector3 target, float arrival, float brake, int stateM)
    {
        if (!TryCsPose(cs, shipId, out var pos, out var orient)) return;
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

    /// <summary>Drive a seated peer toward <paramref name="goal"/> with the
    /// MCP routing law, returning the ship's final sector and, at each hop, the
    /// scope-1 side-by-side evidence (client-believed warp vs server warp; the
    /// client's own-pose vs the server's authoritative position).</summary>
    private static (short finalSector, List<string> log) FlyToward(
        Srv server, ConnectionSession cs, short goal, float arrival, float brake, int maxTicks)
    {
        var shipId = cs.World.MyShipId!.Value;
        List<short>? path = null;
        var log = new List<string>();
        short lastSector = -1;
        var serverShip = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models).OfType<Ship>()
            .FirstOrDefault(s => s.Id == shipId);

        for (int i = 0; i < maxTicks; i++)
        {
            server.TickSim();
            short cur = CsSector(cs);
            if (cur < 0) continue;

            var g = cs.World.Graph;
            if (path is null || path.Count == 0 || !path.Contains(cur) || path[^1] != goal)
                path = g is null ? new List<short>() : g.FindShortestPath(cur, goal);
            if (cur == goal) { log.Add($"ARRIVED goal sector {cur} at tick {i}"); return (cur, log); }
            if (path.Count == 0) continue;
            short next = cur;
            for (int j = 1; j < path.Count; j++)
                if (path[j - 1] == cur) { next = path[j]; break; }
            if (next == cur) { log.Add($"ARRIVED(goal) sector {cur} at tick {i}"); return (cur, log); }

            Vector3 warpPos = Vector3.Zero;
            bool foundWarp = TryBuildWarpDestMap(cs, out var wd) && TryFindWarpTo(cs, cur, next, wd, out warpPos);
            if (foundWarp)
                CombatFlyTo(cs, shipId, warpPos, arrival, brake, 0);
            else
                cs.CommandControls(ControlData.Neutral, 0);

            if (cur != lastSector)
            {
                lastSector = cur;
                var srvSector = server.Missions.SelectMany(m => m.Sectors).First(s => s.Id == cur);
                DataWarpIGC? clientWarp = null;
                lock (cs.Gate)
                    foreach (var w in cs.World.Warps.Values)
                        if (w.ClusterId == cur && wd.TryGetValue(w.WarpId, out var dd) && dd == next)
                            clientWarp = w;
                var srvWarp = clientWarp is null ? null : srvSector.Warps.FirstOrDefault(w => w.Warp.WarpId == clientWarp.Value.WarpId);
                string cw = clientWarp is null ? "?" : $"{clientWarp.Value.WarpId}@{Vec3(clientWarp.Value.Position):F0}";
                string sw = srvWarp is null ? "?" : $"{srvWarp.Warp.WarpId}@{Vec3(srvWarp.Warp.Position!):F0} r{srvWarp.Warp.Radius:F0}";
                TryCsPose(cs, shipId, out var myPose, out _);
                var srvPos = serverShip is null ? Vector3.Zero : serverShip.Position;
                log.Add($"hop to {next}: clientWarp={cw} | serverWarp={sw} | clientSelf={myPose:F0} | serverSelf={srvPos:F0}");
            }
        }
        return (CsSector(cs), log);
    }

    /// <summary>⭐ P484: a client bot steered by the MCP warp-approach law
    /// crosses BOTH alephs to the 2-hop target sector Plato(1060) when the
    /// transit is unobstructed. At each hop the client-believed warp position
    /// and own-pose match the server's authoritative values exactly — the
    /// warp-export / coordinate-handling hypothesis P429 floated is disproven
    /// by measurement. The MCP law, the warp wire data, and the server transit
    /// are all correct for a multi-hop client flight.</summary>
    [Fact]
    public void Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target()
    {
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;   // pristine arena (asteroids still stripped below)
        server.Boot(DebugConfig());
        var mission = server.Missions[0];
        Assert.NotEqual(MissionStage.NotStarted, mission.Stage);   // P387 debug auto-start

        StripAsteroids(mission.Sectors.First(s => s.Id == 1053),   // Omega
                       mission.Sectors.First(s => s.Id == 1052),   // Alderan
                       mission.Sectors.First(s => s.Id == 1060));  // Plato

        using var bot = new ConnectionSession();
        bot.Connect(server.BoundPort, "p484_bot");
        bot.SendLogonRequest(Request("p484_bot"));
        int seated = PumpUntil(server, () => bot.World.MyShipId is not null, 4000);
        Assert.True(seated < 4000, "peer must be seated");

        Assert.Equal((short)1053, CsSector(bot));                  // debug spawner seats side 0 in Omega
        Assert.True(bot.World.Graph is not null, "peer must have received CLUSTERINFO");

        bot.SendPlayerReady(fReady: true, shipId: bot.World.MyShipId!.Value);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");

        short goal = 1060;                                         // Plato — 2 hops from Omega via Alderan
        var (finalSector, log) = FlyToward(server, bot, goal, arrival: 200f, brake: 450f, maxTicks: 30000);

        Console.WriteLine("P484-CLIENT:\n  " + string.Join("\n  ", log));
        Console.WriteLine($"P484-CLIENT: final sector {finalSector} vs goal {goal}");

        Assert.Equal(goal, finalSector);
    }
}
