using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P977 (P971-04) — the eject autopilot target pick must accept ALLIED rescue
/// points and native's side-independent <c>RescueAny</c> probe exception, not
/// just exact own-side ones.
///
/// <para>Native rule (re-derived, every line re-read on master): the eject
/// handler calls <c>FindTarget(pShip, c_ttStation | c_ttFriendly |
/// c_ttAnyCluster | c_ttProbe, …, c_sabmRescue | c_sabmRescueAny |
/// c_sabmLand)</c> (clintlib/appmsg.cpp:233). <c>FindTarget</c> defaults its
/// ally mode to 1 — "your side + allies" (Igc/igc.h:4642) — and passes it to
/// <c>FindableModel</c> (Igc/igc.h:4632). <c>FindableModel</c> classifies a
/// model as <c>c_ttFriendly</c> when it is the seeker's side OR an allied side
/// in mode 1 (Igc/common.cpp:267-271). Its probe exception admits a probe whose
/// TYPE carries <c>c_eabmRescueAny</c> regardless of side while the ability
/// mask includes <c>c_eabmRescueAny</c> (Igc/common.cpp:282-286); the
/// capability test then passes because <c>HasCapability</c> is any-bit
/// (probetypeigc.h:143-146). The station ally exclusion list
/// (<c>c_sabmRestart | c_sabmUnload | c_sabmRipcord</c>, Igc/common.cpp:305-326)
/// never intersects the eject mask (rescue/rescue-any/land), so allied rescue
/// stations are full targets. The lifepod "nearest friendly base" target key
/// passes the SAME triple mask to probes (WinTrek.cpp:10225-10228,10253-10256)
/// — including <c>c_sabmLand</c> (0x20), which aliases
/// <c>c_eabmShootStations</c> on expendables (Igc/igc.h:772,783), so a friendly
/// probe with that bit qualifies too (a native mask-alias quirk kept for
/// parity).</para>
/// </summary>
public class P977EjectAlliedRescueTests
{
    private const short MySide = 1;
    private const short AlliedSide = 2;
    private const short EnemySide = 3;

    private const short RescueType = 1;      // SabmRescue (0x2000)
    private const short LandOnlyType = 3;    // SabmLand (0x20) only
    private const short NoRescueType = 4;    // no rescue/land bits

    private const short ProbeRescueType = 5;     // c_eabmRescue (0x2000)
    private const short ProbeRescueAnyType = 6;  // c_eabmRescueAny (0x4000)
    private const short ProbePlainType = 7;      // no rescue bits
    private const short ProbeLandAliasType = 8;  // 0x20 only (c_sabmLand / c_eabmShootStations)

    private static short StationSabm(short tid) => tid switch
    {
        RescueType => DataStationTypeIGC.SabmRescue,
        LandOnlyType => DataStationTypeIGC.SabmLand,
        _ => 0,
    };

    private static short ProbeEabm(short tid) => tid switch
    {
        ProbeRescueType => DataStationTypeIGC.SabmRescue,      // = c_eabmRescue
        ProbeRescueAnyType => DataStationTypeIGC.SabmRescueAny, // = c_eabmRescueAny
        ProbeLandAliasType => DataStationTypeIGC.SabmLand,      // = 0x20 = c_eabmShootStations
        _ => 0,
    };

    /// <summary>Alliance groups: <paramref name="a"/> and <paramref name="b"/>
    /// share group 0 (native CHANGE_ALLIANCES semantics, igc.h:4297-4302).</summary>
    private static void Ally(ClientWorld w, short a, short b)
    {
        var groups = new sbyte[ChangeAlliances.SideAlliesMax];
        System.Array.Fill(groups, (sbyte)-1);
        groups[a] = 0;
        groups[b] = 0;
        w.Apply(new ChangeAlliances { Allies = groups });
    }

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

