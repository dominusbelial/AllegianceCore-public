using System.Diagnostics;
using Allegiance.Core;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
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
internal sealed partial class BotManager : IDisposable
{
    private readonly string _host;
    private readonly ushort _port;
    private readonly object _gate = new();
    private readonly List<ActiveBot> _bots = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _loopCts = new();

    /// <summary>
    /// P539d — optional host-supplied arena relocation: given a bot's ship id,
    /// the target sector, a mirrored position and a facing, move that ship into
    /// the arena sector at that spot (the in-process live tests wire this to the
    /// server's native SetCluster relocation; a standalone MCP host provides its
    /// own supported relocation). null = arena spawns without relocation (the bot
    /// stays at home and the caller is told).
    /// </summary>
    public Func<short, short, Vector3, Vector3, bool>? Relocator;
    private readonly Thread _loopThread;
    private int _nameCounter;
    private bool _startedLoop;

    /// <summary>P430 — per-bot bounding cap on the rolling cadence sample list
    /// (real (interval, distance) observations), so an unbounded run of the
    /// behavior loop never leaks memory.</summary>
    public const int MaxSamplesPerBot = 200;

    /// <summary>P553 — per-bot bounding cap on the rolling dodge-fire trace, so a
    /// long run of a dense rock field never leaks memory (dodge fires are cheap
    /// and rare, but bounded anyway).</summary>
    public const int MaxDodgeTraces = 512;

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
        Probe.Connect(_port, "mcp-probe", _host);
        Probe.SendLogonRequest(Request("mcp-probe"));
        if (!WaitUntil(() => Probe.Ack is { FValidated: true }, 12000))
            throw new InvalidOperationException($"startup probe: no validated LOGONACK from {_host}:{_port}");
        short side = ProbeSidePref();
        Probe.SendPositionReq(side);
        if (!WaitUntil(() => Probe.World.MyShipId is not null, 15000))
            throw new InvalidOperationException("startup probe: never seated (no ship assigned by the server)");
        lock (Probe.Gate)
            ProbeSide = Probe.World.MySideId ?? (short)(side >= 0 ? side : 0);

        // P485 — the freeze root-cause fix. A NON-debug map holds
        // MissionStage.NotStarted until every occupied side's connected players
        // ready up (PLAYER_READY → RecomputeSideReady → TryAutoStart), and
        // Mission.Update early-returns on NotStarted — so the whole sim is
        // frozen and a freshly-seated combat bot never moves. Debug mode
        // auto-starts at boot, which is why only normal maps froze. vizfight
        // bypasses the lobby either in-process (m.Stage = Started — impossible
        // for an out-of-process wire client) or over the wire (SendPlayerReady,
        // its Showcase ReadyUp phase). The MCP harness does the latter: it readies
        // its OWN probe (and every bot it seats, below) exactly as a human pilot
        // clicks Ready, so the mission auto-starts on normal maps too.
        short probeShip = Probe.World.MyShipId!.Value;
        Probe.SendPlayerReady(true, probeShip);
        Console.WriteLine($"[p485] probe ready-up for ship {probeShip} on side {ProbeSide} — enables the normal-map lobby auto-start (PLAYER_READY)");
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
            if (Probe.World.SideRoster is { Sides.Length: > 0 } sr)
            {
                // P539c test seam: ALLEGIANCE_MCP_PROBE_SIDE_IDX forces which
                // roster side the probe (and thus the "probe's side") seats on,
                // so the per-side repro can swap the probe's side to test whether
                // the dominance follows the probe. Unset in production → sides[0].
                if (int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_PROBE_SIDE_IDX"), out var idx)
                    && idx >= 0 && idx < sr.Sides.Length)
                    return sr.Sides[idx].SideId;
                return sr.Sides[0].SideId;
            }
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
    public readonly record struct SpawnResult(string Name, short ShipId, short Side, string Hull, int ActiveCount, LoadoutGrant Granted, string? Warning);

    /// <summary>P473 — the loadout the server ACTUALLY granted for a spawn: the
    /// acked hull id and the mounted part tail (from the real BUY_LOADOUT_ACK,
    /// or — for the in-world combat peer — the ack the client world applied).
    /// Compared against the requested loadout to surface a refusal or partial
    /// grant instead of silently flying a default ship.</summary>
    public readonly record struct LoadoutGrant(short HullId, IReadOnlyList<ExpandedPartData> Parts);

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
    public SpawnResult SpawnBot(short hullId, string hullName, short? side, IReadOnlyList<ExpandedPartData> parts, string behavior, short? targetSector = null, short? launcherPartId = null, string? launcherRequestName = null,
        bool useStaging = false, bool arena = false, Vector3? arenaPosition = null, Vector3? arenaFacing = null)
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

        // P433 — a requested real missile launcher is appended to the loadout
        // NOW, gated against the hull's real capability mask (gun-only when it
        // does not fit — an honest, logged outcome, never a fabricated launcher).
        List<ExpandedPartData> finalParts = parts.ToList();
        double missileReady = 0;
        if (launcherPartId is { } lp)
        {
            string? note = Catalog.AppendLauncher(finalParts, lp, hullId, hullName, launcherRequestName ?? "");
            missileReady = Catalog.LauncherReadyTime(lp);
            Console.WriteLine($"[p433] spawn_bot hull '{hullName}' (id {hullId}) launcher request: {note ?? "(no launcher)"}");
        }

        string name = NextName();
        ActiveBot bot;
        short shipId;
        short actualSide;
        LoadoutGrant granted = default;
        string? grantWarning = null;

        if (behavior == "combat")
        {
            // A combat bot is driven as its own ConnectionSession (mirrors vizfight's
            // AttachEnemyBot.Cs): it must read its own world's pose, cross-side ships
            // and warp geometry while it transits clusters the probe never sees.
            var cs = SeatCombatClient(name, chosenSide, hullId, finalParts, out shipId, out actualSide, out granted, out grantWarning);
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
                Parts = finalParts,
                WeaponPartId = Catalog.FirstWeaponPartId(finalParts),
                LauncherPartId = launcherPartId,
                MissileReadyTime = missileReady,
                Granted = granted,
                GrantWarning = grantWarning,
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
            // P485 — ready this bot's own seat so a normal-map mission can
            // auto-start (see ConnectProbe's ready-up comment).
            b.SendPlayerReady(true, shipId);

            // P473 — reset the ack accumulator so the only ack considered is the
            // one answering THIS purchase (not the seat's join-burst default-hull
            // ack), then wait for it and evaluate it against the requested
            // loadout BEFORE trusting any streaming signal: a refused purchase
            // leaves the (already-seated) ship streaming with its unchanged
            // default hull, so ProbeHasShip alone would silently pass. Refusal
            // throws; a partial grant is surfaced as a warning.
            b.ResetBuyLoadoutAcks();
            b.SendBuyLoadout(hullId, fLaunch: true, finalParts);
            granted = WaitForAck(() => b.BuyLoadoutAcks, name, hullId, finalParts, out grantWarning);
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

            bot = new ActiveBot(name, hullId, hullName, actualSide, chosenSide, behavior, NextSeed())
            {
                Bot = b,
                Parts = finalParts,
                WeaponPartId = Catalog.FirstWeaponPartId(finalParts),
                LauncherPartId = launcherPartId,
                MissileReadyTime = missileReady,
                Granted = granted,
                GrantWarning = grantWarning,
            };
        }

        // P539d — fair-contact bookkeeping + arena relocation (combat bots only;
        // idle/weave bots never travel or fight, so staging/arena leave them be).
        bot.LaunchedAt = Now();
        bot.UseStaging = useStaging && behavior == "combat";
        if (arena && behavior == "combat")
        {
            bot.Arena = true;
            if (arenaPosition is { } ap) bot.ArenaPosition = ap;
            if (arenaFacing is { } af) bot.ArenaFacing = af;
            if (targetSector is { } ts && arenaPosition is { } ap2 && Relocator is { } reloc && bot.Cs is not null)
            {
                // A supported relocation to a mirrored arena spot in the target sector
                // (launch at home, then relocate — see the P539d report for the native
                // SetCluster/MoveShipToSector mechanism). If no relocator is registered,
                // the bot stays at home and the caller is told via the result's note.
                bool ok = false;
                try { ok = reloc(bot.ShipId, ts, ap2, arenaFacing ?? Vector3.Zero); }
                catch (Exception ex) { Console.WriteLine($"[p539d] arena relocation threw for {name}: {ex.Message}"); }
                if (ok)
                {
                    bot.Arrived = true;
                    bot.ArrivedAt ??= Now();
                    Console.WriteLine($"[p539d] arena: bot {name} (ship {bot.ShipId} side {actualSide}) relocated to mirrored spot in sector {ts}");
                }
                else
                    Console.WriteLine($"[p539d] arena: relocation for {name} (ship {bot.ShipId}) returned false — bot stays at home in sector {ts} (travel mode still applies)");
            }
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
        return new SpawnResult(name, shipId, actualSide, hullName, count, granted, grantWarning);
    }

    /// <summary>
    /// P431 — seat + launch a real combat-capable <see cref="ConnectionSession"/>
    /// peer: connect, logon, request <paramref name="side"/>, buy the loadout and
    /// launch it in one real wire call. Extracted from the SpawnBot combat branch
    /// so both initial spawn and the P431 idle→combat upgrade share the exact
    /// same flow. Throws with a specific message on any genuine server failure.
    /// P473 — waits for the real BUY_LOADOUT_ACK and evaluates it against the
    /// requested loadout (refusal throws, partial grant is a warning), so a
    /// combat spawn can never silently fly a refused default ship.
    /// </summary>
    private ConnectionSession SeatCombatClient(string name, short side, short hullId, IReadOnlyList<ExpandedPartData> parts, out short shipId, out short actualSide, out LoadoutGrant granted, out string? warning)
    {
        var cs = new ConnectionSession();
        cs.Connect(_port, name, _host);
        cs.SendLogonRequest(Request(name));
        if (!WaitUntil(() => cs.Ack is { FValidated: true }, 12000))
            throw new InvalidOperationException($"combat client '{name}' never got a validated LOGONACK from {_host}:{_port}");
        cs.SendPositionReq(side);
        if (!WaitUntil(() => cs.World.MyShipId is not null, 15000))
            throw new InvalidOperationException($"combat client '{name}' was never seated (no ship assigned) on side {side}");

        shipId = cs.World.MyShipId!.Value;
        actualSide = cs.World.MySideId ?? side;
        // P485 — ready this bot's own seat so a normal-map mission can auto-start
        // (see ConnectProbe's ready-up comment). Every MCP-managed side is thus
        // "ready" exactly as a human pilot clicking Ready would be.
        cs.SendPlayerReady(true, shipId);
        // P473 — reset the ack accumulator so the only ack considered is the one
        // answering THIS purchase (not the seat's join-burst default-hull ack).
        cs.ResetBuyLoadoutAcks();
        cs.SendBuyLoadout(hullId, fLaunch: true, parts);
        granted = WaitForAck(() => cs.BuyLoadoutAcks, name, hullId, parts, out warning);
        return cs;
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

    /// <summary>
    /// P539d — the geometric centre of a sector (centroid of its warps and
    /// stations, from the probe's real world), used to mirror arena spawns about
    /// a common point so travel can't favour either side. null when the sector is
    /// unknown or has no anchor geometry yet.
    /// </summary>
    public Vector3? ProbeSectorCenter(short sector)
    {
        var pts = new List<Vector3>();
        lock (Probe.Gate)
        {
            foreach (var w in Probe.World.Warps.Values)
                if (w.ClusterId == sector && w.Position is { Length: 3 } p)
                    pts.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in Probe.World.Stations.Values)
                if (st.ClusterId == sector && st.Position is { Length: 3 } p)
                    pts.Add(new Vector3(p[0], p[1], p[2]));
        }
        if (pts.Count == 0) return null;
        var c = Vector3.Zero;
        foreach (var p in pts) c += p;
        return c / pts.Count;
    }

    // ── P486: commander tools (invest / list_drones / order_drone) ──────────
    //
    // The three tools act through a "harness commander bot": a seated `mcp-`
    // peer on the chosen side that (a) holds the P473 grant money in its
    // wallet to fund real investments, (b) has a real ship id (MyShipId) so it
    // can send Individual quick-chat orders (P457/P469 CommandToDrone), and
    // (c) streams a real ClientWorld that observes the side's ships, buckets
    // (BUCKET_STATUS), money (MONEY_CHANGE/PAYDAY) and chats ("Affirmative.")
    // — the same same-side observability the probe has. No server change; the
    // native drone AI flies the drones, MCP only commands them.
    private readonly Dictionary<short, ConnectionSession> _commanders = new();

    /// <summary>
    /// P486 — get (or lazily seat) the cached commander peer for
    /// <paramref name="side"/>: connect, logon, request that side, wait for the
    /// ship assignment, and ready the seat (P485, so a normal-map mission can
    /// auto-start). Deliberately does NOT buy a loadout — a seated `mcp-` peer
    /// already carries the P473 grant money in its wallet, which is all invest
    /// needs, and the default spawned hull is a valid sender. Registered only in
    /// the commander cache (not the behavior loop), so it never fights.
    /// </summary>
    private ConnectionSession EnsureCommander(short side)
    {
        lock (_commanders)
        {
            if (_commanders.TryGetValue(side, out var existing) && existing.Connected)
                return existing;
            existing?.Dispose();
            string name = $"mcp-cmd{side}-{Interlocked.Increment(ref _nameCounter)}-{Random.Shared.Next(1000, 9999)}";
            var cs = new ConnectionSession();
            cs.Connect(_port, name, _host);
            cs.SendLogonRequest(Request(name));
            if (!WaitUntil(() => cs.Ack is { FValidated: true }, 12000))
                throw new InvalidOperationException($"commander: side {side} peer '{name}' never got a validated LOGONACK from {_host}:{_port}");
            cs.SendPositionReq(side);
            if (!WaitUntil(() => cs.World.MyShipId is not null, 15000))
                throw new InvalidOperationException($"commander: side {side} peer '{name}' was never seated (no ship assigned)");
            cs.SendPlayerReady(true, cs.World.MyShipId!.Value);
            Console.WriteLine($"[p486] commander for side {side} seated: '{name}' ship {cs.World.MyShipId} — P473 grant money in wallet, Individual-chat orders + BUCKET_STATUS/MONEY_CHANGE observable");
            _commanders[side] = cs;
            return cs;
        }
    }

    /// <summary>P486 — one drone's observed wire picture (see each tool for what
    /// is / isn't observable from a same-side player's view).</summary>
    public readonly record struct DroneRow(
        short ShipId, string Type, short HullId, short? SectorId,
        float[]? Position, byte? State, string? CharacterName, string? Notes);

    /// <summary>P486 — the <c>invest</c> outcome: what was donated and observed
    /// (progress / completion) plus the drone that appeared on the wire.</summary>
    public readonly record struct InvestResult(
        short Side, short BucketId, string BucketName, string BucketType,
        int AmountRequested, long AmountSpent, int? WalletBefore, int? WalletAfter,
        long InvestedMoney, int Price, long InvestedTimeMs, uint TimeToBuildSec,
        bool Completed, double ObserveSeconds, DroneRow? Drone);

    /// <summary>P486 — the <c>list_drones</c> outcome.</summary>
    public readonly record struct ListDronesResult(short Side, IReadOnlyList<DroneRow> Drones);

    /// <summary>P486 — the <c>order_drone</c> outcome: the order issued and the
    /// drone's observed reaction (an "Affirmative." reply + a state change).</summary>
    public readonly record struct OrderResult(
        short DroneId, string Command, string TargetDesc, bool Sent,
        bool AffirmativeObserved, string? StateBefore, string? StateAfter,
        short? SectorBefore, short? SectorAfter, string? Note,
        int ChatsSeen, string? DroneReply);

