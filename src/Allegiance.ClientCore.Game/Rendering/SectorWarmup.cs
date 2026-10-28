using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>What kind of per-type GPU resource a warm-up item creates.</summary>
public enum WarmAssetKind
{
    /// <summary>An asteroid rock: a native <c>.mdl</c> mesh + its native texture
    /// (<see cref="NativeAsteroidRenderer"/>).</summary>
    Rock,
    /// <summary>A station model: its native <c>.mdl</c> + per-part textures
    /// (<see cref="NativeStationRenderer"/>).</summary>
    Station,
    /// <summary>A ship hull: its native <c>.mdl</c> + native texture
    /// (<see cref="NativeShipRenderer"/>).</summary>
    Ship,
    /// <summary>A sky asset for a cluster — the native planet poster texture and
    /// (HD tier) the environment dome for its poster.</summary>
    Sky,
}

/// <summary>
/// P862 — one per-type asset a sector needs before its first frame can draw it.
/// <see cref="Stem"/> is the cache key the owning renderer already uses
/// (a model stem, a texture stem, a planet stem or a poster stem), so a warmed
/// item is a plain call into the SAME loader the draw path uses: the resource is
/// created once, stays resident for the match, and the first real draw is a cache
/// hit.
/// </summary>
/// <param name="Kind">Which loader owns the stem.</param>
/// <param name="Stem">The asset stem (e.g. <c>bgrnd05</c>, <c>ss24a</c>,
/// <c>nebplnt06bmp</c>).</param>
/// <param name="Hint">The map's texture-name override for the model (usually
/// empty; the renderers resolve the model's own texture namespace when it is).</param>
/// <param name="SectorId">The sector that needs the asset — used only for
/// priority (an aleph approach promotes the sector being approached).</param>
public readonly record struct WarmAsset(WarmAssetKind Kind, string Stem, string? Hint, short SectorId)
{
    /// <summary>Dedupe key: one resource per (kind, stem) regardless of sector —
    /// two sectors sharing a rock model load it exactly once.</summary>
    public string Key => $"{(int)Kind}/{Stem}";
}

/// <summary>
/// P862 — the sector asset warm-up queue.
///
/// <para><b>Why.</b> The port resolves each model's native mesh, native texture
/// and HD material lazily, inside the first Draw that needs it. Nothing new
/// happens when a sector is crossed that would not happen the first time any
/// model is seen — but a warp from an empty sector into one that has never been
/// drawn (Alderan's 134-rock field, its stations, the hulls of the ships already
/// fighting there) makes ALL of that work land inside the ONE frame that first
/// draws the new sector: 2048² native rock textures are 8.4 MB RGB565 files that
/// decode into a 16.7 MB RGBA buffer and upload through MonoGame, so a handful
/// of them in one frame is the measured 18 fps hitch. Native Allegiance has the
/// same "load on cluster entry" shape (WinTrek/Igc cluster change) but its
/// 2000-era content is 512²-1024², so the stall never showed.</para>
///
/// <para><b>What.</b> This queue is the pure scheduler: it knows which assets a
/// sector needs, dedupes them across sectors, lets an imminent warp promote one
/// sector's items, and hands them out one at a time under a per-frame time
/// budget. The client executes each item by calling the real loader
/// (<see cref="Game1"/>'s <c>WarmAsset</c>) and marks it done, so a second entry
/// into the same sector does no new work at all — the loaders' caches are
/// permanent for the match.</para>
/// </summary>
public sealed class SectorWarmupQueue
{
    private readonly List<WarmAsset> _queue = new();
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warmed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Items waiting to be warmed.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>Items handed to the loader so far.</summary>
    public int WarmedCount { get; private set; }

    /// <summary>Items that actually created a new resource (<c>false</c> = it was
    /// already resident, e.g. two sectors sharing one rock model).</summary>
    public int CreatedCount { get; private set; }

    /// <summary>Items that were already resident when warmed.</summary>
    public int AlreadyResidentCount => WarmedCount - CreatedCount;

