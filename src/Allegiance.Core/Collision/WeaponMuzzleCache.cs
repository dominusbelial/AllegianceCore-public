namespace Allegiance.Core.Collision;

/// <summary>
/// Production resolver/cache for a hull type's per-mount weapon (muzzle)
/// offsets, keyed by the hull's <c>ModelName</c> — the P465c wiring that lets
/// <see cref="Sim.Weapon.FireWeapon"/> spawn each round at its hardpoint
/// (<c>src/Igc/weaponIGC.cpp:203</c>) instead of the ship centre. It resolves a
/// hull's <c>.cvh</c> collision model and computes the offsets once per distinct
/// model name (via <see cref="HullWeaponPositions.Compute"/>), then serves the
/// cached array to every firing weapon of that hull. Frames are therefore never
/// reparsed or re-scaled per shot, mirroring native where the weapon positions
/// are computed exactly once in <c>HullTypeIGC::Initialize</c>
/// (<c>src/Igc/hullTypeIGC.cpp:89-136</c>) and reused for the hull type's life.
/// </summary>
/// <remarks>
/// Same injectable-loader shape as <see cref="HullLandingBayCache"/>: this port
/// does no <c>.cvh</c> IO in <c>Allegiance.Core</c>, so the host supplies a
/// loader (<c>&lt;artPath&gt;/&lt;modelName&gt;.cvh</c>, the same asset-root
/// convention the server already uses for its static core and maps) and tests
/// plug in the real-corpus <c>CvhCorpus</c> without touching IO. No lock: the
/// resolve-then-cache runs on the sim thread only.
/// </remarks>
public sealed class WeaponMuzzleCache
{
    private readonly Func<string, CvhFile?> _loader;   // modelName -> parsed .cvh (or null when corpus lacks it)
    private readonly Dictionary<string, Vector3[]> _offsets = new(StringComparer.Ordinal);

    /// <param name="loader">Resolve a hull <c>ModelName</c> to its parsed
    /// <c>.cvh</c> collision model, or <c>null</c> when the corpus has no such
    /// file. Invoked at most once per distinct model name (the computed offsets
    /// are cached thereafter).</param>
    public WeaponMuzzleCache(Func<string, CvhFile?> loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    /// <summary>
    /// The weapon-muzzle offset for <paramref name="mount"/> of
    /// <paramref name="hull"/>, in the ship's local frame
    /// (<c>GetWeaponPosition(mount)</c>, <c>weaponIGC.cpp:123</c>). Out-of-range
    /// mounts and frame-less mounts are <see cref="Vector3.Zero"/> (the native
    /// missing-frame / zero fallback, <c>hullTypeIGC.cpp:102,132</c>).
    /// </summary>
    public Vector3 Position(DataHullTypeIGC hull, int mount)
    {
        var name = hull.ModelName;
        if (!_offsets.TryGetValue(name, out var arr))
        {
            arr = HullWeaponPositions.Compute(hull, _loader(name));
            _offsets[name] = arr;
        }
        return (uint)mount < (uint)arr.Length ? arr[mount] : Vector3.Zero;
    }
}
