using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P769b — the <c>Ship.SteerToward</c> dead-behind tie-break is the NARROW
/// degenerate case only (goal numerically exactly behind, cross-product signal
/// collapsed): every other rear-hemisphere goal keeps the signed proportional
/// turn, so a goal off to the left turns LEFT and one off to the right turns
/// RIGHT (the P769 broad form forced a fixed +yaw with pitch zeroed — NPC
/// turns up to 270°, reverted in review).
/// </summary>
public sealed class P769bReviewFixTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
    private static MissionMap Map() => MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
    private static DataHullTypeIGC Hull(StaticCore core) =>
        core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));

    private static object? Invoke(Ship ship, string name, params object?[] args)
    {
        try { return typeof(Ship).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    private static Ship ShipFacingForward()
    {
        var mission = Mission.Assemble(Core(), Map(),
            [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], new RecordingIgcSite(), 0);
        var ship = new Ship(7691, Hull(mission.Core), new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeCheatPlayer, SideId = 0 };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    /// <summary>Apply one unit of the commanded yaw the way ExecuteMove does
    /// (Yaw(+rate·dT), Ship.cs:1806-1808) and return the angle between the
    /// turned nose and the goal direction.</summary>
    private static float AngleAfterYaw(Ship ship, float yaw, Vector3 dir)
    {
        var turned = new Orientation(ship.Orientation);   // Orientation is a class — copy, don't alias
        turned.Yaw(MathF.Sign(yaw) * 0.1f);
        var fwd = turned.GetForward();
        float cos = Math.Clamp((fwd * dir) / (fwd.Length() * dir.Length()), -1f, 1f);
        return MathF.Acos(cos);
    }

    private static (float Yaw, float Pitch) Steer(Ship ship, Vector3 goal)
    {
        Invoke(ship, "SteerToward", goal, 0f, 100f, true);
        return (ship.Controls.Yaw, ship.Controls.Pitch);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rear_hemisphere_goals_turn_either_way_and_exactly_behind_still_turns()
    {
        Require.Artwork();
        var ship = ShipFacingForward();
        var fwd = ship.Orientation.GetForward();   // (0, 0, -1) for Identity
        var right = Vector3.CrossProduct(fwd, new Vector3(0, 1, 0)).Normalize();   // horizontal perpendicular

        // Goal 120° off to either side: the proportional law turns each toward
        // its goal, with OPPOSITE yaw signs (the reverted broad form commanded
        // the same fixed +yaw for both, pitch zeroed).
        var dirLeft = (fwd * MathF.Cos(2f * MathF.PI / 3f) - right * MathF.Sin(2f * MathF.PI / 3f));
        var dirRight = (fwd * MathF.Cos(2f * MathF.PI / 3f) + right * MathF.Sin(2f * MathF.PI / 3f));
        var (yawLeft, pitchLeft) = Steer(ship, dirLeft * 300f);
        var (yawRight, pitchRight) = Steer(ship, dirRight * 300f);
        Assert.True(MathF.Abs(yawLeft) > 0.1f, $"left goal produced no turn (yaw {yawLeft})");
        Assert.True(MathF.Abs(yawRight) > 0.1f, $"right goal produced no turn (yaw {yawRight})");
        Assert.True(MathF.Sign(yawLeft) == -MathF.Sign(yawRight),
            $"left/right goals must turn opposite ways, got yaw {yawLeft} vs {yawRight}");
        Assert.True(AngleAfterYaw(ship, yawLeft, dirLeft) < 2f * MathF.PI / 3f,
            "the commanded yaw must carry the nose toward the left goal");
        float angRight = AngleAfterYaw(ship, yawRight, dirRight);
        Assert.True(angRight < 2f * MathF.PI / 3f,
            $"the commanded yaw must carry the nose toward the right goal (yaw {yawRight} pitch {pitchRight} angle {angRight * 180f / MathF.PI:0.#}° fwd-before {fwd} dir {dirRight})");

        // Goal exactly dead-behind: the cross-product signal collapses; the
        // deterministic tie-break still turns (full yaw toward one side).
        var (yawBehind, pitchBehind) = Steer(ship, fwd * -300f);
        Assert.Equal(1f, yawBehind);
    }
}
