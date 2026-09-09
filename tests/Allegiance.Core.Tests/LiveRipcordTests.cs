using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P111 — the ripcord request/activate/deny/abort handshake, the port of
/// <c>FedSrv.CPP:7715-7728</c> <c>case FM_C_RIPCORD_REQUEST</c> and
/// <c>FedSrvSiteBase::RequestRipcord</c> (<c>FedSrv.CPP:13319-13378</c>).
/// What happens when the countdown reaches zero (the real teleport) is a
/// separate follow-up and is <b>not</b> exercised here.
///
/// Topology (measured from XXV_simple, dumped in the P111 report): the two
/// real stations are side 0's garrison in sector 1053 and side 1's in 1061.
/// For deterministic anchors the tests inject synthetic side-0,
/// <c>c_sabmRipcord</c>-capable stations into sectors 1054 (Deucalion) and
/// 1055 (Eris); the requester flies in 1052 (Borasisi), which has no station
/// (so the ships stay flying and never dock), and 1056 (Farout) is the
/// anchor-less deny target.
///
/// Scenarios (real loopback Bot against the in-process server, sequential in
/// one session to prove the state transitions and the exact broadcast scope):
///   (1) first activation → RIPCORD_ACTIVATE broadcast sector-wide (both bots);
///   (2) re-target to a different sector while active → RIPCORD_ACTIVATE to the
///       requester ONLY — the first-vs-re-target broadcast distinction;
///   (3) request to an anchor-less sector → RIPCORD_DENIED requester-only;
///   (4) cancel (sidRipcord = NA) while active → RIPCORD_ABORTED sector-wide;
///   (5) silent gates: docked / boarded / c_habmNoRipcord hull → ignored.
/// The four codecs round-trip byte-exact in <see cref="Wire.RipcordWireTests"/>.
/// </summary>
[Collection("LiveNetwork")]
public class LiveRipcordTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    // Selected sectors in XXV_simple (measured, not guessed — see the report).
    private const short FlySector = 1052;   // Borasisi — no station, ships stay flying
    private const short TargetA = 1054;     // Deucalion — gets an injected side-0 anchor
    private const short TargetB = 1055;     // Eris      — gets an injected side-0 anchor
    private const short NoAnchorSector = 1056; // Farout — no anchor anywhere reachable

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Ripcord Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 611u, DwCookie = 0x611u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7799000000000000000ul, DrmHash = "rip",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    /// <summary>Reflection-free helper note: the Core assembly does not expose
    /// <c>Sector.AttachStations</c> to tests, so synthetic-station injection
    /// walks the private <c>_stations</c> list directly (same technique as
    /// P110's <c>AddStationToSector</c>). Test-only, on the sim thread
    /// (ShipSpawner runs during the join), safe because the server disposes
    /// afterward.</summary>
    private static void AddStationToSector(Sector sector, DataStationIGC station)
    {
        var field = typeof(Sector).GetField("_stations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var list = (List<DataStationIGC>)field.GetValue(sector)!;
        list.Add(station);
    }

    private sealed class RipcordSpawn
    {
        public Ship Requester = null!, Bystander = null!;
        public DataStationIGC AnchorA;   // the injected side-0 anchor in TargetA
        public DataStationIGC AnchorB;   // the injected side-0 anchor in TargetB
    }

    /// <summary>Two-ship, one-side, both-FLYING spawn injection plus synthetic
    /// anchors. The first spawned ship is the requester, the second the
    /// bystander; both are placed in sector <see cref="FlySector"/> (which has
    /// no station, so they never dock and stay flying). Synthetic side-0
    /// <c>c_sabmRipcord</c> stations are injected into sectors
    /// <see cref="TargetA"/> and <see cref="TargetB"/>.</summary>
    private static RipcordSpawn InjectRipcordSpawn(Srv server)
    {
        var sp = new RipcordSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var sectorA = mission.Sectors.First(s => s.Id == TargetA);
            var sectorB = mission.Sectors.First(s => s.Id == TargetB);
            if (n == 0)
            {
                // Pick a real station type that can be a ripcord anchor.
                var anchorType = core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
                sp.AnchorA = new DataStationIGC(
                    Position: [1000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                    ClusterId: TargetA, SideId: 0, StationId: 8290, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "AnchA");
                sp.AnchorB = new DataStationIGC(
                    Position: [1000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                    ClusterId: TargetB, SideId: 0, StationId: 8291, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "AnchB");
                AddStationToSector(sectorA, sp.AnchorA);
                AddStationToSector(sectorB, sp.AnchorB);
            }

            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            // The requester's own hull must NOT be no-ripcord, else the gate
            // would silently swallow the request and the scenario tests fail
            // confusingly. Assert rather than assume.
            Assert.False(hull.HasCapability(DataHullTypeIGC.HabmNoRipcord),
                $"test hull {hull.HullId} has c_habmNoRipcord — cannot exercise the active handshake");
            var ship = new Ship(shipId, hull, new Vector3(300f + n * 10f, 620f, 880f), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
            mission.PlaceShip(ship, fly);

            if (n == 0) sp.Requester = ship; else sp.Bystander = ship;
            n++;
            return (ship, fly);
        };
        return sp;
    }

    /// <summary>Boot a two-bot server with the injected spawn; both bots are
    /// in the flying sector on side 0.</summary>
    private static (Srv server, FltBot requester, FltBot bystander, RipcordSpawn sp)
        Setup(out short requesterId, out short bystanderId)
    {
        var server = new Srv();
        var sp = InjectRipcordSpawn(server);
        server.Boot(Config());

        var a = new FltBot(); var b = new FltBot();
        a.Connect(server.BoundPort, "REQR"); b.Connect(server.BoundPort, "BYST");
        a.SendLogonRequest(Request("REQR")); b.SendLogonRequest(Request("BYST"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        requesterId = a.ShipAssignment!.Ship.ShipId;
        bystanderId = b.ShipAssignment!.Ship.ShipId;
        // Both must be placed flying in the fly sector (never dock).
        Assert.Null(ServerShip(server, requesterId).StationId);
        Assert.Null(ServerShip(server, bystanderId).StationId);
        Assert.Equal(FlySector, ServerShip(server, requesterId).Sector!.Id);
        Assert.Equal(FlySector, ServerShip(server, bystanderId).Sector!.Id);
        return (server, a, b, sp);
    }

    private static void ResetRipcord(FltBot bot)
    {
        bot.ResetRipcordActivates();
        bot.ResetRipcordDenieds();
        bot.ResetRipcordAborteds();
    }

    /// <summary>⭐ Scenario 1 — first activation broadcasts RIPCORD_ACTIVATE(185)
    /// to <b>everyone</b> flying in the requester's sector, naming the correct
    /// anchor object/type and the requested target cluster. Server-side the
    /// countdown is armed (RipcordModel set, time-left re-seeded).</summary>
    [Fact]
    public void First_activation_broadcasts_to_everyone_flying_in_sector()
    {
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        req.SendRipcordRequest(TargetA);
        int seen = PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ACTIVATE reached the requester");

        var a = Assert.Single(req.RipcordActivates);
        Assert.Equal(rid, a.ShipidRipcord);
        Assert.Equal((short)IgcObjectType.Station, a.OtRipcord);   // the anchor is a station
        Assert.Equal(sp.AnchorA.StationId, a.OidRipcord);          // the injected anchor
        Assert.Equal(TargetA, a.SidRipcord);                       // the REQUESTED cluster

        // Broadcast: the bystander (same sector) received the SAME frame.
        var b1 = Assert.Single(byst.RipcordActivates);
        Assert.Equal(a.ShipidRipcord, b1.ShipidRipcord);
        Assert.Equal(a.OtRipcord, b1.OtRipcord);
        Assert.Equal(a.OidRipcord, b1.OidRipcord);
        Assert.Equal(a.SidRipcord, b1.SidRipcord);

        // Server-side countdown state armed and re-seeded to the hull's
        // ripcord speed (ResetRipcordTimeLeft, shipIGC.h:1536-1540).
        var ship = ServerShip(server, rid);
        Assert.True(ship.IsRipcordActive);
        Assert.Equal(new Ship.RipcordTarget((short)IgcObjectType.Station, sp.AnchorA.StationId, TargetA), ship.RipcordModel);
        // P112: the countdown is now LIVE — activation seeds it to the hull's
        // RipcordSpeed, then the per-tick pass (Sector.ResolveRipcordCountdowns)
        // immediately begins ticking it down. By the time the activate reached
        // the bot (a few pumped ticks of async delivery) it has fallen strictly
        // below the seed but is still positive and active. The pre-P112 exact
        // equality (== RipcordSpeed) no longer holds once the countdown driver
        // exists — asserted as seeded-but-ticking instead.
        Assert.True(ship.RipcordTimeLeft > 0f);
        Assert.True(ship.RipcordTimeLeft <= ship.HullType.RipcordSpeed);
        Assert.Empty(req.RipcordDenieds);
        Assert.Empty(req.RipcordAborteds);

        Console.WriteLine($"RIPCORD-LIVE: ship {rid} FIRST activate -> sector {TargetA} anchor station {sp.AnchorA.StationId} broadcast to BOTH bots in {FlySector}; countdown {ship.RipcordTimeLeft}s");
    }

    /// <summary>⭐ Scenario 2 — re-target to a DIFFERENT sector while already
    /// active sends RIPCORD_ACTIVATE to the <b>requester only</b> — a real,
    /// precise first-vs-retarget broadcast distinction, asserted explicitly.</summary>
    [Fact]
    public void Re_target_activates_to_requester_only()
    {
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        // Establish a first activation first (broadcast both bots).
        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.Single(req.RipcordActivates);
        Assert.Single(byst.RipcordActivates);   // the first-activation broadcast reached the bystander

        // Re-target to a different sector while still active.
        ResetRipcord(req); ResetRipcord(byst);
        req.SendRipcordRequest(TargetB);
        int seen = PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ACTIVATE reached the requester on re-target");

        var rt = Assert.Single(req.RipcordActivates);
        Assert.Equal(rid, rt.ShipidRipcord);
        Assert.Equal((short)IgcObjectType.Station, rt.OtRipcord);
        Assert.Equal(sp.AnchorB.StationId, rt.OidRipcord);
        Assert.Equal(TargetB, rt.SidRipcord);

        // THE distinction: the bystander got NOTHING this time (requester-only).
        Assert.Empty(byst.RipcordActivates);

        // Server now anchored to the new target (re-seeded countdown, now
        // ticking down per-tick — asserted as seeded-but-ticking, see the
        // P112 note on the first-activation test).
        var ship = ServerShip(server, rid);
        Assert.Equal(new Ship.RipcordTarget((short)IgcObjectType.Station, sp.AnchorB.StationId, TargetB), ship.RipcordModel);
        Assert.True(ship.RipcordTimeLeft > 0f);
        Assert.True(ship.RipcordTimeLeft <= ship.HullType.RipcordSpeed);

        Console.WriteLine($"RIPCORD-LIVE: ship {rid} RE-TARGET -> sector {TargetB} anchor station {sp.AnchorB.StationId} requester-only (bystander silent)");
    }

    /// <summary>⭐ Scenario 3 — a request to a sector with no valid anchor
    /// answers RIPCORD_DENIED(186) to the <b>requester only</b> (bystander
    /// silent).</summary>
    [Fact]
    public void Request_with_no_anchor_denies_requester_only()
    {
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        // Fresh ship (no active ripcord): request the anchor-less sector 1056.
        req.SendRipcordRequest(NoAnchorSector);
        int seen = PumpUntil(server, () => req.RipcordDenieds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_DENIED reached the requester");
        Assert.Single(req.RipcordDenieds);
        Assert.Empty(byst.RipcordDenieds);          // requester only — no broadcast
        Assert.False(ServerShip(server, rid).IsRipcordActive);   // nothing armed

        Console.WriteLine($"RIPCORD-LIVE: ship {rid} request -> sector {NoAnchorSector} (no anchor) RIPCORD_DENIED(186) requester-only");
    }

    /// <summary>⭐ Scenario 4 — a cancel (sidRipcord = NA) while active
    /// broadcasts RIPCORD_ABORTED(187) sector-wide naming the correct ship,
    /// and clears the server-side countdown state.</summary>
    [Fact]
    public void Cancel_while_active_aborts_sector_wide()
    {
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        // Arm a ripcord first.
        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.True(ServerShip(server, rid).IsRipcordActive);

        // Cancel it.
        ResetRipcord(req); ResetRipcord(byst);
        req.SendRipcordRequest(-1);   // NA
        int seen = PumpUntil(server, () => req.RipcordAborteds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ABORTED reached the requester");

        Assert.Equal(rid, Assert.Single(req.RipcordAborteds).ShipidRipcord);
        // Broadcast: the bystander in the same sector received it too.
        Assert.Equal(rid, Assert.Single(byst.RipcordAborteds).ShipidRipcord);

        Assert.False(ServerShip(server, rid).IsRipcordActive);
        Assert.Null(ServerShip(server, rid).RipcordModel);

        Console.WriteLine($"RIPCORD-LIVE: ship {rid} cancel -> RIPCORD_ABORTED(187) broadcast to both bots in {FlySector}");
    }

    /// <summary>⭐ Scenario 5 — the silent gates. A request from a docked ship,
    /// a boarded ship, or a ship with a <c>c_habmNoRipcord</c> hull is ignored
    /// with <b>no</b> ripcord message at all (native does <c>break</c>,
    /// <c>FedSrv.CPP:7717-7721</c>). Each gate is flipped on the same live
    /// flying bot's ship in turn and verified as a no-op.</summary>
    [Fact]
    public void Gate_violations_are_silently_ignored()
    {
        var server = new Srv();
        Ship parent = null!;
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var targetA = mission.Sectors.First(s => s.Id == TargetA);
            var anchorType = core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
            if (n == 0)
            {
                var anch = new DataStationIGC(
                    Position: [1000f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
                    ClusterId: TargetA, SideId: 0, StationId: 8350, StationTypeId: anchorType, BpHull: 1, BpShield: 1, Name: "AnchC");
                AddStationToSector(targetA, anch);
            }
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            var ship = new Ship(shipId, hull, new Vector3(300f + n * 10f, 620f, 880f), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };
            mission.PlaceShip(ship, fly);
            if (n == 0) parent = ship;   // the 2nd (bot's) ship's future parent
            n++;
            return (ship, fly);
        };
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "GATES");
        bot.SendLogonRequest(Request("GATES"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        short shipId = bot.ShipAssignment!.Ship.ShipId;
        var ship = ServerShip(server, shipId);
        Assert.NotNull(parent);              // the dummy parent was spawned too
        ResetRipcord(bot);

        // (a) docked ship → request to a valid anchor sector is ignored.
        ship.StationId = 8380;
        bot.SendRipcordRequest(TargetA);
        PumpUntil(server, () => false, 200);
        Assert.Empty(bot.RipcordActivates); Assert.Empty(bot.RipcordDenieds); Assert.Empty(bot.RipcordAborteds);

        // (b) boarded ship → ignored (ParentShip set; undock first so the flying
        // gate can't hide the boarding gate — restore flying, then board).
        ship.StationId = null;
        ship.ParentShip = parent; parent.AddChildShip(ship);
        bot.SendRipcordRequest(TargetA);
        PumpUntil(server, () => false, 200);
        Assert.Empty(bot.RipcordActivates); Assert.Empty(bot.RipcordDenieds); Assert.Empty(bot.RipcordAborteds);

        // (c) hull with c_habmNoRipcord → ignored (clear the board, restore the
        // hull and clone it with the no-ripcord ability bit set).
        ship.ParentShip = null;
        var noRip = ship.HullType with { HabmCapabilities = (short)(ship.HullType.HabmCapabilities | DataHullTypeIGC.HabmNoRipcord) };
        Assert.True(noRip.HasCapability(DataHullTypeIGC.HabmNoRipcord));
        ship.ChangeHull(noRip);
        bot.SendRipcordRequest(TargetA);
        PumpUntil(server, () => false, 200);
        Assert.Empty(bot.RipcordActivates); Assert.Empty(bot.RipcordDenieds); Assert.Empty(bot.RipcordAborteds);

        // Nothing ever armed.
        Assert.False(ship.IsRipcordActive);
        Assert.Null(ship.RipcordModel);

        Console.WriteLine($"RIPCORD-GATES-LIVE: ship {shipId} docked / boarded / c_habmNoRipcord hull requests all silently ignored (no activate/deny/abort)");
    }

    // ──────────────────────────── P112: the countdown driver ────────────────────────────

    /// <summary>⭐ P112 — Scenario 1: the countdown genuinely ticks down
    /// tick-over-tick (a live per-tick driver, not a one-shot set). A station-
    /// anchored ripcord stays legal every tick, so RipcordTimeLeft falls by the
    /// per-chunk dt each pump while remaining active.</summary>
    [Fact]
    public void Countdown_ticks_down_over_real_ticks()
    {
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.True(ServerShip(server, rid).IsRipcordActive);

        float t0 = ServerShip(server, rid).RipcordTimeLeft;
        for (int i = 0; i < 10; i++)
            server.TickSim();                       // 10 × 0.05 s chunks

        var ship = ServerShip(server, rid);
        float t1 = ship.RipcordTimeLeft;
        // Still active and legal; fell by ~10 chunks of 0.05 = 0.5 s.
        Assert.True(ship.IsRipcordActive);
        Assert.True(t1 > 0f, "countdown unexpectedly hit zero");
        Assert.True(t0 - t1 >= 0.450f, $"countdown barely moved: t0={t0} t1={t1}");
        Assert.True(t0 - t1 <= 0.552f, $"countdown fell too far: t0={t0} t1={t1}");

        Console.WriteLine($"RIPCORD-P112-TICK: ship {rid} countdown {t0:F3}s -> {t1:F3}s over 10 ticks ({t0 - t1:F3}s elapsed) — live per-tick driver, still active");
    }

    /// <summary>⭐ P112 — Scenario 2: reaching zero does <b>nothing</b> observable
    /// yet. No teleport (position unchanged), no abort, ripcord state stays
    /// active — the countdown is merely clamped at zero until P113 adds
    /// <c>UseRipcord</c>. The countdown is nudged to near-zero (a direct write of
    /// the intentionally get-only <c>_dtRipcordCountdown</c>, the same
    /// reflection technique <see cref="AddStationToSector"/> uses) so a couple
    /// of real ticks cross it, independent of the variable-length activation
    /// pump.</summary>
    [Fact]
    public void Countdown_reaching_zero_does_nothing_yet()
    {
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        var ship = ServerShip(server, rid);
        Assert.True(ship.IsRipcordActive);
        Assert.True(ship.RipcordTimeLeft > 0f, "countdown should still be ticking after activation");
        var pos0 = ship.Position;

        // Nudge the countdown to ~0.031 s so ~1 tick crosses zero.
        var field = typeof(Ship).GetField("_dtRipcordCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(ship, 0.031f);
        for (int i = 0; i < 6; i++)
            server.TickSim();

        // Reached zero: clamped, but the ripcord is still ACTIVE (no UseRipcord /
        // teleport in P112) and the ship never moved (no teleport).
        Assert.Equal(0f, ServerShip(server, rid).RipcordTimeLeft);
        Assert.True(ServerShip(server, rid).IsRipcordActive);
        Assert.NotNull(ServerShip(server, rid).RipcordModel);
        Assert.Equal(pos0, ServerShip(server, rid).Position);
        // And no abort fired — the anchor stayed legal the whole time.
        Assert.Empty(req.RipcordAborteds);
        Assert.Empty(byst.RipcordAborteds);

        Console.WriteLine($"RIPCORD-P112-ZERO: ship {rid} countdown hit 0, still active, position {pos0} unchanged — no teleport/no abort (P113 is the teleport)");
    }

    /// <summary>⭐ P112 — Scenario 3: a real mid-countdown side change on a
    /// <b>ship</b> anchor aborts the ripcord: the per-tick revalidation clears
    /// the countdown state and broadcasts RIPCORD_ABORTED(187) to everyone
    /// flying in the <b>ripper's own sector</b> (both bots there; the anchor
    /// ship in the target sector receives nothing), matching P111's cancel-path
    /// broadcast style.</summary>
    [Fact]
    public void Mid_countdown_anchor_side_change_aborts_sector_wide()
    {
        var server = new Srv();
        Ship ripper = null!, anchor = null!, bystander = null!;
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var targetA = mission.Sectors.First(s => s.Id == TargetA);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            Ship ship; Sector sector;
            if (n == 0)
            {
                ship = new Ship(shipId, baseHull, new Vector3(300f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                sector = fly; ripper = ship;
            }
            else if (n == 1)
            {
                // A ripcord-target-capable hull so FindRipcordModel Tier 4 picks it.
                var ah = baseHull with { HabmCapabilities = (short)(baseHull.HabmCapabilities | DataHullTypeIGC.HabmIsRipcordTarget) };
                ship = new Ship(shipId, ah, new Vector3(700f, 640f, 920f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                sector = targetA; anchor = ship;
            }
            else
            {
                ship = new Ship(shipId, baseHull, new Vector3(500f, 630f, 910f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                sector = fly; bystander = ship;
            }
            mission.PlaceShip(ship, sector);
            n++;
            return (ship, sector);
        };
        server.Boot(Config());

        // Connect ONE AT A TIME (pump each to assignment) so ShipSpawner runs in
        // a deterministic order: n=0 -> ripper (REQA, FlySector), n=1 -> anchor
        // (ANCH, TargetA), n=2 -> bystander (BYSP, FlySector). Concurrent logons
        // race and would put ANCH in the bystander slot, making `aid` wrong.
        using var reqa = new FltBot();
        reqa.Connect(server.BoundPort, "REQA"); reqa.SendLogonRequest(Request("REQA"));
        PumpUntil(server, () => reqa.ShipAssignment is not null, 4000);
        using var anch = new FltBot();
        anch.Connect(server.BoundPort, "ANCH"); anch.SendLogonRequest(Request("ANCH"));
        PumpUntil(server, () => anch.ShipAssignment is not null, 4000);
        using var byst = new FltBot();
        byst.Connect(server.BoundPort, "BYSP"); byst.SendLogonRequest(Request("BYSP"));
        PumpUntil(server, () => byst.ShipAssignment is not null, 4000);
        short rid = reqa.ShipAssignment!.Ship.ShipId;
        short aid = anch.ShipAssignment!.Ship.ShipId;
        ResetRipcord(reqa); ResetRipcord(anch); ResetRipcord(byst);

        // No station in TargetA here, so FindRipcordModel picks the ship anchor.
        reqa.SendRipcordRequest(TargetA);
        int seen = PumpUntil(server, () => reqa.RipcordActivates.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ACTIVATE reached the ripper");
        var act = Assert.Single(reqa.RipcordActivates);
        Assert.Equal((short)IgcObjectType.Ship, act.OtRipcord);
        Assert.Equal(aid, act.OidRipcord);              // the anchor SHIP
        Assert.Equal(TargetA, act.SidRipcord);
        var rs = ServerShip(server, rid);
        Assert.True(rs.IsRipcordActive);
        Assert.Equal(new Ship.RipcordTarget((short)IgcObjectType.Ship, aid, TargetA), rs.RipcordModel);

        // Mid-countdown: the anchor ship switches side. Revalidation must fail.
        ResetRipcord(reqa); ResetRipcord(anch); ResetRipcord(byst);
        ServerShip(server, aid).SideId = 1;

        seen = PumpUntil(server, () => reqa.RipcordAborteds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ABORTED after the anchor changed side");

        // Cleared server-side.
        Assert.False(ServerShip(server, rid).IsRipcordActive);
        Assert.Null(ServerShip(server, rid).RipcordModel);

        // Broadcast to everyone flying in the RIPPER's sector (1052): both the
        // ripper and the bystander there; the anchor ship in 1054 gets nothing.
        Assert.Equal(rid, Assert.Single(reqa.RipcordAborteds).ShipidRipcord);
        Assert.Equal(rid, Assert.Single(byst.RipcordAborteds).ShipidRipcord);
        Assert.Empty(anch.RipcordAborteds);

        Console.WriteLine($"RIPCORD-P112-ABORT: ship {rid} ripcord to ship-anchor {aid} aborted on side change -> RIPCORD_ABORTED(187) to both bots in {FlySector}, anchor sector {TargetA} silent");
    }

    /// <summary>⭐ P112 — Scenario 4: a boarded ship's ripcord does not continue
    /// ticking once boarded (the <c>m_pshipParent == NULL</c> gate,
    /// <c>shipIGC.cpp:271</c>). A ripcord armed before boarding is simply frozen
    /// — the countdown does not advance and no abort fires.</summary>
    [Fact]
    public void Boarded_ship_ripcord_does_not_continue_ticking()
    {
        var (server, req, byst, sp) = Setup(out short rid, out short bystId);
        ResetRipcord(req); ResetRipcord(byst);

        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        var rs = ServerShip(server, rid);
        Assert.True(rs.IsRipcordActive);

        // Board the ripper on the bystander (P111's boarded representation:
        // ParentShip + AddChildShip). Direct manipulation, on the sim thread.
        var parent = ServerShip(server, bystId);
        rs.ParentShip = parent; parent.AddChildShip(rs);

        float frozen = ServerShip(server, rid).RipcordTimeLeft;
        for (int i = 0; i < 6; i++)
            server.TickSim();

        // Not boarded-frame gate holds: countdown frozen, still marked active,
        // no abort broadcast.
        Assert.Equal(frozen, ServerShip(server, rid).RipcordTimeLeft);
        Assert.True(ServerShip(server, rid).IsRipcordActive);
        Assert.Empty(req.RipcordAborteds);
        Assert.Empty(byst.RipcordAborteds);

        Console.WriteLine($"RIPCORD-P112-BOARDED: ship {rid} boarded -> ripcord frozen at {frozen:F3}s, still active, no abort");
    }

    /// <summary>⭐ P112 — Scenario 5: an inactive (never-requested) ship is
    /// untouched by the new per-tick pass — no state, no messages. A no-
    /// regression guard on the pass's gating.</summary>
    [Fact]
    public void Inactive_ship_unaffected_by_ripcord_pass()
    {
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        for (int i = 0; i < 6; i++)
            server.TickSim();

        var rs = ServerShip(server, rid);
        Assert.False(rs.IsRipcordActive);
        Assert.Null(rs.RipcordModel);
        Assert.Equal(0f, rs.RipcordTimeLeft);
        Assert.Empty(req.RipcordActivates); Assert.Empty(req.RipcordDenieds); Assert.Empty(req.RipcordAborteds);
        Assert.Empty(byst.RipcordActivates); Assert.Empty(byst.RipcordDenieds); Assert.Empty(byst.RipcordAborteds);

        Console.WriteLine($"RIPCORD-P112-IDLE: ship {rid} never requested ripcord — unaffected by the per-tick pass");
    }
}
