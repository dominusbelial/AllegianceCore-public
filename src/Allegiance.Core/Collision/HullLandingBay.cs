namespace Allegiance.Core.Collision;

/// <summary>One launch slot (<c>m_positionLaunches[slot]</c> +
/// <c>m_directionLaunches[slot]</c>, <c>src/Igc/hullTypeIGC.h:416-417</c>).</summary>
public readonly record struct HullLaunchSlot(Vector3 Position, Vector3 Direction);

/// <summary>One landing-bay plane (<c>m_positionLandPlanes[slot][plane]</c> +
/// <c>m_directionLandPlanes[slot][plane]</c>, <c>hullTypeIGC.h:422-423</c>).</summary>
public readonly record struct HullLandPlane(Vector3 Position, Vector3 Direction);

/// <summary>One landing slot: an ordered list of planes (<c>m_nLandPlanes[i]</c>
/// of them) that jointly bound the bay (<c>hullTypeIGC.h:419-423</c>).</summary>
public readonly record struct HullLandSlot(IReadOnlyList<HullLandPlane> Planes);

/// <summary>
/// Reconstructed per-hull landing-bay / launch-slot geometry.
/// </summary>
/// <remarks>
/// <see cref="Scale"/> is the model scale factor; <see cref="LaunchSlots"/> and
/// <see cref="LandSlots"/> are already scaled into hull-length units, matching
/// how the native <c>HullTypeIGC::Initialize</c> stores them
/// (<c>hullTypeIGC.cpp:168,198</c> — frame positions <c>* m_scale</c>).
/// </remarks>
public sealed class HullLandingBayGeometry
{
    public required float Scale { get; init; }
    public required IReadOnlyList<HullLaunchSlot> LaunchSlots { get; init; }
    public required IReadOnlyList<HullLandSlot> LandSlots { get; init; }
}

/// <summary>
/// Reconstructs a hull type's landing-bay and launch-slot geometry from its
/// collision-model (<c>.cvh</c>) named frames — the exact source the native
/// engine uses, though there the lookup happens inside
/// <c>HullTypeIGC::Initialize</c> via <c>HitTest::Load(m_data-&gt;modelName)</c>
/// (<c>hullTypeIGC.cpp:56</c>). The <c>.cvh</c> frame names are the contract:
/// launch slots are frames <c>launch1</c>, <c>launch2</c>, …; landing-bay
/// planes are frames <c>garage1A</c>, <c>garage1B</c>, …, <c>garage2A</c>, …
/// (<c>hullTypeIGC.cpp:158-212</c>).
/// </summary>
/// <remarks>
/// <b>Finding (P114):</b> this data is <b>not</b> part of the <c>OT_hullType</c>
/// wire/static record — the native <c>DataHullTypeIGC</c> struct
/// (<c>src/Igc/igc.h:1767-1820</c>) has no slot members, so it can never be
/// decoded from the corpus's <c>.igc</c> hull records. It lives exclusively in
/// the per-model <c>.cvh</c> collision files (136 of the 627 on-disk carry
/// launch/garage frames), which this port already parses
/// (<see cref="CvhReader"/> exposes the named frames as
/// <see cref="CvhFile.Frames"/>). The frame <b>positions</b> are local to the
/// model's unscaled units and must be multiplied by
/// <c>m_scale = 0.5f * length / originalRadius</c> to reach the game's hull
/// length unit — <c>originalRadius</c> is the <c>.cvh</c> header radius
/// (<c>hullTypeIGC.cpp:61-62</c>), i.e. <see cref="CvhFile.Radius"/> here.
/// </remarks>
public static class HullLandingBay
{
    public const int MaxLaunchSlots = 6;       // c_maxLaunchSlots, src/Igc/igc.h:29
    public const int MaxLandSlots = 4;         // c_maxLandSlots,   src/Igc/igc.h:30
    public const int MaxLandPlanes = 6;        // c_maxLandPlanes,  src/Igc/igc.h:31
    public const int MaxShipLaunchSlots = 2;   // c_maxShipLaunchSlots, src/Igc/igc.h:34
    public const int MaxShipLandSlots = 2;     // c_maxShipLandSlots,   src/Igc/igc.h:33

    /// <summary>
    /// Scan <paramref name="model"/>'s frames for launch slots (<c>launchN</c>)
    /// and landing-bay planes (<c>garageNA</c>…), scaled by
    /// <c>0.5f * length / model.Radius</c>.
    /// </summary>
    /// <param name="model">The hull's parsed <c>.cvh</c> collision model (its
    /// <see cref="CvhFile.Frames"/> are scanned).</param>
    /// <param name="length">The hull type's <c>DataHullTypeIGC.Length</c>,
    /// used for the model scale factor.</param>
    public static HullLandingBayGeometry Reconstruct(CvhFile model, float length)
    {
        var scale = 0.5f * length / model.Radius;          // hullTypeIGC.cpp:62
        return Reconstruct(model, scale, "garage", MaxShipLaunchSlots);
    }