    /// <summary>Items whose load failed (missing art — logged by the renderer).</summary>
    public int FailedCount { get; private set; }

    /// <summary>Every (kind, stem) the queue has ever accepted, warmed or not.</summary>
    public int KnownCount => _known.Count;

    /// <summary>Pending items, front = next to warm (diagnostics/tests).</summary>
    public IReadOnlyList<WarmAsset> Pending => _queue;

    /// <summary>Queue <paramref name="asset"/> unless it is already queued or
    /// already warmed. Returns false for a duplicate (any sector).</summary>
    public bool Enqueue(in WarmAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Stem)) return false;
        if (!_known.Add(asset.Key)) return false;
        _queue.Add(asset);
        return true;
    }

    /// <summary>Enqueue every item, returning how many were newly accepted.</summary>
    public int EnqueueAll(IEnumerable<WarmAsset> assets)
    {
        int added = 0;
        foreach (var asset in assets)
            if (Enqueue(asset)) added++;
        return added;
    }

    /// <summary>Move every pending item belonging to <paramref name="sectorId"/>
    /// to the FRONT of the queue, preserving their relative order, so an imminent
    /// warp (the own ship is on final approach to an aleph) is served before the
    /// rest of the match's static world. Returns how many moved.</summary>
    public int Promote(short sectorId)
    {
        if (_queue.Count == 0) return 0;
        var promoted = new List<WarmAsset>();
        var rest = new List<WarmAsset>(_queue.Count);
        foreach (var item in _queue)
            (item.SectorId == sectorId ? promoted : rest).Add(item);
        if (promoted.Count == 0) return 0;
        _queue.Clear();
        _queue.AddRange(promoted);
        _queue.AddRange(rest);
        return promoted.Count;
    }

    /// <summary>How many pending items belong to a sector.</summary>
    public int PendingFor(short sectorId)
    {
        int n = 0;
        foreach (var item in _queue)
            if (item.SectorId == sectorId) n++;
        return n;
    }

    /// <summary>Take the next item (front of the queue), or false when empty.</summary>
    public bool TryTake(out WarmAsset asset)
    {
        if (_queue.Count == 0)
        {
            asset = default;
            return false;
        }
        asset = _queue[0];
        _queue.RemoveAt(0);
        return true;
    }

    /// <summary>Record the outcome of warming <paramref name="asset"/>. A warmed
    /// item is never handed out again (the resource is resident for the match).</summary>
    public void Complete(in WarmAsset asset, bool created, bool failed = false)
    {
        if (!_warmed.Add(asset.Key)) return;
        WarmedCount++;
        if (failed) FailedCount++;
        else if (created) CreatedCount++;
    }

    /// <summary>Whether this asset has already been warmed (a second entry into
    /// the sector must do no new loads).</summary>
    public bool IsWarmed(in WarmAsset asset) => _warmed.Contains(asset.Key);
}

/// <summary>
/// P862 — the per-frame time budget the warm-up drains under. One item is started
/// only when the previous frame's items actually fit: a single 2048² texture
/// decode cannot be split, so the budget bounds the NUMBER of items (usually one
/// heavy texture per frame) and spreads a sector's set over the frames between the
/// aleph approach and the transit.
/// </summary>
public sealed class WarmupBudget
{
    private double _spent;

    /// <param name="frameBudgetMs">How much warm-up work a frame may do. In flight
    /// this is a couple of milliseconds (a 60 fps frame has ~16.7 ms and the scene
    /// owns most of it); while docked or on a loading screen the client raises it
    /// because no flight frame is being measured.</param>
    public WarmupBudget(double frameBudgetMs) => FrameBudgetMs = frameBudgetMs;

    /// <summary>Current per-frame budget in milliseconds.</summary>
    public double FrameBudgetMs { get; set; }

    /// <summary>Milliseconds spent this frame.</summary>
    public double SpentMs => _spent;

