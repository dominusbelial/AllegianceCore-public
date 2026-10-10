using Allegiance.Core;
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
/// P346 — live loopback proof of the <b>cross-side ship identity EXPORT</b>(26)
/// broadcast (Server.BroadcastCrossSideExport), the missing half of the already
/// built cross-side visibility foundation. The per-tick P132 sweep
/// (<see cref="Sector.ResolveCrossSideVisibility"/>) computes WHEN a ship
/// becomes visible to a non-own side; P139's HEAVY/LIGHT_SHIPS_UPDATE(176/175)
/// then carries that foreign ship's live telemetry, but neither the sweep nor
/// 176/175 carry a HullId/name — so a client that decodes them still could not
/// render the model. P346 adds the identity message: on a genuine false→true
/// visibility edge the sweep queues a (ship, side) pair that the host turns
/// into the same EXPORT/ExportShip wire shape the same-side [teamexport] path
/// already sends — to the newly-seeing SIDE's connections only (per-side, the
/// STATIONS_UPDATE P133 distinction; never whole-mission, never the ship's own
/// side).
/// <para>Construction mirrors P139's booted 2-bot harness (no Windows VM, no
/// native client). The same-side test pins the existing [teamexport] path
/// unchanged: exactly one EXPORT of the other (no cross-side duplicate).</para>
/// <para><b>P369 change.</b> Historically the cross-side identity EXPORT was
/// gated on the P132 <em>detection</em> radius (<c>SeenBySide</c>). P369 aligned
/// the identity gate with the heavy-telemetry gate (<c>BroadcastShipsUpdate</c>
/// sends every non-cloaked ship in a peer's own cluster), because the MonoGame
/// client is a pure renderer that draws exactly those ships — so a same-cluster
/// cross-side ship must carry its identity too, or it renders with no/wrong
/// model (hull=0/side=0) forever. Identity therefore now follows telemetry
/// (same cluster + not cloak-hidden), stays one-shot per (peer, ship), and is
/// re-sent on a slow self-heal cadence. The rewritten
/// <c>Cross_side_same_cluster_ship_exchanges_identity_even_before_detection</c>
/// pins that a same-cluster cross-side ship exchanges identity even when the
/// detection radius hasn't marked it seen.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class CrossSideExportLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Cross-Side Export Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000002ul, DrmHash = "crosssideexport",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static bool HasExportAbout(FltBot bot, short shipId)
        => bot.Exports.Any(e => e.ShipId == shipId);

    /// <summary>A booted 2-bot live server. Both ships fly in
    /// <c>Sectors[0]</c> with the mission's largest-scanner-range hull (so a
    /// 300-unit separation is decisively mutually in-range while a 500 km one is
    /// decisively out). <paramref name="sameSide"/> forces both onto side 0 for
    /// the no-regression test; otherwise they round-robin onto different sides.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            ABot?.Dispose();
            BBot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatPair(bool sameSide)
    {
        var seat = new Seated();
        seat.Server = new Srv();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            // P349 — spawn the two ships FAR apart (placed · 900 km on +X) so no
            // cross-side visibility exists at join. Under the per-recipient latch
            // a co-located spawn would permanently mark each (peer, ship) pair as
            // already-exported at join; a far spawn leaves the set empty so the
            // test's "bring them within range" step is a genuine fresh edge (the
            // reset-then-reobserve pattern of the P346 edge test).
            var pos = new Vector3(placed * 900_000f, 0f, 0f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            short side = sameSide
                ? mission.Sides[0].Id
                : mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B onto different sides
            ship.SideId = side;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = sameSide ? new[] { "p346a", "p346b" } : new[] { "p346_x_a", "p346_x_b" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "both bots must be assigned a ship");

            for (int i = 0; i < bots.Length; i++)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bots[i].ShipAssignment!.Ship.ShipId).Ship;
                if (i == 0) { seat.A = ship; seat.ABot = bots[i]; }
                else { seat.B = ship; seat.BBot = bots[i]; }
            }
            Assert.NotNull(seat.A); Assert.NotNull(seat.B);
            // P180 pattern: the cross-side visibility sweep only runs once the
            // mission is Started/Starting (Mission.Update early-returns otherwise),
            // so bypass the ready-up lobby to make the ships' sectors live.
            foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>⭐ P369 — a cross-side ship the client actually RENDERS (its
    /// telemetry reaches the viewer because <c>BroadcastShipsUpdate</c> sends
    /// every non-cloaked ship in the viewer's cluster) must ALSO have its
    /// identity — the client is a pure renderer, so a ship it draws with no
    /// identity shows the wrong/fallback model forever. Before P369 the
    /// cross-side identity EXPORT was gated on the stricter P132 *detection*
    /// radius (<c>scannerRadius + targetRadius + scannerRange·signature</c>),
    /// which is often SMALLER than the telemetry audience — so a low-signature /
    /// distant same-cluster cross-side ship was rendered but never identified.
    /// P369 aligns the identity gate with the telemetry gate: two cross-side
    /// ships in the SAME cluster exchange identity EXPORTs (correct hull+side)
    /// regardless of detection distance, exactly once (one-shot latch), and
    /// detection no longer withholds identity from a ship whose telemetry flows.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_side_same_cluster_ship_exchanges_identity_even_before_detection()
    {
        Require.Artwork();
        using var seat = SeatPair(sameSide: false);
        Assert.NotEqual(seat.A.SideId, seat.B.SideId);

        // Both ships stay in the SAME cluster (Sectors[0]); place them far apart
        // so the P132 detection radius (which scales with signature) does NOT mark
        // them mutually "seen" — the precise case that used to starve a rendered
        // cross-side ship of its model. Telemetry still flows (same cluster,
        // non-cloaked), so under P369 the identity must flow too.
        seat.A.Position = new Vector3(0f, 0f, 0f);
        seat.B.Position = new Vector3(900_000f, 0f, 0f);
        seat.ABot.ResetExports();
        seat.BBot.ResetExports();

        PumpUntil(seat.Server, () => HasExportAbout(seat.ABot, seat.B.Id) && HasExportAbout(seat.BBot, seat.A.Id), 1000);
        Assert.True(HasExportAbout(seat.ABot, seat.B.Id),
            "A must receive B's cross-side identity EXPORT: same cluster -> telemetry flows, so identity must too (P369)");
        Assert.True(HasExportAbout(seat.BBot, seat.A.Id),
            "B must receive A's cross-side identity EXPORT (same cluster -> telemetry flows)");

        // Correct identity: real HullId of the exported ship, and the wire SideId
        // is the exported ship's OWN side (faction identity), not the seeing side.
        var aExp = seat.ABot.Exports.First(e => e.ShipId == seat.B.Id);
        var bExp = seat.BBot.Exports.First(e => e.ShipId == seat.A.Id);
        Assert.Equal(seat.B.HullType.HullId, aExp.HullId);
        Assert.Equal(seat.A.HullType.HullId, bExp.HullId);
        Assert.Equal(seat.B.SideId, aExp.SideId);
        Assert.Equal(seat.A.SideId, bExp.SideId);

        // Detection no longer matters and does NOT re-export: bringing them within
        // scanner range must not create a duplicate (the per-(peer, ship) latch).
        seat.B.Position = seat.A.Position + new Vector3(300f, 0f, 0f);
        PumpUntil(seat.Server, () => false, 60);
        Assert.Equal(1, seat.ABot.Exports.Count(e => e.ShipId == seat.B.Id));
        Assert.Equal(1, seat.BBot.Exports.Count(e => e.ShipId == seat.A.Id));

        Console.WriteLine($"P369-XEXPORT: same-cluster cross-side A({seat.A.Id}, side {seat.A.SideId}) / " +
                          $"B({seat.B.Id}, side {seat.B.SideId}) exchanged identity (hull {aExp.HullId}) with correct side, exactly once [IDENTITY FOLLOWS TELEMETRY]");
    }

    /// <summary>⭐ THE P349 fix, reproduced in miniature: a peer that JOINS an
    /// already-informed side after a foreign ship became cross-side visible to
    /// it is STILL caught up with that ship's EXPORT on its next tick — the case
    /// P346's one-shot transition-queue dropped (vizfight `roster`: 72 bots
    /// became mutually visible before the human spectator connected, so its
    /// cross-side EXPORTs fired to nobody useful and were gone forever).
    /// Arrangement: A (side 0) is the early peer that makes B (side 1) visible
    /// to side 0; C connects LATE on side 0 (round-robin third peer) and must
    /// still receive B's EXPORT. Also proves the early peer is not re-sent
    /// (per-recipient latch → no duplicate) and that the cross-side latch drains
    /// on disconnect (scope item 4).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Late_joining_peer_on_an_already_informed_side_gets_caught_up_cross_side()
    {
        Require.Artwork();
        using var seat = SeatPair(sameSide: false);
        Assert.NotEqual(seat.A.SideId, seat.B.SideId);

        // 1) Establish that B is visible to side 0 BEFORE C connects: park A (the
        //    only side-0 peer so far) at the origin and bring B to 300 u of it, so
        //    A's side-0 scanner sees B. A latches (A→B); B latches (B→A).
        seat.A.Position = new Vector3(0f, 0f, 0f);
        seat.B.Position = seat.A.Position + new Vector3(300f, 0f, 0f);
        PumpUntil(seat.Server, () => HasExportAbout(seat.ABot, seat.B.Id) && HasExportAbout(seat.BBot, seat.A.Id), 1000);
        Assert.True(HasExportAbout(seat.ABot, seat.B.Id), "A must see B before the late joiner connects (precondition)");
        Assert.Equal(1, seat.ABot.Exports.Count(e => e.ShipId == seat.B.Id));

        // 2) NOW connect the late-joining human-viewer peer C. Round-robin spawn
        //    puts the third peer on side 0 (same side as A) — the exact vizfight
        //    roster shape (human + several Bios bots share side 0). B's visibility
        //    to side 0 was established before C joined; C must STILL be caught up.
        FltBot cbot = new();
        try
        {
            cbot.Connect(seat.Server.BoundPort, "p349_late");
            cbot.SendLogonRequest(Request("p349_late"));
            PumpUntil(seat.Server, () => cbot.ShipAssignment is not null, 4000);
            Assert.True(cbot.ShipAssignment is not null, "late joiner C must be assigned a ship");

            PumpUntil(seat.Server, () => HasExportAbout(cbot, seat.B.Id), 1000);
            Assert.True(HasExportAbout(cbot, seat.B.Id),
                "late-joining peer on side 0 must receive the cross-side EXPORT of B (caught up)");

            var cExp = cbot.Exports.First(e => e.ShipId == seat.B.Id);
            Assert.Equal(seat.B.HullType.HullId, cExp.HullId);
            Assert.Equal(seat.B.SideId, cExp.SideId);

            // 3) Regression: A (connected from the start) must NOT be re-sent B's
            //    EXPORT — the per-(peer, ship) latch suppresses the duplicate.
            Assert.Equal(1, seat.ABot.Exports.Count(e => e.ShipId == seat.B.Id));

            // 4) Disconnect cleanup (scope item 4): the cross-side latch populated,
            //    then drains to zero once every peer departs (same RemoveWhere on
            //    Recipient/ShipId as _teammateExportSent).
            Assert.True(seat.Server.ConnectionScopedCacheCounts().CrossSideExportSent >= 1,
                "the cross-side export latch must have populated");
            cbot.Dispose();
            seat.ABot.Dispose();
            seat.BBot.Dispose();
            int settled = PumpUntil(seat.Server,
                () => seat.Server.ConnectionScopedCacheCounts().CrossSideExportSent == 0, 1600);
            Assert.True(settled < 1600,
                $"cross-side export latch did not drain after disconnect: {seat.Server.ConnectionScopedCacheCounts()}");

            Console.WriteLine($"P349-LATE: B became visible to side 0 before C joined; late peer C (side 0) still got EXPORT of B " +
                              $"(hull {cExp.HullId}, side {cExp.SideId}); A not duplicated; cross-side latch drained to 0 on disconnect");
        }
        finally
        {
            cbot.Dispose();
        }
    }

    /// <summary>⭐ P369 — the cross-side identity EXPORT's one-shot latch is the
    /// client's ONLY source of a foreign ship's <c>SideId</c>, and a single
    /// missed EXPORT would otherwise leave that viewer carrying side 0 forever.
    /// The P369 periodic refresh re-sends the identity of an already-latched
    /// (peer, ship) pair on a slow cadence — self-healing — while staying gated
    /// on current visibility and cadence-bounded (never per-tick spam).
    /// Proof: two cross-side ships made mutually visible exchange exactly one
    /// EXPORT each; after pumping PAST <see cref="Srv"/>'s
    /// <c>CrossSideIdentityRefreshTicks</c> (600) cadence with them still in
    /// range, each has received ADDITIONAL identity EXPORTs of the other, and
    /// the growth is cadence-bounded (not one per tick).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_side_identity_refresh_self_heals_on_the_slow_cadence()
    {
        Require.Artwork();
        using var seat = SeatPair(sameSide: false);
        Assert.NotEqual(seat.A.SideId, seat.B.SideId);

        // Mutually visible: each latches exactly one EXPORT of the other.
        seat.A.Position = Vector3.Zero;
        seat.B.Position = seat.A.Position + new Vector3(300f, 0f, 0f);
        PumpUntil(seat.Server, () => HasExportAbout(seat.ABot, seat.B.Id) && HasExportAbout(seat.BBot, seat.A.Id), 1000);
        Assert.True(HasExportAbout(seat.ABot, seat.B.Id), "A must see B (precondition)");

        int beforeA = seat.ABot.Exports.Count(e => e.ShipId == seat.B.Id);
        int beforeB = seat.BBot.Exports.Count(e => e.ShipId == seat.A.Id);
        Assert.Equal(1, beforeA);
        Assert.Equal(1, beforeB);

        // Pump well past one refresh cadence (600) with both still in range.
        // Each already-latched pair is re-sent once per cadence, so both
        // clients must accumulate MORE than the single original EXPORT.
        PumpUntil(seat.Server, () => false, 1300);

        int afterA = seat.ABot.Exports.Count(e => e.ShipId == seat.B.Id);
        int afterB = seat.BBot.Exports.Count(e => e.ShipId == seat.A.Id);
        Assert.True(afterA > beforeA,
            $"A's cross-side identity of B must refresh on the cadence (was {beforeA}, now {afterA})");
        Assert.True(afterB > beforeB,
            $"B's cross-side identity of A must refresh on the cadence (was {beforeB}, now {afterB})");

        // Cadence-bounded: 1300 ticks / 600 per cadence ≈ 2-3 refreshes → total
        // far below per-tick spam. Guards against reintroducing P346's flood.
        Assert.True(afterA <= 4 && afterB <= 4,
            $"identity refresh must be cadence-bounded, not per-tick spam (A={afterA}, B={afterB})");

        // The refresh still carries the correct real identity (side + hull).
        var refreshed = seat.ABot.Exports.Last(e => e.ShipId == seat.B.Id);
        Assert.Equal(seat.B.HullType.HullId, refreshed.HullId);
        Assert.Equal(seat.B.SideId, refreshed.SideId);

        Console.WriteLine($"P369-REFRESH: after {1300}+ ticks both cross-side identities self-healed " +
                          $"(A about B: {beforeA}->{afterA}; B about A: {beforeB}->{afterB}) and stayed cadence-bounded");
    }

    /// <summary>⭐ Regression pin for the existing same-side [teamexport] path:
    /// two teammates on the SAME side still get the existing EXPORT of each other
    /// (real hull id + name) — exactly once — with NO cross-side double-export
    /// (the cross-side sweep never fires for a ship's own side).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_teammate_still_gets_teamexport_no_cross_duplicate()
    {
        Require.Artwork();
        using var seat = SeatPair(sameSide: true);
        Assert.Equal(seat.A.SideId, seat.B.SideId);

        // NOTE: no ResetExports here — the same-side [teamexport] is a one-shot
        // per (recipient, subject) pair sent during join (gated by
        // _teammateExportSent in BroadcastShipStates), so clearing the bots'
        // accumulators after join would erase the only copy. The cross-side
        // export is edge-triggered, so the other test CAN reset and re-observe.
        PumpUntil(seat.Server, () => HasExportAbout(seat.ABot, seat.B.Id) && HasExportAbout(seat.BBot, seat.A.Id), 2000);
        Assert.True(HasExportAbout(seat.ABot, seat.B.Id), "A must receive the same-side [teamexport] of B (no regression)");
        Assert.True(HasExportAbout(seat.BBot, seat.A.Id), "B must receive the same-side [teamexport] of A (no regression)");

        // Exactly once each: the same-side teamexport, and no cross-side duplicate
        // (the P346 cross-side sweep provably never fires for a ship's own side,
        // and a loadout change that would re-export is not induced here).
        Assert.Equal(1, seat.ABot.Exports.Count(e => e.ShipId == seat.B.Id));
        Assert.Equal(1, seat.BBot.Exports.Count(e => e.ShipId == seat.A.Id));

        // Regression: the teamexport still carries the real hull id + name.
        var ab = seat.ABot.Exports.First(e => e.ShipId == seat.B.Id);
        Assert.Equal(seat.B.HullType.HullId, ab.HullId);
        Assert.Equal("p346b", ab.CharacterName);

        Console.WriteLine($"P346-TEAMEXPORT: same-side A({seat.A.Id})/B({seat.B.Id}, side {seat.A.SideId}) -> exactly one EXPORT of the other (hull {ab.HullId}), no cross duplicate [SAME-SIDE INTACT]");
    }
}