    /// <summary>
    /// P486 — resolve a bucket by id or name (exact-over-unique-substring,
    /// case-insensitive) from the side's synthesized catalog
    /// (<see cref="Allegiance.Core.Model.BucketCatalog.Describe"/>, the SAME id
    /// numbering the server baked into its list at <c>Mission.Assemble</c>).
    /// Throws a specific error on ambiguity or no-match — never a guess.
    /// </summary>
    private static Allegiance.Core.Model.BucketCatalog.BucketView ResolveBucket(
        IReadOnlyList<Allegiance.Core.Model.BucketCatalog.BucketView> views, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("invest: required argument `bucket` (name or id) is missing");
        if (short.TryParse(query.Trim(), out short id))
        {
            var byId = views.FirstOrDefault(v => v.Id == id);
            if (byId is not null) return byId;
            throw new ArgumentException($"invest: no bucket with id {id} on this side (available ids: {string.Join(", ", views.Select(v => v.Id))})");
        }
        var matches = views.Where(v => v.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        var subs = views.Where(v => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (subs.Count == 1) return subs[0];
        if (subs.Count > 1)
            throw new ArgumentException($"invest: bucket '{query}' is ambiguous ({subs.Count} matches: {string.Join(", ", subs.Select(v => v.Name))})");
        throw new ArgumentException($"invest: no bucket named '{query}' (available: {string.Join(", ", views.Select(v => v.Name))})");
    }

    /// <summary>P486 — invest into a real side bucket. Funds through
    /// <see cref="ConnectionSession.SendBucketDonate"/> (the P473-granted
    /// commander's wallet), then observes the real BUCKET_STATUS progress and,
    /// when the bucket completes within the window (money full AND build time
    /// elapsed), the drone that appears on the wire (a NEW same-side NPC with
    /// the completed bucket's drone hull). Returns observed progress otherwise.
    /// </summary>
    public InvestResult Invest(short side, string bucketQuery, int? amountArg, double observeSeconds)
    {
        if (!ServerReady)
            throw new InvalidOperationException("invest: server probe not connected/validated — cannot invest");
        var commander = EnsureCommander(side);
        var core = Catalog.RepresentativeCore();

        // Wait for the side's development-tech mask (CREATE_BUCKETS join sync)
        // so the synthesized catalog ids line up bucket-for-bucket with the
        // server's (P487's exact premise).
        if (!WaitUntil(() => commander.World.DevelopmentTechsFor(side) is not null, 8000))
            throw new InvalidOperationException(
                $"invest: side {side}'s bucket/tech sync (CREATE_BUCKETS) never arrived — cannot resolve real bucket ids yet");

        var techs = commander.World.DevelopmentTechsFor(side)!.Value;
        var ga = commander.World.GlobalAttributesFor(side);
        var views = Allegiance.Core.Model.BucketCatalog.Describe(core, techs, ga);
        var view = ResolveBucket(views, bucketQuery);
        if (view.ItemType == Allegiance.Core.Model.BucketItemType.BitDevelopment)
            // Funding a development is a real invest too, but it completes a
            // research tech, not a drone — P486 is about drone production.
            throw new ArgumentException(
                $"invest: bucket '{view.Name}' (id {view.Id}) is a DEVELOPMENT bucket (research), not a drone/station production bucket; this tool invests in drone-type and station-type buckets that spawn drones.");

        long already = 0;
        var preFund = commander.World.BucketFundingOf(side, view.Id);
        if (preFund is { } pf) already = pf.InvestedMoney;
        long needed = Math.Max(0, (long)view.Price - already);
        int donate = amountArg ?? (int)Math.Min(needed, int.MaxValue);
        if (donate > needed) donate = (int)needed;   // the server caps at the outstanding balance anyway
        int? walletBefore = commander.World.MyMoney;
        if (walletBefore is { } wb && donate > wb)
            throw new InvalidOperationException(
                $"invest: commander wallet {wb} < requested donation {donate} for bucket '{view.Name}' (price {view.Price}, already invested {already}) — donate less or raise the P473 grant money");

        // Snapshot the side's ship ids BEFORE the donation so the spawned drone
        // is recognized as genuinely NEW (the side may already have an initial
        // miner / other drones).
        HashSet<short> beforeIds;
        lock (commander.Gate) beforeIds = new HashSet<short>(
            commander.World.Ships.Where(kv => kv.Value.SideId == side && !kv.Value.IsMine).Select(kv => kv.Key));

        commander.SendBucketDonate(view.Id, donate);
        Console.WriteLine($"[p486] invested {donate} (spent ≤ {needed}) into bucket {view.Id} '{view.Name}' on side {side}");

        // Observe: wallet debit, funding progress, and — on completion — a new drone.
        // Note: the client's BUCKET_STATUS is refreshed only on a donation
        // broadcast, so InvestedTimeMs here does NOT tick visibly even though
        // the server's bucket build-time accrues every sim tick. Completion is
        // therefore signalled by the drone APPEARING on the wire once the money
        // is full — not by the observed time reaching the build threshold.
        var start = Now();
        int? walletAfter = walletBefore;
        long investedMoney = already;
        long investedTimeMs = preFund?.InvestedTimeMs ?? 0;
        bool completed = false;
        DroneRow? drone = null;
        var deadline = start + observeSeconds;
        // Phase 1: watch the funding climb to the full price (usually instant).
        while (Now() < deadline)
        {
            long im; int? mw;
            lock (commander.Gate)
            {
                im = commander.World.BucketFundingOf(side, view.Id)?.InvestedMoney ?? already;
                mw = commander.World.MyMoney;
            }
            investedMoney = Math.Max(investedMoney, im);
            if (mw is not null) walletAfter = mw;
            if (investedMoney >= view.Price) break;
            Thread.Sleep(100);
        }
        // Phase 2: money full — the server's build time is elapsing invisibly;
        // watch for the spawned drone (the real completion signal) up to the end
        // of the window.
        if (investedMoney >= view.Price)
        {
            while (Now() < deadline)
            {
                drone = FindNewDrone(commander, side, beforeIds, view);
                if (drone is not null) { completed = true; break; }
                long it; int? mw2;
                lock (commander.Gate)
                {
                    it = commander.World.BucketFundingOf(side, view.Id)?.InvestedTimeMs ?? 0;
                    mw2 = commander.World.MyMoney;
                }
                investedTimeMs = Math.Max(investedTimeMs, it);
                if (mw2 is not null) walletAfter = mw2;
                Thread.Sleep(100);
            }
        }

        return new InvestResult(side, view.Id, view.Name, view.ItemType.ToString(),
            donate, Math.Min(donate, Math.Max(0, needed)), walletBefore, walletAfter,
            investedMoney, view.Price, investedTimeMs, view.TimeToBuildSec,
            completed, observeSeconds, drone);
    }

    /// <summary>P486 — find the drone that a completed bucket produced: a NEW
    /// (post-invest) same-side NPC. Prefers the completed bucket's own drone
    /// hull (view.HullIdDrone when set) but reports any newly-appeared NPC so a
    /// station-type bucket (whose constructor hull the view doesn't carry) is
    /// still captured.</summary>
    private DroneRow? FindNewDrone(ConnectionSession commander, short side,
        HashSet<short> beforeIds, Allegiance.Core.Model.BucketCatalog.BucketView view)
    {
        List<(short Id, Allegiance.ClientCore.State.ShipEntity s)> news = new();
        lock (commander.Gate)
        {
            foreach (var kv in commander.World.Ships)
            {
                if (kv.Value.IsMine || kv.Value.SideId != side) continue;
                if (beforeIds.Contains(kv.Key)) continue;        // existed before invest
                if (kv.Value.Player is not null) continue;       // a real (new) player, not a drone
                news.Add((kv.Key, kv.Value));
            }
        }
        // Prefer the bucket's own drone hull if set (>0).
        var exact = news.Where(n => view.HullIdDrone > 0 && n.s.HullId == view.HullIdDrone)
                        .OrderBy(n => n.s.Latest is null).FirstOrDefault();
        var any = news.OrderBy(n => n.s.Latest is null).FirstOrDefault();
        var chosen = exact.Id != 0 ? exact : any;
        if (chosen.Id == 0) return null;
        return ToDroneRow(chosen.s);
    }

    /// <summary>P486 — snapshot one same-side ship as an observable
    /// <see cref="DroneRow"/> (position only when the latest update is known;
    /// sector only when telemetry has been seen).</summary>
    private static DroneRow ToDroneRow(Allegiance.ClientCore.State.ShipEntity s)
    {
        var pos = s.Latest is not null
            ? new[] { s.Latest.Position.X, s.Latest.Position.Y, s.Latest.Position.Z }
            : null;
        return new DroneRow(s.ShipId, ResolveDroneTypeName(s.HullId), s.HullId,
            s.Latest is not null ? s.SectorId : null, pos,
            s.Latest is not null ? s.State : (byte?)null,
            string.IsNullOrEmpty(s.CharacterName) ? null : s.CharacterName,
            "same-side NPC (Player==null) as observed by the commander; cargo/ore not on the ship wire from a peer's view");
    }

    /// <summary>P486 — the human drone-type name for a hull id, from the static
    /// core's drone-type roster (or the synthetic ship-type hull 504), else a
    /// plain "(hull N)" so an unknown NPC hull is still identifiable.</summary>
    private static string ResolveDroneTypeName(short hullId)
    {
        var core = Catalog.RepresentativeCore();
        foreach (var dt in core.DroneTypes.Values)
            if (dt.HullTypeId == hullId) return dt.Name.Trim('.');
        if (hullId == Allegiance.Core.Model.BucketCatalog.SyntheticDroneHullId)
            return "ship-type";
        return $"(hull {hullId})";
    }

    /// <summary>P486 — <c>list_drones</c>: every same-side NPC (drone) the
    /// commander observes on the wire, with its id, drone type (from its real
    /// hull), sector, position, state and name where those are observable, plus
    /// an honest note about what a player's view can't see (cargo/ore/order).</summary>
    public ListDronesResult ListDrones(short side, string? typeFilter)
    {
        var commander = EnsureCommander(side);
        var rows = new List<DroneRow>();
        lock (commander.Gate)
        {
            foreach (var kv in commander.World.Ships)
            {
                var s = kv.Value;
                if (s.IsMine) continue;
                if (s.SideId != side) continue;          // same-side only (the only visibility)
                if (s.Player is not null) continue;      // a real player, not a drone
                var row = ToDroneRow(s);
                if (typeFilter is not null &&
                    !row.Type.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !s.HullId.ToString().Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows.Add(row);
            }
        }
        rows.Sort((a, b) => a.ShipId.CompareTo(b.ShipId));
        return new ListDronesResult(side, rows);
    }

    /// <summary>P486 — the commander that can see a given drone id (seating a
    /// commander per side until one of them streams the ship). The drone's own
    /// side determines which commander (and which wallet/sender) is used.</summary>
    private ConnectionSession CommanderSeeing(short droneId)
    {
        foreach (var side in ValidSides())
        {
            var cs = EnsureCommander(side);
            bool has;
            lock (cs.Gate)
                has = cs.World.Ships.TryGetValue(droneId, out var e) && e.SideId == side && e.Player is null;
            if (has) return cs;
        }
        throw new InvalidOperationException(
            $"order_drone: drone {droneId} is not observable by any commander peer (not a same-side NPC on any valid side {string.Join(",", ValidSides())} — use list_drones first)");
    }

    /// <summary>
    /// P486 — <c>order_drone</c>: issue a real order to a drone through the
    /// P469/P457 path — an Individual quick-chat (FM_CS_CHATMESSAGE 23) naming
    /// the drone as recipient with the command's native <c>CommandID</c> and a
    /// resolved target — exactly the shape native's <c>CommandToDrone</c> consumes
    /// (FedSrv.CPP:3555-3570 → 3175-3325). Returns the drone's observed reaction:
    /// the server's "Affirmative." reply in the commander's chat stream and any
    /// sector/state change over a short window.
    /// </summary>
    public OrderResult OrderDrone(short droneId, string command, string? targetDesc)
    {
        if (!ServerReady)
            throw new InvalidOperationException("order_drone: server probe not connected/validated");
        if (!DroneCommands.TryParseCommand(command, out short cid))
            throw new ArgumentException(
                $"order_drone: unknown command '{command}' (recognized: default, goto, attack, defend, mine, build, donothing, repair, pickup, join)");
        if (!DroneCommands.TryParseTarget(targetDesc, out IgcObjectType ot, out short oid))
            throw new ArgumentException(
                $"order_drone: cannot parse target '{targetDesc}' (forms: asteroid:<id>, ship:<id>, station:<id>, warp:<id>, a bare ship id, or none)");

        var commander = CommanderSeeing(droneId);
        short stateBefore; short sectorBefore;
        lock (commander.Gate)
        {
            var e = commander.World.Ships[droneId];
            stateBefore = e.Latest is not null ? e.State : (short)-1;
            sectorBefore = e.Latest is not null ? e.SectorId : (short)-1;
        }

        int chatsBefore;
        lock (commander.Gate) chatsBefore = commander.World.Events.Chats.Count;

        // P469: Individual chat to the drone with CommandID + target.
        commander.SendChat("", ChatTarget.Individual, oidRecipient: droneId,
            commandId: cid, oidTarget: oid, otTarget: (short)ot);
        string targetLabel = oid < 0 ? "(none)" : $"{DroneCommands.KindName(ot)}:{oid}";
        Console.WriteLine($"[p486] ordered drone {droneId} (cid={cid} '{command}') target {targetLabel}");

        // Observe the reaction: "Affirmative." from the drone + any state change.
        bool affirmative = false;
        short stateAfter = stateBefore; short sectorAfter = sectorBefore;
        double deadline = Now() + 8.0;
        while (Now() < deadline)
        {
            bool aff; short st; short sec;
            lock (commander.Gate)
            {
                aff = commander.World.Events.Chats.Skip(Math.Max(0, chatsBefore))
                        .Any(c => c.SidSender == droneId && c.Message != null && c.Message.StartsWith("Affirmative", StringComparison.OrdinalIgnoreCase));
                st = commander.World.Ships[droneId].Latest is not null ? commander.World.Ships[droneId].State : stateAfter;
                sec = commander.World.Ships[droneId].Latest is not null ? commander.World.Ships[droneId].SectorId : sectorAfter;
            }
            if (aff) affirmative = true;
            stateAfter = st; sectorAfter = sec;
            if (affirmative && (stateAfter != stateBefore || sectorAfter != sectorBefore)) break;
            Thread.Sleep(100);
        }

        string? note = null;
        if (cid != OrderChange.CidDoNothing && oid >= 0 && !affirmative)
            note = "no \"Affirmative.\" reply observed — the order may have been refused (illegal command for this pilot type), the target unresolved, or the reply not yet relayed; the drone state/sector is the secondary check.";
        if (affirmative && stateAfter == stateBefore && sectorAfter == sectorBefore)
            note = "replied \"Affirmative.\" but no sector/state change within the window (e.g. already at target / awaiting build time) — the ack is the primary signal.";
        // P486 diagnostic: what chats the commander actually received from the drone
        // (an empty reply here pinpoints whether the server's CommandToDrone ack
        // reached the commander at all, independent of state/sector heuristics).
        string? droneReply = null;
        int chatsSeen;
        lock (commander.Gate)
        {
            var fromDrone = commander.World.Events.Chats.Where(c => c.SidSender == droneId).Select(c => c.Message).ToList();
            chatsSeen = commander.World.Events.Chats.Count;
            if (fromDrone.Count > 0) droneReply = string.Join(" | ", fromDrone.Take(3));
        }
        return new OrderResult(droneId, command, targetLabel, Sent: true, affirmative,
            StateName(stateBefore), StateName(stateAfter), sectorBefore, sectorAfter, note,
            chatsSeen, droneReply);
    }

    private static string? StateName(short state)
    {
        return state switch
        {
            0 => "no-order", 1 => "waiting-to-undock", 2 => "auto-pilot",
            3 => "disabled", 4 => "semi-active", 5 => "attack", 6 => "react-to-self",
            7 => "goto", 8 => "launch", 9 => "run-away", 10 => "stay", 11 => "dock",
            12 => "mine", 13 => "collect", 14 => "build", 15 => "repair",
            16 => "pickup", 17 => "warp", 18 => "inbattle", _ => $"({state})",
        };
    }

    private string NextName()
    {
        int n = Interlocked.Increment(ref _nameCounter);
        int rand = Random.Shared.Next(1000, 9999);
        // P473 — the "mcp-" prefix matches the server's liveSettings.testHarness
        // characterNamePrefix (default "mcp-"), so a granted debug server treats
        // every MCP bot as a harness connection — and a human's name never does.
        return $"mcp-b{n:D3}-{rand}";
    }

    /// <summary>The side the server actually seated the ship on (from the real
    /// JOIN_SIDE broadcast for this ship), falling back to the requested side.</summary>
    private static short ActualSide(BotClass b, short shipId, short requested)
    {
        foreach (var js in b.JoinSides)
            if (js.ShipId == shipId) return js.SideId;
        return requested;
    }

    /// <summary>
    /// P473 — wait for the real <c>BUY_LOADOUT_ACK(5)</c> that answers THIS
    /// purchase, take it as the granted loadout, and evaluate it against the
    /// requested loadout (<see cref="EvaluateGrant"/>: refusal throws, partial
    /// is a warning). The caller must <c>ResetBuyLoadoutAcks</c> immediately
    /// before its <see cref="SendBuyLoadout"/> so the list holds only THIS
    /// purchase's acks (a peer also gets a join-burst ack for its default hull
    /// at seat). Definitively recognizes the purchase by waiting for an ack that
    /// reflects the REQUESTED hull (a no-op same-hull buy's ack carries it too),
    /// or — P488 — an ack that reflects a SUCCESSOR of the requested hull (a
    /// side that has out-teched the requested hull is granted the successor, so
    /// its ack HullId differs); a refused purchase (no such ack) is evaluated
    /// from the last ack (which acks the unchanged hull) so the refusal is
    /// reported, not swallowed.
    /// </summary>
    private static LoadoutGrant WaitForAck(Func<IReadOnlyList<BuyLoadoutAck>> acks, string name, short requestedHull, IReadOnlyList<ExpandedPartData> requestedParts, out string? warning)
    {
        WaitUntil(() =>
        {
            var list = acks();
            return list.Count > 0 && list.Any(a => a.HullId == requestedHull || IsSuccessorOf(requestedHull, a.HullId));
        }, 4000);

        var all = acks();
        if (all.Count == 0)
            throw new InvalidOperationException(
                $"spawn_bot: bot '{name}' never received a BUY_LOADOUT_ACK after requesting hull {requestedHull} — the purchase was not processed by the server's ApplyLoadout");
        var ack = all.LastOrDefault(a => a.HullId == requestedHull || IsSuccessorOf(requestedHull, a.HullId)) ?? all[^1];
        var granted = new LoadoutGrant(ack.HullId, ack.Parts.ToList());
        warning = EvaluateGrant(name, requestedHull, requestedParts, granted);
        return granted;
    }

    /// <summary>The real core, resolved once (PCore001 — the same core the
    /// server boots), used to tell a P488 successor upgrade from a P441 refusal
    /// by walking the requested hull's successor chain.</summary>
    private static readonly Lazy<StaticCore> HarnessCore = new(() => Catalog.RepresentativeCore());

    /// <summary>P488 — true when <paramref name="grantedHull"/> is the requested
    /// hull or a descendant of it in the real successor chain (any depth, the
    /// structural GetSuccessorHullType walk). Used to recognise a legitimate
    /// successor grant vs a refusal (whose ack echoes the unchanged default
    /// hull, which is not in the requested hull's chain).</summary>
    private static bool IsSuccessorOf(short requestedHull, short grantedHull)
    {
        if (requestedHull == grantedHull) return true;
        var core = HarnessCore.Value;
        short cur = requestedHull;
        var seen = new HashSet<short> { cur };
        while (core.HullTypes.TryGetValue(cur, out var h) && h.SuccessorHullId is { } nx && nx >= 0 && seen.Add(nx))
        {
            if (nx == grantedHull) return true;
            cur = nx;
        }
        return false;
    }

    /// <summary>
    /// P473 — compare the granted loadout against the requested one. An outright
    /// refusal (the server acked a DIFFERENT — unchanged — hull, i.e.
    /// <c>fbought=false</c> under P441's log-and-refuse) is an error: throw a
    /// specific message so a <c>spawn_bot</c>/matrix row fails loudly instead of
    /// silently flying a default ship. A partial grant (requested hull acked but
    /// some requested parts not mounted — tech-gated or no compatible hardpoint)
    /// is returned as a warning string, never a silent success.
    /// <para><b>P488</b> — a granted hull that is a SUCCESSOR of the requested
    /// one is a legitimate upgrade (the side out-teched the requested hull,
    /// native GetSuccessor), not a refusal: it is surfaced as an informational
    /// warning (plus a partial-grant note when some requested parts don't fit
    /// the successor), never thrown.</para>
    /// </summary>
    private static string? EvaluateGrant(string botName, short requestedHull, IReadOnlyList<ExpandedPartData> requestedParts, LoadoutGrant granted)
    {
        var requestedIds = new HashSet<short>(requestedParts.Select(p => p.PartId));
        var grantedIds = granted.Parts.Select(p => p.PartId).ToHashSet();
        var missing = requestedIds.Where(id => !grantedIds.Contains(id)).OrderBy(id => id).ToList();

        if (granted.HullId != requestedHull)
        {
            if (IsSuccessorOf(requestedHull, granted.HullId))
            {
                // P488 — legitimate successor upgrade. Not an error; surface it.
                var note = $"bot '{botName}' was granted the SUCCESSOR hull {granted.HullId} (an upgrade of the requested {requestedHull} — the side has out-teched it, native GetSuccessor, shipIGC.h:1004)";
                if (missing.Count > 0)
                    note += $"; PARTIAL GRANT: requested parts [{string.Join(", ", missing)}] were not mounted on the successor ({granted.Parts.Count} granted)";
                return note;
            }
            throw new InvalidOperationException(
                $"spawn_bot: bot '{botName}' was REFUSED hull {requestedHull} — the server granted the unchanged hull {granted.HullId} instead (fbought=false). "
                + "Under native tech gating most hulls aren't buyable at a fresh side's garrison; enable liveSettings.testHarness (prefix match) for the harness "
                + "to buy it, or request a sellable hull.");
        }
        if (missing.Count > 0)
            return $"bot '{botName}' got a PARTIAL GRANT on hull {requestedHull}: requested parts [{string.Join(", ", missing)}] were not mounted "
                   + $"(tech-gated or no compatible hardpoint on the granted hull); {granted.Parts.Count} part(s) granted.";
        return null;
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
                        // P474 — a bot owned by an active run_scenario preset is
                        // driven by the scenario's own controller thread (blocking
                        // the tool call), never by this generic loop, so the two
                        // never race on the same wire peer.
                        if (bot.InScenario) continue;

                        // P431 — every active bot accepts real teammate orders each
                        // tick (no human to ask: MCP bots obey immediately) and adopts
                        // the commanded ship-target; this may upgrade an idle/weave
                        // BotClass into a combat ConnectionSession and set Behavior.
                        HandleOrders(bot, now);
                        if (bot.Behavior == "combat")
                        {
                            DriveCombat(bot, now);
                        }
                        else if (bot.Bot?.ShipAssignment is not null && bot.Behavior == "weave")
                        {
                            double yaw = Math.Sin(now * bot.WeaveFreq + bot.WeavePhase) * bot.WeaveAmp;
                            double throttle = 0.55 + 0.25 * Math.Sin(now * bot.WeaveFreq * 0.7 + bot.WeavePhase * 1.3);
                            bot.Bot.CommandControls(new ControlData((float)yaw, 0f, 0f, (float)throttle), 0);
                        }
                        // idle: seat but send no movement commands — the simplest real state.
                        // Every bot (any behavior) also contributes its real update
                        // cadence, so get_perf_stats can measure the live stream.
                        SampleCadence(bot, now);
                    }
                    catch { /* one bot's tick failure never kills the loop */ }
                }
            }
            catch { }
            Thread.Sleep(50);
        }
    }

    // ── Order acceptance + translation (P431) ─────────────────────────────
    //
    // An MCP bot is a real connected peer, so it ALREADY receives the real
    // owner-only QUEUED_COMMAND_UPDATE(215) decode — the same generic path any
    // client uses (ConnectionSession.cs:625-629; Bot.cs P424 accumulator) —
    // whenever the server names its ship as the commandee of a same-side ship's
    // queued order (P424's accept/reject mechanic). MCP bots have no human to
    // ask: they auto-accept immediately, always (that is the whole point), and
    // translate the accepted order into real behavior — engaging the SPECIFIC
    // commanded enemy ship, never "nearest enemy".

    /// <summary>P431 — for ONE bot each loop tick: read its real pending queued
    /// command, auto-accept it over the real ORDER_CHANGE wire, and adopt the
    /// commanded ship-target so the combat engine engages THAT specific enemy.
    /// Universal: runs for every active bot — both the
    /// <see cref="ConnectionSession"/>-driven combat bots (incl. every
    /// spawn_scenario bot) and the plain <see cref="BotClass"/> idle/weave ones
    /// (an idle bot that accepts a ship-target order is UPGRADED to a combat
    /// ConnectionSession so it can genuinely engage).</summary>
    private void HandleOrders(ActiveBot bot, double now)
    {
        // 1) Read the bot's real pending queued order from its own wire state.
        QueuedCommandState? pending;
        if (bot.Cs is { } cs)
        {
            lock (cs.Gate) pending = cs.World.MyQueuedCommand;
        }
        else if (bot.Bot is { } b)
        {
            // A BotClass has no world; it accumulates every QUEUED_COMMAND_UPDATE(215)
            // it receives (P424). Fold ONLY the updates since the last tick; the
            // newest one wins (a set, or a CidNone clear), yielding current pending.
            var updates = b.QueuedCommandUpdates;
            for (int i = bot.QcuConsumed; i < updates.Count; i++)
            {
                var u = updates[i];
                bot.CurrentQueued = u.HasPending
                    ? new QueuedCommandState(u.CommandId, u.TargetType, u.TargetId)
                    : null;
            }
            bot.QcuConsumed = updates.Count;
            pending = bot.CurrentQueued;
        }
        else
        {
            return;   // no wire peer yet (mid-upgrade); nothing to read
        }

        // After the server clears a pending order (our accept / a reject /
        // auto-reject) it sends a CidNone update; reset the accepted bookmark so
        // a later, same-commandId order is seen as fresh (DecideAcceptTranslate).
        if (pending is null)
            bot.AcceptedQueuedId = OrderChange.CidNone;

        var d = DecideAcceptTranslate(pending, bot.AcceptedQueuedId, bot.OrderedTargetId);

        if (d.ShouldSendAccept && pending is { } pq)
        {
            short shipId = ShipIdOf(bot);
            if (bot.Bot is { } bb)
                bb.SendOrderChange(shipId, pq.CommandId, OrderChange.CmdAccepted, pq.TargetType, pq.TargetId);
            else if (bot.Cs is { } cc)
                cc.SendOrderChange(shipId, pq.CommandId, OrderChange.CmdAccepted, pq.TargetType, pq.TargetId);
            bot.AcceptedQueuedId = pq.CommandId;
            Console.WriteLine($"[p431] bot {bot.Name} (ship {shipId}) AUTO-ACCEPTED queued order cmdID={pq.CommandId} target={pq.TargetType}#{pq.TargetId} — MCP bot obeys immediately (real ORDER_CHANGE CmdAccepted)");
        }

        if (d.OrderedTarget != bot.OrderedTargetId)
        {
            bool wasOrdered = bot.OrderedTargetId is not null;
            bot.OrderedTargetId = d.OrderedTarget;
            if (bot.OrderedTargetId is not null && !wasOrdered)
                Console.WriteLine($"[p431] bot {bot.Name} adopted commanded ship-target #{bot.OrderedTargetId} — switching to combat to engage THAT ship");

            if (bot.OrderedTargetId is not null)
            {
                // An ordered bot fights. An idle/weave BotClass has no world to
                // engage with, so upgrade it to a real combat ConnectionSession
                // (a fresh client, seated+launched like any spawned combat bot).
                if (bot.Bot is not null)
                    UpgradeToCombat(bot);
                bot.Behavior = "combat";
            }
        }
    }

    /// <summary>
    /// P431 — the pure accept-and-translate decision for ONE bot tick, factored
    /// out of the Loop so the whole order lifecycle is provable with a synthetic
    /// pending <see cref="QueuedCommandState"/> and no server. A
    /// <see cref="QueuedCommandState"/> is the real pending order the wire
    /// delivers (<c>null</c> = none). <paramref name="acceptedQueuedId"/> is the
    /// command id this bot has already accepted (avoids a double-accept while
    /// the server is clearing the slot). Returns whether to send the real ACCEPT
    /// this tick and which enemy ship (if any) the bot is now committed to
    /// engage. An adopted order is PERSISTED (not dropped on a transient
    /// cross-side visibility gap) until a fresh order supersedes it.
    /// </summary>
    public static OrderDecision DecideAcceptTranslate(
        QueuedCommandState? pending,
        short acceptedQueuedId,
        short? currentOrderedTarget)
    {
        if (pending is not { } pq)
            // No pending order — nothing to accept. Keep the current ordered target
            // even if it is momentarily not visible in the bot's world: cross-side
            // visibility is unreliable (the documented P429 reality), so a temporary
            // gap must not be mistaken for a dead target and drop a live order.
            return new OrderDecision(false, currentOrderedTarget);

        bool isFresh = pq.CommandId != OrderChange.CidNone && acceptedQueuedId != pq.CommandId;

        if (pq.TargetType == IgcObjectType.Ship && pq.TargetId >= 0)
        {
            // A ship-target order IS the engage command: the bot must engage THAT
            // named ship, never "nearest enemy". A fresh one supersedes any prior
            // target and must be accepted; an already-accepted one is the ongoing
            // engagement (persisted — see the no-pending case above).
            return isFresh
                ? new OrderDecision(true, pq.TargetId)
                : new OrderDecision(false, currentOrderedTarget);
        }

        // A non-ship / targetless pending order (OT_invalid): the queued-order
        // wire carries NO steerable position, so there is no goto/waypoint
        // semantic to translate — accept it, but no ship-target is adopted.
        // (See docs/reports/P431_MCP_BOTS_ACCEPT_TEAM_ORDERS.md for the real
        // order-type findings.)
        return new OrderDecision(isFresh, currentOrderedTarget);
    }

    /// <summary>
    /// P431 — upgrade an idle/weave <see cref="BotClass"/> bot to a real combat
    /// <see cref="ConnectionSession"/> so it can genuinely obey a ship-target
    /// order (a BotClass has no ClientWorld, hence no way to navigate to or
    /// engage a named enemy). Seats + launches a fresh combat client on the
    /// bot's side with its original loadout, then swaps the peer over. A fresh
    /// unique name is used (server names are unique; the old peer is disposed
    /// only after the new one is fully seated). Failure is logged and returns
    /// false — the bot stays as-is (order accepted, not physically obeyed), a
    /// real degraded state never silently glossed over.
    /// </summary>
    private bool UpgradeToCombat(ActiveBot bot)
    {
        if (bot.Bot is not { } old) return false;
        string name = NextName();
        var parts = bot.Parts ?? Catalog.DefaultParts();
        try
        {
            var cs = SeatCombatClient(name, bot.Side, bot.HullId, parts, out short shipId, out short actualSide, out var upGranted, out var upWarning);
            try { old.Dispose(); } catch { }   // free the old plain-client peer now that the combat one is real
            bot.Bot = null;
            bot.Cs = cs;
            bot.ShipId = shipId;
            bot.Side = actualSide;
            bot.Launched = true;
            bot.Granted = upGranted;
            bot.GrantWarning = upWarning;
            Console.WriteLine($"[p431] upgraded idle bot '{bot.Name}' -> combat client '{name}' (ship {shipId}, side {actualSide}) to obey its accepted order");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[p431] upgrade of idle bot {bot.Name} to combat failed: {ex.Message} — order accepted but not physically obeyed");
            return false;
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

    /// <summary>[vz TryFindWarpTo] The position AND wire radius of the warp in
    /// cluster <paramref name="cur"/> that leads to cluster <paramref name="next"/>.
    /// The radius is the authoritative <c>DataWarpIGC.Radius</c> the client sees —
    /// the P496 warp-approach law needs it (with the ship radius) to know where
    /// the aleph's transit sphere actually is, so it can fly THROUGH it instead
    /// of halting 90+ units short (P490's board-hull "park at the aleph").</summary>
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

    /// <summary>P496 — the warp-hop approach law: a warp has NO "arrival" short
    /// of its transit sphere (the aleph transit fires at
    /// <c>|ship−warp| ≤ warp.Radius + ship.Radius</c>; the sector pass at
    /// <c>Sector.ResolveWarpCollisions</c> builds that threshold from the raw
    /// wire radius — P484 measured ≈110 u here). So a bot heading for a warp
    /// must FLY THROUGH the aleph, never be commanded to stop before it. Returns
    /// the (arrival, brake) pair for <see cref="CombatFlyTo"/> that places the
    /// throttle ramp INSIDE the transit sphere: the bot is still thrusting
    /// FORWARD when it crosses the boundary and only eases off at the centre —
    /// which is exactly what a slow board hull (Hvy Troop Transport id 26,
    /// accel 15.0) needs to enter the volume instead of parking 90 u outside it
    /// (P490's "park at the aleph", blamed there on hull maneuverability but
    /// actually commanded by the old fixed arrival=200 &gt; the ~115 u sphere).</summary>
    internal static (float arrival, float brake) WarpApproachParams(float warpRadius, float shipRadius)
    {
        float threshold = warpRadius + shipRadius;            // the transit sphere
        return (arrival: 0f, brake: Math.Max(1f, threshold)); // stop only at the centre
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
    /// has AllWeapons.
    /// <para><b>P489</b> — before the straight-line fly, the bot consults the
    /// SHARED native dodge law (<see cref="Allegiance.Core.Sim.ShipDodgeLaw"/>,
    /// the same module the server NPC autopilot's <c>Ship.TryComputeDodgeGoal</c>
    /// calls) fed from its own <c>ClientWorld</c>: asteroids, stations and known
    /// same-side ships in its cluster. When an imminent collision is found the
    /// bot flies the dodge goal instead of the obstacle-blind bee-line — the
    /// P484 "park at the aleph" fix (a straight warp-approach line into Alderan's
    /// dense mining-hub field was being bounced/destroyed before reaching the
    /// transit volume).</para></summary>
    private static void CombatFlyTo(ActiveBot bot, Vector3 target, float arrival, float brake, int stateM)
    {
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out var pos, out var orient)) return;
        // P489 — dodge pre-empts the straight-line goal, exactly like the server
        // NPC autopilot's universal Stage-1 check (Dodge runs before every goal
        // branch). No imminent collision → fall through to the unchanged law.
        if (TryDodgeGoal(cs, bot, pos, out var dodgeGoal))
        {
            target = dodgeGoal;
            // P553 — when the dodge fires during COMBAT the dodge OWNS steering
            // and throttle for this tick, and weapon fire is suppressed — native's
            // combat drone "if (!bDodge)" (it doesn't thrust at / shoot the
            // target while dodging, shipIGC.cpp:2410) and the dodgeCooldown
            // no-fire rule (shipIGC.cpp:2485-2490). We keep firing only when NOT
            // dodging ("keep firing only if native does"); the bot still boosts.
            stateM = CombatStateWhileDodging(stateM);
            // P553 — telemetry: record every dodge-law fire (non-behavioural) so a
            // live proof can correlate a collision death with active dodging.
            lock (bot.DodgeGate)
            {
                bot.DodgeFires.Add(new BotDodgeFire(bot.ShipId, CsSector(cs),
                    (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds, pos, dodgeGoal));
                if (bot.DodgeFires.Count > MaxDodgeTraces) bot.DodgeFires.RemoveAt(0);
            }
        }
        // P539d — cross-side approach avoidance: steer around any ship (incl.
        // cross-side) lying in the forward path cone so the approach never rams —
        // the same cone test the friendly-fire gate uses. The bot's own actively-
        // engaged target is excluded (the P539 stand-off ring keeps that at range).
        {
            short avoidCur = CsSector(cs);
            List<(Vector3 Pos, short Id)>? pathShips = null;
            lock (cs.Gate)
            {
                if (avoidCur >= 0)
                    foreach (var kv in cs.World.Ships)
                    {
                        if (kv.Key == bot.ShipId) continue;
                        if (kv.Value.SectorId != avoidCur) continue;
                        if (kv.Value.Latest is not { } lu) continue;
                        (pathShips ??= new List<(Vector3, short)>()).Add((lu.Position, kv.Key));
                    }
            }
            if (pathShips is not null && AvoidShipsInPath(pos, target, AvoidHorizon, AvoidCosHalfAngle,
                    AvoidClearancePad + HullRadius(bot.HullId) + 6f, pathShips, bot.TargetId, out var avoidGoal))
                target = avoidGoal;
        }
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

    /// <summary>P489/P524 — feed the SHARED native dodge law
    /// (<see cref="Allegiance.ClientCore.Flight.ClientAutopilotDodge"/>) from the
    /// bot's own <c>ClientWorld</c>: the mover's pose (position here, velocity
    /// read live) and hull, plus the asteroids / stations / known ships in its
    /// cluster. This is the SAME canonical feed the MonoGame player <c>G</c>
    /// autopilot uses (P524) — no separate copy — so the bots and the player
    /// autopilot steer with byte-identical obstacle avoidance. Returns a dodge
    /// goal when an imminent collision is found (mirrors the server NPC
    /// autopilot), else <c>false</c> — in which case the caller flies its normal
    /// straight-line goal unchanged. No <see cref="Allegiance.ClientCore.Flight.ClientAutopilotDodge.SkipGoal"/>
    /// is passed, preserving the P489 bot behaviour exactly.</summary>
    private static bool TryDodgeGoal(ConnectionSession cs, ActiveBot bot, Vector3 myPos, out Vector3 goal)
    {
        goal = default;
        short cur = CsSector(cs);
        if (cur < 0) return false;
        Vector3 myVel;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } mu) return false;
            myVel = mu.Velocity;
        }
        var core = Catalog.RepresentativeCore();
        if (core.HullTypes.TryGetValue(bot.HullId, out var hull) && hull.Mass > 0f && hull.Thrust > 0f)
        {
            // P497 — a board-capable hull does not dodge the station it is flying
            // into to board (native boards by flying into the bay, not by evading
            // the base); the shared feed drops every station for such a hull.
            bool dodgeStations = !hull.HasCapability(DataHullTypeIGC.HabmBoard);
            lock (cs.Gate)
            {
                return Allegiance.ClientCore.Flight.ClientAutopilotDodge.TryComputeDodgeGoal(
                    cs.World, bot.ShipId, bot.Side, myPos, myVel,
                    new Allegiance.ClientCore.Flight.ClientAutopilotDodge.MoverHull(hull.Length, hull.Thrust, hull.Mass),
                    dodgeStations,
                    id => core.HullTypes.TryGetValue(id, out var h) ? h.Length : (float?)null,
                    tid => core.StationTypes.TryGetValue(tid, out var t) && t.Radius > 0f ? t.Radius : (float?)null,
                    skipGoal: null,
                    out goal);
            }
        }
        return false;
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
    /// the engagement. Loiters when no enemy is visible. P431: a commanded
    /// ship-target order (an accepted teammate order) TAKES PRECEDENCE over
    /// nearest-enemy — the bot engages THAT specific ship, never the nearest
    /// one, while it is alive.</summary>
    private static void EngageCombat(ActiveBot bot, double now)
    {
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out var myPos, out _)) return;
        short myCluster = CsSector(cs);

        // P431 — the commanded ship-target wins over nearest-enemy. While it is
        // alive and co-located, engage exactly it; if it is not currently engageable
        // (cross-side visibility can be intermittent — P429) or not yet co-located,
        // loiter (committed to this order, attacking no one else). The order is only
        // released by a NEW superseding order (DecideAcceptTranslate), not by a
        // transient visibility gap.
        if (bot.OrderedTargetId is { } ordered)
        {
            bool engageable; short osector;
            lock (cs.Gate)
            {
                bool found = cs.World.Ships.TryGetValue(ordered, out var se);
                engageable = found && !(se?.IsNotVisible ?? true) && (se?.SideId ?? bot.Side) != bot.Side && se?.Latest is not null;
                osector = found ? se!.SectorId : (short)-1;
            }
            bot.TargetId = ordered;
            if (engageable && osector == myCluster)
            {
                EngageShipAt(bot, cs, ordered, now);
            }
            else
            {
                LoiterCombat(bot, now);   // committed to this order; not engageable right now
            }
            return;
        }

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
        EngageShipAt(bot, cs, best, now);
    }

    /// <summary>[vz EngageEnemy's engagement body, P539-rewritten] Point the bot
    /// at <paramref name="targetId"/> via a real cross-side ORDER_CHANGE target
    /// acquisition (re-armed every ~3s) and run the P539 lead-aimed dogfight:
    /// steer to a STAND-OFF ring with the nose on the solved LEAD direction
    /// (native <c>solveForLead</c>, <c>common.cpp:121-149</c> /
    /// <c>shipIGC.cpp:2463-2468</c>), and hold <c>AllWeapons</c> only inside the
    /// small lead-aim cone and the weapon's real reach (<see cref="DecideFire"/>)
    /// — never at the target's current position, never ramming. No periodic
    /// cloak pulse (a mid-fight cloak drops the bot out of visibility/targeting).
    /// Shared by the nearest-enemy and P431 commanded-target paths.</summary>
    private static void EngageShipAt(ActiveBot bot, ConnectionSession cs, short targetId, double now)
    {
        if (now - bot.LastTargetAt > 3)
        {
            cs.SendOrderChange(bot.ShipId, OrderChange.CmdQueued, OrderChange.CmdCurrent, IgcObjectType.Ship, targetId);
            bot.LastTargetAt = now;
        }
        if (!TryCsPose(cs, bot.ShipId, out var myPos, out var orient)) return;

        Vector3 targetPos, targetVel, myVel;
        short targetSector; short targetHullId;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(targetId, out var te) || te.Latest is not { } tlu)
            { LoiterCombat(bot, now); return; }
            targetPos = tlu.Position;
            targetVel = tlu.Velocity;
            targetSector = te.SectorId;
            targetHullId = te.HullId;
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } mu)
            { LoiterCombat(bot, now); return; }
            myVel = mu.Velocity;
        }
        if (targetSector != CsSector(cs)) { LoiterCombat(bot, now); return; }  // co-location guard (P429 one-sided)

        // The ballistics of the bot's LEAD weapon (first mounted gun) from the
        // REAL part/projectile data — never a literal.
        float projSpeed = 0f, weaponRange = 0f; bool abs = false;
        if (bot.WeaponPartId is { } wp) TryWeaponBallistics(wp, out projSpeed, out weaponRange, out abs);

        // Lead aim: where the round actually intercepts the moving target.
        float targetR = HullRadius(targetHullId);
        Vector3 aim = Vector3.Zero;
        bool haveLead = false;
        if (projSpeed > 0f) haveLead = TrySolveLead(myPos, myVel, targetPos, targetVel, targetR, projSpeed, abs, out aim, out _);
        Vector3 rawAim = targetPos - myPos;
        float rawLen = rawAim.Length();
        if (rawLen > 1e-4f) rawAim /= rawLen; else rawAim = orient.GetForward();
        Vector3 aimDir = haveLead ? aim : rawAim;    // fall back to raw bearing when no intercept exists

        float distance = (targetPos - myPos).Length();
        float myR = HullRadius(bot.HullId);
        float ring = weaponRange > 0f ? weaponRange * DogfightStandoffFraction : 0f;
        float standOff = Math.Max(DogfightStandoffFloor + myR + targetR, ring);   // never inside the collision ring

        // P539b — hold fire when a FRIENDLY (same-side) ship lies in the line of
        // fire ahead (native wingmen don't shoot through a teammate; the server
        // suppresses friendly damage anyway, shipIGC.cpp:1222-1228, but a round
        // into a friend wastes the shot and feeds the pack-up collisions).
        bool friendlyBlocked = weaponRange > 0f
            && FriendlyInCone(myPos, aimDir, weaponRange, SameSideShipsInLine(cs, bot, myPos, weaponRange), bot.Side);

        bool fire = weaponRange > 0f && !friendlyBlocked
            && DecideFire(orient, aimDir, haveLead, distance, weaponRange);
        // P539b — ShotsFired is NOT incremented here: a fire DECISION is not a
        // real round. The bot's real shots are counted from its own PROJ_INST
        // echoes in SampleCombatStats, so the metric reflects rounds the server
        // actually fired, not ticks it merely tried to.

        bool boost = now - bot.LastBoostAt < 4;
        if (now - bot.LastBoostAt > 11) { bot.LastBoostAt = now; boost = true; }
        // P539 — no periodic cloak pulse (root-caused: mid-fight cloaking drops
        // the bot out of visibility/targeting, cutting its own shots). The
        // mounted cloak part stays armed; we simply never pulse it in combat.
        int stateM = (fire ? ShipControlState.AllWeapons : 0)
                     | (boost ? ShipControlState.AfterburnerButton : 0);

        // Stand-off goal: nose on the lead, hover at the stand-off ring.
        CombatFlyTo(bot, StandOffLeadGoal(myPos, aimDir, distance, standOff), 15f, 160f, stateM);

        MaybeFireMissile(bot, cs, now);
    }

    /// <summary>P539 — the real projectile ballistics (speed, reach =
    /// speed × lifespan, absolute-flag) of a mounted weapon part, read from the
    /// REAL part/projectile data. Returns <c>false</c> when the part is not a
    /// real weapon with a real projectile (the bot then cannot lead/fire).</summary>
    private static bool TryWeaponBallistics(short weaponPartId, out float speed, out float range, out bool absolute)
    {
        var core = Catalog.RepresentativeCore();
        if (core.PartTypes.TryGetValue(weaponPartId, out var dp)
            && dp.Type is DataPartTypeIGC.Weapon w
            && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt)
            && pt.Speed > 0f)
        {
            speed = pt.Speed;
            range = pt.Speed * pt.Lifespan;
            absolute = pt.AbsoluteF;
            return true;
        }
        speed = 0f; range = 0f; absolute = false;
        return false;
    }

    /// <summary>P539 — the 0.5 × Length radius convention for a hull id (cf.
    /// the ship-radius convention, <c>Ship.cs:1873</c>). Falls back to a small
    /// nominal 6 u for an unknown hull so steering never degenerates.</summary>
    private static float HullRadius(short hullId)
    {
        var core = Catalog.RepresentativeCore();
        return core.HullTypes.TryGetValue(hullId, out var h) && h.Length > 0f ? 0.5f * h.Length : 6f;
    }

    /// <summary>P539b — tally the bot's REAL combat stats from its OWN wire/world
    /// events since the last tick, ALL attributed to THIS bot as the shooter:
    /// <list type="bullet">
    /// <item><b>Shots</b> — this bot's OWN <c>PROJECTILE_INSTANCE</c> echoes
    /// (rounds the server actually fired and broadcast back to the shooter's own
    /// peer, <c>ProjectileInstance.LauncherId == myShipId</c>) — NOT a fire
    /// decision (P539's <c>ShotsFired++</c> at the fire gate over-counted
    /// "about to fire" ticks that never resolved into a round).</item>
    /// <item><b>Hits</b> — <c>PROJECTILE_HULL_HIT</c> whose <c>SourceId</c> is
    /// our own ship (the wire now carries the shooter; the OLD code counted
    /// every sector-wide hull hit as this bot's — that is exactly why P539's
    /// 4,400 "ship hits" bore no relation to real damage). Split by the struck
    /// target's side into enemy / friendly / none(-1, no ship), and by the
    /// <c>AbsorbedByShield</c> flag into enemy shield vs hull hits.</item>
    /// <item><b>Kills</b> — <c>KILL_SHIP</c> where this bot is the credited
    /// killer (unchanged).</item>
    /// </list>
    /// Called each combat tick under <c>cs.Gate</c>.</summary>
    private static void SampleCombatStats(ActiveBot bot, ConnectionSession cs, short myShipId)
    {
        lock (cs.Gate)
        {
            // Real shots: this bot's own fired rounds (the shooter's own peer
            // always receives its PROJECTILE_INSTANCE).
            foreach (var pj in cs.World.TakeNewProjectiles())
                if (pj.LauncherId == myShipId) bot.ShotsFired++;

            // Real hits attributed to the shooter: only hull-hits WE caused,
            // bucketed by the struck target's side + shield/hull (pure logic).
            var buckets = BucketHits(myShipId, bot.Side, DrainHitsForBucket(cs, myShipId));
            bot.ShipHits += buckets.EnemyShield + buckets.EnemyHull;
            bot.EnemyShieldHits += buckets.EnemyShield;
            bot.EnemyHullHits += buckets.EnemyHull;
            bot.FriendlyHits += buckets.Friendly;
            bot.NoneHits += buckets.None;
            var kills = cs.World.Events.Kills;
            for (int i = Math.Min(bot.KillCursor, kills.Count); i < kills.Count; i++)
            {
                var k = kills[i];
                if (k.IdCredit == myShipId && k.TypeCredit == 0) bot.Kills++;   // OT_ship killer
            }
            bot.KillCursor = kills.Count;
        }
    }

    /// <summary>P539b — pure bucketing of drained projectile hits into enemy
    /// (shield / hull) / friendly / none (-1 or an unknown side), counting ONLY
    /// the hits whose <paramref name="myShipId"/> is the SHOOTER
    /// (<c>SourceId == me</c>). A hit fired by ANYONE ELSE is never counted for
    /// this bot — that is the P539 metric bug this replaces (the old code counted
    /// every sector-wide hit as every bot's). Unit-provable with synthetic hits
    /// and no server.</summary>
    public readonly record struct HitBuckets(long EnemyShield, long EnemyHull, long Friendly, long None);

    public static HitBuckets BucketHits(
        short myShipId, short mySide,
        IEnumerable<(short SourceId, short TargetId, bool Absorbed, short? TargetSide)> hits)
    {
        long es = 0, eh = 0, f = 0, n = 0;
        foreach (var h in hits)
        {
            if (h.SourceId != myShipId) continue;          // someone else's hit — never ours
            if (h.TargetSide is { } ts && ts != -1 && ts != mySide)
            { if (h.Absorbed) es++; else eh++; }
            else if (h.TargetSide is { } tf && tf != -1 && tf == mySide) f++;
            else n++;
        }
        return new HitBuckets(es, eh, f, n);
    }

    /// <summary>Drain this bot's world's new hull-hits and project them into the
    /// bucketing shape, resolving each target's side from the bot's own
    /// ClientWorld (the truthful source for ships it can see).</summary>
    private static List<(short SourceId, short TargetId, bool Absorbed, short? TargetSide)> DrainHitsForBucket(
        ConnectionSession cs, short myShipId)
    {
        var list = new List<(short, short, bool, short?)>();
        foreach (var h in cs.World.TakeNewHullHits())
        {
            short? tgtSide = null;
            if (h.TargetId != -1 && cs.World.Ships.TryGetValue(h.TargetId, out var tgt))
                tgtSide = tgt.SideId;
            list.Add((h.SourceId, h.TargetId, h.AbsorbedByShield, tgtSide));
        }
        return list;
    }

    /// <summary>
    /// P539 — detect whether this bot's own ship has become a LIFEPOD: the
    /// server swaps the SAME ship to the side's lifepod hull on eject
    /// (<c>Server.Combat.EjectSurvivor</c>, <c>FedSrv.CPP:11942</c>), keeping
    /// the same ship id, so the bot recognizes it from its OWN world view via
    /// the hull's real <c>HabmLifepod</c> capability. A pod drops any committed
    /// order/target and returns <c>true</c> so the caller flies it home instead
    /// of fighting (fixing the observed "bots won't die — they chase and ram as
    /// unarmed pods"). When the bot relaunches into a fresh (non-pod) hull the
    /// pod state is cleared and combat resumes.
    /// </summary>
    private static bool UpdatePodState(ActiveBot bot, ConnectionSession cs)
    {
        short hull;
        short stationId;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me)) return bot.IsPod;
            hull = me.HullId;
            stationId = me.StationId;
        }
        bool isPod = Catalog.IsLifepodHull(hull);
        if (isPod)
        {
            if (!bot.IsPod)
            {
                bot.IsPod = true;
                bot.EverPod = true;
                bot.Deaths++;
                // P539d — classify this death: a credited ship shooter (KILL_SHIP
                // TypeCredit 0 = OT_ship, distinct killer) = gunfire kill; otherwise
                // (no credited shooter — a ram/crash) = collision/other death.
                List<(short, short, short)> kills = new();
                lock (cs.Gate)
                    foreach (var k in cs.World.Events.Kills)
                        kills.Add((k.ShipId, k.IdCredit, k.TypeCredit));
                bool gunfire = ClassifyDeath(bot.ShipId, kills) == DeathCause.GunfireKill;
                if (gunfire) bot.GunfireDeaths++; else bot.CollisionDeaths++;
                bot.OrderedTargetId = null;
                bot.TargetId = null;
                Console.WriteLine($"[p539d] DEATH bot {bot.Name} ship {bot.ShipId} side {bot.Side} cause={(gunfire ? "gunfire" : "collision")} deaths={bot.Deaths} gunfire={bot.GunfireDeaths} collision={bot.CollisionDeaths}");
                Console.WriteLine($"[p539] bot {bot.Name} (ship {bot.ShipId}) is now a LIFEPOD (hull {hull}) — stopping combat, flying home to dock");
            }
            if (!bot.PodDocked && stationId != -1)
            {
                bot.PodDocked = true;
                bot.PodDockStationId = stationId;
                Console.WriteLine($"[p539] pod bot {bot.Name} (ship {bot.ShipId}) DOCKED at station {stationId} — pod reached home");
            }
            return true;
        }
        if (bot.IsPod)
        {
            bot.IsPod = false;
            Console.WriteLine($"[p539] bot {bot.Name} (ship {bot.ShipId}) relaunched into hull {hull} — resuming combat");
        }
        return false;
    }

    /// <summary>
    /// P539 — fly a lifepod HOME to dock, as a human pilot would: route (real
    /// warps, the same P496 warp-approach law) to the nearest friendly station's
    /// sector, then fly at the station centre with no weapons (a pod doesn't
    /// fight) until docked. Once docked the server's normal dock/relaunch path
    /// takes over; when the bot relaunches into a fresh fighter
    /// (<see cref="UpdatePodState"/> clears <c>IsPod</c>) combat resumes.
    /// </summary>
    private void DrivePodHome(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (bot.PodDocked) return;   // already home — the server holds it docked

        if (bot.PodStationId < 0)
        {
            lock (cs.Gate)
            {
                Vector3 myPos = Vector3.Zero;
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.Latest is { } mu) myPos = mu.Position;
                DataStationIGC? best = null;
                float bestD = float.MaxValue;
                foreach (var st in cs.World.Stations.Values)
                {
                    if (st.SideId != bot.Side) continue;
                    if (st.Position is not { Length: 3 } p) continue;
                    float d = (new Vector3(p[0], p[1], p[2]) - myPos).LengthSquared();
                    if (d < bestD) { bestD = d; best = st; }
                }
                if (best is { } st2)
                {
                    bot.PodStationId = st2.StationId;
                    bot.PodStationSector = st2.ClusterId;
                    bot.PodStationCenter = new Vector3(st2.Position![0], st2.Position[1], st2.Position[2]);
                }
            }
            if (bot.PodStationId < 0)
            {
                LoiterCombat(bot, now);   // no friendly station known yet — hold, retry next tick
                return;
            }
            Console.WriteLine($"[p539] pod bot {bot.Name} (ship {bot.ShipId}) flying home to friendly station {bot.PodStationId} in sector {bot.PodStationSector}");
        }

        short cur = CsSector(cs);
        if (cur < 0) return;
        if (cur == bot.PodStationSector)
        {
            // In-sector: fly at the station centre to reach the dock footprint,
            // no weapons. Arrival/brake straddle the station hull (the P497 law).
            CombatFlyTo(bot, bot.PodStationCenter, 120f, 400f, 0);
            return;
        }
        EnsureCombatPath(bot, cur, bot.PodStationSector);
        if (bot.Path.Count == 0) { LoiterCombat(bot, now); return; }
        short next = CombatNextHop(bot, cur);
        if (next == cur) return;
        bot.WarpDest ??= TryBuildWarpDestMap(cs, out var m) ? m : null;
        if (bot.WarpDest is { } wd && TryFindWarpTo(cs, cur, next, wd, out var warpPos, out var warpR))
        {
            var core = Catalog.RepresentativeCore();
            float shipR = core.HullTypes.TryGetValue(bot.HullId, out var ht) && ht.Length > 0f ? 0.5f * ht.Length : 0f;
            var (arrival, brake) = WarpApproachParams(warpR, shipR);
            CombatFlyTo(bot, warpPos, arrival, brake, 0);
        }
        else
            LoiterCombat(bot, now);
    }

    /// <summary>
    /// P433 — fire the bot's real mounted missile launcher when its REAL lock
    /// reaches full on its current engagement target. The server streams the
    /// owner-only <c>MISSILE_LOCK_UPDATE(204)</c> each tick
    /// (Server.Broadcast.BroadcastMissileLock) into the bot's own ClientWorld
    /// (<c>cs.World.MissileLock</c>, a <see cref="Allegiance.ClientCore.State.MissileLockState"/>),
    /// so <c>IsFullLock</c> is the authoritative server-computed lock quality.
    /// On full lock, send the real <c>SendFireMissile()</c> (the wire request the
    /// server's HandleFireMissile → <see cref="Allegiance.Core.Sim.Ship.TryFireMissile"/>
    /// gates on lock+ammo and rate-limits via lock-rebuild + ammo decrement). The
    /// fire is additionally paced by the missile's real <see cref="MissileReadyTime"/>
    /// so a dumbfire launcher (instant lock, always-full) is never spammed —
    /// mirroring how the guns' AllWeapons beat is naturally paced by the weapon's
    /// refire. A launcher-less bot does nothing.
    /// </summary>
    private static void MaybeFireMissile(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (bot.LauncherPartId is null || bot.MissileReadyTime <= 0) return;
        if (now - bot.LastFireAt < bot.MissileReadyTime) return;
        bool full;
        lock (cs.Gate) full = cs.World.MissileLock.IsFullLock;
        if (!full) return;
        cs.SendFireMissile();
        bot.LastFireAt = now;
        Console.WriteLine($"[p433] bot {bot.Name} (ship {bot.ShipId}) fired real missile launcher at lock 1.0 -> SendFireMissile (ready {bot.MissileReadyTime:F2}s)");
    }

    // ── P539 — lead-aimed dogfight: pure decision helpers ────────────────
    //
    // The testable cores of the P539 combat fixes, factored out of the live
    // EngageShipAt wiring exactly like ComputePerf (P430) / DecideAcceptTranslate
    // (P431): pure static functions taking plain vectors/floats so the aim,
    // fire-gate and stand-off decisions are provable with synthetic geometry
    // and no server.

    /// <summary>P539 — the native lead-aim cone gate: the bot's nose must be
    /// within this cosine of the solved lead direction before it holds the
    /// trigger. Faithful to the wingman's fire gate — native turns to face the
    /// LEAD point (<c>turnToFace(direction)</c>, <c>shipIGC.cpp:2470-2476</c>)
    /// and fires once the angle is small; this port's wingman uses 0.90
    /// (<see cref="Allegiance.Core.Sim.Ship"/>.WingmanFireFacingCosine,
    /// <c>Ship.cs:4485</c>, = ~25.8°).</summary>
    public const float DogfightFireFacingCosine = 0.90f;

    /// <summary>P539 — where the bot holds its stand-off ring: <c>0.72 ×</c>
    /// the weapon's max reach, so rounds comfortably span the gap.</summary>
    private const float DogfightStandoffFraction = 0.72f;

    /// <summary>P539 — the absolute collision-safety floor for the stand-off
    /// ring (added to both ships' radii), so a bot never drives into its
    /// target no matter how short the gun.</summary>
    private const float DogfightStandoffFloor = 25f;

    // ── P539d — fair contact tuning ──
    /// <summary>How long a side's first-staged bot may hold before the side is
    /// forced to advance anyway (a straggler must not freeze the group forever;
    /// also covers a bot stuck unroutable that never stages).</summary>
    public const double StagingHoldSeconds = 15.0;
    /// <summary>Horizon (units) for the cross-side approach avoidance: only a
    /// ship this close and inside the forward path cone is steered around.</summary>
    public const float AvoidHorizon = 200f;
    /// <summary>Cosine of the half-angle of the forward path-cone that counts as
    /// "in my way" for approach avoidance (≈ 30°).</summary>
    public const float AvoidCosHalfAngle = 0.860f;
    /// <summary>Extra lateral clearance added to both radii when steering around
    /// a ship in the path (units).</summary>
    public const float AvoidClearancePad = 12f;
    /// <summary>Arena offset (units) of each mirrored spawn from the target
    /// sector centre along the facing axis.</summary>
    public const float ArenaOffset = 650f;

    /// <summary>
    /// P539 — solve the LEAD aim: the world-space unit direction to point the
    /// bot's fixed forward guns so a round travelling at
    /// <paramref name="projectileSpeed"/> intercepts a MOVING target, using the
    /// SHARED native lead solver <see cref="Allegiance.Core.Sim.ShipDodgeLaw.SolveForImpact"/>
    /// (the faithful port of native <c>solveForImpact</c>, <c>common.cpp:59-119</c>,
    /// that the wingman's <c>solveForLead</c> layers on, <c>shipIGC.cpp:2463-2468</c>;
    /// see <c>Ship.SolveForLead</c>, <c>Ship.cs:5051-5103</c> for the faithful
    /// formulation — target velocity used absolutely for an absolute projectile,
    /// else relative to the shooter). Returns <c>false</c> when no intercept
    /// exists (the round can never catch the target); the caller then does not
    /// fire.
    /// </summary>
    public static bool TrySolveLead(
        Vector3 myPos, Vector3 myVel,
        Vector3 targetPos, Vector3 targetVel,
        float targetRadius, float projectileSpeed, bool absoluteProjectile,
        out Vector3 aimDirection, out float interceptTime)
    {
        Vector3 deltaP = targetPos - myPos;
        Vector3 deltaV = absoluteProjectile ? targetVel : targetVel - myVel;   // common.cpp:144-146
        interceptTime = ShipDodgeLaw.SolveForImpact(deltaP, deltaV, projectileSpeed, targetRadius, out aimDirection);
        return aimDirection.LengthSquared() > 1e-9f;   // native's direction is unset on no-intercept
    }

    /// <summary>
    /// P539 — the FIRE GATE: hold the trigger only while the target is
    /// (a) inside the mounted weapon's real reach (<paramref name="weaponRange"/>
    /// = projectileSpeed × lifespan, the honest range, cf. the wingman's
    /// <c>EstimateWeaponRange</c>, <c>Ship.cs:5232-5241</c>) AND (b) inside the
    /// small lead-aim cone: the bot's forward within
    /// <see cref="DogfightFireFacingCosine"/> of the solved LEAD direction
    /// (<c>forward·dir</c>, the P155 facing metric, <c>Ship.cs:2545</c>). A
    /// disabled lead (<paramref name="haveLead"/> false) never fires. Pure so
    /// the cone/range cut-offs are unit-provable.
    /// </summary>
    public static bool DecideFire(
        Orientation myOrient, Vector3 aimDirection, bool haveLead,
        float distanceToTarget, float weaponRange)
    {
        if (!haveLead) return false;
        if (distanceToTarget > weaponRange) return false;
        float facing = -myOrient.TimesInverse(aimDirection).Z;   // forward·dir (P155 metric)
        return facing >= DogfightFireFacingCosine;
    }

    /// <summary>
    /// P539b — the friendly-in-line-of-fire gate: whether a FRIENDLY (same-side)
    /// ship lies inside the bot's line of fire ahead — within the weapon's real
    /// reach (<paramref name="reach"/>) and inside the narrow firing cone around
    /// the aim direction (angle &lt; <paramref name="cosHalfAngle"/> — default
    /// cosine 0.9975 ≈ 4°). When true the bot HOLDS FIRE, matching native
    /// wingman combat (a wingman doesn't shoot through a teammate; the server's
    /// friendly-fire gate <c>shipIGC.cpp:1222-1228</c> suppresses the damage
    /// anyway, but a round aimed into a friend wastes the shot and feeds the
    /// pack-up collision piles P539 measured). Pure: the caller gathers the
    /// candidate same-side ships (<see cref="SameSideShipsInLine"/>) and passes
    /// their positions — the math is unit-provable with no server.
    /// </summary>
    public static bool FriendlyInCone(
        Vector3 myPos, Vector3 aimDir, float reach,
        IEnumerable<(Vector3 Pos, short Id, short Side)> ships, short mySide,
        float cosHalfAngle = 0.996f)
    {
        foreach (var (pos, _, side) in ships)
        {
            if (side != mySide) continue;              // only same-side friends block
            var toFriend = pos - myPos;
            float distSq = toFriend.LengthSquared();
            if (distSq > reach * reach || distSq < 1e-4f) continue;
            float inv = 1f / MathF.Sqrt(distSq);
            float dot = (toFriend.X * aimDir.X + toFriend.Y * aimDir.Y + toFriend.Z * aimDir.Z) * inv;
            if (dot >= cosHalfAngle) return true;      // a friend is right in the aim cone
        }
        return false;
    }

    /// <summary>P539b — gather the candidate same-side ships that could lie in
    /// this bot's line of fire (its own sector, its own side, not itself), each
    /// as (position, id, side), for <see cref="FriendlyInCone"/> to test. Reads
    /// the bot's own ClientWorld under <c>cs.Gate</c>.</summary>
    private static IEnumerable<(Vector3 Pos, short Id, short Side)> SameSideShipsInLine(
        ConnectionSession cs, ActiveBot bot, Vector3 myPos, float reach)
    {
        var list = new List<(Vector3, short, short)>();
        short myCluster = CsSector(cs);
        lock (cs.Gate)
        {
            foreach (var (id, ent) in cs.World.Ships)
            {
                if (id == bot.ShipId) continue;
                if (ent.SideId != bot.Side) continue;
                if (ent.SectorId != myCluster) continue;
                if (ent.Latest is not { } lu) continue;
                if ((lu.Position - myPos).LengthSquared() > reach * reach) continue;
                list.Add((lu.Position, id, ent.SideId));
            }
        }
        return list;
    }

    /// <summary>
    /// P539 — the STAND-OFF steering goal: a world point the bot flies toward
    /// that (a) keeps its nose ON the lead direction (so fixed guns bear) and
    /// (b) never coincides with the target's position (no ramming). Constructed
    /// as <c>myPos + aimDir × (distToTarget − standOff)</c>: a point along the
    /// lead aim that lies <paramref name="standOff"/> short of the target.
    /// Far away it pulls the bot in to the firing ring; inside the ring it sits
    /// behind the bot so <see cref="CombatFlyTo"/> eases/reverses — a
    /// self-correcting stand-off. <c>aimDir</c> is a unit vector (the solved
    /// lead, or the raw direction to the target when no intercept exists).
    /// </summary>
    public static Vector3 StandOffLeadGoal(Vector3 myPos, Vector3 aimDir, float distanceToTarget, float standOff)
        => myPos + aimDir * Math.Max(0f, distanceToTarget - standOff);

    /// <summary>P553 — pure rule for the "the dodge owns the combat tick" fix:
    /// while the shared dodge fires during combat the bot keeps steering and
    /// throttling to the dodge goal and does NOT fire — native's combat drone
    /// "if (!bDodge)" (<c>shipIGC.cpp:2410</c>) and dodgeCooldown no-fire rule
    /// (<c>shipIGC.cpp:2485-2490</c>): keep firing only if native does. Clears
    /// the <see cref="ShipControlState.AllWeapons"/> fire bit, leaving boost and
    /// every other state bit intact.</summary>
    internal static int CombatStateWhileDodging(int stateM) => stateM & ~ShipControlState.AllWeapons;

    /// <summary>
    /// P539 — which LIFE-PHASE a combat bot is in: a bot whose own ship has
    /// become a LIFEPOD hull (the server swapped it on eject, keeping the same
    /// ship id — <c>Server.Combat.EjectSurvivor</c>, <c>FedSrv.CPP:11942</c>)
    /// is an unarmed survivor and must STOP combat and fly home to dock, never
    /// chase/ram. Pure decision over the observable inputs so the pod-vs-combat
    /// choice is unit-provable.
    /// </summary>
    public static bool DecidePodBehavior(bool ownHullIsLifepod) => ownHullIsLifepod;

    // ── P539d — fair contact: pure, unit-testable decision helpers ──

    /// <summary>
    /// P539d — how ONE death of a bot ship should be classified. A death is a
    /// <b>gunfire kill</b> when the death correlates with a KILL_SHIP event
    /// crediting a distinct ship (TypeCredit 0 = OT_ship) as the killer of this
    /// victim; otherwise (no credited ship shooter) it is a <b>collision/other</b>
    /// death (a ram / a crash, an eject without a shooter). Pure over a plain
    /// enumerable of (victimId, killerId, killerType) tuples so it is unit-provable
    /// with no server. A bot's own suicide/self-hit is not credited to another ship.
    /// </summary>
    public enum DeathCause { GunfireKill, CollisionOrOther }

    public static DeathCause ClassifyDeath(
        short victimId,
        IEnumerable<(short VictimId, short KillerId, short KillerType)> kills)
    {
        foreach (var (vid, kid, ktype) in kills)
        {
            if (vid == victimId && ktype == 0 && kid >= 0 && kid != victimId)
                return DeathCause.GunfireKill;
        }
        return DeathCause.CollisionOrOther;
    }

    /// <summary>
    /// P539d — the pure staging-release gate. A side advances together only when
    /// every LIVING bot on the side has reached its staging hold, or when the
    /// first-staged bot has waited past <paramref name="timeoutSeconds"/>. Dead /
    /// docked bots are excluded (they are not counted as "living"), and a side
    /// with no living bots releases trivially so a wiped side never deadlocks.
    /// Pure so the hold-vs-timeout decision is unit-provable.
    /// </summary>
    public static bool SideReadyToAdvance(
        int aliveCount, int stagedCount,
        double now, double? firstStagedAt, double timeoutSeconds)
    {
        if (aliveCount <= 0) return true;                      // nobody left to wait for
        if (stagedCount >= aliveCount) return true;            // fully assembled
        if (firstStagedAt is { } f0 && now - f0 >= timeoutSeconds) return true;  // timeout
        return false;
    }

    /// <summary>
    /// P539d — pure cross-side approach avoidance: among the ships given (their
    /// positions), if one lies within <paramref name="horizon"/> of the bot and
    /// inside the forward path-cone (cosine &ge; <paramref name="cosHalfAngle"/>)
    /// around the straight-line direction to <paramref name="target"/>, deflect
    /// the goal laterally by <paramref name="clearance"/> perpendicular to the
    /// path so the bot steers around it instead of ramming it (the same cone test
    /// the friendly-fire gate uses, applied to ALL ships incl. cross-side during
    /// the approach). The bot's own actively-engaged target is excluded (handled
    /// by the stand-off ring). Returns a deflected goal when a ship is in the way.
    /// </summary>
    public static bool AvoidShipsInPath(
        Vector3 myPos, Vector3 target, float horizon, float cosHalfAngle, float clearance,
        IEnumerable<(Vector3 Pos, short Id)> ships, short? engagedTargetId,
        out Vector3 goal)
    {
        goal = default;
        var toTarget = target - myPos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f) return false;
        var dir = toTarget / dist;
        foreach (var (pos, id) in ships)
        {
            if (engagedTargetId is { } et && id == et) continue;   // stand-off handles the engaged target
            var toS = pos - myPos;
            float ds = toS.Length();
            if (ds > horizon || ds < 1e-4f) continue;
            float dot = (toS.X * dir.X + toS.Y * dir.Y + toS.Z * dir.Z) / ds;
            if (dot < cosHalfAngle) continue;                       // not in the forward path cone
            // Steer laterally around it: radial (toward-perpendicular) component, scaled to clearance.
            var radial = dir * ds - toS;                            // outward from the line
            if (radial.LengthSquared() < 1e-6f)
                radial = Vector3.CrossProduct(dir, new Vector3(0f, 1f, 0f));
            radial.Normalize();
            goal = myPos + dir * dist + radial * clearance;
            return true;
        }
        return false;
    }

    /// <summary>
    /// P539d — pure mirrored arena placements: <paramref name="count"/> ships per
    /// side placed <paramref name="offset"/> on either side of the sector centre
    /// along <paramref name="axis"/>, each side spread along a perpendicular
    /// (<paramref name="spread"/>) lane so same-side ships don't stack on the same
    /// point. Side 0 faces +axis (toward side 1), side 1 faces −axis. Positions
    /// are equidistant from the centre and mirror images across the centre plane.
    /// </summary>
    public static (Vector3[] SideA, Vector3[] SideB, Vector3 FaceA, Vector3 FaceB) ComputeArenaPlacements(
        int count, Vector3 center, Vector3 axis, float offset, float spread)
    {
        var a = new Vector3[count];
        var b = new Vector3[count];
        axis.Normalize();
        // A perpendicular lane basis.
        var up = Math.Abs(axis.Y) > 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f);
        var laneX = Vector3.CrossProduct(axis, up); laneX.Normalize();
        var laneY = Vector3.CrossProduct(axis, laneX); laneY.Normalize();
        for (int i = 0; i < count; i++)
        {
            // Spread each bot along the two lanes by its index (alternating quadrants).
            float sx = (i % 2 == 0 ? 1f : -1f) * spread * (i / 2 + 1);
            float sy = (i % 4 >= 2 ? 1f : -1f) * spread * 0.6f;
            var lane = laneX * sx + laneY * sy;
            a[i] = center + axis * offset + lane;
            b[i] = center - axis * offset - lane;       // exact mirror across the centre plane
        }
        // Facing: side A sits at +axis, so it faces −axis (toward side B); side B at
        // −axis faces +axis (toward side A). They look at each other across the centre.
        return (a, b, -axis, axis);
    }

    /// <summary>[vz DriveEnemyContingent] Drive one combat bot a tick. The bot is
    /// already launched at spawn; if it has a <c>target_sector</c> it routes real
    /// warps along the BFS shortest path there before engaging; otherwise (or once
    /// arrived) it engages any different-side ship already in its own cluster.
    /// P539: tallies real combat stats, and a bot whose own ship has become a
    /// LIFEPOD stops combat and flies home to dock instead of fighting.</summary>
    private void DriveCombat(ActiveBot bot, double now)
    {
        var cs = bot.Cs!;
        if (cs.World.MyShipId is not { } my) return;     // not seated yet
        bot.ShipId = my;

        // P539 — tally this bot's REAL combat stats (hits/kills) from its own
        // wire/world events each tick.
        SampleCombatStats(bot, cs, my);

        // P539 — a lifepod stops fighting and flies home to dock.
        if (UpdatePodState(bot, cs))
        {
            DrivePodHome(bot, cs, now);
            return;
        }

        if (bot.TargetSector is not { } goal) { EngageCombat(bot, now); return; }

        // P539d — arena: both sides were relocated to mirrored spots in the target
        // sector at spawn; no travel, so engage in place (travel can't decide it).
        if (bot.Arena)
        {
            if (!bot.Arrived) { bot.Arrived = true; bot.ArrivedAt ??= now; }
            EngageCombat(bot, now);
            return;
        }

        short cur = CsSector(cs);
        if (cur < 0) return;                             // own pose not streaming yet

        // P539d — arrival + warp-hop telemetry (a pod relaunch is not a warp hop).
        if (cur == goal && !bot.Arrived)
        {
            bot.Arrived = true;
            bot.ArrivedAt ??= now;
        }
        if (!bot.IsPod && bot.LastCluster >= 0 && cur != bot.LastCluster)
        {
            if (bot.WarpHops.Count == 0 || bot.WarpHops[^1].To != cur)
                bot.WarpHops.Add((now, bot.LastCluster, cur));   // cross-check vs server [warp] lines
        }

        if (bot.Arrived) { EngageCombat(bot, now); return; }

        // ── P539d — travel-with-staging: gather the whole side at a staging sector
        //    (the last sector before the rendezvous), hold just outside the final
        //    warp's transit sphere until the side is assembled (or the timeout),
        //    then the WHOLE side advances together through the final warp — so no
        //    side trickles into the other's settled position one ship at a time.
        if (bot.UseStaging)
        {
            if (bot.StagingSector < 0)
            {
                EnsureCombatPath(bot, cur, goal);
                bot.StagingSector = bot.Path.Count >= 2 && bot.Path[^1] == goal ? bot.Path[^2] : cur;
            }
            if (bot.StagePhase < 2)
            {
                EnsureCombatPath(bot, cur, bot.StagingSector);
                if (bot.Path.Count == 0) { LoiterCombat(bot, now); bot.LastCluster = cur; return; }
                short next = CombatNextHop(bot, cur);
                if (next != cur)
                {
                    // Still en route to the staging sector.
                    MaybeNarrateTransit(bot, cs, now, cur, bot.StagingSector, "stage");
                    bot.WarpDest ??= TryBuildWarpDestMap(cs, out var sm) ? sm : null;
                    if (bot.WarpDest is { } sd && TryFindWarpTo(cs, cur, next, sd, out var sp, out var sr))
                    {
                        var (sa, sb) = WarpApproachParams(sr, HullRadius(bot.HullId));
                        CombatFlyTo(bot, sp, sa, sb, 0);
                    }
                    else LoiterCombat(bot, now);
                    bot.LastCluster = cur;
                    return;
                }
                // At the staging sector: mark staged, then HOLD (loiter in the
                // sector — dodge keeps it off rocks) until the whole living side is
                // assembled here or the timeout, then the whole side advances
                // together through the final warp. Loitering in-sector is robust
                // (no reliance on a precise warp-adjacent hold point) and preserves
                // the fair-contact property: no bot trickles into the rendezvous
                // ahead of its side's group.
                if (bot.StagePhase == 0) { bot.StagePhase = 1; bot.FirstStagingAt ??= now; }
                if (StageReleaseRequired(bot.Side, now))
                {
                    bot.StagePhase = 2;
                    bot.ReleasedAt ??= now;
                    Console.WriteLine($"[p539d] side {bot.Side} bot {bot.Name} released from staging (side assembled/timeout) — advancing together through final warp to {goal}");
                    // fall through to the normal transit below (this is the release tick)
                }
                else
                {
                    LoiterCombat(bot, now);                 // hold in the staging sector
                    if (now - bot.LastNarrateAt > 4)
                    {
                        bot.LastNarrateAt = now;
                        int alive, staged;
                        lock (_gate)
                        {
                            alive = _bots.Count(o => o.Cs is not null && o.Behavior == "combat" && o.UseStaging && o.TargetSector is not null && o.Side == bot.Side && !o.IsPod && !o.PodDocked);
                            staged = _bots.Count(o => o.Cs is not null && o.Behavior == "combat" && o.UseStaging && o.TargetSector is not null && o.Side == bot.Side && o.StagePhase >= 1 && !o.IsPod && !o.PodDocked);
                        }
                        Console.WriteLine($"[p539d] STAGE bot {bot.Name} side={bot.Side} sector={cur} phase={bot.StagePhase} alive={alive} staged={staged}");
                    }
                    bot.LastCluster = cur;
                    return;
                }
            }
            // released (StagePhase == 2) → fall through to the normal transit below.
        }

        EnsureCombatPath(bot, cur, goal);
        MaybeNarrateTransit(bot, cs, now, cur, goal, "transit");
        if (bot.Path.Count == 0) { LoiterCombat(bot, now); bot.LastCluster = cur; return; }   // unroutable / no CLUSTERINFO yet
        short gn = CombatNextHop(bot, cur);
        if (gn == cur) { bot.Arrived = true; bot.ArrivedAt ??= now; bot.LastCluster = cur; EngageCombat(bot, now); return; }
        bot.WarpDest ??= TryBuildWarpDestMap(cs, out var m) ? m : null;
        if (bot.WarpDest is { } wd && TryFindWarpTo(cs, cur, gn, wd, out var warpPos, out var warpR))
        {
            // P496 — fly THROUGH the aleph. A warp hop has no "arrival" short
            // of its transit sphere (warp.Radius + ship.Radius), so the arrival/
            // brake are derived from that sphere (WarpApproachParams) instead of
            // the old fixed 200/450, which commanded the slow board hull to halt
            // ~90 u outside the transit volume and never cross (P490 "park at
            // the aleph"). P489's dodge pre-emption (inside CombatFlyTo) is
            // unchanged and still runs first.
            var (arrival, brake) = WarpApproachParams(warpR, HullRadius(bot.HullId));
            CombatFlyTo(bot, warpPos, arrival, brake, 0);
        }
        else
            LoiterCombat(bot, now);
        bot.LastCluster = cur;
    }

    /// <summary>P539d — throttled [p433-diag]-style route-health narration so a
    /// live stall is diagnosable (preserved from P433; now covers the staging leg
    /// too, labelled by <paramref name="leg"/>).</summary>
    private void MaybeNarrateTransit(ActiveBot bot, ConnectionSession cs, double now, short cur, short goal, string leg)
    {
        if (now - bot.LastNarrateAt <= 20) return;
        bot.LastNarrateAt = now;
        lock (cs.Gate)
        {
            bool graphOk = cs.World.Graph is not null;
            int nWarps = cs.World.Warps.Count;
            short nextHop = CombatNextHop(bot, cur);
            bool destMap = TryBuildWarpDestMap(cs, out var dm);
            bool warpFound = nextHop != cur && destMap && TryFindWarpTo(cs, cur, nextHop, dm, out _, out _);
            Console.WriteLine($"[p433-diag] bot {bot.Name} ({leg}) cluster {cur} goal {goal} path[{bot.Path.Count}] next={nextHop} graph={graphOk} warps={nWarps} destMap={destMap} warpFound={warpFound}");
        }
    }

    /// <summary>P539d — whether THIS bot's side has finished staging and may
    /// advance: every living (non-pod, non-docked) staged combat bot of the side
    /// is holding/released, or the first-staged bot has waited past
    /// <see cref="StagingHoldSeconds"/>. A side with no living bots releases
    /// trivially. Single-threaded (called from the Loop).</summary>
    private bool StageReleaseRequired(short side, double now)
    {
        int alive = 0, staged = 0; double? first = null;
        lock (_gate)
            foreach (var o in _bots)
            {
                if (o.Cs is null || o.Behavior != "combat" || !o.UseStaging || o.TargetSector is null) continue;
                if (o.Side != side) continue;
                if (o.IsPod || o.PodDocked) continue;         // not living
                alive++;
                if (o.StagePhase >= 1)
                {
                    staged++;
                    first ??= o.FirstStagingAt;
                }
            }
        return SideReadyToAdvance(alive, staged, now, first, StagingHoldSeconds);
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
        /// <summary>P431 — the specific enemy ship this bot is under a real
        /// accepted teammate order to engage (null = doing its own spawn-time
        /// behavior). Distinct from <see cref="Target"/> (the current live
        /// engagement) and from the spawn-time <see cref="Behavior"/>.</summary>
        public required short? OrderedTarget { get; init; }
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
                            Position = [pos.X, pos.Y, pos.Z], Target = bot.TargetId, OrderedTarget = bot.OrderedTargetId,
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
                            Position = [p.X, p.Y, p.Z], Target = bot.TargetId, OrderedTarget = bot.OrderedTargetId,
                        });
                        continue;
                    }
                    // Cross-side idle/weave (rare): report the assigned sector + a
                    // zeroed position honestly (the documented scope of visibility).
                    short s = bot.Bot is { } b2 && b2.ShipAssignment is { } a ? a.SectorId : (short)-1;
                    views.Add(new ActiveBotView
                    {
                        Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                        Sector = s, Behavior = bot.Behavior, Position = [0, 0, 0], Target = null, OrderedTarget = bot.OrderedTargetId,
                    });
                }
                catch { /* one bot's bad state never breaks list_active_bots */ }
            }
        }
        return views;
    }

    // ── P539c — per-bot world-view diagnosis (read-only; the per-side repro) ──

    /// <summary>One combat bot's live world view as <em>that bot's own client</em>
    /// holds it — the exact data its <see cref="DriveCombat"/> branch reasons over:
    /// its own sector, the target-warp record it is flying toward (id / cluster /
    /// position / radius / destination as <c>cs.World.Warps</c> holds it), every
    /// different-side ship its world lists with its stamped <c>SectorId</c> +
    /// pose + last update tick (the P539c acquisition inputs), and its live pose.
    /// Used by the P539c per-side repro to contrast one bot's belief against the
    /// server's authoritative view and find a side-dependent divergence.</summary>
    public sealed record WorldShipDiag(short ShipId, short Sector, double[] Pos, long LastTick, bool Visible);
    public sealed record WarpDiag(short Id, short Cluster, double[] Pos, short Radius, short Dest);
    public sealed record BotWorldDiag(
        string Name, short ShipId, short Side, short Sector,
        double[] Pos, int Target, long OwnLastTick,
        List<WorldShipDiag> CrossSideShips,
        List<WarpDiag> SectorWarps,
        List<WarpDiag> AllWarps);

    public IReadOnlyList<BotWorldDiag> DiagnoseWorlds()
    {
        var outList = new List<BotWorldDiag>();
        ActiveBot[] snap;
        lock (_gate) snap = _bots.ToArray();
        foreach (var bot in snap)
        {
            if (bot.Cs is not { } cs) continue;
            var d = new BotWorldDiag(bot.Name, bot.ShipId, bot.Side, -1, [0, 0, 0],
                bot.TargetId ?? -1, -1, new List<WorldShipDiag>(), new List<WarpDiag>(), new List<WarpDiag>());
            lock (cs.Gate)
            {
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var me))
                {
                    d = d with { Sector = me.SectorId, OwnLastTick = me.LastUpdateTick };
                    if (me.Latest is { } mu) d = d with { Pos = [mu.Position.X, mu.Position.Y, mu.Position.Z] };
                }
                foreach (var kv in cs.World.Ships)
                {
                    if (kv.Key == bot.ShipId) continue;
                    if (kv.Value.SideId == bot.Side) continue;
                    var e = kv.Value;
                    var p = e.Latest is { } lu ? new double[] { lu.Position.X, lu.Position.Y, lu.Position.Z } : new double[] { 0, 0, 0 };
                    d.CrossSideShips.Add(new WorldShipDiag(kv.Key, e.SectorId, p, e.LastUpdateTick, !e.IsNotVisible));
                }
                foreach (var w in cs.World.Warps.Values)
                {
                    var p = w.Position is { Length: 3 } fp ? new double[] { fp[0], fp[1], fp[2] } : new double[] { 0, 0, 0 };
                    var wd = new WarpDiag(w.WarpId, w.ClusterId, p, w.Radius, w.DestinationId);
                    if (w.ClusterId == d.Sector) d.SectorWarps.Add(wd);
                    d.AllWarps.Add(wd);
                }
            }
            outList.Add(d);
        }
        return outList;
    }

    // ── P539d — per-bot transit / arrival / death telemetry ─────────────────

    /// <summary>One combat bot's P539d fair-contact telemetry row: launch clock,
    /// arrival clock (first time in the target sector, travel mode), warp hops,
    /// and the death ledger (total / gunfire / collision-other) accumulated over
    /// the session. Read from the bot's live state on demand.</summary>
    public sealed record BotTransitRow(
        string Name, short Side, double LaunchedAt, double? ArrivedAt, int Hops,
        IReadOnlyList<(double T, short From, short To)> WarpHops,
        int Deaths, int GunfireDeaths, int CollisionDeaths);

    /// <summary>P553 — one dodge-law fire (the shared native law returned an
    /// imminent collision and a dodge goal): the bot, its sector, the wall-clock
    /// (UTC, seconds since epoch — the same clock a proof timestamps deaths
    /// with), the bot's position and the dodge goal it steered to instead.
    /// Read-only snapshot of <see cref="ActiveBot.DodgeFires"/>.</summary>
    public sealed record BotDodgeFire(
        short ShipId, short Sector, double WallUtc, Vector3 MyPos, Vector3 Goal);

    /// <summary>P553 — snapshot every bot's dodge-fire trace (locked, non-
    /// destructive) so a live proof can correlate collision deaths with active
    /// dodging. Sorted by wall clock.</summary>
    public IReadOnlyList<BotDodgeFire> DodgeTrace()
    {
        var all = new List<BotDodgeFire>();
        lock (_gate)
        {
            foreach (var b in _bots)
            {
                if (b.DodgeFires.Count == 0) continue;
                lock (b.DodgeGate) all.AddRange(b.DodgeFires);
            }
        }
        all.Sort((x, y) => x.WallUtc.CompareTo(y.WallUtc));
        return all;
    }

    public IReadOnlyList<BotTransitRow> TransitRows()
    {
        var rows = new List<BotTransitRow>();
        lock (_gate)
            foreach (var b in _bots)
                if (b.Cs is not null)
                    rows.Add(new BotTransitRow(b.Name, b.Side, b.LaunchedAt, b.ArrivedAt, b.WarpHops.Count,
                        b.WarpHops.ToArray(), b.Deaths, b.GunfireDeaths, b.CollisionDeaths));
        return rows;
    }

    // ── Real update-cadence tracking (P430) ───────────────────────────────
    //
    // "Teleporting" is a real symptom of the server's per-tick broadcast
    // falling behind at higher connection counts: the real wall-clock GAP
    // between successive real position updates grows, and/or the real DISTANCE
    // moved per update grows. Rather than reasoning about it, the Loop() tick
    // records a real (interval, distance) sample each time a bot's live
    // server-reported position actually changes, and get_perf_stats aggregates
    // those samples into mean / p95 / worst-offender numbers — measured
    // evidence for the concurrency sweet spot.

    /// <summary>Read a bot's CURRENT real position from the same authoritative
    /// source <c>list_active_bots</c> uses (the probe's world for a same-side
    /// bot; the bot's own ClientWorld for a cross-side combat bot).</summary>
    private bool TryCurrentPose(ActiveBot bot, out Vector3 pos)
    {
        short shipId = ShipIdOf(bot);
        if (TryProbePose(shipId, out pos, out _)) return true;
        if (bot.Cs is { } cs)
        {
            lock (cs.Gate)
                if (cs.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
                { pos = lu.Position; return true; }
        }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>Record ONE real cadence sample for a bot when its live position
    /// has changed since the last observation: the elapsed wall-clock seconds
    /// since the previous change (the real update interval the server is
    /// delivering to this bot's connection right now) and the real distance
    /// moved (meters). Kept in a bounded rolling list (see
    /// <see cref="MaxSamplesPerBot"/>) so long runs don't leak.</summary>
    private void SampleCadence(ActiveBot bot, double now)
    {
        if (!TryCurrentPose(bot, out var pos)) return;
        bool changed = bot.HasPos
            && (pos.X != bot.LastPos.X || pos.Y != bot.LastPos.Y || pos.Z != bot.LastPos.Z);
        if (changed)
        {
            double interval = now - bot.LastPosAt;
            double dist = (pos - bot.LastPos).Length();
            lock (bot.SampleGate)
            {
                bot.Samples.Add(new CadenceSample(interval, dist));
                if (bot.Samples.Count > MaxSamplesPerBot)
                    bot.Samples.RemoveRange(0, bot.Samples.Count - MaxSamplesPerBot);
            }
        }
        bot.LastPos = pos;
        bot.LastPosAt = now;
        bot.HasPos = true;
    }

    /// <summary>Aggregate every tracked bot's rolling cadence samples into a real
    /// summary (optionally restricted to one bot by name substring): the number
    /// of contributing bots, and for update INTERVAL and JUMP DISTANCE each the
    /// pooled mean, nearest-rank p95 and the single worst offender (bot name +
    /// its worst single-sample value). Pure math over the recorded samples — no
    /// modeling, no guessing.</summary>
    public PerfStats GetPerfStats(string? botFilter)
    {
        var cadence = new List<BotCadence>();
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                if (botFilter is { Length: > 0 } && !bot.Name.Contains(botFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                CadenceSample[] arr;
                lock (bot.SampleGate) arr = bot.Samples.ToArray();
                cadence.Add(new BotCadence(bot.Name, arr));
            }
        }
        return ComputePerf(cadence);
    }

    // ── P539: real per-bot combat stats (shots / ship hits / kills / pods) ──

    /// <summary>P539 — one combat bot's REAL cumulative combat statistics,
    /// tallied from its own wire/world events, ALL attributed to this bot as the
    /// shooter (shots = real fired rounds; hits = the ones IT caused), plus its
    /// lifepod status. <see cref="BotCombatRow.HitRate"/> is enemy ship-hits ÷
    /// real shots-fired.</summary>
    public readonly record struct BotCombatRow(
        string Name, short Side, long ShotsFired, long ShipHits,
        long EnemyShieldHits, long EnemyHullHits, long FriendlyHits, long NoneHits,
        long Kills, double HitRate, bool IsPod, bool PodDocked);

    /// <summary>P539b — the per-side / overall aggregate of the combat stats and
    /// the lifepod outcome counts (pods that ever formed, reached a station, or
    /// were still lingering as pods). All hits are attributed to the shooter.</summary>
    public readonly record struct CombatSummary(
        IReadOnlyList<BotCombatRow> Bots,
        long ShotsFired, long ShipHits, long Kills, double OverallHitRate,
        long FriendlyHits, long EnemyShieldHits, long EnemyHullHits,
        int PodsCreated, int PodsDocked, int PodsLingering);

    /// <summary>P539 — snapshot every bot's real combat stats (and per-side /
    /// overall aggregates + lifepod outcome counts) for get_perf_stats / the
    /// scenario summary.</summary>
    public CombatSummary GetCombatSummary()
    {
        var rows = new List<BotCombatRow>();
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                if (bot.Cs is null) continue;   // combat bots only (idle/weave have no world)
                double hr = bot.ShotsFired > 0 ? (double)bot.ShipHits / bot.ShotsFired : 0.0;
                rows.Add(new BotCombatRow(
                    bot.Name, bot.Side, bot.ShotsFired, bot.ShipHits,
                    bot.EnemyShieldHits, bot.EnemyHullHits, bot.FriendlyHits, bot.NoneHits,
                    bot.Kills, hr, bot.IsPod, bot.PodDocked));
            }
        }
        long shots = rows.Sum(r => r.ShotsFired), hits = rows.Sum(r => r.ShipHits), kills = rows.Sum(r => r.Kills);
        return new CombatSummary(
            rows, shots, hits, kills,
            shots > 0 ? (double)hits / shots : 0.0,
            rows.Sum(r => r.FriendlyHits),
            rows.Sum(r => r.EnemyShieldHits),
            rows.Sum(r => r.EnemyHullHits),
            rows.Count(r => r.IsPod || r.PodDocked) /* pods created ≈ any ever-pod */,
            rows.Count(r => r.PodDocked),
            rows.Count(r => r.IsPod && !r.PodDocked));
    }

    /// <summary>Pure, unit-testable aggregation of a set of bots' rolling cadence
    /// samples into per-metric statistics (mean, nearest-rank p95, worst
    /// offender). Shares the same pool of samples a live <c>get_perf_stats</c>
    /// call is built from, so the math can be proven against a synthetic sample
    /// set with no server involved.</summary>
    public static PerfStats ComputePerf(IEnumerable<BotCadence> bots)
    {
        var intervals = new List<double>();
        var jumps = new List<double>();
        string worstIntervalBot = "";
        string worstJumpBot = "";
        double worstInterval = 0, worstJump = 0;
        bool haveInterval = false, haveJump = false;
        int contributing = 0;

        foreach (var b in bots)
        {
            if (b.Samples.Count == 0) continue;
            contributing++;
            double bi = double.NegativeInfinity, bj = double.NegativeInfinity;
            foreach (var s in b.Samples)
            {
                intervals.Add(s.Interval);
                jumps.Add(s.Distance);
                if (s.Interval > bi) bi = s.Interval;
                if (s.Distance > bj) bj = s.Distance;
            }
            if (!haveInterval || bi > worstInterval) { worstInterval = bi; worstIntervalBot = b.Name; haveInterval = true; }
            if (!haveJump || bj > worstJump) { worstJump = bj; worstJumpBot = b.Name; haveJump = true; }
        }

        return new PerfStats(
            contributing,
            Metric(intervals, worstIntervalBot),
            Metric(jumps, worstJumpBot));
    }

    /// <summary>Reduce a pooled value list to a mean / nearest-rank p95 / worst
    /// value, paired with the offender's name. Empty input yields a clean zero
    /// summary (no NaN / no divide-by-zero).</summary>
    private static MetricStat Metric(List<double> values, string worstBot)
    {
        if (values.Count == 0) return new MetricStat(0, 0, "", 0);
        values.Sort();
        double mean = values.Sum() / values.Count;
        double p95 = values[(int)Math.Min(values.Count - 1, Math.Max(0, Math.Ceiling(0.95 * values.Count) - 1))];
        return new MetricStat(mean, p95, worstBot, values[^1]);
    }

    // ── P432: run_hull_matrix — per-combination execution + lifecycle ────────
    //
    // The matrix tool's inner orchestration. One call runs ONE real (hull,
    // weapon, size) combination and returns its real captured stats. It spawns
    // `size` real 'combat' bots per side with that exact hull+weapon loadout,
    // lets the background behavior loop fight them for a real observation
    // window, captures real combat outcomes (via the same KILL_SHIP / ClientWorld
    // mechanism vizfight decodes) + the real per-bot update cadence, then CLEARS
    // exactly the bots it spawned — each matrix bot triggering its own real
    // disconnect (closing its ConnectionSession = a real peer logoff from the
    // server) — so the next combination starts from a clean board. This disposal
    // is self-contained to the matrix's own loop; spawn_bot / spawn_scenario
    // remain exactly as spawn-and-forget as before, and no general-purpose
    // despawn tool is exposed.

    /// <summary>One real (hull, weapon, size) combination's captured result:
    /// per-side spawn / kill / survivor counts (kills = real KILL_SHIP(105)
    /// events whose victim was one of this combination's own ships, the same
    /// mechanism vizfight decodes), time-to-first-kill from the combo start
    /// (null when no kill was observed in the window), the pooled real
    /// update-cadence stats (<see cref="PerfStats"/>) across this combo's bots,
    /// and (P473) the real granted hull per spawned bot + how many got a
    /// partial-grant warning.</summary>
    public readonly record struct MatrixComboResult(
        string HullName,
        string WeaponName,
        int Size,
        int SpawnedA,
        int KilledA,
        int SurvivedA,
        int SpawnedB,
        int KilledB,
        int SurvivedB,
        double? TimeToFirstKill,
        PerfStats Perf,
        IReadOnlyList<short> GrantedHullIds,
        int PartialGrants);

    /// <summary>Run one real combination against the live server: spawn
    /// <paramref name="size"/> 'combat' bots per side (the server's first two
    /// real sides) with the given hull + weapon loadout, observe them for
    /// <paramref name="observeSeconds"/> real seconds while the background
    /// behavior loop drives the combat, capture the real combat outcomes and
    /// update cadence, then clear exactly the bots it spawned so the next
    /// combination starts clean. Any genuine per-spawn failure aborts this combo
    /// (rolling back the bots already spawned) as a specific error — never a
    /// silent partial run.</summary>
    public MatrixComboResult RunCombination(
        short hullId, string hullName, short weaponPartId, string weaponName,
        int size, short? targetSector, double observeSeconds,
        short? launcherPartId = null, string? launcherRequestName = null)
    {
        if (!ServerReady)
            throw new InvalidOperationException("run_hull_matrix: server probe not connected/validated — cannot run a combination");
        var sides = ValidSides();
        if (sides.Count < 2)
            throw new InvalidOperationException($"run_hull_matrix: a combination needs two real sides; this server has: {string.Join(", ", sides)}");
        short sideA = sides[0], sideB = sides[1];
        // P433 — an optional real missile launcher rides the loadout, gated
        // against this hull's real capability mask (gun-only when it does not
        // fit, reported, never fabricated).
        var parts = Catalog.PartsForWeapon(weaponPartId, launcherPartId, hullId, hullName, launcherRequestName ?? "", out var launcherNote);
        if (launcherNote is not null)
            Console.WriteLine($"[p433] run_hull_matrix {hullName}+{weaponName} size {size}: {launcherNote}");

        // Spawn the whole combination (size per side). Names are unique, so we can
        // both attribute kills back to a side and clear exactly these bots later.
        var roster = new List<(string Name, short ShipId, short Side)>();
        // P473 — the REAL hull each spawned bot was granted + how many got a
        // partial-grant warning, surfaced in the per-combination row.
        var grantedHulls = new List<short>();
        int partialGrants = 0;
        double start = Now();
        try
        {
            for (int i = 0; i < size; i++)
            {
                var ra = SpawnBot(hullId, hullName, sideA, parts, "combat", targetSector, launcherPartId, launcherRequestName);
                roster.Add((ra.Name, ra.ShipId, ra.Side));
                grantedHulls.Add(ra.Granted.HullId);
                if (ra.Warning is not null) partialGrants++;
                var rb = SpawnBot(hullId, hullName, sideB, parts, "combat", targetSector, launcherPartId, launcherRequestName);
                roster.Add((rb.Name, rb.ShipId, rb.Side));
                grantedHulls.Add(rb.Granted.HullId);
                if (rb.Warning is not null) partialGrants++;
            }
        }
        catch (Exception ex)
        {
            ClearBots(roster.Select(r => r.Name).ToList());   // roll back a partial spawn
            throw new InvalidOperationException(
                $"run_hull_matrix: combo {hullName}+{weaponName} size {size} failed to spawn fully after {roster.Count} bot(s): {ex.Message}");
        }

        // Observe the real fight for the fixed window: poll every real 250ms for
        // KILL_SHIP events on any of this combo's own ships. The background
        // behavior loop does the driving; this thread only watches and times.
        var myIds = new HashSet<short>(roster.Select(r => r.ShipId));
        var killed = new HashSet<short>();
        double? firstKillSec = null;
        var deadline = start + observeSeconds;
        while (Now() < deadline)
        {
            ScanComboKills(myIds, killed, ref firstKillSec, start);
            Thread.Sleep(250);
        }

        int spawnedA = roster.Count(r => r.Side == sideA);
        int spawnedB = roster.Count(r => r.Side == sideB);
        int killedA = roster.Count(r => r.Side == sideA && killed.Contains(r.ShipId));
        int killedB = roster.Count(r => r.Side == sideB && killed.Contains(r.ShipId));

        var perf = ComboPerf(roster.Select(r => r.Name).ToList());

        // Clear exactly this combination's bots (each triggers its own real
        // disconnect) before the caller runs the next combination.
        ClearBots(roster.Select(r => r.Name).ToList());

        return new MatrixComboResult(
            hullName, weaponName, size,
            spawnedA, killedA, spawnedA - killedA,
            spawnedB, killedB, spawnedB - killedB,
            firstKillSec, perf, grantedHulls, partialGrants);
    }

    /// <summary>Poll every combat bot this combination spawned for real
    /// KILL_SHIP(105) events whose victim is one of the combination's own ships —
    /// the exact same kill mechanism vizfight decodes (each real connected peer
    /// receives the server's same-sector KILL_SHIP broadcasts via its own
    /// ClientWorld). Dedupe by victim ship id; latch the first kill's real
    /// wall-clock offset. A victim ejected-but-pod-alive is NOT (yet) a kill —
    /// faithful to the P107 eject mechanic (a player ship must be a lifepod / be
    /// finished as a pod before it produces a real KILL_SHIP).</summary>
    private void ScanComboKills(HashSet<short> myIds, HashSet<short> killed, ref double? firstKillSec, double start)
    {
        ActiveBot[] snap;
        lock (_gate) snap = _bots.Where(b => b.Cs is not null).ToArray();
        foreach (var bot in snap)
        {
            KillShip[] kills;
            lock (bot.Cs!.Gate) kills = bot.Cs.World.Events.Kills.ToArray();
            foreach (var k in kills)
            {
                if (!myIds.Contains(k.ShipId)) continue;   // a kill of OUR bot only
                if (killed.Add(k.ShipId))
                    firstKillSec ??= Now() - start;
            }
        }
    }

    /// <summary>Aggregate the real update-cadence samples of exactly this
    /// combination's bots (the same pure math get_perf_stats uses).</summary>
    private PerfStats ComboPerf(IReadOnlyCollection<string> names)
    {
        var cadence = new List<BotCadence>();
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                if (!names.Contains(bot.Name)) continue;
                CadenceSample[] arr;
                lock (bot.SampleGate) arr = bot.Samples.ToArray();
                cadence.Add(new BotCadence(bot.Name, arr));
            }
        }
        return ComputePerf(cadence);
    }

    /// <summary>
    /// P432 — remove + dispose EXACTLY the named bots, so the hull/weapon matrix
    /// can clear one combination's own bots before the next. Each removed combat
    /// bot triggers its own real disconnect (closing its ConnectionSession = a
    /// real peer logoff from the server); idle/weave plain clients are disposed
    /// the same way. Self-contained to the matrix orchestration: it is NOT
    /// exposed as a general-purpose despawn tool, and spawn_bot / spawn_scenario
    /// callers are unaffected (their bots are simply never handed to this).
    /// </summary>
    private void ClearBots(IReadOnlyCollection<string> names)
    {
        if (names is null || names.Count == 0) return;
        var nameSet = new HashSet<string>(names);
        ActiveBot[] toDispose;
        lock (_gate)
        {
            toDispose = _bots.Where(b => nameSet.Contains(b.Name)).ToArray();
            _bots.RemoveAll(b => nameSet.Contains(b.Name));
        }
        foreach (var bot in toDispose)
        {
            try { bot.Bot?.Dispose(); } catch { }
            try { bot.Cs?.Dispose(); } catch { }
        }
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
        lock (_commanders)
        {
            foreach (var c in _commanders.Values) { try { c.Dispose(); } catch { } }
            _commanders.Clear();
        }
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
        // P539 — the bot's LEAD weapon part id (the first real weapon in its
        // loadout), used by the fire loop to read the real projectile
        // ballistics it leads with. null = unarmed (no lead/fire).
        public short? WeaponPartId;
        // P539 — lifepod state: whether the bot's own ship is currently a pod,
        // the home station it chose, and whether the pod reached (docked at) a
        // station — the live "pods reached home vs lingered" measure.
        public bool IsPod;
        public bool EverPod;                         // has this bot ever become a pod
        public short PodStationId = -1;
        public short PodStationSector = -1;
        public Vector3 PodStationCenter;
        public bool PodDocked;
        public short PodDockStationId = -1;
        // P539b — REAL combat stats, all attributed to THIS bot as the shooter:
        // ShotsFired = this bot's OWN fired rounds (PROJ_INST echoes carrying its
        // ship as the launcher), NOT a fire decision; ShipHits = the damaging
        // hits on ENEMY ships THIS bot caused (the meaningful hit-rate
        // numerator); plus finer splits by the struck target's side (friendly =
        // friendly fire, none = asteroid / no ship) and by shield vs hull.
        public long ShotsFired;
        public long ShipHits;            // enemy damaging hits by this bot
        public long EnemyShieldHits;     // …of those, absorbed by the target's shield
        public long EnemyHullHits;       // …of those, hull-or-worse
        public long FriendlyHits;        // friendly-fire hits by this bot
        public long NoneHits;            // hits by this bot on a no-side/no-ship target (-1)
        public long Kills;
        public int KillCursor;
        // P433 — the real missile launcher this bot carries (null = gun-only),
        // the real inter-shot readiness delay of its missile, and the wall-clock
        // of the last SendFireMissile() (paces firing like the guns' refire).
        public short? LauncherPartId;
        public double MissileReadyTime;
        public double LastFireAt = double.NegativeInfinity;
        public short? TargetSector;                // rendezvous sector, or null to engage in place
        public List<short> Path = new();
        public short PathStart = -1;
        public short PathGoal = -1;
        public short LastCluster = -1;
        public Dictionary<short, short>? WarpDest;

        // ── real update-cadence tracking (P430) ──
        // Mirrors the WeavePhase/Freq/Amp inline-field style: a small, bounded
        // per-bot ring of REAL (interval, distance) samples, recorded whenever
        // the bot's live server-reported position actually changes, so
        // get_perf_stats can answer "is this bot's update stream thinning /
        // teleporting?" with measured evidence instead of a guess. `SampleGate`
        // guards the list (loop thread writes it, get_perf_stats reads it).
        public bool HasPos;                  // a live position has been observed at least once
        public Vector3 LastPos;              // last-observed real position
        public double LastPosAt;             // wall-clock time of the last observed change
        public readonly object SampleGate = new();
        public List<CadenceSample> Samples = new();

        // ── accepted-order bookkeeping (P431) — inline-field style like the rest ──
        // The loadout this bot launched with (re-used when an idle bot is upgraded
        // to a combat client).
        public IReadOnlyList<ExpandedPartData>? Parts;
        // P473 — the loadout the server actually granted at spawn (from the real
        // BUY_LOADOUT_ACK) and any partial-grant warning (null = full grant).
        // Kept so an upgraded/listed bot retains the honest granted record.
        public LoadoutGrant Granted;
        public string? GrantWarning;
        // A BotClass bot's current pending queued order, folded from its
        // QUEUED_COMMAND_UPDATE(215) accumulator (Cs bots read the live
        // cs.World.MyQueuedCommand instead); QcuConsumed is the fold cursor.
        public QueuedCommandState? CurrentQueued;
        public int QcuConsumed;
        // The queued order id this bot has already accepted (prevents re-accepting
        // while the server clears the slot); reset when the slot empties.
        public short AcceptedQueuedId = OrderChange.CidNone;
        // The specific enemy ship this bot is under a real accepted teammate order
        // to engage (null = doing its own spawn-time behavior). Distinct from the
        // spawn-time `Behavior` and from `TargetId` (the current live engagement).
        public short? OrderedTargetId;
        // P474 — true while this bot is owned by an active run_scenario preset's
        // controller thread; the generic Loop then leaves it alone entirely.
        public bool InScenario;

        // ── P539d — fair contact: transit/arrival telemetry ──
        public double LaunchedAt;                       // wall-clock Now() at spawn
        public List<(double T, short From, short To)> WarpHops = new();  // real cluster transitions in transit
        public double? ArrivedAt;                       // first time in the target sector (travel mode)
        public int Deaths;                              // times this bot's ship ejected (became a lifepod)
        public int GunfireDeaths;                       // of those, killed by a credited ship shooter
        public int CollisionDeaths;                     // of those, eject without a credited shooter (ram/other)

        // ── P553 — dodge-fire telemetry ──
        // Every tick where the SHARED native dodge law (<see cref="TryDodgeGoal"/>)
        // fired for this bot while navigating (transit/stand-off/engage), recorded
        // so a live proof can correlate a collision death with whether the bot was
        // actively dodging at the time. Guarded by <see cref="DodgeGate"/> (the
        // BotManager loop thread writes, a proof reads). Non-behavioural.
        public readonly object DodgeGate = new();
        public List<BotDodgeFire> DodgeFires = new();

        // ── P539d — travel-with-staging mode ──
        public bool UseStaging;                         // gather the whole side before advancing
        public byte StagePhase;                         // 0 transit-to-stage, 1 holding, 2 released/advanced
        public short StagingSector = -1;                // the last sector before the rendezvous
        public double? FirstStagingAt;                  // when this bot first began holding
        public double? ReleasedAt;                      // when the side released it to advance

        // ── P539d — arena mode ──
        public bool Arena;                              // starts engaged in the target sector (mirrored spots)
        public Vector3 ArenaPosition;                   // mirrored arena spot
        public Vector3 ArenaFacing;                     // facing the opposing side's mirrored spot
    }
}