    /// <summary>Frames this budget has been reset for (diagnostics).</summary>
    public int Frames { get; private set; }

    /// <summary>Start a new frame's budget.</summary>
    public void BeginFrame()
    {
        _spent = 0;
        Frames++;
    }

    /// <summary>True while another item may be started this frame. A frame always
    /// allows its FIRST item even when the previous item overshot, so a queue can
    /// never wedge on a budget that a single heavy decode blew past.</summary>
    public bool CanStart() => _spent <= 0 || _spent < FrameBudgetMs;

    /// <summary>Charge a completed item's cost.</summary>
    public void Spend(double ms) => _spent += ms < 0 ? 0 : ms;
}

/// <summary>
/// P862 — the pure "which assets does this sector need" rules. The client feeds
/// them the world's own definitions (the same <c>AsteroidDef</c>/station-type
/// model names the draw path resolves), so nothing here duplicates a lookup the
/// renderer would do differently.
/// </summary>
public static class SectorWarmupPlanner
{
    /// <summary>One unique rock model/texture pair of a sector.</summary>
    public static IEnumerable<WarmAsset> Rocks(short sectorId, IEnumerable<(string? Model, string? Texture)> rocks)
    {
        foreach (var (model, texture) in rocks)
        {
            var stem = model?.Trim();
            if (string.IsNullOrEmpty(stem)) continue;
            yield return new WarmAsset(WarmAssetKind.Rock, stem, Trimmed(texture), sectorId);
        }
    }

    /// <summary>One unique station model per station TYPE in the sector (every
    /// station of a type shares one mesh + texture set).</summary>
    public static IEnumerable<WarmAsset> Stations(short sectorId, IEnumerable<(string? Model, string? Texture)> stations)
    {
        foreach (var (model, texture) in stations)
        {
            var stem = model?.Trim();
            if (string.IsNullOrEmpty(stem)) continue;
            yield return new WarmAsset(WarmAssetKind.Station, stem, Trimmed(texture), sectorId);
        }
    }

    /// <summary>One unique hull model per hull type flying in the sector. The
    /// client only knows a sector's ships from the wire, so this list is built on
    /// demand (at the aleph approach) rather than at mission start.</summary>
    public static IEnumerable<WarmAsset> Ships(short sectorId, IEnumerable<(string? Model, string? Texture)> hulls)
    {
        foreach (var (model, texture) in hulls)
        {
            var stem = model?.Trim();
            if (string.IsNullOrEmpty(stem)) continue;
            yield return new WarmAsset(WarmAssetKind.Ship, stem, Trimmed(texture), sectorId);
        }
    }

    /// <summary>The cluster's sky assets: the native planet poster texture (every
    /// tier draws it) plus, for the native/HD tiers, the poster's environment dome.
    /// The planet stem rides in <see cref="WarmAsset.Stem"/> and the poster (dome)
    /// stem in <see cref="WarmAsset.Hint"/>; either may be empty (a cluster with
    /// neither declares no sky asset and the caller skips the item).</summary>
    public static WarmAsset Sky(short sectorId, string? planetName, string? posterName)
        => new(WarmAssetKind.Sky, Trimmed(planetName) ?? "", Trimmed(posterName), sectorId);

    /// <summary>The key a sky item warms: the planet poster stem when the cluster
    /// has a planet, else the poster (dome) stem. Empty when the cluster declares
    /// neither — the caller skips the item.</summary>
    public static string SkyStem(string? planetName, string? posterName)
    {
        var planet = Trimmed(planetName);
        if (!string.IsNullOrEmpty(planet)) return planet;
        return Trimmed(posterName) ?? "";
    }

    /// <summary>Whether a sky item has anything to warm (the cluster declares a
    /// planet or a poster).</summary>
    public static bool HasSky(in WarmAsset asset)
        => !string.IsNullOrEmpty(asset.Stem) || !string.IsNullOrEmpty(asset.Hint);

    private static string? Trimmed(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim();
    }
}