    /// <summary>
    /// Reconstruct a <b>station</b> type's landing-bay geometry — the same
    /// <c>garageNA</c> slot/plane algorithm, but from the station's own
    /// <c>capgarage{N}{A..}</c> frames and scaled by
    /// <c>stationtypeigc.cpp:92</c>'s <c>scale = radius / originalRadius</c>
    /// (the station type's collision <c>Radius</c> over the model's header
    /// radius). Native loads these in <c>CstationTypeIGC::Initialize</c>
    /// (<c>stationtypeigc.cpp:77-101</c>: frames <c>capgarageXA</c>,
    /// <c>capgarageXB</c>, …), the data the station dock gate's
    /// <c>CstationIGC::InGarage</c> (<c>stationIGC.cpp:385-436</c>) consumes —
    /// the station twin of <see cref="Reconstruct(CvhFile, float)"/>'s carrier
    /// geometry. Note native sets <c>m_nCapLandSlots = m_nLandSlots</c>
    /// (<c>stationtypeigc.cpp:101</c>), so a station exposes a single land-slot
    /// set used for both fighters and capitals. Launch slots are not consumed
    /// by the dock predicate, so none are reconstructed here
    /// (<c>maxLaunchSlots: 0</c>); the returned geometry is the shared
    /// <see cref="HullLandingBayGeometry"/> shape so it feeds the SAME
    /// geometric <c>InGarage</c> test the carrier bays feed.
    /// </summary>
    public static HullLandingBayGeometry ReconstructStation(CvhFile model, float radius)
    {
        var scale = radius / model.Radius;                 // stationtypeigc.cpp:92
        return Reconstruct(model, scale, "capgarage", maxLaunchSlots: 0);
    }

    /// <summary>The shared slot/plane reconstruction: scan <c>launchN</c> and
    /// <c>{landPrefix}{N}{A..}</c> frames, transform each (the
    /// <see cref="Transform"/> HitTest::Load flip) and scale positions by
    /// <paramref name="scale"/>. <paramref name="landPrefix"/> is <c>garage</c>
    /// for a carrier hull (<c>hullTypeIGC.cpp:178-212</c>) and <c>capgarage</c>
    /// for a station (<c>stationtypeigc.cpp:83-101</c>);
    /// <paramref name="maxLaunchSlots"/> caps the launch scan (hulls: 2, the
    /// port's established <c>MaxShipLaunchSlots</c>; stations: none here).</summary>
    private static HullLandingBayGeometry Reconstruct(CvhFile model, float scale, string landPrefix, int maxLaunchSlots)
    {
        var launches = new List<HullLaunchSlot>();
        for (var slot = 0; ; slot++)                       // hullTypeIGC.cpp:160-173
        {
            if (slot >= maxLaunchSlots) break;
            var frame = Find(model, $"launch{1 + slot}");
            if (frame is null) break;
            var (p, d) = Transform(frame.Value);
            launches.Add(new HullLaunchSlot(p * scale, d));
        }

        var lands = new List<HullLandSlot>();
        for (var slot = 0; ; slot++)                       // hullTypeIGC.cpp:180-212
        {
            if (slot >= MaxLandSlots) break;
            var planes = new List<HullLandPlane>();
            for (var plane = 0; ; plane++)
            {
                if (plane >= MaxLandPlanes) break;
                var frame = Find(model, $"{landPrefix}{1 + slot}{(char)('A' + plane)}");
                if (frame is null) break;
                var (p, d) = Transform(frame.Value);
                planes.Add(new HullLandPlane(p * scale, d));
            }
            if (planes.Count == 0) break;
            lands.Add(new HullLandSlot(planes));
        }

        return new HullLandingBayGeometry
        {
            Scale = scale,
            LaunchSlots = launches,
            LandSlots = lands,
        };
    }

    /// <summary>The load-time frame transform native <c>HitTest::Load</c> applies to
    /// every <c>.cvh</c> frame (<c>src/_Utility/HitTest.cpp:790-797</c>:
    /// <c>position.x = -x; forward.y = -y; forward.z = -z</c>). The port's
    /// <see cref="CvhReader"/> deliberately stores the raw file values (see its
    /// remarks), so this transform is reconstructed here — it is the bridge
    /// between on-disk frame space and the game-space values <c>InGarage</c>'s
    /// native sibling consumes (<c>m_positionLandPlanes</c>, populated from the
    /// transformed frames in <c>HullTypeIGC::Initialize</c>). Also used by
    /// <see cref="HullWeaponPositions"/> so the weapon-muzzle offsets come from
    /// the same HitTest::Load-flipped frame space.</summary>
    internal static (Vector3 Position, Vector3 Direction) Transform(CvhFrame f)
    {
        var p = new Vector3(-f.Position.X, f.Position.Y, f.Position.Z);  // HitTest.cpp:792
        var d = new Vector3(f.Forward.X, -f.Forward.Y, -f.Forward.Z);    // HitTest.cpp:794-796
        return (p, d);
    }

    private static CvhFrame? Find(CvhFile model, string name)
    {
        foreach (var f in model.Frames)
            if (string.Equals(f.Name, name, StringComparison.Ordinal))
                return f;
        return null;
    }
}
