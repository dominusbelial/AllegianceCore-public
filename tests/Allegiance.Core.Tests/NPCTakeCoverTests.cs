using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P151 (ORDER_CHANGE Stage 5) — pure-domain (no sockets) proofs of the NPC
/// take-cover / self-preservation behavior: the first genuinely autonomous
/// (no player command needed) NPC decision in this port. Each test drives the
/// real per-tick hook through the public <see cref="Model.Update"/> entry
/// (which runs <see cref="Ship.OnUpdate"/> on a single ship), so the wire-up —
/// including the <see cref="Ship.IsNpc"/> gate and the simplified
/// "Plan is valid iff PlanTarget is not null" legality check — is exercised as
/// it actually runs, not mocked.
///
/// The take-cover decision itself is the port of the self-contained sub-block
/// at the head of native <c>PickDefaultOrder</c>'s <c>c_ptMiner</c> case
/// (<c>src/Igc/shipIGC.h:2230-2247</c>), and the hiding-station search is
/// <see cref="Ship.TryFindHidingStation"/> (the <c>GetLocalStationToHide</c>
/// port, <c>shipIGC.h:2181-2212</c>). The per-tick trigger (with the
/// Accepted-to-Plan promotion) is native <c>shipIGC.cpp:1934-1945</c>.
///
/// All station types are real records from the <c>PCore001.igc</c> static
/// core — a land-capable type (<c>c_sabmLand = 0x20</c>) and a ripcord-capable
/// type (<c>c_sabmRipcord = 0x08</c>) are selected data-driven so the
/// ability-bit branches run against genuine capability masks. The ship sits in
/// sector 1052 of <c>XXV_simple.igc</c>, the P111-documented no-station fly
/// sector, so the only stations in play are the ones each test injects.
/// </summary>
/// <remarks>
/// <para>Unit tests are the honest, sufficient bar here (stated plainly, per
/// the task): this is pure per-tick server logic with no wire message and no
/// client involved — matching P149's own "no live test needed for pure logic"
/// precedent. Hull/shield fractions are set directly via reflection on the
/// private <c>_fraction</c>/<c>_shieldFraction</c> fields — the same
/// established pattern MissileCombatTests.cs:350 uses.</para>
/// </remarks>
public sealed class NPCTakeCoverTests
{
    /// <summary><c>XXV_simple.igc</c>: the no-station fly sector (P111 report).</summary>
    private const short FlySector = 1052;

    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap DemoMap()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("XXV_simple.igc not in corpus");
        return MissionMap.Load(IgcReader.ParseFile(path));
    }

    private static Sector Sector(Mission m) =>
        m.Sectors.FirstOrDefault(s => s.Id == FlySector)
        ?? throw new InvalidOperationException($"XXV_simple has no sector {FlySector}");

    private static Mission NewMission(StaticCore core)
    {
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, DemoMap(), [new SideSpec(0, "Team 1", civ)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A newly-placed ship on <paramref name="side"/> at the given
    /// position, undocked (no <see cref="Ship.StationId"/>), full hull. When
    /// <paramref name="npc"/> is true the <see cref="Ship.IsNpc"/> marker is
    /// set, marking it for autonomous take-cover evaluation.</summary>
    private static Ship NewShip(Mission m, Sector sector, short side, float x, float y, float z, bool npc)
    {
        var ship = new Ship(9100, m.Core.HullTypes.Values.OrderBy(h => h.HitPoints).First(),
                            new Vector3(x, y, z), Vector3.Zero, Orientation.Identity)
        {
            IsNpc = npc,
        };
        ship.SideId = side;
        m.PlaceShip(ship, sector);   // gives it Mission + Sector (needed by FindNearestFriendlyStation)
        return ship;
    }

    /// <summary>Place a real-type, friendly (<paramref name="side"/>) station
    /// with the given capability mask into the fly sector via the public
    /// <see cref="Mission.PlaceStation"/> seam.</summary>
    private static void PlaceStation(Mission m, Sector sector, short id, short side,
                                     float x, float y, float z, short sabmMask)
    {
        var type = m.Core.StationTypes.Values.FirstOrDefault(t => (t.SabmCapabilities & sabmMask) != 0);
        Assert.False(EqualityComparer<DataStationTypeIGC>.Default.Equals(type, default),
            $"PCore001 must contain a station type with capability 0x{sabmMask:X} (data-driven)");
        var spot = new Vector3(x, y, z);
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
        m.PlaceStation(new Station(data, type), sector);
    }

    private static void SetFraction(Ship ship, float fraction)
        => typeof(Ship).GetField("_fraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, fraction);

    private static void SetShieldFraction(Ship ship, float fraction)
        => typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, fraction);

    /// <summary>⭐ A damaged NPC (IsNpc=true, hull fraction below threshold,
    /// not docked) ticks and ends up with <c>PlanTarget</c> pointing at a real
    /// nearby friendly land-capable station — verification point 1 of the task.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Damaged_npc_ticks_into_take_cover_at_nearest_land_capable_station()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: true);
        PlaceStation(mission, sector, id: 3111, side: 0, x: 100f, y: 0f, z: 0f, DataStationTypeIGC.SabmLand);

        SetFraction(ship, 0.4f);        // Fraction < 0.5 → hull-critical branch trips (shipIGC.h:2235)
        SetShieldFraction(ship, 0.0f);  // no shield → ShieldFraction == 0 also trips (shipIGC.h:2233)

        ship.Update(now: 1.0);          // one OnUpdate chunk (Model.Update → Ship.OnUpdate)

        Assert.NotNull(ship.PlanTarget);
        Assert.Equal(3111, ship.PlanTarget!.Value.StationId);   // nearest friendly land station
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);  // SetCommand(c_cmdPlan,...,c_cidGoto), shipIGC.h:2240
        Console.WriteLine($"P151-HIDE: damaged NPC {ship.Id} took cover: PlanTarget=station 3111 cmdID={ship.PlanCommandId} (shipIGC.h:2230-2247)");
    }

    /// <summary>⭐ An undamaged NPC's <c>PlanTarget</c> stays null — the
    /// <c>GetFraction() &lt; 1.0f</c> outer gate (shipIGC.h:2230) blocks before
    /// any hide branch — verification point 2.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Undamaged_npc_plan_target_stays_null()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: true);
        PlaceStation(mission, sector, id: 3131, side: 0, x: 100f, y: 0f, z: 0f, DataStationTypeIGC.SabmLand);

        // Leave Fraction at the full-hull default 1.0 (shield default 0.0).
        ship.Update(now: 1.0);

        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);
        Console.WriteLine($"P151-UNDAMAGED: full-hull NPC {ship.Id} left Plan unset (shipIGC.h:2230 gate)");
    }

    /// <summary>⭐ A docked NPC's <c>PlanTarget</c> stays null — the
    /// <c>!bDocked</c> gate (this port's <see cref="Ship.StationId"/> not null,
    /// shipIGC.h:2230) blocks even when badly damaged — verification point 3.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_npc_plan_target_stays_null()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: true);
        PlaceStation(mission, sector, id: 3151, side: 0, x: 100f, y: 0f, z: 0f, DataStationTypeIGC.SabmLand);

        SetFraction(ship, 0.4f);
        SetShieldFraction(ship, 0.0f);
        ship.StationId = 3149;   // bDocked — the port's docked sentinel

        ship.Update(now: 1.0);

        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);
        Console.WriteLine($"P151-DOCKED: docked NPC {ship.Id} left Plan unset (shipIGC.h:2230 !bDocked gate)");
    }

    /// <summary>The <see cref="Ship.IsNpc"/> gate: a damaged REAL player ship
    /// (IsNpc=false) never evaluates take-cover, so its <c>PlanTarget</c> stays
    /// null — confirming "real player ships never evaluate this".</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Damaged_non_npc_player_ship_plan_target_stays_null()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: false);
        PlaceStation(mission, sector, id: 3161, side: 0, x: 100f, y: 0f, z: 0f, DataStationTypeIGC.SabmLand);

        SetFraction(ship, 0.4f);
        SetShieldFraction(ship, 0.0f);

        ship.Update(now: 1.0);

        Assert.Null(ship.PlanTarget);   // IsNpc gate: take-cover is for NPCs only
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);
        Console.WriteLine($"P151-PLAYER: damaged real player ship {ship.Id} left Plan unset (IsNpc gate)");
    }

    /// <summary>The <c>Accepted</c>-to-<c>Plan</c> promotion (native
    /// shipIGC.cpp:1936-1939): a damaged NPC that already holds a valid
    /// "return to base" Accepted order gets that promoted into Plan and is NOT
    /// redirected to take-cover's land station — proving the promotion branch
    /// precedes and wins over the autonomous flee.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Valid_accepted_order_promotes_into_plan_not_overridden_by_take_cover()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: true);
        // A land-capable station close by that take-cover WOULD pick if it ran…
        PlaceStation(mission, sector, id: 3171, side: 0, x: 50f, y: 0f, z: 0f, DataStationTypeIGC.SabmLand);
        // …but the NPC already holds a (farther) return-to-base Accepted order to station 3090.
        var acceptedType = mission.Core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmLand) != 0);
        ship.AcceptedTarget = new DataStationIGC(
            Position: [600f, 0f, 0f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: 0, StationId: 3090,
            StationTypeId: (short)acceptedType.StationTypeId, BpHull: 1, BpShield: 1, Name: "BASE_3090");
        ship.AcceptedCommandId = OrderChange.CidGoto;

        SetFraction(ship, 0.4f);   // badly damaged — take-cover would fire were it reached
        SetShieldFraction(ship, 0.0f);

        ship.Update(now: 1.0);

        Assert.Equal(3090, ship.PlanTarget!.Value.StationId);   // promotion, not the nearby land station 3171
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);
        Console.WriteLine($"P151-PROMOTE: valid Accepted order promoted into Plan (station 3090) before take-cover (shipIGC.cpp:1936-1939)");
    }

    /// <summary>A damaged NPC with no qualifying station in its own sector
    /// keeps <c>PlanTarget</c> null — <see cref="Ship.TryFindHidingStation"/>
    /// returning null leaves the slot untouched (native's <c>if (pstationToHide)</c>
    /// gate, shipIGC.h:2239).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Damaged_npc_with_no_hiding_station_leaves_plan_null()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: true);
        // No station placed in 1052 → own-sector search finds nothing (P149 scoping).

        SetFraction(ship, 0.4f);
        SetShieldFraction(ship, 0.0f);

        ship.Update(now: 1.0);

        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);
        Console.WriteLine($"P151-NOSTATION: damaged NPC {ship.Id} with no local station left Plan unset (shipIGC.h:2239 gate)");
    }

    /// <summary>The ripcord fallback of <see cref="Ship.TryFindHidingStation"/>
    /// (shipIGC.h:2185-2191): no land-capable station, but a friendly
    /// ripcord-capable one exists and the shield is below 0.8 — the NPC hides
    /// at the ripcord station directly (no secret-location buoy substitution,
    /// the documented simplification).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Damaged_npc_falls_back_to_ripcord_station_when_no_land_station_and_shield_low()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: true);
        // Only a ripcord-capable station exists in the sector.
        PlaceStation(mission, sector, id: 3191, side: 0, x: 120f, y: 0f, z: 0f, DataStationTypeIGC.SabmRipcord);

        SetFraction(ship, 0.4f);          // damaged hull
        SetShieldFraction(ship, 0.5f);    // < 0.8 → ripcord fallback gate (shipIGC.h:2188)

        ship.Update(now: 1.0);

        Assert.NotNull(ship.PlanTarget);
        Assert.Equal(3191, ship.PlanTarget!.Value.StationId);   // the ripcord station
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);
        Console.WriteLine($"P151-RIPCORD: damaged NPC fell back to ripcord-capable station 3191 (shipIGC.h:2185-2191)");
    }
}
