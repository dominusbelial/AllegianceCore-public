using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// The <b>building effect</b> — the sim representation of a Builder ship
/// committing to construct a station at a target asteroid. P163, Stage 1 of the
/// builder/station-construction arc. This is the port of the native
/// <c>CbuildingEffectIGC</c> (<c>src/Igc/buildingeffectigc.h</c>) reduced to
/// exactly the <b>persistent, timed world-transformation</b> this task scopes:
/// the native's five-phase cosmetic envelope→grow→opaque→shrink animation
/// (<c>buildingeffectigc.h:160-225</c>) is deliberately <b>not</b> represented
/// (this port has no client / no visual-effects system to drive it — confirmed
/// throughout the preceding slices), so the effect tracks only the <b>timing</b>
/// — when the transformation is due — not any radius/colour/alpha progression.
/// The object deliberately mirrors <see cref="Asteroid"/>'s shape/conventions:
/// like a rock it is <b>not</b> a <see cref="Model"/>, does not join the
/// sector's <c>_models</c> loop, and is instead advanced by a dedicated
/// per-sector-tick resolver (<see cref="Model.Sector.ResolveBuildingEffects"/>),
/// the same pass shape as <see cref="Model.Sector.ResolveMining"/> /
/// <see cref="Model.Sector.ResolveWarpCollisions"/>.
/// </summary>
/// <remarks>
/// <para><b>Timing.</b> The native phases are four <c>10.0f</c> constants —
/// <c>c_dtEnvelope = c_dtGrow = c_dtOpaque = c_dtShrink = 10.0f</c>
/// (<c>buildingeffectigc.h:19-22</c>) — summing to a 40-second lifecycle, and the
/// effect's completion time is built as
/// <c>m_timeComplete = m_timeEnvelope + c_dtEnvelope + c_dtGrow + c_dtOpaque +
/// c_dtShrink</c> (<c>buildingeffectigc.h:50-54</c>). This task needs only that
/// sum, captured as <see cref="BuildDuration"/> and applied as the completion
/// deadline <see cref="CompletionTime"/>. (Native nuance, recorded for the doc:
/// the terminal transformation itself fires in <c>AsteroidUpdate</c> at the
/// <b>shrink</b> boundary, <c>now &gt;= m_timeShrink</c>,
/// <c>buildingeffectigc.h:314</c> — 30 s in — whereas the cosmetic object winds
/// down to <c>m_timeComplete</c> (40 s). Since nothing observes the animation
/// here, Stage 1 uses the full 40-second completion deadline the task specifies;
/// the distinction is immaterial to the sim outcome and is noted rather than
/// reproduced.)</para>
/// <para><b>Lifecycle.</b> On completion (<see cref="Model.Sector.ResolveBuildingEffects"/>
/// seeing <c>now &gt;= CompletionTime</c>) the transformation is the native
/// <c>AsteroidUpdate</c> terminal sequence (<c>buildingeffectigc.h:312-353</c>):
/// the asteroid is destroyed and replaced by a real new station of
/// <see cref="StationType"/> at the asteroid's position, and the Builder ship
/// consumes itself (removed from the sector). On interruption (the Builder dies
/// mid-build) the resolver aborts the effect cleanly: no station is created and
/// the asteroid is untouched — the <c>BuilderTerminated()</c> +
/// <c>KillBuildingEffectEvent</c> path (<c>buildingeffectigc.h:294-297</c>,
/// <c>shipIGC.cpp:209-220</c>), minus the wire (the <c>BUILDINGEFFECT_DESTROYED</c>(181)
/// broadcast is explicitly Stage 2, out of scope here).</para>
/// </remarks>
public sealed class BuildingEffect
{
    /// <summary>
    /// The full lifecycle duration in sim-seconds — the sum of the four native
    /// phase constants <c>c_dtEnvelope + c_dtGrow + c_dtOpaque + c_dtShrink =
    /// 10+10+10+10 = 40</c> (<c>buildingeffectigc.h:19-22</c>), the same
    /// accumulation <c>buildingeffectigc.h:50-54</c> folds into
    /// <c>m_timeComplete</c>. Stage 1's whole reason for existing is the timing:
    /// this is the single "how long does a build take" fact the mechanic carries.
    /// </summary>
    public const double BuildDuration = 40.0;

    /// <summary>The asteroid being transformed into a station — the native
    /// <c>m_pasteroid</c>, the rock <c>SetBuildingEffect</c>'d at Initialize
    /// (<c>buildingeffectigc.h:73,86-88</c>). Its position is where the finished
    /// station is placed and it is removed on completion.</summary>
    public Asteroid Target { get; }

    /// <summary>The Builder ship performing the construction — the native
    /// <c>m_pshipBuilder</c> (<c>buildingeffectigc.h:64</c>). Consumed
    /// (self-sacrificed) on a successful build; if it dies first the effect
    /// aborts.</summary>
    public Ship Builder { get; }

    /// <summary>The station type the build produces — the native
    /// <c>m_pstationType</c> (<c>buildingeffectigc.h:69</c>), supplied directly
    /// by the caller (the test) for Stage 1. The full
    /// <c>m_abmOrders</c>/<c>c_aabmBuildable</c> build-ability-bitmask discovery
    /// that lets a Builder <em>choose</em> among types is explicitly Stage 3, not
    /// needed here.</summary>
    public DataStationTypeIGC StationType { get; }

    /// <summary>The side the finished station belongs to — the native
    /// <c>m_pside</c>, captured from the Builder at Initialize
    /// (<c>buildingeffectigc.h:68</c>). Carried here so the completion keeps the
    /// side that committed to the build even if the Builder's own side were to
    /// change before the 40 s elapse (faithful to native's capture-at-start).</summary>
    public short SideId { get; }

    /// <summary>The sim-time at which the build completes and the asteroid →
    /// station transformation fires: <c>start + BuildDuration</c> (the native
    /// <c>m_timeComplete</c>, <c>buildingeffectigc.h:54</c>).</summary>
    public double CompletionTime { get; }

    /// <summary>
    /// Construct an in-progress build. The simulator expects the caller to have
    /// verified the Builder is actually in this sector (placed via
    /// <see cref="Mission.PlaceShip"/>) and that <paramref name="sideId"/> is
    /// the Builder's <see cref="Ship.SideId"/> — kept as a separate argument so
    /// the effect carries the captured side explicitly, mirroring native's
    /// initialize-time capture.
    /// </summary>
    public BuildingEffect(
        Asteroid target,
        Ship builder,
        DataStationTypeIGC stationType,
        short sideId,
        double completionTime)
    {
        Target = target;
        Builder = builder;
        StationType = stationType;
        SideId = sideId;
        CompletionTime = completionTime;
    }
}
