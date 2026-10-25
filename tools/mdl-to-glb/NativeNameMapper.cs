namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P823 — the INVERSE of <see cref="MountKindMapper"/>: synthesizes the native
/// frame name a <c>HP_&lt;Kind&gt;_&lt;Index&gt;</c> node becomes in the written
/// <c>.cvh</c>/<c>.mdl</c>. Used only when no sidecar mount table names the
/// mount (new art); a P812 sidecar carries the original native names and those
/// win unchanged.
/// </summary>
/// <remarks>
/// The synthesized names use the native substring vocabulary so BOTH consumers
/// recognise them:
/// <list type="bullet">
/// <item>The FX classifier keys on substrings (<c>effect/efapp.cpp:562-583</c>:
/// thrust/smoke/rocket → engine, weapon/wepatt/wepemt/wepmnt/trail → attachment
/// point).</item>
/// <item>The sim reads frames BY EXACT NAME from the <c>.cvh</c>
/// (<c>Igc/hullTypeIGC.cpp:80</c> "cockpt", <c>:85</c> "chaff", <c>:89-136</c>
/// the core's per-hardpoint <c>frameName</c>, <c>:158-204</c>
/// "launch&lt;N&gt;" / "garage&lt;N&gt;&lt;A..E&gt;").</item>
/// </list>
/// The 1-based numbering follows native's own conventions (launch slots count
/// from 1, <c>hullTypeIGC.cpp:160-174</c>; land planes A..E per bay,
/// <c>:176-204</c>). Weapon hardpoints are keyed by the CORE's
/// <c>HardpointData.frameName</c>, which core authors set to the same
/// <c>wepmnt&lt;N&gt;</c> vocabulary — the checklist in the P823 report makes
/// that contract explicit for new cores.
/// </remarks>
public static class NativeNameMapper
{
    /// <summary>Number of land planes (garage slots A..E) per docking bay —
    /// native <c>c_maxLandPlanes</c> is 5 in practice
    /// (<c>Igc/hullTypeIGC.cpp:176-204</c> walks 'A'..'E').</summary>
    public const int LandPlanesPerBay = 5;

    public static string NativeNameFor(string kind, int index) => kind switch
    {
        "Turret" => $"turwepmnt{index + 1}",             // "tur" prefix (era files: turwepemt1..) + weapon substring
        "Weapon" => $"wepmnt{index + 1}",                // "wepmnt" is in the FX attachment-point list (efapp.cpp:578)
        "MainEngine" => index == 0 ? "thrust" : $"thrust{index + 1}",
        "Thruster" => $"smoke{index + 1}",               // "smoke" → engine class (efapp.cpp:562-570)
        "Cockpit" => "cockpt",                           // hullTypeIGC.cpp:80
        "Chaff" => "chaff",                              // hullTypeIGC.cpp:85
        "DockingEntrance" => $"garage{index / LandPlanesPerBay + 1}{(char)('A' + index % LandPlanesPerBay)}",
        "DockingExit" => $"launch{index + 1}",           // hullTypeIGC.cpp:158-174
        _ => $"frame{index + 1}",                        // escape kind: no native consumer reads these by name
    };

    /// <summary>True when <paramref name="nativeName"/> is already a usable
    /// native frame name (i.e. it is NOT the GLB node name itself). The GLB
    /// reader stub fills <c>NativeName</c> with the node name when no sidecar
    /// mount table exists; such a value must be re-synthesized.</summary>
    public static bool IsNativeName(string mount, string nodeName) =>
        !string.IsNullOrWhiteSpace(mount) && !string.Equals(mount, nodeName, StringComparison.Ordinal);
}