/// <summary>P430 — one real cadence observation for a bot: the wall-clock
/// interval in seconds since its previous position update, and the real
/// distance (meters) it moved in that update.</summary>
public readonly record struct CadenceSample(double Interval, double Distance);

/// <summary>P430 — a bot's bounded rolling cadence sample list, as fed to
/// <see cref="BotManager.ComputePerf"/> for aggregation.</summary>
public readonly record struct BotCadence(string Name, IReadOnlyList<CadenceSample> Samples);

/// <summary>P430 — one metric's aggregate over all pooled samples: the mean,
/// the nearest-rank p95, and the single worst offender (its name + its worst
/// single-sample value).</summary>
public readonly record struct MetricStat(double Mean, double P95, string WorstBot, double WorstValue);

/// <summary>P430 — the aggregate concurrency picture across every contributing
/// bot: how many bots yielded samples, plus interval and jump-distance
/// statistics. Produced by <see cref="BotManager.ComputePerf"/>.</summary>
public readonly record struct PerfStats(int BotCount, MetricStat Interval, MetricStat Jump);

/// <summary>P431 — one bot tick's accept-and-translate decision (see
/// <see cref="BotManager.DecideAcceptTranslate"/>): whether the bot must send a
/// real <c>ORDER_CHANGE</c> ACCEPT this tick, and which enemy ship (if any) the
/// bot is now committed to engage (<see cref="OrderedTarget"/> = null means the
/// bot is doing its own spawn-time behavior).</summary>
public readonly record struct OrderDecision(bool ShouldSendAccept, short? OrderedTarget);
