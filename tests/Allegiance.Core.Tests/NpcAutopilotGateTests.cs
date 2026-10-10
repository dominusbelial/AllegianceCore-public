using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P155 (NPC autopilot foundation) — the safety and exclusion gates of the new
/// same-cluster "face and thrust" autopilot, tested in the pure domain (no
/// sockets) exactly like <c>NPCTakeCoverTests</c>: each test drives the real
/// per-tick hook through the public <see cref="Model.Update"/> entry (which
/// runs <see cref="Ship.OnUpdate"/> on a single ship), so the gates are
/// exercised as they actually run.
///
/// These prove the two invariants a real-player or out-of-sector NPC must never
/// violate:
///   • <see cref="Ship.IsNpc"/> == false ⇒ the autopilot never touches
///     <see cref="Ship.Controls"/> (a real player's Controls are wire-only), so
///     a non-NPC ship with a stray PlanTarget does not budge;
///   • a PlanTarget in a DIFFERENT cluster (target.ClusterId != the ship's
///     <see cref="Sector"/> id) ⇒ the same-cluster gate holds and the ship does
///     not guess at unreachable movement.
/// A positive control (same-cluster NPC with PlanTarget gets measurably closer)
/// is included so the gates are shown against a working primitive, not in a
/// vacuum.
/// </summary>
public sealed class NpcAutopilotGateTests
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

    private static Ship NewShip(Mission m, Sector sector, short side, float x, float y, float z, bool npc)
    {
        var ship = new Ship(9100, m.Core.HullTypes.Values.OrderBy(h => h.HitPoints).First(),
                            new Vector3(x, y, z), Vector3.Zero, Orientation.Identity)
        {
            IsNpc = npc,
        };
        ship.SideId = side;
        m.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>Build a genuine <see cref="DataStationIGC"/> at the given
    /// position carrying a land-capable type, as a drop-in autopilot target.</summary>
    private static DataStationIGC Target(StaticCore core, short clusterId, short side, short id,
                                         float x, float y, float z)
    {
        var type = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmLand) != 0);
        return new DataStationIGC(
            Position: [x, y, z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: clusterId, SideId: side, StationId: id,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
    }

    private static float Dist(Ship s, DataStationIGC t) =>
        (new Vector3(t.Position[0], t.Position[1], t.Position[2]) - s.Position).Length();

    /// <summary>Tick a single ship forward <paramref name="steps"/> chunks of
    /// <paramref name="dt"/> seconds each (real <see cref="Ship.OnUpdate"/> each
    /// step, via <see cref="Model.Update"/>).</summary>
    private static void Tick(Ship ship, int steps, double dt)
    {
        double t = 0.0;
        for (int i = 0; i < steps; i++) { t += dt; ship.Update(t); }
    }

    /// <summary>⭐ The <see cref="Ship.IsNpc"/> gate: a real player ship
    /// (IsNpc=false) with a stray PlanTarget set must NOT have its Controls
    /// overridden nor move — its Controls remain exclusively wire-driven
    /// (P155 scope: this task only ever touches IsNpc ships).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_player_ship_controls_are_never_overridden()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: false);

        // Give the non-NPC a (hypothetical) promoted PlanTarget in the SAME sector —
        // the wire-up must still refuse to steer a real player's ship.
        ship.PlanTarget = Target(core, sector.Id, side: 0, id: 4151, x: 100f, y: 0f, z: 0f);

        Tick(ship, 200, dt: 0.05);   // 10 simulated seconds

        Assert.Equal(ControlData.Neutral, ship.Controls);   // untouched by the autopilot
        Assert.Equal(0f, ship.Velocity.Length());           // never accelerated
        Assert.Equal(new Vector3(700f, 720f, 680f), ship.Position);   // never moved
        Console.WriteLine("P155-PLAYER: non-NPC ship with a PlanTarget did not move and Controls stayed Neutral (IsNpc gate)");
    }

    /// <summary>⭐ The same-cluster gate: an NPC whose PlanTarget lives in a
    /// DIFFERENT cluster than its own <see cref="Sector"/> id does nothing —
    /// it does not guess at unreachable cross-cluster movement (the honest gap
    /// this session also established for FindTarget's c_ttAnyCluster exclusion).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_cluster_plan_target_does_not_move_the_npc()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 700f, y: 720f, z: 680f, npc: true);

        // PlanTarget names a station in some OTHER cluster (id != 1052).
        ship.PlanTarget = Target(core, clusterId: (short)(sector.Id + 987), side: 0, id: 4161, x: 100f, y: 0f, z: 0f);

        Tick(ship, 200, dt: 0.05);

        Assert.Equal(ControlData.Neutral, ship.Controls);   // autopilot never engaged
        Assert.Equal(new Vector3(700f, 720f, 680f), ship.Position);   // no movement
        Console.WriteLine("P155-CROSSCLUSTER: NPC with an out-of-sector PlanTarget did not move (same-cluster gate, Sector.cs:33)");
    }

    /// <summary>Positive control: an NPC with a same-cluster PlanTarget actually
    /// gets measurably closer to it over real ticks — the primitive the gates
    /// protect, working. Kept here as the fast pure-domain complement to the
    /// live Server.SpawnNpcShip proof in <c>NpcAutopilotLiveTests</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_cluster_npc_with_plan_target_gets_measurably_closer()
    {
        Require.Artwork();
        var core = Core();
        var mission = NewMission(core);
        var sector = Sector(mission);
        var ship = NewShip(mission, sector, side: 0, x: 880f, y: 895f, z: 922f, npc: true);
        var target = Target(core, sector.Id, side: 0, id: 4171, x: 590f, y: 902f, z: 928f);
        ship.PlanTarget = target;

        float d0 = Dist(ship, target);
        Assert.True(d0 > 200f, "initial separation should be substantial for a fair closing test");

        Tick(ship, 4000, dt: 0.025);   // 100 simulated seconds, fine chunks

        float d1 = Dist(ship, target);
        Assert.True(d1 < d0 - 120f, $"NPC should close a lot: d0={d0:F1} d1={d1:F1}");
        Console.WriteLine($"P155-DOMAIN: same-cluster NPC d0={d0:F1} -> d1={d1:F1} (gets measurably closer)");
    }
}
