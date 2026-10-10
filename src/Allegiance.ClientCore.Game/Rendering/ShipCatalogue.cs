using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P581b — the committed replacement-model catalogue
/// (<c>assets/ships/catalogue.json</c>). Lists every usable Meshy ship model
/// (clean originals and repaired copies) that any hull may adopt via
/// <c>ship-art.json</c> value <c>"catalogue:&lt;id&gt;"</c>. Pure resolver —
/// no Content, no GPU — so it is headless-testable exactly like
/// <see cref="ShipArtOverride"/>.
///
/// <para><b>Preferred source.</b> A catalogue entry whose repaired copy
/// (<c>repaired/&lt;asset&gt;</c>) is available is preferred over the raw
/// Meshy asset automatically (Domingo: repaired models preferred over raw
/// Meshy). The caller passes the set of actually-loaded model keys (from the
/// client's <c>_shipModels</c>, which includes <c>repaired/…</c> variants), and
/// <see cref="ResolveKey"/> returns the best loaded key for a catalogue id.</para>
/// </summary>
public sealed class ShipCatalogue
{
    /// <summary>A single catalogue entry (mirrors one object in catalogue.json).</summary>
    public sealed record Entry(
        string Id, string Asset, string Source, string Grade,
        string Civ, string Class, string SizeClass, string Thumbnail);

    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);

    /// <summary>Every catalogue id in file order.</summary>
    public IReadOnlyList<string> Ids { get; }

    public int Count => _byId.Count;

    private ShipCatalogue(IReadOnlyList<Entry> entries)
    {
        Ids = entries.Select(e => e.Id).ToList();
        foreach (var e in entries) _byId[e.Id] = e;
    }

    public static ShipCatalogue Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var arr))
                return new ShipCatalogue(Array.Empty<Entry>());
            var list = new List<Entry>();
            foreach (var el in arr.EnumerateArray())
            {
                string S(string n) => el.TryGetProperty(n, out var p) ? p.GetString() ?? "" : "";
                var e = new Entry(S("id"), S("asset"), S("source"), S("grade"),
                                  S("civ"), S("class"), S("size_class"), S("thumbnail"));
                if (e.Id.Length > 0 && e.Asset.Length > 0)
                    list.Add(e);
            }
            return new ShipCatalogue(list);
        }
        catch
        {
            return new ShipCatalogue(Array.Empty<Entry>());
        }
    }

    public static ShipCatalogue LoadFile(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new ShipCatalogue(Array.Empty<Entry>()); }
        catch { return new ShipCatalogue(Array.Empty<Entry>()); }
    }

    public Entry? ById(string id) => _byId.TryGetValue(id, out var e) ? e : null;

    /// <summary>
    /// The best loaded model key for a catalogue id: its repaired copy
    /// (<c>repaired/&lt;asset&gt;</c>) when loaded, else the raw asset when loaded,
    /// else null (asset missing → the caller falls back to native, the same
    /// missing-asset rule as the Meshy override). <paramref name="loadedKeys"/>
    /// is the set of loaded model keys including <c>repaired/…</c> variants.
    /// </summary>
    public string? ResolveKey(string id, IReadOnlySet<string> loadedKeys)
    {
        var e = ById(id);
        if (e is null) return null;
        var repaired = "repaired/" + e.Asset;
        if (loadedKeys.Contains(repaired)) return repaired;
        return loadedKeys.Contains(e.Asset) ? e.Asset : null;
    }

    /// <summary>The underlying asset for a catalogue id (unprefixed), or null.</summary>
    public string? AssetFor(string id) => ById(id)?.Asset;
}
