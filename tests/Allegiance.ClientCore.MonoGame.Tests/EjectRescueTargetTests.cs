using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P540 — the eject autopilot target pick
/// (<see cref="Game1.ChooseEjectRescueTarget"/>), the pure mirror of native's
/// <c>FindTarget(pShip, c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttProbe,
/// …, c_sabmRescue | c_sabmRescueAny | c_sabmLand)</c> (appmsg.cpp:233):
/// nearest FRIENDLY station or probe, in ANY cluster, carrying rescue/land
/// capability — enemy and non-rescue objects ignored.
/// </summary>
public class EjectRescueTargetTests
{
    private const short MySide = 1;
    private const short EnemySide = 2;

    private const short RescueType = 1;      // SabmRescue bit (0x2000)
    private const short RescueAnyType = 2;   // SabmRescueAny bit (0x4000)
    private const short LandOnlyType = 3;    // SabmLand (0x20) only
    private const short NoRescueType = 4;    // no rescue/land bits

    private const short ProbeRescueType = 5; // EabmRescueAny bit (0x4000)
    private const short ProbePlainType = 6;  // no rescue bit

    // Station TypeId → SabmCapabilities (the same source StationRoleBits reads).
    private static short StationSabm(short tid) => tid switch
    {
        RescueType => DataStationTypeIGC.SabmRescue,
        RescueAnyType => DataStationTypeIGC.SabmRescueAny,
        LandOnlyType => DataStationTypeIGC.SabmLand,
        _ => 0,
    };

    // Probe ProbetypeId → EabmCapabilities.
    private static short ProbeEabm(short tid) => tid switch
    {
        ProbeRescueType => DataStationTypeIGC.SabmRescueAny,  // = c_eabmRescueAny
        _ => 0,
    };

    private static DataStationIGC Station(short id, short side, short type, float[] pos) =>
        new(pos, Up: [0f, 1f, 0f], Forward: [0f, 0f, -1f], Rotation: [0f, 0f, 0f, 0f],
            ClusterId: (short)(1 + id), SideId: side, StationId: id, StationTypeId: type,
            BpHull: 0, BpShield: 0, Name: $"station-{id}");

    private static void AddStation(ClientWorld w, DataStationIGC st) =>
        w.Apply(new ExportStation { Station = st });

    private static void AddProbe(ClientWorld w, short id, short side, short type, SimVec pos) =>
        w.Apply(new ExportProbe
        {
            Probe = new DataProbeExport(P0: pos, Time0: 0, ProbeId: id, ExportF: true,
                ProbetypeId: type, SideId: side, ClusterId: 1, ShipId: -1,
                OtTarget: 0, OidTarget: 0, CreateNow: true),
        });

    [Fact]
    public void Chooses_NearestFriendlyRescueStation_AcrossClusters()
    {
        var w = new ClientWorld();
        var from = new SimVec(0f, 0f, 0f);
        AddStation(w, Station(1, MySide, RescueType, new[] { 100f, 0f, 0f }));     // friendly rescue, farther
        AddStation(w, Station(2, MySide, RescueType, new[] { 20f, 0f, 0f }));      // friendly rescue, NEAREST
        AddStation(w, Station(3, EnemySide, RescueType, new[] { 1f, 0f, 0f }));    // enemy rescue — ignored
        AddStation(w, Station(4, MySide, NoRescueType, new[] { 5f, 0f, 0f }));     // friendly, no rescue — ignored
        AddStation(w, Station(5, MySide, LandOnlyType, new[] { 50f, 0f, 0f }));    // friendly, land-only — acceptable

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, from, StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected a rescue target");

        Assert.Equal(Game1.TargetKind.Station, p.Kind);
        Assert.Equal((short)2, p.Id);               // the nearest friendly rescue-capable one
        Assert.Equal("station-2", p.Name);
    }

    [Fact]
    public void RescueAny_And_LandCapable_AreValidTargets()
    {
        var w = new ClientWorld();
        // Only RescueAny and Land-only friendly stations — both must be candidates
        // (native's mask is Rescue|RescueAny|Land). The nearer Land-only wins.
        AddStation(w, Station(10, MySide, RescueAnyType, new[] { 30f, 0f, 0f }));
        AddStation(w, Station(11, MySide, LandOnlyType, new[] { 5f, 0f, 0f }));

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected a rescue target");
        Assert.Equal((short)11, p.Id);   // nearest = Land-only
    }

    [Fact]
    public void Ignores_EnemyAndNonRescueStations_ReturnsNullWhenNoneUsable()
    {
        var w = new ClientWorld();
        AddStation(w, Station(20, EnemySide, RescueType, new[] { 1f, 0f, 0f }));   // enemy — ignored
        AddStation(w, Station(21, MySide, NoRescueType, new[] { 1f, 0f, 0f }));    // friendly, no rescue — ignored

        Assert.Null(Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm));
    }

    [Fact]
    public void CanPick_A_FriendlyRescueProbe_AsNearest()
    {
        var w = new ClientWorld();
        AddStation(w, Station(30, MySide, RescueType, new[] { 500f, 0f, 0f }));    // friendly station, far
        AddProbe(w, 1, MySide, ProbeRescueType, new SimVec(10f, 0f, 0f));          // friendly rescue probe, near
        AddProbe(w, 2, EnemySide, ProbeRescueType, new SimVec(1f, 0f, 0f));        // enemy probe — ignored
        AddProbe(w, 3, MySide, ProbePlainType, new SimVec(3f, 0f, 0f));            // friendly, no rescue — ignored

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected a rescue probe target");

        Assert.Equal(Game1.TargetKind.Probe, p.Kind);
        Assert.Equal((short)1, p.Id);
    }
}
