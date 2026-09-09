namespace Allegiance.Core.Collision;

/// <summary>
/// Production resolver/cache for a hull type's landing-bay geometry, keyed by
/// the hull's <c>ModelName</c> — the wiring P114 deliberately left undone. It
/// resolves a carrier hull's <c>.cvh</c> collision model, reconstructs the bay
/// once per distinct model name (via <see cref="HullLandingBay.Reconstruct"/>),
/// then serves the cached geometry to every subsequent per-tick
/// <see cref="Ship.InGarage"/> test for that hull. Frames are therefore never
/// reparsed or re-scanned per tick, mirroring the native engine where the bay
/// planes are loaded exactly once in <c>HullTypeIGC::Initialize</c>
/// (<c>src/Igc/hullTypeIGC.cpp:56,158-212</c> via <c>HitTest::Load(modelName)</c>)
/// and reused for the lifetime of the hull type.
/// </summary>
/// <remarks>
/// <b>Production load path (confirmed, P116):</b> the test-only
/// <c>CvhCorpus.Files</c> (tests/Allegiance.Core.Tests/CvhCorpus.cs) is <em>not</em>
/// the intended production source — it scans two developer trees on every access.
/// Production has no other corpus-loading facility for collision hulls (grep
/// confirms no <c>.cvh</c> is loaded anywhere in <c>src/</c>), so this cache
/// takes a <see cref="Func{TResult}"/> loader and the host supplies the real one:
/// <c>&lt;artPath&gt;/&lt;modelName&gt;.cvh</c>, the same asset-root convention the
/// server already uses to load its static core and maps
/// (<c>ServerConfig.ArtPath</c>, src/Allegiance.Server/ServerConfig.cs:43 — all
/// corpus <c>.cvh</c> files sit flat at that root). Keeping the loader injectable
/// also lets tests plug in the real-corpus <c>CvhCorpus</c> without touching IO.
/// </remarks>
public sealed class HullLandingBayCache
{
    private readonly Func<string, CvhFile?> _loader;   // modelName -> parsed .cvh (or null when corpus lacks it)
    private readonly Dictionary<string, HullLandingBayGeometry?> _bay = new(StringComparer.Ordinal);

    /// <param name="loader">Resolve a hull <c>ModelName</c> to its parsed
    /// <c>.cvh</c> collision model, or <c>null</c> when the corpus has no such
    /// file. Invoked at most once per distinct model name (the reconstructed
    /// geometry is cached thereafter).</param>
    public HullLandingBayCache(Func<string, CvhFile?> loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    /// <summary>
    /// The landing-bay geometry for a hull type (keyed by its <c>ModelName</c>,
    /// so every ship of the same carrier hull shares one reconstruction), or
    /// <c>null</c> when the corpus provides no <c>.cvh</c> for that model or it
    /// has no <c>garageN…</c> frames — the native <c>HitTest::Load</c>-failed /
    /// no-land-slots case under which <c>InGarage</c> is necessarily
    /// <c>false</c> (native <c>landSlots-1 &lt; 0</c>, <c>shipIGC.cpp:4431</c>).
    /// No lock: the per-tick pass runs on the sim thread only.
    /// </summary>
    public HullLandingBayGeometry? Bay(DataHullTypeIGC hull)
    {
        var name = hull.ModelName;
        if (_bay.TryGetValue(name, out var cached)) return cached;

        HullLandingBayGeometry? g = null;
        var model = _loader(name);
        if (model is not null)
        {
            var reconstructed = HullLandingBay.Reconstruct(model, hull.Length);
            g = reconstructed.LandSlots.Count == 0 ? null : reconstructed;
        }
        _bay[name] = g;
        return g;
    }
}
