namespace Allegiance.Core.MapGen;

/// <summary>
/// P397/P398 — the mission-map topology selector. Port of the native
/// <c>MissionParams.mmMapType</c> enum's <em>dispatch</em> concept
/// (<c>mapmakerigc.cpp:129-166, 182-201</c>): a mission config picks a topology
/// explicitly, never at random. The enum values mirror the native
/// <c>c_mm*</c> constants (<c>igc.h:977-991</c>) so a config naming the native
/// value maps straight onto the same topology. P397 landed the foundation +
/// <see cref="SingleRing"/>; P398 adds the rest of the ring family:
/// <see cref="DoubleRing"/>, <see cref="BigRing"/>, <see cref="HiLo"/>,
/// <c>HiHigher</c> and its two zero-override subclasses
/// <see cref="LargeSplit"/>/<see cref="InsideOut"/> (which dispatch to
/// <see cref="HiHigher"/>'s layout/link logic). P399 adds the radial/spoke
/// family: <see cref="Star"/>, <see cref="PinWheel"/>,
/// <see cref="DiamondRing"/> and <see cref="SnowFlake"/>.
/// </summary>
public enum MapGenType
{
    /// <summary>
    /// The native default <c>c_mmSingleRing</c> — the base-class
    /// <c>CmapMakerIGC</c> behavior with zero topology-specific overrides:
    /// a plain ring of alternating team (home) and neutral clusters
    /// (<c>mapmakerigc.h:262-269</c>). <c>c_mmSingleRing = 0</c>
    /// (<c>igc.h:977</c>).
    /// </summary>
    SingleRing = 0,

    /// <summary><c>c_mmDoubleRing = 1</c> (<c>igc.h:978</c>) — two concentric
    /// rings: the inner ring of neutral clusters links to the outer ring of
    /// team clusters. Overrides <c>LinkClusters</c> only (base layout counts:
    /// 1 neutral per team; <c>mapmakerigc.h:282-301</c>,
    /// <c>mapmakerigc.cpp:1107-1155</c>).</summary>
    DoubleRing = 1,

    /// <summary><c>c_mmBigRing = 7</c> (<c>igc.h:984</c>) — a single ring with 2
    /// neutral sectors between every home sector. Overrides
    /// <c>GenerateLayout</c> (sets neutral-per-team=2) and <c>LinkClusters</c>
    /// (<c>mapmakerigc.h:384-403</c>, <c>mapmakerigc.cpp:1166-1242</c>).</summary>
    BigRing = 7,

    /// <summary><c>c_mmHiLo = 8</c> (<c>igc.h:985</c>) — one "low" ring of 3
    /// neutral sectors per team around a single central (other) sector.
    /// Overrides <c>GenerateLayout</c> and <c>LinkClusters</c>
    /// (<c>mapmakerigc.h:307-326</c>, <c>mapmakerigc.cpp:1252-1369</c>).</summary>
    HiLo = 8,

    /// <summary><c>c_mmHiHigher = 9</c> (<c>igc.h:986</c>) — five neutral
    /// sectors per team plus a central other sector, the richest ring-family
    /// layout; the shared layout/link that <see cref="LargeSplit"/> and
    /// <see cref="InsideOut"/> dispatch onto. Overrides <c>GenerateLayout</c>
    /// and <c>LinkClusters</c> (<c>mapmakerigc.h:332-351</c>,
    /// <c>mapmakerigc.cpp:1380-1544</c>).</summary>
    HiHigher = 9,

    /// <summary><c>c_mmInsideOut = 11</c> (<c>igc.h:988</c>) — a zero-override
    /// subclass of <c>CmapMakerHiHigherIGC</c> (<c>mapmakerigc.h:531-540</c>);
    /// its constructor pins team-clusters-per-team=1 and the shared layout/link
    /// code branches on the id to place each team's home at the inner position
    /// and link it to the centre with a hidden-to-player aleph
    /// (<c>mapmakerigc.cpp:1380-1401, 1483-1544</c>).</summary>
    InsideOut = 11,

    /// <summary><c>c_mmLargeSplit = 14</c> (<c>igc.h:991</c>) — a zero-override
    /// subclass of <c>CmapMakerHiHigherIGC</c> (<c>mapmakerigc.h:523-529</c>);
    /// its constructor pins team-clusters-per-team=2 and the shared
    /// layout/link code skips the split-base intra-team link for this id
    /// (<c>mapmakerigc.cpp:1380-1401, 1483-1544</c>).</summary>
    LargeSplit = 14,

    /// <summary><c>c_mmPinWheel = 2</c> (<c>igc.h:979</c>) — one or more neutral
    /// sectors at the centre with one player sector per team outstretched; the
    /// only topology that overrides <em>population</em> (its centre sector in
    /// single-centre mode gets 2&times; the asteroid counts).
    /// Overrides <c>GenerateLayout</c>, <c>LinkClusters</c> and
    /// <c>PopulateCluster</c> (<c>mapmakerigc.h:439-459</c>,
    /// <c>mapmakerigc.cpp:1821-1917</c>).</summary>
    PinWheel = 2,

    /// <summary><c>c_mmDiamondRing = 3</c> (<c>igc.h:980</c>) — one ring of
    /// neutral sectors in the centre and one around the outside; each team
    /// gets 1-3 sectors connected to both rings. Requires more than 2 teams
    /// (its own <c>IsValid</c>). Overrides <c>GenerateLayout</c> and
    /// <c>LinkClusters</c> (<c>mapmakerigc.h:465-481</c>,
    /// <c>mapmakerigc.cpp:1940-2051</c>).</summary>
    DiamondRing = 3,

    /// <summary><c>c_mmSnowFlake = 4</c> (<c>igc.h:981</c>) — 3 neutral sectors
    /// per team connected in a triangle; each triangle joins its neighbours
    /// to form a (double for &gt;3 teams) ring. Overrides <c>GenerateLayout</c>
    /// and <c>LinkClusters</c> (<c>mapmakerigc.h:493-512</c>,
    /// <c>mapmakerigc.cpp:2061-2236</c>).</summary>
    SnowFlake = 4,

    /// <summary><c>c_mmStar = 10</c> (<c>igc.h:987</c>) — a spoked \"star\":
    /// 5 neutral sectors per team radiating from each team's home, with the
    /// far spokes cross-linked between opposing teams. Overrides
    /// <c>GenerateLayout</c> and <c>LinkClusters</c> (<c>mapmakerigc.h:357-372</c>,
    /// <c>mapmakerigc.cpp:1555-1683</c>).</summary>
    Star = 10,
}
