using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Tools.McpSpawner;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P496 — the failing-first LIVE proof that a slow board-capable hull (Hvy Troop
/// Transport id 26, the <c>c_habmBoard</c> boarding hull, accel 15.0 vs the
/// default fighter's 30.0) can now transit a real aleph when the bot's
/// warp-approach law flies THROUGH the warp instead of halting short of it.
///
/// Setup is exactly P484's <see cref="ClientWarpApproachLiveTests"/> (debug
/// m2xspiderw, asteroids STRIPPED from the transit sectors so the transit itself
/// is observable, 2-hop target Plato 1060) but with the peer re-seated onto hull
/// 26 via a real BUY_LOADOUT.
///
/// <para>The mechanism under test (step 1 of the brief, not assumed): the MCP
/// warp-approach call was <c>CombatFlyTo(bot, warpPos, 200f, 450f, 0)</c> — the
/// <c>arrival=200</c> drives <c>CombatFlyTo</c>'s <c>distFactor</c> to 0 (full
/// reverse throttle) at 200 u from the warp centre, but the server's transit
/// trigger fires at <c>warp.Radius + ship.Radius</c> (≈115 u for this aleph +
/// this hull). So a slow transport is commanded to STOP ~90 u OUTSIDE the
/// transit volume and never crosses — P490's "park at the aleph", which P490
/// attributed to hull maneuverability. The two tests below measure it directly:
/// the legacy 200/450 law parks hull 26 above the transit threshold (asserted),
/// and the P496 law (<see cref="BotManager.WarpApproachParams"/>, arrival 0 /
/// brake = the transit sphere) flies it THROUGH (asserted).</para>
/// </summary>
// Synchronize each generated pose in both flights, including the legacy comparison.
[Collection("LiveNetwork")]
public sealed class ClientWarpApproachSlowHullLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    /// <summary>Debug map + the P473 test-harness grant (prefix "mcp-",
    /// ignoreTechGate) so the peer can really BUY_LOADOUT hull 26 — a slow board
    /// hull is usually tech-gated at a fresh garrison otherwise.</summary>
    private static Cfg DebugConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        LiveSettings = new Ls
        {
            DebugMode = true,
            TestHarness = new Allegiance.Server.TestHarnessConfig { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        Games =
        [
            new Gm { ShowMap = true,   // P797: client-bot navigation needs known alephs until P798.
                Name = "P496 Slow Board Hull Warp", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 611u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 62200000000000000ul, DrmHash = "p496slow",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

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

    // ── The MCP combat routing + warp-approach law, ported verbatim ────────
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

    private static bool TryFindWarpTo(ConnectionSession cs, short cur, short next, IReadOnlyDictionary<short, short> warpDest, out Vector3 pos, out float radius)
    {
        lock (cs.Gate)
        {
            foreach (var w in cs.World.Warps.Values)
            {
                if (w.ClusterId != cur) continue;
                if (warpDest.TryGetValue(w.WarpId, out var d) && d == next)
                {
                    pos = Vec3(w.Position);
                    radius = w.Radius;
                    return true;
                }
            }
        }
        pos = Vector3.Zero;
        radius = 0f;
        return false;
    }

    /// <summary>The MCP <c>CombatFlyTo</c> face-and-thrust law (identical to
    /// BotManager.cs), returning the <see cref="ControlData"/> it sent so the
    /// caller can record the throttle for the P496 mechanism evidence.</summary>
    private static ControlData CombatFlyTo(ConnectionSession cs, short shipId, Vector3 target, float arrival, float brake, int stateM)
    {
        if (!TryCsPose(cs, shipId, out var pos, out var orient)) return default;
        var toTarget = target - pos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f) { cs.CommandControls(ControlData.Neutral, stateM); return ControlData.Neutral; }
        var dir = toTarget / dist;
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float yaw = Math.Clamp(2.0f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2.0f * -body.X, -1f, 1f);
        float distFactor = dist >= brake ? 1f : Math.Clamp((dist - arrival) / (brake - arrival), 0f, 1f);
        float align = Math.Clamp(-orient.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * align - 1f, -1f, 1f);
        var cd = new ControlData(yaw, pitch, 0f, throttle);
        cs.CommandControls(cd, stateM);
        return cd;
    }

    /// <summary>Drive a seated peer toward <paramref name="goal"/> with the MCP
    /// routing law, deriving each warp hop's arrival/brake from
    /// <paramref name="approach"/> (given the hop warp's transit sphere). Records
    /// the P496 mechanism evidence: min distance to each hop-warp, and (at the
    /// point of minimum distance) the commanded throttle, the ship's speed and
    /// the transit threshold <c>warp.Radius + ship.Radius</c>.</summary>
    private static (short finalSector, float minWarpDist, float minTransitClearance, string parkEvidence, List<string> log) FlyToward(
        Srv server, ConnectionSession cs, StaticCore core, short goal,
        Func<float, float, (float arrival, float brake)> approach, int maxTicks)
    {
        var shipId = cs.World.MyShipId!.Value;
        float shipRadius;
        lock (cs.Gate)
        {
            shipRadius = cs.World.Ships.TryGetValue(shipId, out var me)
                         && core.HullTypes.TryGetValue(me.HullId, out var mh) ? 0.5f * mh.Length : 0f;
        }

        var log = new List<string>();
        short lastSector = -1;
        float minWarpDist = float.MaxValue;
        float minTransitClearance = float.MaxValue;
        string parkEvidence = "";
        var serverShip = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models).OfType<Ship>()
            .FirstOrDefault(s => s.Id == shipId);

        for (int i = 0; i < maxTicks; i++)
        {
            server.TickSim();
            // TickSim has no wall-clock cadence. Do not advance another 50 ms
            // of simulation while the TCP client still steers from an older
            // pose: that tests arbitrary network backlog, not the two arrival
            // laws. Stationary poses need not be rebroadcast, so compare the
            // decoded pose (with the server's dirty/compact tolerances).
            if (serverShip is not null)
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    lock (cs.Gate)
                        return cs.World.Ships.TryGetValue(shipId, out var own)
                            && own.Latest is { } pose
                            && own.SectorId == serverShip.Sector?.Id
                            && (pose.Position - serverShip.Position).Length() < 0.002f
                            && (pose.Orientation.GetForward() - serverShip.Orientation.GetForward()).Length() < 0.002f;
                }, TimeSpan.FromSeconds(2)), "own TCP pose must catch up before the next steering sample");
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
                    if (next != cur && TryBuildWarpDestMap(cs, out var wd) && TryFindWarpTo(cs, cur, next, wd, out var warpPos, out var warpR))
                    {
                        float threshold = warpR + shipRadius;           // the transit sphere (server trigger)
                        var (arrival, brake) = approach(warpR, shipRadius);
                        var cd = CombatFlyTo(cs, shipId, warpPos, arrival, brake, 0);
                        if (serverShip is not null)
                        {
                            float d = (serverShip.Position - warpPos).Length();
                            minTransitClearance = MathF.Min(minTransitClearance, d - threshold);
                            if (d < minWarpDist)
                            {
                                minWarpDist = d;
                                float spd = serverShip.Velocity.Length();
                                parkEvidence = $"minDist={d:F1} u (transit threshold {threshold:F1}) throttle={cd.Throttle:F2} speed={spd:F1} u/s sector={cur}";
                            }
                        }
                    }
                    else
                        cs.CommandControls(ControlData.Neutral, 0);
                }
            }

            if (cur != lastSector)
            {
                lastSector = cur;
                log.Add($"reached sector {cur} at tick {i}");
            }
            if (cur == goal) { log.Add($"ARRIVED goal sector {cur} at tick {i}"); break; }
        }
        return (CsSector(cs), minWarpDist, minTransitClearance, parkEvidence, log);
    }

    private static (Srv server, ConnectionSession bot, StaticCore core, short hullId) BootSeat()
    {
        var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(DebugConfig());
        var mission = server.Missions[0];
        Assert.NotEqual(MissionStage.NotStarted, mission.Stage);   // P387 debug auto-start
        StripAsteroids(mission.Sectors.First(s => s.Id == 1053),
                       mission.Sectors.First(s => s.Id == 1052),
                       mission.Sectors.First(s => s.Id == 1060));

        var core = mission.Core;
        var plan = ScenarioPlanner.PlanCaptureAttempt();
        short hullId = plan.BoardingHullId;
        Assert.Equal((short)26, hullId);                       // Hvy Troop Transport — the P450 board hull
        Assert.True(core.HullTypes[hullId].Mass > 0f && core.HullTypes[hullId].Thrust > 0f, "hull 26 must resolve real stats");

        var bot = new ConnectionSession();
        bot.Connect(server.BoundPort, "mcp-p496-boarder");
        bot.SendLogonRequest(Request("mcp-p496-boarder"));
        int seated = PumpUntil(server, () => bot.World.MyShipId is not null, 4000);
        Assert.True(seated < 4000, "peer must be seated");
        Assert.Equal((short)1053, CsSector(bot));
        Assert.True(bot.World.Graph is not null, "peer must have received CLUSTERINFO");

        // Re-seat onto the board hull RIGHT AFTER seating (still at the dock,
        // like the MCP SeatCombatClient buys its loadout), so the buy+launch is
        // valid — a hull swap mid-flight is refused.
        var myId = bot.World.MyShipId!.Value;
        bot.ResetBuyLoadoutAcks();
        bot.SendBuyLoadout(hullId, fLaunch: true);
        int swapped = PumpUntil(server, () =>
        {
            lock (bot.Gate) return bot.World.Ships.TryGetValue(myId, out var e) && e.HullId == hullId;
        }, 8000);
        Assert.True(swapped < 8000, $"peer never re-seated onto board hull {hullId}");
        Console.WriteLine($"P496-HULL: boarded hull id {hullId} '{plan.BoardingHullName}' (accel {core.HullTypes[hullId].Thrust / core.HullTypes[hullId].Mass:F1}) shipRadius {0.5f * core.HullTypes[hullId].Length:F1}");

        bot.SendPlayerReady(fReady: true, shipId: myId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");

        return (server, bot, core, hullId);
    }

    /// <summary>⭐ Step 1 evidence + RED: with the LEGACY warp-approach arrival
    /// (200/450 — the constant BotManager shipped before P496), the slow board
    /// hull 26 is commanded to stop ~90 u OUTSIDE the transit sphere and never
    /// transits the aleph: it parks above the threshold (asserted) exactly as
    /// P490's live run observed. This measures the mechanism, not assumes it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")] // Measured 18-19 s alone; exceeds the Fast-tier limit.
    public void Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume()
    {
        Require.Artwork();
        var (server, bot, core, hullId) = BootSeat();
        using var srv = server;
        using var b = bot;

        // P484 setup: 2-hop target Plato(1060); the legacy 200/450 constants.
        short goal = 1060;
        var (finalSector, minWarpDist, minTransitClearance, parkEvidence, log) =
            FlyToward(srv, b, core, goal, (_, _) => (arrival: 200f, brake: 450f), maxTicks: 35000);

        Console.WriteLine("P496-LEGACY:\n  " + string.Join("\n  ", log));
        Console.WriteLine($"P496-LEGACY: final sector {finalSector} (start 1053, goal 1060); {parkEvidence}");

        // The board hull must NOT have crossed: it parks above the transit threshold.
        Assert.Equal((short)1053, finalSector); // no transit, including the first hop
        // Compare every approach sample to the decoded warp + hull radius.
        // A 128-unit stop is safely outside a 115-unit transit sphere; the
        // former arbitrary 150-unit margin falsely rejected that valid stop.
        Assert.True(minTransitClearance > 0f,
            $"legacy 200/450 must stay OUTSIDE the actual transit volume: clearance={minTransitClearance:F1}, minWarpDist={minWarpDist:F1}");
    }

    /// <summary>⭐ GREEN: with the P496 warp-approach law
    /// (<see cref="BotManager.WarpApproachParams"/> — arrival 0, brake = the
    /// transit sphere), the same slow board hull 26 flies THROUGH both alephs to
    /// the 2-hop target. Fails on the legacy 200/450 law (and did not compile
    /// before the member existed) — the failing-first proof of the fix.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Slow_board_hull_26_transits_when_the_warp_approach_flies_through_the_aleph()
    {
        Require.Artwork();
        var (server, bot, core, hullId) = BootSeat();
        using var srv = server;
        using var b = bot;

        short goal = 1060;
        var (finalSector, minWarpDist, _, parkEvidence, log) =
            FlyToward(srv, b, core, goal,
                (warpR, shpR) => BotManager.WarpApproachParams(warpR, shpR),
                maxTicks: 60000);

        Console.WriteLine("P496-FIX:\n  " + string.Join("\n  ", log));
        Console.WriteLine($"P496-FIX: final sector {finalSector} (goal 1060); {parkEvidence}");

        Assert.Equal(goal, finalSector);
        Assert.True(minWarpDist < 120f,
            $"fixed law must drive hull 26 INSIDE the transit volume, but minWarpDist={minWarpDist:F1}");
    }
}
