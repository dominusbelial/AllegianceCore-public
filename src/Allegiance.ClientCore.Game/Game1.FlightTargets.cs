using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    /// <summary>The kind of object an autopilot target can be.</summary>
    internal enum TargetKind { Ship, Station, Asteroid, Warp, Probe, Cluster, Treasure, Buoy }

    /// <summary>A selected autopilot target: enough to re-resolve its live
    /// position each frame from <see cref="ClientWorld"/> (ships may move),
    /// plus a display name for the console / window-title readout.
    /// <para><b>HUD-12b — <see cref="UseDefault"/>.</b> Native only resolves a
    /// <c>c_cidDefault</c> command through <c>GetDefaultOrder</c>; every other id
    /// (including <c>c_cidNone</c>) is written unchanged
    /// (<c>WinTrek/WinTrek.cpp:8827-8833</c>). A plain T / click / cycle pick
    /// constructs a target with the default id and therefore resolves; the
    /// explicit <c>c_cidNone</c> callers — <c>TK_TargetSelf</c>
    /// (<c>WinTrek.cpp:10442</c>) and <c>TK_TargetCommand</c>
    /// (<c>WinTrek.cpp:10410</c>) — set <see cref="UseDefault"/> false so the
    /// native no-command payload survives.</para></summary>
    internal readonly record struct AutopilotTarget(
        TargetKind Kind, short Id, string Name, short CommandId = OrderChange.CidNone,
        bool UseDefault = true);
    /// <summary>
    /// P540b — decide the client autopilot's current steering GOAL, routing
    /// cross-sector targets through warps first. This is the single decision
    /// point that fixes the P540 eject bug (\"pod autopilot took me to the middle
    /// of nowhere\": the rescue station was two warps away, but
    /// <see cref=\"ResolveTargetPosition\"/> returned its foreign-sector
    /// coordinates and the P524 law steered at empty space inside the current
    /// sector). Pure + headless so it is unit-testable without a live world.
    ///
    /// Given the own ship's sector and the autopilot target's sector + real local
    /// position, returns:
    /// <list type=\"bullet\">
    /// <item><b>same sector</b> (or unknown target sector) → the target's own
    /// local position (<c>routed=false</c>) — the pre-P540b law unchanged;</item>
    /// <item><b>cross sector</b> → the next warp mouth toward the target's
    /// sector, routed over the REAL warp table via
    /// <see cref=\"Flight.StarmapGotoRouter.TryNextWaypointFromWarps\"/>
    /// (<c>routed=true</c>) — a local coordinate we can actually fly at, not the
    /// target's coordinates in its own sector.</item>
    /// </list>
    /// Returns <c>false</c> when there is no reachable goal (no local position
    /// <i>and</i> no route). The router is only consulted when cross-sector, so
    /// <paramref name=\"allWarps\"/> may be empty for same-sector calls.
    /// </summary>
    internal static bool TryResolveAutopilotGoal(
        short ownSector,
        short? targetSector,
        SimVec? targetLocalPos,
        IReadOnlyList<Flight.StarmapGotoRouter.AllWarp> allWarps,
        out SimVec goal,
        out bool routed)
    {
        goal = default;
        routed = false;
        if (targetSector is null || targetSector.Value == ownSector)
        {
            if (targetLocalPos is { } lp) { goal = lp; return true; }
            return false;
        }
        if (Flight.StarmapGotoRouter.TryNextWaypointFromWarps(
                ownSector, targetSector.Value, allWarps, out var wp))
        {
            goal = new SimVec(wp.X, wp.Y, wp.Z);
            routed = true;
            return true;
        }
        return false;
    }

}
