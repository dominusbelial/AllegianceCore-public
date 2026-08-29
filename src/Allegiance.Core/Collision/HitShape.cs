namespace Allegiance.Core.Collision;

/// <summary>
/// Mirror of the C++ <c>HitTestShape</c> constants
/// (<c>src/_Utility/Utility.h:688-701</c>). A collider's shape is an
/// <see cref="int"/>: the negative values are the fixed primitive shapes, and any
/// non-negative value is a <b>convex-hull sub-index</b> in the range
/// [<see cref="ConvexHullMin"/>, <see cref="ConvexHullMax"/>), selecting one
/// sub-hull of a multi-hull <c>.cvh</c> file.
/// </summary>
public static class HitShape
{
    /// <summary><c>c_htsPoint</c> — a single point (<c>Utility.h:691</c>).</summary>
    public const int Point = -4;

    /// <summary><c>c_htsSphere</c> — an oriented-free bounding sphere (<c>Utility.h:692</c>).</summary>
    public const int Sphere = -3;

    /// <summary><c>c_htsEllipse</c> — has a closed-form point-inside test (<c>Utility.h:695</c>).</summary>
    public const int Ellipse = -2;

    /// <summary><c>c_htsCone</c> — (<c>Utility.h:696</c>).</summary>
    public const int Cone = -1;

    /// <summary><c>c_htsConvexHullMin</c> — first convex-hull sub-index (<c>Utility.h:700</c>).</summary>
    public const int ConvexHullMin = 0;

    /// <summary><c>c_htsConvexHullMax</c> — one past the last convex-hull sub-index (<c>Utility.h:701</c>).</summary>
    public const int ConvexHullMax = 127;
}
