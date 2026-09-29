using System.Diagnostics;
using Allegiance.Core;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using BotClass = global::Allegiance.Bot.Bot;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P426 — owns the real wire peers behind every spawned bot. One long-lived
/// <see cref="ConnectionSession"/> probe validates the live server at startup
/// (the same connect/logon/seat flow <c>vizfight attach</c> uses) and supplies
/// the real side roster for side validation / auto-pick. Each spawned bot is a
/// real <see cref="BotClass"/> connected as an ordinary client to that same
/// server; the caller is done the moment the tool call returns (spawn-and-forget)
/// and the behavior loop drives each bot for the life of this process.
/// </summary>
internal sealed class BotManager : IDisposable
{
    private readonly string _host;
    private readonly ushort _port;
    private readonly object _gate = new();
    private readonly List<ActiveBot> _bots = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _loopCts = new();
    private readonly Thread _loopThread;
    private int _nameCounter;
    private bool _startedLoop;

    public ConnectionSession Probe { get; }

    /// <summary>The side the server actually seated the probe on (authoritative
    /// world state). Spawned bots default to this side so the probe can see them
    /// (a client only ever sees same-side ships anywhere — the vizfight rule).
    /// Callers may still request the other side explicitly.</summary>
    public short ProbeSide { get; private set; } = -1;

    /// <summary>True once the startup probe is connected, logged on and seated
    /// (i.e. the server is real and serving), so tools know a live server is
    /// reachable before they run.</summary>
    public bool ServerReady => Probe.Ack is { FValidated: true };

    public BotManager(string host, ushort port)
    {
        _host = host;
        _port = port;
        Probe = new ConnectionSession();
        ConnectProbe();
        _loopThread = new Thread(Loop) { IsBackground = true, Name = "allegiance-mcp-behavior" };
    }

    private double Now() => _clock.Elapsed.TotalSeconds;

