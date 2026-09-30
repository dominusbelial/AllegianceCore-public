using Allegiance.Core;
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
/// P126 — live loopback proof of the <b>hostile-capture</b> branch of
/// <see cref="Sector.ResolveDockCollisions"/> (the branch P110's own doc comment
/// on that pass explicitly deferred): a DIFFERENT-side ship whose hull has
/// <c>c_habmBoard</c> (<see cref="DataHullTypeIGC.HabmBoard"/>) that flies into a
/// station's dock radius captures it — re-sides the base, applies the post-capture
/// fudge (<c>ArmorFraction</c> halved, <c>ShieldFraction</c> = the heroic-engineer
/// regen value), broadcasts a real <c>FM_S_STATION_CAPTURE(58)</c> to the WHOLE
/// MISSION (<c>GetGroupRealSides()</c>, <c>fsship.cpp:629</c>), and then the ship
/// docks normally at its newly-captured base (native docking continues after the
/// capture, <c>FedSrv.CPP:13107</c>). Two negatives prove the gate:
/// <list type="number">
/// <item>a hostile ship WITHOUT <c>c_habmBoard</c> is rejected — no capture, no
/// dock, base unchanged, no broadcast;</item>
/// <item>a same-side ship docking at its own station is completely unaffected —
/// the pre-existing friendly path, no regression, no capture.</item>
/// </list>
/// Construction mirrors <see cref="StationCombatLiveTests"/>' 3-bot / 2-side
/// harness (a synthetic station owned by side A in sector 0 via
/// <c>Mission.PlaceStation</c>, real hulls chosen from the core). Ground truth is
/// read off the live server-side station (<see cref="Station.SideId"/> /
/// <see cref="Station.ArmorFraction"/> / <see cref="Station.ShieldFraction"/> /
/// sector membership / the static <c>SideId</c>) and the wire
/// <see cref="Bot.StationCaptures"/> on every peer.
/// </summary>
[Collection("LiveNetwork")]
public sealed class StationCaptureLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const short BaseId = 7901;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    /// <summary>One placed ship: <see cref="Boardable"/> selects a hull carrying
    /// <c>c_habmBoard</c> (a real core hull that has it) vs one that does not (the
    /// weakest hull — an Observer/lifepod, confirmed no board bit); <see cref="Pos"/>
    /// is its spawn.</summary>
    private sealed record Spec(bool Boardable, Vector3 Pos);

    private static readonly Vector3 StationSpot = new(0f, 0f, -1000f);   // base sits down the −z beam
    private static readonly Vector3 Far = new(0f, 680f, 740f);           // well outside DockRadius(200)
    private static readonly Vector3 AtBase = new(0f, 0f, -1000f);        // dist 0 < DockRadius → dockable

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Station Capture Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 779u, DwCookie = 0x779u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 77300000000000000ul, DrmHash = "stationcapture",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 3-bot / 2-side live server with a single synthetic station
    /// owned by side A placed in sector 0 by the ShipSpawner. Slots alternate sides
    /// round-robin (A,B,A): slot 0 = side A, slot 1 = side B, slot 2 = side A.
    /// <see cref="Ships"/>/[i] and <see cref="Bots"/>[i] align to the specs/slots.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship[] Ships = null!;
        public FltBot[] Bots = null!;
        public Station Base = null!;
        public Sector Sector = null!;
        public StaticCore Core = null!;
        public short SideA, SideB;

        public void Dispose()
        {
            foreach (var b in Bots) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated Seat(params Spec[] specs)
    {
        var seat = new Seated();
        var core = Core();
        seat.Core = core;
        seat.Server = new Srv();
        seat.Ships = new Ship[specs.Length];

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            if (seat.Base is null)
            {
                var baseType = core.StationTypes.Values.First();
                var type = baseType with
                {
                    MaxArmorHitPoints = 5000f,
                    MaxShieldHitPoints = 100f,      // regenRatio = 10/100 = 0.1 → shield 0.7 after capture
                    ShieldRegeneration = 10f,
                    ArmorRegeneration = 0f,
                    SabmCapabilities = 0,
                    Radius = 40,                    // ≤ DockRadius margin; not used by the dock pass (DockRadius is the gate)
                };
                seat.SideA = mission.Sides[0].Id;
                seat.SideB = mission.Sides[1].Id;
                var data = new DataStationIGC(
                    Position: [StationSpot.X, StationSpot.Y, StationSpot.Z],
                    Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
                    ClusterId: sector.Id, SideId: seat.SideA, StationId: BaseId,
                    StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SYN_BASE");
                seat.Base = new Station(data, type);
                mission.PlaceStation(seat.Base, sector);
            }
            var spec = specs[placed];
            DataHullTypeIGC hull = spec.Boardable
                ? core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBoard))
                : core.HullTypes.Values.OrderBy(h => h.HitPoints).First();
            var ship = new Ship(id, hull, spec.Pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            seat.Ships[placed] = ship;
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable mission; bypass ready-up
        var port = seat.Server.BoundPort;

        int n = specs.Length;
        seat.Bots = new FltBot[n];
        string[] names = { "p126_a", "p126_b", "p126_team" };
        try
        {
            for (int i = 0; i < n; i++)
            {
                seat.Bots[i] = new FltBot();
                seat.Bots[i].Connect(port, names[i]);
                seat.Bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(seat.Server, () => seat.Bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(seat.Bots.All(b => b.ShipAssignment is not null), "all bots must be assigned a ship");
            Assert.All(seat.Ships, s => Assert.NotNull(s));
            foreach (var s in seat.Ships) s.Velocity = Vector3.Zero;
            Assert.True(seat.SideA != seat.SideB, "harness needs two distinct sides");
            Assert.Equal(seat.SideA, seat.Ships[0].SideId);   // slot 0 = side A
            Assert.Equal(seat.SideB, seat.Ships[1].SideId);   // slot 1 = side B
            return seat;
        }
        catch
        {
            foreach (var b in seat.Bots) b?.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static StationCapture[] AllCaptures(Seated seat)
        => seat.Bots.SelectMany(b => b.StationCaptures).ToArray();

    /// <summary>⭐ A hostile ship WITH <c>c_habmBoard</c> docks an enemy station:
    /// the base re-sides to the capturer's side, <see cref="Station.ArmorFraction"/>
    /// is exactly halved (1.0 → 0.5), <see cref="Station.ShieldFraction"/> equals the
    /// heroic-engineer value (regen/maxShield·7 = 10/100·7 = 0.7), a real
    /// <c>STATION_CAPTURE(58)</c> with the correct
    /// stationID/sidOld/sidNew/shipIDCredit reaches EVERY peer (whole mission),
    /// and the ship then docks at its newly-captured base. Verification point 1.</summary>
    [Fact]
    public void Hostile_boardable_ship_docks_and_captures_enemy_station()
    {
        using var seat = Seat(new Spec(false, Far), new Spec(true, Far), new Spec(false, Far));
        var capturer = seat.Ships[1];      // slot 1 = side B, boardable (parked FAR first)
        var bas = seat.Base;

        // Reset accumulators after seating (which may have logged things already).
        foreach (var b in seat.Bots) b.ResetStationCaptures();

        // Pre-capture ground truth: the base is still side A, armor untouched.
        Assert.Equal(seat.SideA, bas.SideId);
        Assert.Equal(1f, bas.ArmorFraction, 3);

        // P450: capture now REQUIRES the station's shields to be DOWN (strictly
        // below c_fcidDownedShield = 0.1f). Knock the base's shield out through the
        // real damage path — Station.ReceiveDamage (stationIGC.cpp:172): a neutral,
        // launcher-less 100 HP hit drains the 100 HP shield exactly to 0.0 (absorption
        // bottoms out at the shield pool), leaving armor untouched.
        bas.ReceiveDamage(0, 100f, 0.0, Vector3.Zero, Vector3.Zero, launcher: null);
        Assert.Equal(0f, bas.ShieldFraction, 3);   // shields down < 0.1 → capturable

        // Fly the hostile boardable capturer into the base's dock radius — the very
        // next sector tick's dock pass must capture (raise the seam) and then dock it.
        capturer.Position = AtBase;
        PumpUntil(seat.Server, () => bas.SideId == seat.SideB, 9000);
        Assert.True(bas.SideId == seat.SideB, "hostile boardable ship must capture the station");

        // The station really re-sided: runtime side AND the static record both flip.
        Assert.Equal(seat.SideB, bas.SideId);
        Assert.Contains(bas, seat.Sector.StationRuntimes);                       // still alive
        Assert.Equal(seat.SideB, seat.Sector.Stations.First(s => s.StationId == BaseId).SideId);

        // Post-capture fudge: armor exactly halved (1.0 → 0.5), shield = heroic formula.
        Assert.Equal(0.5f, bas.ArmorFraction, 3);
        Assert.Equal(0.7f, bas.ShieldFraction, 3);

        // The capturing ship docks at its newly-captured base (native continues docking).
        PumpUntil(seat.Server, () => capturer.StationId == BaseId, 6000);
        Assert.Equal(BaseId, capturer.StationId);

        // Whole-mission STATION_CAPTURE: EVERY peer (cross-side capturer AND the two
        // same-side teammates) received it, with the correct field triple.
        PumpUntil(seat.Server, () => AllCaptures(seat).Length >= seat.Bots.Length, 6000);
        Assert.True(AllCaptures(seat).Length >= seat.Bots.Length,
            $"every peer must receive STATION_CAPTURE; got {AllCaptures(seat).Length}/{seat.Bots.Length}");
        foreach (var c in AllCaptures(seat))
        {
            Assert.Equal(BaseId, c.StationId);
            Assert.Equal(seat.SideA, c.SidOld);       // the OLD owner
            Assert.Equal(seat.SideB, c.SidNew);       // the NEW owner (capturer side)
            Assert.Equal(capturer.Id, c.ShipIdCredit); // the capturing ship
        }
        Console.WriteLine($"P126-LIVE-CAPTURE: ship {capturer.Id} (side {seat.SideB}) captured base {BaseId} " +
                          $"({seat.SideA}->{seat.SideB}); armor 1.0->0.5, shield 0.7; docked (StationId={capturer.StationId}); " +
                          $"STATION_CAPTURE(58) -> {AllCaptures(seat).Length} peer(s) [WHOLE MISSION]");
    }

    /// <summary>⭐ P450 — a hostile <c>c_habmBoard</c> ship reaching the dock of an
    /// enemy station at <b>full shields</b> is rejected: the base keeps its side,
    /// armor/shield are untouched (no post-capture fudge, no heroic shield boost),
    /// the ship does NOT dock, and no <c>STATION_CAPTURE(58)</c> reaches any peer.
    /// Native's IGC collision gate (shipIGC.cpp:849-851) only lets a hostile board
    /// hull reach <c>DockWithStationEvent→CaptureStation</c> when the base's shields
    /// are BELOW <c>c_fcidDownedShield</c>; above it the board attempt falls through
    /// to the collision bounce. This port's <c>continue</c> is that fall-through —
    /// identical to the non-board rejection above (verification point 4).</summary>
    [Fact]
    public void Hostile_boardable_ship_cannot_capture_full_shield_station()
    {
        using var seat = Seat(new Spec(false, Far), new Spec(true, Far), new Spec(false, Far));
        var capturer = seat.Ships[1];      // slot 1 = side B, boardable (parked FAR first)
        var bas = seat.Base;

        Assert.True(capturer.HullType.HasCapability(DataHullTypeIGC.HabmBoard), "capturer hull must carry c_habmBoard");
        Assert.Equal(1f, bas.ShieldFraction, 3);   // base shields fully UP
        foreach (var b in seat.Bots) b.ResetStationCaptures();

        // Fly the boardable capturer into the base's dock radius with shields FULL —
        // the dock pass must REJECT it (P450), exactly like a non-board hull.
        capturer.Position = AtBase;
        for (int i = 0; i < 800; i++) { seat.Server.TickSim(); Thread.Sleep(1); }

        Assert.Equal(seat.SideA, bas.SideId);          // station keeps its side
        Assert.Equal(1f, bas.ArmorFraction, 3);        // no post-capture armor fudge
        Assert.Equal(1f, bas.ShieldFraction, 3);       // shield not overwritten by the heroic boost
        Assert.Null(capturer.StationId);               // hostile board ship at full shields → no dock
        Assert.Empty(AllCaptures(seat));               // no STATION_CAPTURE broadcast to any peer
        Console.WriteLine("P450-LIVE-FULLSHIELD: hostile boardable ship at full-shield enemy base -> no capture, no dock, no broadcast (side/armor/shield unchanged)");
    }

    /// <summary>⭐ A hostile ship WITHOUT <c>c_habmBoard</c> is rejected: no capture
    /// (station side, armor and shield all unchanged), no dock, no broadcast —
    /// the normal cross-side dock rejection this port always had. Verification point 2.</summary>
    [Fact]
    public void Hostile_non_boardable_ship_does_not_capture_or_dock()
    {
        using var seat = Seat(new Spec(false, Far), new Spec(false, AtBase), new Spec(false, Far));
        var wouldBe = seat.Ships[1];       // slot 1 = side B, NON-boardable, parked at the base
        var bas = seat.Base;

        Assert.False(wouldBe.HullType.HasCapability(DataHullTypeIGC.HabmBoard), "test hull must lack c_habmBoard");
        foreach (var b in seat.Bots) b.ResetStationCaptures();

        // Let many ticks elapse so the rejected ship would have captured if the gate
        // were wrong — it must NOT.
        for (int i = 0; i < 400; i++) { seat.Server.TickSim(); Thread.Sleep(1); }

        Assert.Equal(seat.SideA, bas.SideId);            // side unchanged
        Assert.Equal(1f, bas.ArmorFraction, 3);          // no fudge
        Assert.Equal(1f, bas.ShieldFraction, 3);         // no fudge
        Assert.Null(wouldBe.StationId);                  // hostile + no board → no dock
        Assert.Empty(AllCaptures(seat));                 // no broadcast
        Console.WriteLine("P126-LIVE-NOBOARD: hostile non-boardable ship parked at enemy base -> no capture, no dock, no broadcast (side/armor/shield unchanged)");
    }

    /// <summary>⭐ A same-side ship docking at its OWN station is completely
    /// unaffected — the existing friendly path, no capture, no regression
    /// (side/armor/shield unchanged, plain dock, no broadcast). Verification point 3.</summary>
    [Fact]
    public void Same_side_ship_docks_own_station_unaffected()
    {
        using var seat = Seat(new Spec(false, AtBase), new Spec(false, Far), new Spec(false, Far));
        var friend = seat.Ships[0];        // slot 0 = side A = the station's owner
        var bas = seat.Base;

        Assert.Equal(seat.SideA, friend.SideId);
        foreach (var b in seat.Bots) b.ResetStationCaptures();

        // Friendly dock: the same-side ship latches docked at its own base.
        PumpUntil(seat.Server, () => friend.StationId == BaseId, 9000);
        Assert.Equal(BaseId, friend.StationId);

        // No capture side-effects at all.
        Assert.Equal(seat.SideA, bas.SideId);
        Assert.Equal(1f, bas.ArmorFraction, 3);     // no fudge on a friendly dock
        Assert.Equal(1f, bas.ShieldFraction, 3);
        Assert.Empty(AllCaptures(seat));            // no broadcast
        Assert.Contains(bas, seat.Sector.StationRuntimes);
        Console.WriteLine($"P126-LIVE-FRIENDLY: same-side ship {friend.Id} docked its own base {BaseId} -> plain friendly dock, no capture (armor/shield untouched, no broadcast)");
    }
}
