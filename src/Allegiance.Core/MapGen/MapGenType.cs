namespace Allegiance.Core.MapGen;

/// <summary>
/// P397 — the mission-map topology selector. Port of the native
/// <c>MissionParams.mmMapType</c> enum's <em>dispatch</em> concept
/// (<c>mapmakerigc.cpp:129-166</c>): a mission config picks a topology
/// explicitly, never at random. Only <see cref="SingleRing"/> is implemented
/// this task — the native default/simplest topology — as the foundation proof;
/// the other 14 native values are separate follow-up tasks.
/// </summary>
public enum MapGenType
{
    /// <summary>
    /// The native default <c>c_mmSingleRing</c> — the base-class
    /// <c>CmapMakerIGC</c> behavior with zero topology-specific overrides:
    /// a plain ring of alternating team (home) and neutral clusters
    /// (<c>mapmakerigc.h:262-269</c>).
    /// </summary>
    SingleRing = 0,
}