    // ── Startup probe (server validation + side roster) ────────────────────

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xC0FFEEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 76000000000000000ul, DrmHash = "allegiance-mcp",
    };

    /// <summary>Connect the probe to the real server and seat it before serving
    /// any tool call, exactly like <c>AttachMain</c> seats its spectator. Throws
    /// with a specific message if the server is unreachable / never answers —
    /// startup therefore fails loudly instead of serving tools against nothing.</summary>
    private void ConnectProbe()
    {
        Probe.Connect(_port, "MCP-PROBE", _host);
        Probe.SendLogonRequest(Request("MCP-PROBE"));
        if (!WaitUntil(() => Probe.Ack is { FValidated: true }, 12000))
            throw new InvalidOperationException($"startup probe: no validated LOGONACK from {_host}:{_port}");
        short side = ProbeSidePref();
        Probe.SendPositionReq(side);
        if (!WaitUntil(() => Probe.World.MyShipId is not null, 15000))
            throw new InvalidOperationException("startup probe: never seated (no ship assigned by the server)");
        lock (Probe.Gate)
            ProbeSide = Probe.World.MySideId ?? (short)(side >= 0 ? side : 0);
    }

    private static bool WaitUntil(Func<bool> done, int maxMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!done() && DateTime.UtcNow < deadline) Thread.Sleep(20);
        return done();
    }

    private short ProbeSidePref()
    {
        lock (Probe.Gate)
            if (Probe.World.SideRoster is { Sides.Length: > 0 } sr) return sr.Sides[0].SideId;
        return 0;
    }

    /// <summary>The set of sides this server can seat a bot on: the real positive
    /// roster ids when the roster reports them, else the {0,1} universe (a
    /// 2-team server, the documented norm) so an explicit side is still validated.</summary>
    public IReadOnlyList<short> ValidSides()
    {
        lock (Probe.Gate)
        {
            if (Probe.World.SideRoster is { Sides.Length: > 0 } sr)
            {
                var ids = sr.Sides.Select(s => s.SideId).Where(s => s >= 0).Distinct().ToList();
                if (ids.Count > 0) return ids;
            }
        }
        return new short[] { 0, 1 };
    }

    /// <summary>True once the probe's real ClientWorld is streaming the given ship
    /// (authoritative proof the server launched/accepted it — same-side visibility).</summary>
    private bool ProbeHasShip(short shipId)
    {
        lock (Probe.Gate)
            return Probe.World.Ships.TryGetValue(shipId, out var e) && e.Latest is not null;
    }

    /// <summary>Read a spawned bot's LIVE pose from the probe's real ClientWorld
    /// (the same authoritative same-side source vizfight uses). False when the
    /// probe can't see that ship (cross-side or another cluster).</summary>
    private bool TryProbePose(short shipId, out Vector3 pos, out short sector)
    {
        lock (Probe.Gate)
        {
            if (Probe.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
            {
                pos = lu.Position;
                sector = e.SectorId;
                return true;
            }
        }
        pos = Vector3.Zero;
        sector = -1;
        return false;
    }

    // ── Spawn (the primary tool) ───────────────────────────────────────────

    /// <summary>Something a <c>spawn_bot</c> call hands back the moment it returns.</summary>
    public readonly record struct SpawnResult(string Name, short ShipId, short Side, string Hull, int ActiveCount);

    /// <summary>
    /// Connect a real peer, seat it on <paramref name="side"/> (or auto-pick the
    /// first available side when null), buy <paramref name="parts"/> and launch
    /// it in one real wire call, then register it with the behavior loop. The
    /// peer is a <see cref="BotClass"/> for <c>idle</c>/<c>weave</c> (a simple
    /// client) and a <see cref="ConnectionSession"/> for <c>combat</c> — combat
    /// needs the peer's OWN <see cref="ClientWorld"/> (own pose, co-located
    /// cross-side ships, warp geometry) to navigate and engage, exactly the
    /// vizfight enemy-peer shape. Blocks only until the seat + launch round-trips
    /// (a short, real confirmation); the ongoing behavior runs in the background
    /// thereafter. Any unresolvable name/side or a genuine server failure throws
    /// a specific error — never a silent no-op.
    /// </summary>
    public SpawnResult SpawnBot(short hullId, string hullName, short? side, IReadOnlyList<ExpandedPartData> parts, string behavior, short? targetSector = null)
    {
        if (!ServerReady)
            throw new InvalidOperationException("spawn_bot: server probe not connected/validated — cannot spawn");

        // Validate / auto-pick the side. Default = the probe's own side (guarantees
        // the probe sees the bot's live pose). An explicit side is validated against
        // the real roster when it reports positive ids, else the {0,1} universe.
        short chosenSide;
        if (side is { } requested)
        {
            var sides = ValidSides();
            if (!sides.Contains(requested))
                throw new InvalidOperationException($"spawn_bot: side {requested} is not a side of this server (valid sides: {string.Join(", ", sides)})");
            chosenSide = requested;
        }
        else
        {
            chosenSide = ProbeSide >= 0 ? ProbeSide : (ValidSides().Count > 0 ? ValidSides()[0] : (short)0);
        }

        string name = NextName();
        ActiveBot bot;
        short shipId;
        short actualSide;

        if (behavior == "combat")
        {
            // A combat bot is driven as its own ConnectionSession (mirrors vizfight's
            // AttachEnemyBot.Cs): it must read its own world's pose, cross-side ships
            // and warp geometry while it transits clusters the probe never sees.
            var cs = new ConnectionSession();
            cs.Connect(_port, name, _host);
            cs.SendLogonRequest(Request(name));
            if (!WaitUntil(() => cs.Ack is { FValidated: true }, 12000))
                throw new InvalidOperationException($"spawn_bot: combat bot '{name}' never got a validated LOGONACK from {_host}:{_port}");
            cs.SendPositionReq(chosenSide);
            if (!WaitUntil(() => cs.World.MyShipId is not null, 15000))
                throw new InvalidOperationException($"spawn_bot: combat bot '{name}' was never seated (no ship assigned) on side {chosenSide}");

            shipId = cs.World.MyShipId!.Value;
            actualSide = cs.World.MySideId ?? chosenSide;

            // Buy + launch in one real wire call (the exact AttachMain flow). A
            // refused loadout sends no ack and the ship never streams — a real error.
            cs.SendBuyLoadout(hullId, fLaunch: true, parts);
            if (actualSide == ProbeSide)
            {
                if (!WaitUntil(() => ProbeHasShip(shipId) || !cs.Connected, 4000))
                    throw new InvalidOperationException(
                        $"spawn_bot: server never began streaming ship '{name}' (hull id {hullId} '{hullName}') "
                        + "after BUY_LOADOUT — the loadout may have been refused by the server's ApplyLoadout");
                if (!cs.Connected)
                    throw new InvalidOperationException($"spawn_bot: bot '{name}' disconnected during launch");
            }
            // Cross-side from the probe: launch is trusted from the seat round-trip.

            bot = new ActiveBot(name, hullId, hullName, actualSide, chosenSide, behavior, NextSeed())
            {
                Cs = cs,
                ShipId = shipId,
                Launched = true,
                TargetSector = targetSector,
            };
        }
        else
        {
            // idle / weave: a plain BotClass client (same-side observable by the probe).
            var b = new BotClass { AutoSelectSideAfterLogon = false };
            b.Connect(_port, name, _host);
            b.SendLogonRequest(Request(name));
            if (!WaitUntil(() => b.Ack is { FValidated: true }, 12000))
                throw new InvalidOperationException($"spawn_bot: bot '{name}' never got a validated LOGONACK from {_host}:{_port}");
            b.SendPositionReq(chosenSide);
            if (!WaitUntil(() => b.ShipAssignment is not null, 15000))
                throw new InvalidOperationException($"spawn_bot: bot '{name}' was never seated (no ship assigned) on side {chosenSide}");

            shipId = b.ShipAssignment!.Ship.ShipId;
            actualSide = ActualSide(b, shipId, chosenSide);

            b.SendBuyLoadout(hullId, fLaunch: true, parts);
            if (actualSide == ProbeSide)
            {
                // Same side as the probe: require the probe to actually stream the
                // ship (proof the loadout was accepted and the ship is real).
                if (!WaitUntil(() => ProbeHasShip(shipId) || !b.Connected, 4000))
                    throw new InvalidOperationException(
                        $"spawn_bot: server never began streaming ship '{name}' (hull id {hullId} '{hullName}') "
                        + "after BUY_LOADOUT — the loadout may have been refused by the server's ApplyLoadout");
                if (!b.Connected)
                    throw new InvalidOperationException($"spawn_bot: bot '{name}' disconnected during launch");
            }

            bot = new ActiveBot(name, hullId, hullName, actualSide, chosenSide, behavior, NextSeed()) { Bot = b };
        }

        lock (_gate)
        {
            _bots.Add(bot);
            if (!_startedLoop)
            {
                _startedLoop = true;
                _loopThread.Start();
            }
        }
        int count = _bots.Count;
        return new SpawnResult(name, shipId, actualSide, hullName, count);
    }

    /// <summary>A snapshot of the probe's live sector map (id → cluster record
    /// with its real Name) — the authoritative real corpus <c>target_sector</c>
    /// resolves against. Waits briefly for the join-burst cluster exports so a
    /// name resolves even on the very first tool call after startup.</summary>
    public IReadOnlyDictionary<short, DataClusterIGC> ProbeClusters()
    {
        WaitUntil(() =>
        {
            lock (Probe.Gate) return Probe.World.Clusters.Count > 0;
        }, 3000);
        lock (Probe.Gate)
            return new Dictionary<short, DataClusterIGC>(Probe.World.Clusters);
    }

    private string NextName()
    {
        int n = Interlocked.Increment(ref _nameCounter);
        int rand = Random.Shared.Next(1000, 9999);
        return $"MCPB{n:D3}-{rand}";
    }

    /// <summary>The side the server actually seated the ship on (from the real
    /// JOIN_SIDE broadcast for this ship), falling back to the requested side.</summary>
    private static short ActualSide(BotClass b, short shipId, short requested)
    {
        foreach (var js in b.JoinSides)
            if (js.ShipId == shipId) return js.SideId;
        return requested;
    }

    private static double NextSeed() => Random.Shared.NextDouble() * (2 * Math.PI);

    // ── Behavior loop (background; drives every bot for the process life) ──

    private void Loop()
    {
        while (!_loopCts.IsCancellationRequested)
        {
            try
            {
                double now = Now();
                ActiveBot[] snap;
                lock (_gate) snap = _bots.ToArray();
                foreach (var bot in snap)
                {
                    try
                    {
                        if (bot.Behavior == "combat")
                        {
                            DriveCombat(bot, now);
                            continue;
                        }
                        if (bot.Bot?.ShipAssignment is null) continue;
                        if (bot.Behavior == "weave")
                        {
                            double yaw = Math.Sin(now * bot.WeaveFreq + bot.WeavePhase) * bot.WeaveAmp;
                            double throttle = 0.55 + 0.25 * Math.Sin(now * bot.WeaveFreq * 0.7 + bot.WeavePhase * 1.3);
                            bot.Bot.CommandControls(new ControlData((float)yaw, 0f, 0f, (float)throttle), 0);
                        }
                        // idle: seat but send no movement commands — the simplest real state.
                    }
                    catch { /* one bot's tick failure never kills the loop */ }
                }
            }
            catch { }
            Thread.Sleep(50);
        }
    }

    // ── Combat behavior (P429 — faithful port of vizfight's P420 choreography) ──
    //
    // Every helper below is a direct port of the equivalent in
    // tools/vizfight/Program.cs (attached as `[vz]`): the rendezvous routing
    // (AttachEnemyFlyTo / EnsureEnemyPath / EnemyNextHop / TryBuildWarpDestMap /
    // TryFindWarpTo) and the engagement (EngageEnemy / LoiterEnemy). A combat bot
    // is driven as its own ConnectionSession `Cs`, reading its own world for its
    // pose, co-located cross-side ships and warp geometry — exactly how vizfight
    // drives its opposing contingent.

    /// <summary>[vz EnemyCluster] The cluster the bot's own client is in (its own
    /// ship's sector, re-stamped by the server's SET_CLUSTER per transit).</summary>
    private static short CsSector(ConnectionSession cs)
    {
        lock (cs.Gate)
            if (cs.World.MyShipId is { } me && cs.World.Ships.TryGetValue(me, out var e))
                return e.SectorId;
        return -1;
    }

    /// <summary>[vz TryGetEnemyPose] The bot's LIVE pose from ITS OWN ClientWorld.</summary>
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

    /// <summary>[vz TryBuildWarpDestMap] warpId → destination-cluster map from the
    /// bot's real world (each warp's <c>DestinationId</c> names its partner warp;
    /// the partner's cluster is the destination sector).</summary>
    private static bool TryBuildWarpDestMap(ConnectionSession cs, out Dictionary<short, short> map)
    {
        map = new Dictionary<short, short>();
        lock (cs.Gate)
        {
            foreach (var w in cs.World.Warps.Values)
                if (cs.World.Warps.TryGetValue(w.DestinationId, out var partner))
                    map[w.WarpId] = partner.ClusterId;
        }
        return map.Count > 0;
    }

    /// <summary>[vz TryFindWarpTo] The position of the warp in cluster
    /// <paramref name="cur"/> that leads to cluster <paramref name="next"/>.</summary>
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

    /// <summary>[vz EnsureEnemyPath] Recompute the bot's cluster path only when
    /// needed: keep the existing one while it still covers the current cluster
    /// and the goal is unchanged, else re-BFS from the current cluster.</summary>
    private static void EnsureCombatPath(ActiveBot bot, short cur, short goal)
    {
        if (bot.PathGoal == goal && bot.Path.Count > 0 && bot.Path.Contains(cur)) return;
        lock (bot.Cs!.Gate)
        {
            var g = bot.Cs.World.Graph;
            if (g is null) return;
            bot.Path = g.FindShortestPath(cur, goal);
        }
        bot.PathStart = cur;
        bot.PathGoal = goal;
    }

    /// <summary>[vz EnemyNextHop] The next cluster on the path after
    /// <paramref name="cur"/>; <c>cur</c> itself when it is the goal.</summary>
    private static short CombatNextHop(ActiveBot bot, short cur)
    {
        for (int i = 1; i < bot.Path.Count; i++)
            if (bot.Path[i - 1] == cur) return bot.Path[i];
        return cur;
    }

    /// <summary>[vz AttachEnemyFlyTo] Face-and-thrust autopilot law (the same
    /// <c>Ship.SteerToward</c> law vizfight's FlyTo uses), reading the bot's pose
    /// from ITS OWN ClientWorld. Fires mounted weapons when <paramref name="stateM"/>
    /// has AllWeapons.</summary>
    private static void CombatFlyTo(ActiveBot bot, Vector3 target, float arrival, float brake, int stateM)
    {
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out var pos, out var orient)) return;
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

    /// <summary>[vz LoiterEnemy] A bounded in-place weave for a bot with nothing
    /// specific to fly to (transit stall, waiting on combat, no co-located enemy).</summary>
    private static void LoiterCombat(ActiveBot bot, double now)
    {
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out _, out _)) return;
        double yaw = Math.Sin(now * bot.WeaveFreq + bot.WeavePhase) * bot.WeaveAmp;
        double throttle = 0.5 + 0.2 * Math.Sin(now * bot.WeaveFreq * 0.7 + bot.WeavePhase * 1.3);
        cs.CommandControls(new ControlData((float)yaw, 0f, 0f, (float)throttle), 0);
    }

    /// <summary>[vz EngageEnemy] Pick the nearest DIFFERENT-side ship co-located in
    /// the bot's OWN cluster, point itself at it via a real cross-side
    /// ORDER_CHANGE target acquisition, and hold AllWeapons with periodic
    /// afterburner-boost / evasive-cloak beats — the full real loadout. Sets the
    /// bot's <see cref="ActiveBot.TargetId"/> so <c>list_active_bots</c> can show
    /// the engagement. Loiters when no enemy is visible.</summary>
    private static void EngageCombat(ActiveBot bot, double now)
    {
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out var myPos, out _)) return;
        short myCluster = CsSector(cs);
        short best = -1;
        float bestD = float.MaxValue;
        lock (cs.Gate)
        {
            foreach (var ship in cs.World.Ships.Values)
            {
                if (ship.ShipId == bot.ShipId || ship.IsNotVisible) continue;
                if (ship.SideId == bot.Side) continue;      // a DIFFERENT side only
                if (ship.SectorId != myCluster) continue;   // co-located
                if (ship.Latest is not { } lu) continue;
                float d = (lu.Position - myPos).Length();
                if (d < bestD) { bestD = d; best = ship.ShipId; }
            }
        }
        if (best < 0) { bot.TargetId = null; LoiterCombat(bot, now); return; }
        bot.TargetId = best;
        if (now - bot.LastTargetAt > 3)
        {
            cs.SendOrderChange(bot.ShipId, OrderChange.CmdQueued, OrderChange.CmdCurrent, IgcObjectType.Ship, best);
            bot.LastTargetAt = now;
        }
        Vector3 tp = Vector3.Zero;
        lock (cs.Gate)
            if (cs.World.Ships.TryGetValue(best, out var te) && te.Latest is { } tlu) tp = tlu.Position;

        bool boost = now - bot.LastBoostAt < 4;
        if (now - bot.LastBoostAt > 11) { bot.LastBoostAt = now; boost = true; }
        bool cloak = now - bot.LastCloakAt < 4;
        if (now - bot.LastCloakAt > 18) { bot.LastCloakAt = now; cloak = true; }
        int stateM = ShipControlState.AllWeapons
                     | (boost ? ShipControlState.AfterburnerButton : 0)
                     | (cloak ? ShipControlState.CloakActive : 0);
        CombatFlyTo(bot, tp, 40f, 220f, stateM);
    }

    /// <summary>[vz DriveEnemyContingent] Drive one combat bot a tick. The bot is
    /// already launched at spawn; if it has a <c>target_sector</c> it routes real
    /// warps along the BFS shortest path there before engaging; otherwise (or once
    /// arrived) it engages any different-side ship already in its own cluster.</summary>
    private void DriveCombat(ActiveBot bot, double now)
    {
        var cs = bot.Cs!;
        if (cs.World.MyShipId is not { } my) return;     // not seated yet
        bot.ShipId = my;

        if (bot.TargetSector is not { } goal) { EngageCombat(bot, now); return; }

        short cur = CsSector(cs);
        if (cur < 0) return;                             // own pose not streaming yet
        if (cur == goal) bot.Arrived = true;

        if (!bot.Arrived)
        {
            EnsureCombatPath(bot, cur, goal);
            if (bot.Path.Count == 0) { LoiterCombat(bot, now); return; }   // unroutable / no CLUSTERINFO yet
            short next = CombatNextHop(bot, cur);
            if (next == cur) { bot.Arrived = true; bot.LastCluster = cur; return; }
            bot.WarpDest ??= TryBuildWarpDestMap(cs, out var m) ? m : null;
            if (bot.WarpDest is { } wd && TryFindWarpTo(cs, cur, next, wd, out var warpPos))
                CombatFlyTo(bot, warpPos, 200f, 450f, 0);
            else
                LoiterCombat(bot, now);
            bot.LastCluster = cur;
        }
        else
        {
            EngageCombat(bot, now);
        }
    }

    // ── Read-only visibility ──────────────────────────────────────────────

    /// <summary>One spawned bot's live, real state (read straight from its own /
    /// the probe's server-reported world on demand — no synthetic state).</summary>
    public sealed class ActiveBotView
    {
        public required string Name { get; init; }
        public required short ShipId { get; init; }
        public required short Side { get; init; }
        public required string Hull { get; init; }
        public required short Sector { get; init; }
        public required string Behavior { get; init; }
        public required double[] Position { get; init; }
        /// <summary>A combat bot's current engagement target ship id (null when
        /// idle/weave, or a combat bot not yet engaged).</summary>
        public required int? Target { get; init; }
    }

    private static short ShipIdOf(ActiveBot bot)
    {
        if (bot.Bot is { } b && b.ShipAssignment is { } asn) return asn.Ship.ShipId;
        lock (bot.Cs!.Gate)
            return bot.Cs.World.MyShipId ?? -1;
    }

    public IReadOnlyList<ActiveBotView> ActiveBots()
    {
        var views = new List<ActiveBotView>();
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                try
                {
                    short shipId = ShipIdOf(bot);
                    // Authoritative live pose from the probe's real ClientWorld
                    // (same-side ships anywhere — the vizfight visibility rule).
                    if (TryProbePose(shipId, out var pos, out var sector))
                    {
                        views.Add(new ActiveBotView
                        {
                            Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                            Sector = sector, Behavior = bot.Behavior,
                            Position = [pos.X, pos.Y, pos.Z], Target = bot.TargetId,
                        });
                        continue;
                    }
                    if (bot.Cs is { } cs)
                    {
                        // A cross-side combat bot: the probe can't see it. Read its
                        // REAL pose/sector from ITS OWN ClientWorld (the truthful
                        // source — vizfight reads enemy poses this way).
                        short sec = -1;
                        Vector3 p = Vector3.Zero;
                        lock (cs.Gate)
                            if (cs.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
                            { sec = e.SectorId; p = lu.Position; }
                        views.Add(new ActiveBotView
                        {
                            Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                            Sector = sec, Behavior = bot.Behavior,
                            Position = [p.X, p.Y, p.Z], Target = bot.TargetId,
                        });
                        continue;
                    }
                    // Cross-side idle/weave (rare): report the assigned sector + a
                    // zeroed position honestly (the documented scope of visibility).
                    short s = bot.Bot is { } b2 && b2.ShipAssignment is { } a ? a.SectorId : (short)-1;
                    views.Add(new ActiveBotView
                    {
                        Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                        Sector = s, Behavior = bot.Behavior, Position = [0, 0, 0], Target = null,
                    });
                }
                catch { /* one bot's bad state never breaks list_active_bots */ }
            }
        }
        return views;
    }

    public void Dispose()
    {
        _loopCts.Cancel();
        lock (_gate)
            foreach (var bot in _bots)
            {
                try { bot.Bot?.Dispose(); } catch { }
                try { bot.Cs?.Dispose(); } catch { }
            }
        _bots.Clear();
        Probe.Dispose();
    }

    /// <summary>
    /// The per-bot bookkeeping. Exactly one of <see cref="Bot"/> (idle/weave — a
    /// plain client) and <see cref="Cs"/> (combat — its own ClientWorld) is set.
    /// The combat fields mirror vizfight's <c>AttachEnemyBot</c> exactly (same
    /// names/shapes: TargetId, LastTargetAt/BoostAt/CloakAt, Path/PathStart/
    /// PathGoal/LastCluster, WarpDest, Arrived/Launched).
    /// </summary>
    private sealed class ActiveBot
    {
        public ActiveBot(string name, short hullId, string hullName, short side, short requestedSide, string behavior, double phase)
        {
            Name = name; HullId = hullId; HullName = hullName;
            Side = side; RequestedSide = requestedSide; Behavior = behavior;
            WeavePhase = phase;
            WeaveFreq = 0.033 + Random.Shared.NextDouble() * 0.009;
            WeaveAmp = 0.024 + Random.Shared.NextDouble() * 0.015;
        }

        // The wire peer — exactly one is non-null (see the class doc).
        public BotClass? Bot;
        public ConnectionSession? Cs;

        public string Name;
        public short HullId;
        public string HullName;
        public short Side;
        public short RequestedSide;
        public string Behavior;

        // weave seed (idle/weave + combat loiter).
        public double WeavePhase;
        public double WeaveFreq;
        public double WeaveAmp;

        // ── combat state (mirrors vizfight's AttachEnemyBot) ──
        public short ShipId = -1;
        public bool Launched;
        public bool Arrived;                       // reached the target sector
        public short? TargetId;                    // the enemy ship this bot is engaging
        public double LastTargetAt = double.NegativeInfinity;
        public double LastBoostAt = double.NegativeInfinity;
        public double LastCloakAt = double.NegativeInfinity;
        public double LastNarrateAt = double.NegativeInfinity;
        public short? TargetSector;                // rendezvous sector, or null to engage in place
        public List<short> Path = new();
        public short PathStart = -1;
        public short PathGoal = -1;
        public short LastCluster = -1;
        public Dictionary<short, short>? WarpDest;
    }
}
