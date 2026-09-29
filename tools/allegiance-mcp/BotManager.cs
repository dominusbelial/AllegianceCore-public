using System.Diagnostics;
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
    /// Connect a real <see cref="BotClass"/>, seat it on <paramref name="side"/>
    /// (or auto-pick the first available side when null), buy <paramref name="parts"/>
    /// and launch it in one real wire call, then register it with the behavior loop.
    /// Blocks only until the seat + launch round-trips (a short, real confirmation);
    /// the ongoing behavior runs in the background thereafter. Any unresolvable
    /// name/side or a genuine server failure throws a specific error — never a silent no-op.
    /// </summary>
    public SpawnResult SpawnBot(short hullId, string hullName, short? side, IReadOnlyList<ExpandedPartData> parts, string behavior)
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

        var b = new BotClass { AutoSelectSideAfterLogon = false };
        string name = NextName();
        b.Connect(_port, name, _host);
        b.SendLogonRequest(Request(name));
        if (!WaitUntil(() => b.Ack is { FValidated: true }, 12000))
            throw new InvalidOperationException($"spawn_bot: bot '{name}' never got a validated LOGONACK from {_host}:{_port}");
        b.SendPositionReq(chosenSide);
        if (!WaitUntil(() => b.ShipAssignment is not null, 15000))
            throw new InvalidOperationException($"spawn_bot: bot '{name}' was never seated (no ship assigned) on side {chosenSide}");

        short shipId = b.ShipAssignment!.Ship.ShipId;
        short actualSide = ActualSide(b, shipId, chosenSide);

        // Buy + launch in one real wire call (the exact AttachMain flow). If the
        // server's authoritative ApplyLoadout refuses (e.g. an unknown hull id),
        // it sends NO ack and the ship never streams — surfaced as a real error.
        b.SendBuyLoadout(hullId, fLaunch: true, parts);
        if (actualSide == ProbeSide)
        {
            // Same side as the probe: the probe's ClientWorld is the authoritative
            // live source, so require it to actually stream the ship (proof the
            // loadout was accepted and the ship is real) before confirming.
            if (!WaitUntil(() => ProbeHasShip(shipId) || !b.Connected, 4000))
                throw new InvalidOperationException(
                    $"spawn_bot: server never began streaming ship '{name}' (hull id {hullId} '{hullName}') "
                    + "after BUY_LOADOUT — the loadout may have been refused by the server's ApplyLoadout");
            if (!b.Connected)
                throw new InvalidOperationException($"spawn_bot: bot '{name}' disconnected during launch");
        }
        // Cross-side from the probe: the probe cannot see the bot's own cluster, so
        // launch is trusted from the successful seat round-trip (documented scope).

        var bot = new ActiveBot(b, name, hullId, hullName, actualSide, chosenSide, behavior, NextSeed());
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
                        if (bot.Bot.ShipAssignment is null) continue;
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

    // ── Read-only visibility ──────────────────────────────────────────────

    /// <summary>One spawned bot's live, real state (read straight from its own
    /// server-reported world on demand — no synthetic state).</summary>
    public sealed class ActiveBotView
    {
        public required string Name { get; init; }
        public required short ShipId { get; init; }
        public required short Side { get; init; }
        public required string Hull { get; init; }
        public required short Sector { get; init; }
        public required string Behavior { get; init; }
        public required double[] Position { get; init; }
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
                    short shipId = -1;
                    if (bot.Bot.ShipAssignment is { } asn) shipId = asn.Ship.ShipId;
                    // Authoritative live pose from the probe's real ClientWorld
                    // (same-side ships anywhere — the vizfight visibility rule).
                    if (TryProbePose(shipId, out var pos, out var sector))
                    {
                        views.Add(new ActiveBotView
                        {
                            Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                            Sector = sector, Behavior = bot.Behavior,
                            Position = [pos.X, pos.Y, pos.Z],
                        });
                    }
                    else
                    {
                        // Cross-side / other cluster: the probe can't see it. Report the
                        // ship's assigned sector and a zeroed position honestly (the
                        // documented scope of live visibility).
                        short sec = bot.Bot.ShipAssignment is { } a ? a.SectorId : (short)-1;
                        views.Add(new ActiveBotView
                        {
                            Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                            Sector = sec, Behavior = bot.Behavior, Position = [0, 0, 0],
                        });
                    }
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
                try { bot.Bot.Dispose(); } catch { }
        _bots.Clear();
        Probe.Dispose();
    }

    /// <summary>The per-bot bookkeeping + weave seed (mirrors vizfight's
    /// AttachBot weave fields).</summary>
    private sealed class ActiveBot
    {
        public ActiveBot(BotClass bot, string name, short hullId, string hullName, short side, short requestedSide, string behavior, double phase)
        {
            Bot = bot; Name = name; HullId = hullId; HullName = hullName;
            Side = side; RequestedSide = requestedSide; Behavior = behavior;
            WeavePhase = phase;
            WeaveFreq = 0.042 + Random.Shared.NextDouble() * 0.012;
            WeaveAmp = 0.022 + Random.Shared.NextDouble() * 0.016;
        }
        public BotClass Bot;
        public string Name;
        public short HullId;
        public string HullName;
        public short Side;
        public short RequestedSide;
        public string Behavior;
        public double WeavePhase;
        public double WeaveFreq;
        public double WeaveAmp;
    }
}
