namespace Allegiance.Core.Collision;

/// <summary>
/// Computes a hull type's per-mount weapon (muzzle) offsets from its
/// collision-model (<c>.cvh</c>) named frames — the port of the weapon-hardpoint
/// block of <c>ChullTypeIGC::Initialize</c> (<c>src/Igc/hullTypeIGC.cpp:89-136</c>).
/// Native builds <c>m_positionWeapons[mount]</c> once at hull-type load from
/// <c>pmhb-&gt;GetFrame(hd.frameName)-&gt;position * m_scale</c> (zero when the
/// frame is missing); <c>CweaponIGC::FireWeapon</c> then spawns each round at
/// <c>ship position + m_positionWeapons[mount] * shipOrientation</c>
/// (<c>src/Igc/weaponIGC.cpp:123,203</c>) — i.e. rounds leave the guns, not the
/// ship's centre (P465c).
/// </summary>
/// <remarks>
/// <b>Source of the frame data (P114-style finding):</b> the weapon-hardpoint
/// offsets are <b>not</b> part of the <c>OT_hullType</c> static record — the
/// native <c>DataHullTypeIGC</c> struct (<c>src/Igc/igc.h:1767-1820</c>) has no
/// muzzle member. They live exclusively in the per-model <c>.cvh</c> named
/// frames (<see cref="CvhFile.Frames"/>), keyed by each hardpoint's
/// <see cref="DataHullTypeIGC.Hardpoint.FrameName"/>. As with the landing bay
/// (<see cref="HullLandingBay"/>), the frame <b>positions</b> are in the model's
/// unscaled units and must be multiplied by
/// <c>m_scale = 0.5f * length / originalRadius</c> (<c>hullTypeIGC.cpp:62</c>,
/// where <c>originalRadius</c> is the <c>.cvh</c> header radius =
/// <see cref="CvhFile.Radius"/>). The port's <see cref="CvhReader"/> stores the
/// <b>raw</b> file values (see its remarks), so the sign flip native
/// <c>HitTest::Load</c> bakes in at load time (<c>src/_Utility/HitTest.cpp:796-801</c>:
/// <c>position.x = -x</c>) is applied here via <see cref="HullLandingBay.Transform"/>
/// — the shared bridge, so the muzzle offsets live in the same flipped frame
/// space as the launch slots / land planes.
/// </remarks>
public static class HullWeaponPositions
{
    /// <summary>
    /// The per-mount weapon offsets for a hull type, already sign-flipped and
    /// scaled into hull-length units. Index <c>i</c> corresponds to hardpoint
    /// <c>i</c> (<c>m_positionWeapons[i]</c>); a mount whose frame is missing
    /// (or an absent <paramref name="model"/>) is <see cref="Vector3.Zero"/> —
    /// the native <c>pfd ? position*scale : Vector::GetZero()</c>
    /// (<c>hullTypeIGC.cpp:102,132</c>), and the port's historic "no hull mesh"
    /// fallback. The returned array is <c>hull.Hardpoints.Length</c> (=<c>MaxWeapons</c>)
    /// long; caching it per <c>ModelName</c> (see <see cref="WeaponMuzzleCache"/>)
    /// reproduces native's once-per-hull-type computation.
    /// </summary>
    public static Vector3[] Compute(DataHullTypeIGC hull, CvhFile? model)
    {
        var result = new Vector3[hull.Hardpoints.Length];
        if (model is null) return result;                        // no .cvh → no muzzle geometry (centre)

        float scale = 0.5f * hull.Length / model.Radius;         // hullTypeIGC.cpp:62
        for (var i = 0; i < hull.Hardpoints.Length; i++)
        {
            var frame = Find(model, hull.Hardpoints[i].FrameName);
            if (frame is null) continue;                         // missing frame → zero (hullTypeIGC.cpp:102,132)
            result[i] = HullLandingBay.Transform(frame.Value).Position * scale;
        }
        return result;
    }

    private static CvhFrame? Find(CvhFile model, string name)
    {
        foreach (var f in model.Frames)
            if (string.Equals(f.Name, name, StringComparison.Ordinal))
                return f;
        return null;
    }
}
