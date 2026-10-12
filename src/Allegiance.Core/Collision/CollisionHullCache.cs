using System.Collections.Generic;

namespace Allegiance.Core.Collision;

/// <summary>
/// Production resolver/cache for a model's <c>.cvh</c> collision hull, keyed by
/// the model's <c>ModelName</c> — the P621 wiring that lets the ship/station/
/// asteroid collision passes collide against the model's real convex hulls
/// instead of a bounding sphere. It resolves the parsed <see cref="CvhFile"/>
/// once per distinct model name and serves the cached file to every collider
/// built for that model thereafter.
/// </summary>
/// <remarks>
/// <para><b>Faithful to native.</b> Native <c>modelIGC.cpp:LoadCVH</c>
/// (<c>:105-118</c>) calls <c>HitTest::Create(modelName, …)</c>, which loads the
/// <c>.cvh</c> via <c>HitTest::Load</c> (<c>_Utility/HitTest.cpp:639-825</c>).
/// The C++ caches the parsed <c>MultiHull</c> in a module-level
/// <c>cachedMultiHulls</c> list keyed by the model name (<c>HitTest.cpp:637,
/// 646-662, 810-821</c>) — this cache is the port's equivalent. The filename is
/// <c>&lt;name&gt; + &quot;.cvh&quot;</c>, exactly native's
/// <c>UTL::getFile(name, &quot;.cvh&quot;, …)</c> (<c>_Utility/Utility.cpp:105-133</c>),
/// which appends the <c>.cvh</c> extension to the raw model name (model names
/// carry no extension, e.g. <c>fig05</c> → <c>fig05.cvh</c>).</para>
/// <para><b>Fallback.</b> A model with no resolvable <c>.cvh</c> yields
/// <c>null</c>, and the caller's collider falls back to the sphere primitive —
/// native's <c>HitTest::Create</c> falls back to <c>htsDefault</c>
/// (<c>HitTest.cpp:844-860</c>). Effects/mines/probes pass <c>NULL</c> as the
/// file and use a sphere anyway (scope rule: keep spheres where native uses
/// them).</para>
/// <para><b>Scale.</b> The parsed <c>CvhFile</c> is stored at file scale and is
/// scale-agnostic: each <see cref="Collider"/> applies
/// <c>desiredRadius / originalRadius</c> at construction (<c>HitTest.cpp:568-581</c>),
/// so a single cached file serves every differently-sized instance of a model.
/// No lock: the per-tick collision passes run on the sim thread only.</para>
/// </remarks>
public sealed class CollisionHullCache
{
    private readonly Func<string, CvhFile?> _loader;   // modelName -> parsed .cvh (or null when the corpus lacks it)
    private readonly Dictionary<string, CvhFile?> _hull = new(StringComparer.Ordinal);

    /// <param name="loader">Resolve a model <c>ModelName</c> to its parsed
    /// <c>.cvh</c> collision model, or <c>null</c> when the corpus has no such
    /// file. Invoked at most once per distinct model name (the parsed file is
    /// cached thereafter).</param>
    public CollisionHullCache(Func<string, CvhFile?> loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    /// <summary>
    /// The collision hull for a model name (a cached, shared <see cref="CvhFile"/>),
    /// or <c>null</c> when the corpus provides no <c>.cvh</c> for it. Callers
    /// fall back to a sphere primitive for a <c>null</c> result (native's
    /// <c>HitTest::Create</c> fallback).
    /// </summary>
    public CvhFile? Resolve(string modelName)
    {
        if (_hull.TryGetValue(modelName, out var cached)) return cached;
        var hull = _loader(modelName);
        _hull[modelName] = hull;
        return hull;
    }

    /// <summary>The number of distinct model names resolved so far (diagnostics / tests).</summary>
    public int Count => _hull.Count;
}