    private static void AddWarp(ClientWorld w, short id, short partnerId, short clusterId, float x) =>
        w.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC(id, partnerId, 100, "", "", $"warp-{id}",
                [x, 0f, 0f], [0f, 0f, -1f], [0f, 0f, 0f, 1f], 1f, clusterId),
        });

    private static void AddWarpPair(ClientWorld w, short firstId, short firstCluster, short secondCluster,
        float firstX, float secondX)
    {
        AddWarp(w, firstId, (short)(firstId + 1), firstCluster, firstX);
        AddWarp(w, (short)(firstId + 1), firstId, secondCluster, secondX);
    }

    [Fact]
    public void Allied_rescue_station_is_chosen_when_it_is_the_only_rescue_point()
    {
        // Igc/common.cpp:267-271 — an ALLIED side's model is c_ttFriendly in
        // ally mode 1 (FindTarget's default, igc.h:4642), so its rescue
        // station is an eject target. The restart/unload/ripcord ally
        // exclusion (common.cpp:305-326) does not apply to the eject mask.
        var w = new ClientWorld();
        Ally(w, MySide, AlliedSide);
        AddStation(w, Station(1, AlliedSide, RescueType, new[] { 100f, 0f, 0f }));

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected the allied rescue station");
        Assert.Equal(Game1.TargetKind.Station, p.Kind);
        Assert.Equal((short)1, p.Id);
    }

    [Fact]
    public void Allied_land_capable_station_is_chosen()
    {
        // Same allied rule with c_sabmLand (0x20) — the third bit of the
        // eject mask (appmsg.cpp:233).
        var w = new ClientWorld();
        Ally(w, MySide, AlliedSide);
        AddStation(w, Station(2, AlliedSide, LandOnlyType, new[] { 40f, 0f, 0f }));

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected the allied land-capable station");
        Assert.Equal((short)2, p.Id);
    }

    [Fact]
    public void Enemy_station_is_never_chosen_even_when_rescue_capable()
    {
        // common.cpp:267-271 — a non-allied side maps to c_ttEnemy, which the
        // c_ttFriendly mask rejects; only the OT_probe RescueAny exception
        // (common.cpp:282-286) can bypass the side gate, never a station.
        var w = new ClientWorld();
        Ally(w, MySide, AlliedSide);
        AddStation(w, Station(3, EnemySide, RescueType, new[] { 1f, 0f, 0f }));

        Assert.Null(Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm));
    }

    [Fact]
    public void Allied_rescue_any_probe_is_chosen()
    {
        // An allied probe is already c_ttFriendly (common.cpp:267-271) and its
        // RescueAny type passes the any-bit capability test
        // (common.cpp:329-333; probetypeigc.h:143-146).
        var w = new ClientWorld();
        Ally(w, MySide, AlliedSide);
        AddProbe(w, 1, AlliedSide, ProbeRescueAnyType, new SimVec(30f, 0f, 0f));

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected the allied rescue-any probe");
        Assert.Equal(Game1.TargetKind.Probe, p.Kind);
        Assert.Equal((short)1, p.Id);
    }

    [Fact]
    public void Non_allied_rescue_any_probe_is_chosen_for_any_side()
    {
        // common.cpp:282-286 — the probe RescueAny exception is checked
        // independently of the side mask: an ENEMY probe whose type carries
        // c_eabmRescueAny is findable (it rescues any lifepod that touches
        // it, igc.h:782), and the following HasCapability(mask) then passes
        // because the mask includes RescueAny.
        var w = new ClientWorld();
        Ally(w, MySide, AlliedSide);
        AddProbe(w, 2, EnemySide, ProbeRescueAnyType, new SimVec(10f, 0f, 0f));

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected the enemy rescue-any probe");
        Assert.Equal(Game1.TargetKind.Probe, p.Kind);
        Assert.Equal((short)2, p.Id);
    }

    [Fact]
    public void Enemy_probe_with_plain_rescue_only_is_not_chosen()
    {
        // The side bypass exists ONLY for c_eabmRescueAny (common.cpp:282-286):
        // an enemy probe with plain c_eabmRescue stays c_ttEnemy and is
        // rejected by the c_ttFriendly mask.
        var w = new ClientWorld();
        AddProbe(w, 3, EnemySide, ProbeRescueType, new SimVec(10f, 0f, 0f));

        Assert.Null(Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm));
    }

    [Fact]
    public void Friendly_probe_with_only_the_land_bit_alias_qualifies()
    {
        // WinTrek.cpp:10225-10228 — the lifepod target key passes
        // c_sabmRescue | c_sabmRescueAny | c_sabmLand to PROBES too, and
        // 0x20 aliases c_eabmShootStations on expendables (igc.h:772,783), so
        // native admits a friendly probe carrying only that bit. PCore001's
        // 'Nova Bomb Reactor [T]' (eabm 0x0078) is the real-art example.
        var w = new ClientWorld();
        AddProbe(w, 4, MySide, ProbeLandAliasType, new SimVec(20f, 0f, 0f));

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected the friendly land-alias probe");
        Assert.Equal(Game1.TargetKind.Probe, p.Kind);
        Assert.Equal((short)4, p.Id);
    }

    [Fact]
    public void Non_allied_probe_with_only_the_land_bit_alias_is_not_chosen()
    {
        // The 0x20 alias only helps FRIENDLY probes (the side gate first,
        // common.cpp:267-271); the any-side bypass is RescueAny only
        // (common.cpp:282-286).
        var w = new ClientWorld();
        AddProbe(w, 5, EnemySide, ProbeLandAliasType, new SimVec(20f, 0f, 0f));

        Assert.Null(Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm));
    }

    [Fact]
    public void Nearest_qualifying_target_wins_allied_or_own()
    {
        // FindTarget with no current command target adds c_ttNearest
        // (common.cpp:489-491) and keeps the strictly nearest findable model
        // (common.cpp:568-573,606-615 bReplace = d < distance). The port's
        // pick is nearest across the known sectors (P540's documented
        // approximation of the c_ttAnyCluster warp BFS).
        var w = new ClientWorld();
        Ally(w, MySide, AlliedSide);
        AddStation(w, Station(10, AlliedSide, RescueType, new[] { 100f, 0f, 0f }));  // allied, NEAR
        AddStation(w, Station(11, MySide, RescueType, new[] { 900f, 0f, 0f }));      // own, far
        AddStation(w, Station(12, EnemySide, RescueType, new[] { 1f, 0f, 0f }));     // enemy — never

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected a rescue target");
        Assert.Equal((short)10, p.Id);   // the nearer ALLIED station beats the farther own-side one

        // And the mirror: an own-side station that is nearest still wins.
        var w2 = new ClientWorld();
        Ally(w2, MySide, AlliedSide);
        AddStation(w2, Station(13, MySide, RescueType, new[] { 50f, 0f, 0f }));       // own, NEAR
        AddStation(w2, Station(14, AlliedSide, RescueType, new[] { 800f, 0f, 0f })); // allied, far

        var pick2 = Game1.ChooseEjectRescueTarget(w2, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick2 is not { } p2) throw new InvalidOperationException("expected a rescue target");
        Assert.Equal((short)13, p2.Id);
    }

    [Fact]
    public void Nearest_rescue_any_probe_beats_farther_own_station()
    {
        var w = new ClientWorld();
        AddProbe(w, 6, EnemySide, ProbeRescueAnyType, new SimVec(25f, 0f, 0f));     // any-side RescueAny, near
        AddStation(w, Station(15, MySide, RescueType, new[] { 700f, 0f, 0f }));     // own, far
        AddProbe(w, 7, EnemySide, ProbePlainType, new SimVec(5f, 0f, 0f));        // enemy plain — never

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0), StationSabm, ProbeEabm);
        if (pick is not { } p) throw new InvalidOperationException("expected the rescue-any probe");
        Assert.Equal(Game1.TargetKind.Probe, p.Kind);
        Assert.Equal((short)6, p.Id);
    }

    [Fact]
    public void Current_cluster_target_wins_before_any_remote_cluster()
    {
        // common.cpp:648-655 returns a same-cluster target immediately. Raw
        // coordinates in another sector are NOT commensurate with the pod's
        // position; native never lets a remote Euclidean-nearer target jump
        // ahead of a local rescue point.
        var w = new ClientWorld();
        AddWarpPair(w, 100, 1, 2, 500f, 0f);
        AddStation(w, Station(20, MySide, RescueType, [1000f, 0f, 0f]) with { ClusterId = 1 });
        AddStation(w, Station(21, MySide, RescueType, [1f, 0f, 0f]) with { ClusterId = 2 });

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0),
            StationSabm, ProbeEabm, fromClusterId: 1);
        Assert.Equal((short)20, pick!.Value.Id);
    }

    [Fact]
    public void Remote_search_is_breadth_first_then_nearest_from_the_entry_warp()
    {
        // common.cpp:658-729: hop brackets dominate raw distance; within the
        // first matching remote cluster pposition is that cluster's entry warp.
        var w = new ClientWorld();
        AddWarpPair(w, 100, 1, 2, 500f, 100f);   // cluster 1 -> 2 (one hop)
        AddWarpPair(w, 102, 2, 3, 200f, 300f);   // cluster 2 -> 3 (two hops)
        AddStation(w, Station(22, MySide, RescueType, [900f, 0f, 0f]) with { ClusterId = 2 });   // 800 from entry
        AddStation(w, Station(23, MySide, RescueType, [110f, 0f, 0f]) with { ClusterId = 2 });   // 10 from entry: wins
        AddStation(w, Station(24, MySide, RescueType, [1f, 0f, 0f]) with { ClusterId = 3 });     // raw-nearer but 2 hops

        var pick = Game1.ChooseEjectRescueTarget(w, MySide, new SimVec(0, 0, 0),
            StationSabm, ProbeEabm, fromClusterId: 1);
        Assert.Equal((short)23, pick!.Value.Id);
    }
}
