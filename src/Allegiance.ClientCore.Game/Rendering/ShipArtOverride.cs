using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P565 — the committed per-hull art override (Domingo, 2026-10-07: "some of the meshy
/// ships do actually look good, we may want to save a few"). A small JSON file,
/// conventionally <c>assets/ships/ship-art.json</c>, maps a hull identifier (either its
/// native <c>ModelName</c> or its numeric <c>HullId</c>, as a string key) to a source
/// choice:
/// <code>{ "fig05": "meshy", "12": "native", ... }</code>
///
/// <para><b>Resolution order</b> (this is the pure rule, unit-tested): a GLOBAL force
/// (<c>--ships native|meshy</c>, <see cref="GlobalForce"/>) beats an entry-level override
/// beats the DEFAULT (native, per P566). See <see cref="Resolve"/>.</para>
///
/// <para><b>Format.</b> Keys may be a model name (matched case-insensitively against the
/// hull's <c>ModelName</c>) or a hull id string ("12"). Values are <c>"meshy"</c> /
/// <c>"native"</c>. Unknown keys are ignored; a malformed file degrades to an empty
/// override (never throws at load). Persisted <see cref="Save"/> pretty-prints sorted keys
/// so diffs are stable and reviewable.</para>
///
/// <para>The missing-asset fallback (a chosen Meshy asset that fails to decode → native)
/// is NOT this file's concern: it is a render-time rule in the client (falls back to
/// native, logged once), kept out so this stays a pure key→choice map.</para>
/// </summary>
public sealed class ShipArtOverride
{
    private readonly Dictionary<string, string> _byModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byHullId = new(StringComparer.Ordinal);

    /// <summary>Whether the operator supplied a global force (<c>--ships meshy|native</c>).
    /// When set, it wins over every per-hull entry.</summary>
    public ArtChoice GlobalForce;

    public enum ArtChoice { None, Native, Meshy }

    /// <summary>Load from JSON text. Never throws: malformed input yields an empty override.</summary>
    public static ShipArtOverride Parse(string json)
    {
        var ov = new ShipArtOverride();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (root is null) return ov;
            foreach (var (k, v) in root)
            {
                if (!TryChoice(v, out var c)) continue;
                if (short.TryParse(k, out var id))
                    ov._byHullId[k] = ChoiceWord(c);   // numeric id key (as given, ordinal)
                else
                    ov._byModel[k] = ChoiceWord(c);
            }
        }
        catch { /* degraded override */ }
        return ov;
    }

    /// <summary>Parse a JSON file from disk (UTF-8). Returns empty override on IO/parse failure.</summary>
    public static ShipArtOverride LoadFile(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new ShipArtOverride(); }
        catch { return new ShipArtOverride(); }
    }

    /// <summary>
    /// The resolution rule (flag > override > default native):
    /// <list type="number">
    ///   <item>if <see cref="GlobalForce"/> is set, it is the answer;</item>
    ///   <item>else if the hull (by its <paramref name="modelName"/> or
    ///   <paramref name="hullId"/>) has an override entry, that is the answer;</item>
    ///   <item>else <see cref="ArtChoice.Native"/> (the P566 default).</item>
    /// </list>
    /// </summary>
    public ArtChoice Resolve(string? modelName, short hullId)
    {
        if (GlobalForce != ArtChoice.None) return GlobalForce;
        if (modelName is not null && _byModel.TryGetValue(modelName, out var mc)) return ChoiceOf(mc);
        if (_byHullId.TryGetValue(hullId.ToString(), out var hc)) return ChoiceOf(hc);
        return ArtChoice.Native;
    }

    /// <summary>Set (or clear) a hull's override choice by its model name. Returns the
    /// prior value (null when none) so a caller can report the change.</summary>
    public ArtChoice? SetByModel(string modelName, ArtChoice choice)
    {
        var key = modelName.Trim();
        ArtChoice? prev = _byModel.TryGetValue(key, out var old) ? ChoiceOf(old) : null;
        if (choice == ArtChoice.None) { _byModel.Remove(key); return prev; }
        _byModel[key] = ChoiceWord(choice);
        return prev;
    }

    /// <summary>The number of explicit overrides (for logging).</summary>
    public int Count => _byModel.Count + _byHullId.Count;

    /// <summary>Serialize to pretty JSON with sorted keys (deterministic commits).</summary>
    public string ToJson()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in _byModel) all[k] = v;
        foreach (var (k, v) in _byHullId) all[k] = v;
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(all.OrderBy(x => x.Key, StringComparer.Ordinal)
                                           .ToDictionary(x => x.Key, x => x.Value), options);
    }

    /// <summary>Write the override to <paramref name="path"/> (creates the directory).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }

    private static bool TryChoice(string v, out ArtChoice c)
    {
        c = ChoiceOf(v);
        return c != ArtChoice.None;
    }
    private static ArtChoice ChoiceOf(string v) =>
        string.Equals(v, "meshy", StringComparison.OrdinalIgnoreCase) ? ArtChoice.Meshy
        : string.Equals(v, "native", StringComparison.OrdinalIgnoreCase) ? ArtChoice.Native
        : ArtChoice.None;
    private static string ChoiceWord(ArtChoice c) => c switch
    {
        ArtChoice.Meshy => "meshy",
        ArtChoice.Native => "native",
        _ => "none",
    };
}
