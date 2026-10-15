namespace Allegiance.Core.Collision;

/// <summary>
/// Production resolver/cache for a station type's landing-bay geometry, keyed
/// by the type's <c>ModelName</c> — the station twin of
/// <see cref="HullLandingBayCache"/> (which serves the carrier landing bays).
/// It resolves a station type's <c>.cvh</c> collision model, reconstructs the
/// bay once per distinct model name (via
/// <see cref="HullLandingBay.ReconstructStation"/>), then serves the cached
/// geometry to every subsequent per-tick
/// <see cref="Sim.Station.InGarage"/> dock test for that type. Frames are never
/// reparsed or re-scanned per tick, mirroring the native engine where the bay
/// planes are loaded exactly once in <c>CstationTypeIGC::Initialize</c>
/// (<c>stationtypeigc.cpp:77-101</c> via <c>HitTest::Load(modelName)</c>) and
/// reused for the lifetime of the station type.
/// </summary>
/// <remarks>
/// <b>Model vs type keying.</b> Native loads the bay per <em>station type</em>
/// from that type's own model <b>and</b> its own radius
/// (<c>scale = radius / originalRadius</c>, <c>stationtypeigc.cpp:92</c>), so
/// two types sharing a model but differing in radius must not share a cached
/// reconstruction. The reconstructed geometry therefore caches per
/// <c>StationTypeId</c> (the model parse caches per <c>ModelName</c>).
/// <b>Absent-model contract.</b> When the corpus provides no <c>.cvh</c> for
/// the model or it has no <c>capgarageN…</c> frames, the bay is <c>null</c> —
/// the native <c>HitTest::Load</c>-failed / no-land-slots case under which
/// <c>InGarage</c> is necessarily <c>false</c> (native <c>landSlots-1 &lt; 0</c>,
/// <c>stationIGC.cpp:388-390</c>), so such a station can never be docked at.
/// No lock: the per-tick pass runs on the sim thread only.
/// </remarks>
public sealed class StationLandingBayCache
{
    private readonly Func<string, CvhFile?> _loader;   // modelName -> parsed .cvh (or null when corpus lacks it)
    private readonly Dictionary<string, CvhFile?> _model = new(StringComparer.Ordinal);
    private readonly Dictionary<short, HullLandingBayGeometry?> _bay = [];

    /// <param name="loader">Resolve a model name to its parsed <c>.cvh</c>
    /// collision model, or <c>null</c> when the corpus has no such file.
    /// Invoked at most once per distinct model name (the parsed model is
    /// cached thereafter); the reconstructed geometry is cached per type.</param>
    public StationLandingBayCache(Func<string, CvhFile?> loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    /// <summary>
    /// The landing-bay geometry for a station type, or <c>null</c> when the
    /// corpus provides no <c>.cvh</c> for its model or that model has no
    /// <c>capgarageN…</c> frames (under which <see cref="Sim.Station.InGarage"/>
    /// is necessarily <c>false</c> — the native no-land-slots case).
    /// </summary>
    public HullLandingBayGeometry? Bay(DataStationTypeIGC type)
    {
        var geometry = LaunchGeometry(type);
        return geometry?.LandSlots.Count > 0 ? geometry : null;
    }

    /// <summary>Station launch frames, including models with no landing bay.</summary>
    public HullLandingBayGeometry? LaunchGeometry(DataStationTypeIGC type)
    {
        if (_bay.TryGetValue(type.StationTypeId, out var cached)) return cached;

        HullLandingBayGeometry? g = null;
        if (!_model.TryGetValue(type.ModelName, out var model))
        {
            model = _loader(type.ModelName);
            _model[type.ModelName] = model;
        }
        if (model is not null)
        {
            var reconstructed = HullLandingBay.ReconstructStation(model, type.Radius);
            g = reconstructed;
        }
        _bay[type.StationTypeId] = g;
        return g;
    }
}
