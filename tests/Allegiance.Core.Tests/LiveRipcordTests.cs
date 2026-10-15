using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

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
    private static readonly string ArtRoot = TestArtRoot.Value;

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

    /// <summary>Register both the station record and its runtime, as a normal
    /// mission does, so ripcord/undock can exercise the native launch routine.</summary>
    private static void AddStationToSector(Sector sector, DataStationIGC station)
    {
        var mission = sector.Mission!;
        mission.PlaceStation(new Station(station, mission.Core.StationTypes[station.StationTypeId]), sector);
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
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void First_activation_broadcasts_to_everyone_flying_in_sector()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Re_target_activates_to_requester_only()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Request_with_no_anchor_denies_requester_only()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: live-wire ripcord abort receipt is load-sensitive (passes 10/10 isolated)
    [SkippableFact]
    public void Cancel_while_active_aborts_sector_wide()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Gate_violations_are_silently_ignored()
    {
        Require.Artwork();
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
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Countdown_ticks_down_over_real_ticks()
    {
        Require.Artwork();
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

    /// <summary>⭐ P112 — Scenario 2, SUPERSEDED BY P113. P112 pinned that
    /// reaching zero does <b>nothing</b> observable (no teleport, ripcord stays
    /// active) — the deliberate no-op this port shipped before P113 added the
    /// real <c>UseRipcord</c> trigger. P113 now makes reaching zero WITH a
    /// still-legal station anchor actually relocate the ship and clear the
    /// state, so this test is rewritten to assert the new behavior — the same
    /// inversion P112 itself applied to P111's two countdown-equality
    /// assertions, driven by the P113 task's scope. It reuses the near-zero
    /// nudge (a direct write of the intentionally get-only
    /// <c>_dtRipcordCountdown</c>, same reflection technique as
    /// <see cref="AddStationToSector"/>) so a couple of real ticks cross zero.
    /// Asserts the station-launch relocation shape (P69's launch mechanic,
    /// native's <c>UseRipcord</c> station branch, <c>FedSrv.CPP:13441-13443</c>)
    /// and that the ripcord state clears and never re-triggers.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Station_anchor_reaching_zero_relocates_to_launch_shape_and_clears()
    {
        Require.Artwork();
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        var ship = ServerShip(server, rid);
        Assert.True(ship.IsRipcordActive);
        Assert.True(ship.RipcordTimeLeft > 0f, "countdown should still be ticking after activation");
        Assert.Equal(new Ship.RipcordTarget((short)IgcObjectType.Station, sp.AnchorA.StationId, TargetA), ship.RipcordModel);
        var stationPos = new Vector3(sp.AnchorA.Position[0], sp.AnchorA.Position[1], sp.AnchorA.Position[2]);

        // Nudge the countdown to ~0.011 s so ~1 real tick crosses zero, then pump
        // EXACTLY ONE tick and measure: the teleport executes in the same
        // SimPass as the zero-cross (drain after Mission.Update), so after one
        // tick the ship sits at its freshly-relocated launch position before any
        // post-teleport flight at ExitStationSpeed has advanced it.
        NudgeCountdownTo(ship, 0.011f);
        server.TickSim();

        var after = ServerShip(server, rid);
        // Real teleport happened: the ripcord state is CLEARED.
        Assert.False(after.IsRipcordActive);
        Assert.Null(after.RipcordModel);
        Assert.Equal(TargetA, after.Sector!.Id);   // moved into the station's cluster
        // Native stationIGC.cpp:350-366: a rotating model slot, +/-5 jitter,
        // and either the isolated or rapid-launch outward offset.
        var runtime = after.Sector.StationRuntimes.Single(s => s.StationId == sp.AnchorA.StationId);
        var bay = after.Sector.StationLandingBays?.LaunchGeometry(runtime.Type);
        float speed = after.Mission!.Core.Constants.FloatConstants[4];
        float radius = after.HullType.Length * .5f;
        if (bay?.LaunchSlots is { Count: > 0 } slots)
            Assert.Contains(slots, slot => new[] { .5f, .85f }.Any(factor =>
                (after.Position - (stationPos + slot.Position * runtime.Orientation
                    + slot.Direction * runtime.Orientation * (radius + speed * factor + 5))).Length() <= 8.67f));
        else
            Assert.InRange((after.Position - stationPos).Length(),
                runtime.Radius + radius + speed * .5f + 5 - 8.67f,
                runtime.Radius + radius + speed * .5f + 5 + 8.67f);
        Assert.True(MathF.Abs(after.Velocity.Length() - 50f) < 1.0f,
            $"launch speed {after.Velocity.Length()} != ExitStationSpeed 50");
        // And no abort fired — the anchor stayed legal the whole time.
        Assert.Empty(req.RipcordAborteds);
        Assert.Empty(byst.RipcordAborteds);

        // Pumping further ticks does NOT re-trigger: the state is genuinely
        // cleared (native consumes the attempt — UseRipcord always returns true),
        // so the ripcord is not re-armed despite the ship now flying (it has a
        // nonzero ExitStationSpeed velocity, so position legitimately advances).
        for (int i = 0; i < 6; i++)
            server.TickSim();
        Assert.False(ServerShip(server, rid).IsRipcordActive);
        Assert.Null(ServerShip(server, rid).RipcordModel);

        Console.WriteLine($"RIPCORD-P113-STATION: ship {rid} station-anchor ripcord reached 0 -> relocated to launch shape {(after.Position - stationPos).Length():F1}u @ {after.Velocity.Length():F1}u/s in sector {TargetA}, state cleared, no re-trigger");
    }

    /// <summary>⭐ P112 — Scenario 3: a real mid-countdown side change on a
    /// <b>ship</b> anchor aborts the ripcord: the per-tick revalidation clears
    /// the countdown state and broadcasts RIPCORD_ABORTED(187) to everyone
    /// flying in the <b>ripper's own sector</b> (both bots there; the anchor
    /// ship in the target sector receives nothing), matching P111's cancel-path
    /// broadcast style.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mid_countdown_anchor_side_change_aborts_sector_wide()
    {
        Require.Artwork();
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
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

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

        seen = PumpUntil(server, () => reqa.RipcordAborteds.Count >= 1 && byst.RipcordAborteds.Count >= 1, 20000);
        Assert.True(seen < 20000, "no RIPCORD_ABORTED after the anchor changed side");

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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Boarded_ship_ripcord_does_not_continue_ticking()
    {
        Require.Artwork();
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Inactive_ship_unaffected_by_ripcord_pass()
    {
        Require.Artwork();
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

    // ──────────────────────────── P113: the teleport ────────────────────────────

    /// <summary>Nudge the countdown scalar to <paramref name="v"/> seconds (a
    /// direct write of the intentionally get-only <c>_dtRipcordCountdown</c>,
    /// same reflection technique as <see cref="AddStationToSector"/>), so a
    /// real tick crosses zero on the next pump regardless of the variable-length
    /// activation pump.</summary>
    private static void NudgeCountdownTo(Ship ship, float v)
    {
        var field = typeof(Ship).GetField("_dtRipcordCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(ship, v);
    }

    /// <summary>Three-ship ship-anchor topology for the P113 relocation tests:
    /// n=0 ripper (side 0, flying in <see cref="FlySector"/>), n=1 anchor SHIP
    /// (side 0, flying in <see cref="TargetA"/>, hull granted
    /// <c>c_habmIsRipcordTarget</c> so FindRipcordModel Tier 4 picks it), n=2
    /// bystander (side 0, flying in <see cref="FlySector"/> to observe abort
    /// broadcasts). Connecting one-at-a-time (pump each to assignment) keeps
    /// ShipSpawner order deterministic, as the P112 side-change test does.
    /// <see cref="TargetA"/> has NO injected station here, so the request names
    /// the ship anchor.</summary>
    private static (Srv server, FltBot req, FltBot anch, FltBot byst, Ship rip, Ship tgt)
        ShipAnchorSetup(out short rid, out short aid)
    {
        // Force a NONZERO RipcordCost on the ripper's hull: the energy gate
        // (FedSrv.CPP:13452-13460) reads the RIPPER's hull cost, and the stock
        // lowest-id non-no-ripcord hull decodes RipcordCost == 0 (measured at
        // test authoring), which would make the gate untestable. Giving it a
        // deterministic cost exercises both the pay-the-target path and the
        // insufficient-energy abort path regardless of the shipped data value.
        const float TestRipcordCost = 60f;
        var server = new Srv();
        Ship ripper = null!, target = null!, bystander = null!;
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var targetA = mission.Sectors.First(s => s.Id == TargetA);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            Ship ship; Sector sector;
            if (n == 0)
            {
                ship = new Ship(shipId, baseHull with { RipcordCost = TestRipcordCost }, new Vector3(300f, 620f, 880f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                sector = fly; ripper = ship;
            }
            else if (n == 1)
            {
                // A ripcord-target-capable hull so FindRipcordModel Tier 4 picks
                // it, with RechargeRate zeroed so its energy is FROZEN at whatever
                // the test sets (otherwise per-tick regeneration climbs it during
                // the async activation pump and defeats the energy gate / makes
                // the exact-payment assertion nondeterministic).
                var ah = baseHull with
                {
                    HabmCapabilities = (short)(baseHull.HabmCapabilities | DataHullTypeIGC.HabmIsRipcordTarget),
                    RechargeRate = 0f,
                    // P461 — the anchor must also carry a REAL energy pool
                    // (baseHull is the low-id lifepod hull with MaxEnergy 0):
                    // the native-faithful recharge clamps the pool to
                    // [0, MaxEnergy], so a MaxEnergy-0 hull can no longer hold
                    // the artificial test energy the ripcord payment needs.
                    MaxEnergy = 5000f,
                };
                ship = new Ship(shipId, ah, new Vector3(700f, 640f, 920f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
                sector = targetA; target = ship;
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
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var reqa = new FltBot();
        reqa.Connect(server.BoundPort, "REQA"); reqa.SendLogonRequest(Request("REQA"));
        PumpUntil(server, () => reqa.ShipAssignment is not null, 4000);
        var anch = new FltBot();
        anch.Connect(server.BoundPort, "ANCH"); anch.SendLogonRequest(Request("ANCH"));
        PumpUntil(server, () => anch.ShipAssignment is not null, 4000);
        var byst = new FltBot();
        byst.Connect(server.BoundPort, "BYSP"); byst.SendLogonRequest(Request("BYSP"));
        PumpUntil(server, () => byst.ShipAssignment is not null, 4000);

        rid = reqa.ShipAssignment!.Ship.ShipId;
        aid = anch.ShipAssignment!.Ship.ShipId;
        Assert.NotNull(ripper); Assert.NotNull(target); Assert.NotNull(bystander);
        return (server, reqa, anch, byst, ripper, target);
    }

    /// <summary>⭐ P113 — Scenario 2: a REAL ship-anchored ripcord reaching zero
    /// with the target having enough energy relocates the ripper near the target
    /// (<c>r = target.Radius + ripper.Radius + 25f</c> — the literal 25, distinct
    /// from the station launch's 50 margin — in a random unit direction), moves
    /// it into the target's cluster, pays <c>RipcordCost</c> from the target's
    /// energy, and clears the ripcord state. Pumping further confirms the
    /// attempt is consumed (no re-trigger).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ship_anchor_reaching_zero_relocates_near_target_and_pays_energy()
    {
        Require.Artwork();
        var (server, req, anch, byst, rip, tgt) = ShipAnchorSetup(out short rid, out short aid);
        ResetRipcord(req); ResetRipcord(anch); ResetRipcord(byst);

        // Give the target plenty of energy to cover the ripper's RipcordCost.
        float cost = rip.HullType.RipcordCost;
        float startEnergy = cost + 750f;
        tgt.Energy = startEnergy;

        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.Equal((short)IgcObjectType.Ship, Assert.Single(req.RipcordActivates).OtRipcord);
        var rship = ServerShip(server, rid);
        Assert.True(rship.IsRipcordActive);
        Assert.Equal(new Ship.RipcordTarget((short)IgcObjectType.Ship, aid, TargetA), rship.RipcordModel);

        NudgeCountdownTo(rship, 0.011f);
        server.TickSim();   // ONE tick: teleport + measure before post-flight drift

        var after = ServerShip(server, rid);
        var tgtAfter = ServerShip(server, aid);
        // Teleport happened and the ripcord state is CLEARED.
        Assert.False(after.IsRipcordActive);
        Assert.Null(after.RipcordModel);
        Assert.Equal(TargetA, after.Sector!.Id);   // now in the anchor's cluster

        // Near the target: r = target.Radius + ripper.Radius + 25f (radius =
        // 0.5×HullType.Length, the port's established GetRadius() approximation).
        float radRipper = 0.5f * after.HullType.Length;
        float radTarget = 0.5f * tgtAfter.HullType.Length;
        float expectedR = radTarget + radRipper + 25.0f;
        float dist = (after.Position - tgtAfter.Position).Length();
        Assert.True(MathF.Abs(dist - expectedR) < 1.0f,
            $"ship-anchor distance {dist} != r {expectedR} (target.Radius {radTarget} + ripper.Radius {radRipper} + 25)");
        // Velocity is the SAME ExitStationSpeed constant the station launch uses.
        Assert.True(MathF.Abs(after.Velocity.Length() - 50f) < 1.0f,
            $"exit speed {after.Velocity.Length()} != ExitStationSpeed 50");
        // Energy was paid from the TARGET by exactly RipcordCost.
        Assert.True(MathF.Abs(tgtAfter.Energy - (startEnergy - cost)) < 0.001f,
            $"target energy {tgtAfter.Energy} != {startEnergy} - RipcordCost {cost}");

        // Further ticks: state genuinely cleared — the ripcord is not re-armed
        // (the ship now flies at ExitStationSpeed, so it legitimately moves).
        for (int i = 0; i < 6; i++)
            server.TickSim();
        Assert.False(ServerShip(server, rid).IsRipcordActive);
        Assert.Null(ServerShip(server, rid).RipcordModel);
        Assert.Empty(req.RipcordAborteds);

        Console.WriteLine($"RIPCORD-P113-SHIP: ship {rid} ship-anchor ripcord reached 0 -> relocated to r={dist:F1} ({radTarget}+{radRipper}+25) in sector {TargetA}, target energy {startEnergy}->{tgtAfter.Energy} (cost {cost}), no re-trigger");
    }

    /// <summary>⭐ P113 — Scenario 3: a REAL ship-anchored ripcord reaching zero
    /// where the target does NOT have enough energy broadcasts RIPCORD_ABORTED(187)
    /// to everyone flying in the ripper's cluster, does NOT move the ripper, and
    /// CLEARS the ripcord state — the attempt is CONSUMED, not retried when the
    /// target later regains energy (native's UseRipcord returns true either way,
    /// FedSrv.CPP:13451-13461).</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: flaky live-wire ripcord-energy receipt even in isolation
    [SkippableFact]
    public void Ship_anchor_insufficient_energy_aborts_consumed_not_retried()
    {
        Require.Artwork();
        var (server, req, anch, byst, rip, tgt) = ShipAnchorSetup(out short rid, out short aid);
        ResetRipcord(req); ResetRipcord(anch); ResetRipcord(byst);

        // Target energy BELOW the ripper's RipcordCost (kept >= 0).
        float cost = rip.HullType.RipcordCost;
        tgt.Energy = MathF.Max(0f, cost - 800f);

        req.SendRipcordRequest(TargetA);
        PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.Equal((short)IgcObjectType.Ship, Assert.Single(req.RipcordActivates).OtRipcord);
        var rship = ServerShip(server, rid);
        Assert.True(rship.IsRipcordActive);
        var pos0 = rship.Position;

        NudgeCountdownTo(rship, 0.011f);
        // The zero-cross executes the teleport on the FIRST tick after the nudge
        // (decrement crosses zero -> clamped -> drained in the same SimPass), so
        // pump UNTIL the abort broadcast is observed rather than a fixed tick
        // count — async delivery to the bot is timing-sensitive under collection
        // load (the abort itself is deterministic on tick 1).
        int seen = PumpUntil(server, () => req.RipcordAborteds.Count >= 1, 4000);
        Assert.True(seen < 4000, "no RIPCORD_ABORTED reached the ripper on zero-cross with insufficient target energy");

        // RIPCORD_ABORTED broadcast to everyone flying in the RIPPER's sector:
        // both the ripper and the bystander there; the anchor in TargetA silent.
        Assert.Equal(rid, Assert.Single(req.RipcordAborteds).ShipidRipcord);
        Assert.Equal(rid, Assert.Single(byst.RipcordAborteds).ShipidRipcord);
        Assert.Empty(anch.RipcordAborteds);

        var after = ServerShip(server, rid);
        // Ripcord state cleared (attempt consumed) and the ripper did NOT move.
        Assert.False(after.IsRipcordActive);
        Assert.Null(after.RipcordModel);
        Assert.Equal(pos0, after.Position);
        Assert.Equal(FlySector, after.Sector!.Id);   // stayed in its own sector

        // NOT retried: even when the target later regains energy, a future tick
        // does NOT re-attempt the teleport (native returns true -> consumed).
        tgt.Energy = cost + 950f;
        for (int i = 0; i < 6; i++)
            server.TickSim();
        Assert.False(ServerShip(server, rid).IsRipcordActive);
        Assert.Equal(pos0, ServerShip(server, rid).Position);
        // And no extra abort broadcast accumulated on the retry-ticks.
        Assert.Single(req.RipcordAborteds);
        Assert.Single(byst.RipcordAborteds);

        Console.WriteLine($"RIPCORD-P113-ABORT: ship {rid} ship-anchor ripcord reached 0 with insufficient target energy -> RIPCORD_ABORTED(187) to both bots in {FlySector}, ripper unmoved, attempt consumed (not retried after energy restored)");
    }

    // ───────────────── P405: ripcord countdown freezes maneuver + thrust ─────────────────

    /// <summary>⭐ P405 live proof: a REAL ship with an active ripcord countdown
    /// cannot maneuver or thrust — commanded full yaw + full forward thrust
    /// during the countdown, it keeps drifting unpowered (drag only) until the
    /// teleport fires. Two discriminating observations while
    /// <c>IsRipcordActive</c>: (1) the turn rate stays ~0 (the yaw command
    /// never ramps it), and (2) the speed STRICTLY DECAYS below the cruising
    /// speed at activation despite continuous full forward thrust — a missing
    /// guard would let that thrust grow or hold the speed instead. Then it is
    /// pumped to the teleport firing, confirming the drift spanned the whole
    /// vulnerable window. Mirrors the unit proof
    /// (<see cref="ShipRipcordFreezeTests"/>) on a real wired server.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ripcord_countdown_freezes_maneuver_and_thrust_until_teleport()
    {
        Require.Artwork();
        var (server, req, byst, sp) = Setup(out short rid, out _);
        ResetRipcord(req); ResetRipcord(byst);

        // Phase 1 — build forward cruising speed so the post-ripcord "drag only"
        // decay is observable (pure forward, no yaw: turn rate stays 0).
        req.CommandControls(new ControlData(0f, 0f, 0f, +1f), stateM: ShipControlState.ForwardButton);
        int built = PumpUntil(server, () => ServerShip(server, rid).Velocity.Length() >= 60f, 800);
        Assert.True(built < 800, $"ship never reached cruising speed; v={ServerShip(server, rid).Velocity.Length():F1}");
        Assert.True(MathF.Abs(ServerShip(server, rid).GetTurnRates(new float[3])[ControlData.AxisYaw]) < 1e-4f,
            "build phase must keep the turn rate at 0 (pure forward)");

        // Phase 2 — engage ripcord, then mid-countdown DEMAND full yaw + forward.
        req.SendRipcordRequest(TargetA);
        int act = PumpUntil(server, () => req.RipcordActivates.Count >= 1, 4000);
        Assert.True(act < 4000, "no RIPCORD_ACTIVATE reached the requester");
        Assert.True(ServerShip(server, rid).IsRipcordActive);

        // Demand full yaw + full forward thrust for the whole countdown.
        req.CommandControls(new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: +1f),
                            stateM: ShipControlState.ForwardButton);

        float vAtRip = ServerShip(server, rid).Velocity.Length();
        Assert.True(vAtRip > 0f, "need nonzero speed at ripcord to observe drag");

        // Observe ~2 s (40 × 0.05 s ticks) of the ~10 s countdown under full
        // yaw + thrust command.
        for (int i = 0; i < 40; i++)
            server.TickSim();

        var mid = ServerShip(server, rid);
        Assert.True(mid.IsRipcordActive, "countdown must still be running in the observation window");
        float vMid = mid.Velocity.Length();
        Assert.True(vMid < vAtRip,
            $"ripcording speed must DECAY (drag only) from {vAtRip:F1} to {vMid:F1} despite full forward thrust");
        var midRates = mid.GetTurnRates(new float[3]);
        Assert.True(MathF.Abs(midRates[ControlData.AxisYaw]) < 1e-4f,
            $"turn rate must stay frozen at 0 during the countdown despite full yaw; yaw={midRates[ControlData.AxisYaw]:R}");
        Assert.True(MathF.Abs(midRates[ControlData.AxisPitch]) < 1e-4f, "pitch rate must stay frozen");
        Assert.True(MathF.Abs(midRates[ControlData.AxisRoll]) < 1e-4f, "roll rate must stay frozen");

        // Phase 3 — force the teleport to fire promptly using the established
        // countdown-nudge seam (the same direct write the P112/P113 tests use),
        // so the teleport executes right after the observed unpowered drift and
        // before the drifting ship could wander out of the fly sector. This
        // confirms the unpowered drift continued until the teleport fired.
        NudgeCountdownTo(ServerShip(server, rid), 0.035f);
        int fire = PumpUntil(server, () => !ServerShip(server, rid).IsRipcordActive, 100);
        Assert.True(fire < 100, "ripcord never teleported");
        var fin = ServerShip(server, rid);
        Assert.Null(fin.RipcordModel);
        Assert.Equal(TargetA, fin.Sector!.Id);   // relocated to the anchor's cluster

        Console.WriteLine($"RIPCORD-P405-FREEZE: ship {rid} during ripcord countdown -> turn rates held ~0 & speed {vAtRip:F1}->{vMid:F1}u/s (drag only) under full yaw+thrust, drifted unpowered until teleport to {TargetA} fired");
    }
}
