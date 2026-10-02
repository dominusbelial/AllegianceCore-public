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
    private readonly Thread _loopThread;
    private int _nameCounter;
    private bool _startedLoop;

    /// <summary>P430 — per-bot bounding cap on the rolling cadence sample list
    /// (real (interval, distance) observations), so an unbounded run of the
    /// behavior loop never leaks memory.</summary>
    public const int MaxSamplesPerBot = 200;

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
    public SpawnResult SpawnBot(short hullId, string hullName, short? side, IReadOnlyList<ExpandedPartData> parts, string behavior, short? targetSector = null, short? launcherPartId = null, string? launcherRequestName = null)
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
                LauncherPartId = launcherPartId,
                MissileReadyTime = missileReady,
                Granted = granted,
                GrantWarning = grantWarning,
            };
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

    /// <summary>[vz EngageEnemy's engagement body] Point the bot at
    /// <paramref name="targetId"/> via a real cross-side ORDER_CHANGE target
    /// acquisition (re-armed every ~3s) and hold AllWeapons with the periodic
    /// afterburner/cloak beats — shared by both the nearest-enemy path and the
    /// P431 commanded-target path.</summary>
    private static void EngageShipAt(ActiveBot bot, ConnectionSession cs, short targetId, double now)
    {
        if (now - bot.LastTargetAt > 3)
        {
            cs.SendOrderChange(bot.ShipId, OrderChange.CmdQueued, OrderChange.CmdCurrent, IgcObjectType.Ship, targetId);
            bot.LastTargetAt = now;
        }
        Vector3 tp = Vector3.Zero;
        lock (cs.Gate)
            if (cs.World.Ships.TryGetValue(targetId, out var te) && te.Latest is { } tlu) tp = tlu.Position;

        bool boost = now - bot.LastBoostAt < 4;
        if (now - bot.LastBoostAt > 11) { bot.LastBoostAt = now; boost = true; }
        bool cloak = now - bot.LastCloakAt < 4;
        if (now - bot.LastCloakAt > 18) { bot.LastCloakAt = now; cloak = true; }
        int stateM = ShipControlState.AllWeapons
                     | (boost ? ShipControlState.AfterburnerButton : 0)
                     | (cloak ? ShipControlState.CloakActive : 0);
        CombatFlyTo(bot, tp, 40f, 220f, stateM);

        MaybeFireMissile(bot, cs, now);
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
            // P433-diagnostic (throttled): reveal WHY a transit bot is stalled —
            // missing cluster graph, empty path, or no warp found — so a live
            // co-location/fire demo can be set up instead of guessing.
            if (now - bot.LastNarrateAt > 20)
            {
                bot.LastNarrateAt = now;
                lock (cs.Gate)
                {
                    bool graphOk = cs.World.Graph is not null;
                    int nWarps = cs.World.Warps.Count;
                    short nextHop = CombatNextHop(bot, cur);
                    bool destMap = TryBuildWarpDestMap(cs, out var dm);
                    bool warpFound = nextHop != cur && destMap && TryFindWarpTo(cs, cur, nextHop, dm, out _);
                    Console.WriteLine($"[p433-diag] bot {bot.Name} cluster {cur} goal {goal} path[{bot.Path.Count}] next={nextHop} graph={graphOk} warps={nWarps} destMap={destMap} warpFound={warpFound}");
                }
            }
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
