using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>How one <c>visuals.*</c> flag is expressed in the profile, the launch
/// override and the options page.</summary>
public enum VisualFlagKind
{
    /// <summary>An on/off switch (profile value <c>true</c>/<c>false</c>).</summary>
    Toggle,

    /// <summary>A numeric setting in world units (profile value a number).</summary>
    Range,
}

/// <summary>
/// P979 — ONE row of the visuals flag table. Adding a visual enhancement means
/// adding a row here plus its use site; there is no per-flag plumbing.
/// <para><b>Default OFF (Domingo, 2026-11-08).</b> Visual enhancements may
/// depart from native's look because native's look is a 1999 artefact, so every
/// flag starts OFF: the default path stays the native one, pinned by tests, and
/// the enhancement is opt-in. A flag changes how things LOOK, never a game rule
/// or what the server lets the client know (fog/sensor rules are behaviour;
/// drawing an object the client already has is visual).</para>
/// </summary>
/// <param name="Key">The profile/launch key, e.g. <c>radialVisibility</c>.</param>
/// <param name="Label">Short human label shown on the Visuals options page.</param>
/// <param name="Description">The one-line description shown next to the label.</param>
/// <param name="Kind">Toggle or numeric range.</param>
/// <param name="DefaultOn">Toggle default; always OFF for the reasons above.</param>
/// <param name="DefaultValue">Numeric default (world units).</param>
/// <param name="MinValue">Numeric clamp floor.</param>
/// <param name="MaxValue">Numeric clamp ceiling.</param>
/// <param name="Step">Numeric step used by the options page's raise/lower rows.</param>
/// <param name="Hotkey">Menu hotkey for a toggle row ('\0' = none).</param>
public sealed record VisualFlag(
    string Key,
    string Label,
    string Description,
    VisualFlagKind Kind,
    bool DefaultOn = false,
    float DefaultValue = 0f,
    float MinValue = 0f,
    float MaxValue = 0f,
    float Step = 0f,
    char Hotkey = '\0');

/// <summary>
/// P979 — the table of visual-enhancement flags. Keep it a TABLE: every future
/// visual task (skybox, tonemap/palette, lighting) appends a row and reads its
/// value at its use site; the profile parsing, launch override, options page and
/// startup log are all table-driven and need no change.
/// </summary>
public static class VisualFlags
{
    /// <summary>Draw objects by true camera distance instead of the planar far
    /// clip (the no-pop-in rule). See <see cref="RadialVisibility"/>.</summary>
    public const string RadialVisibilityKey = "radialVisibility";

    /// <summary>Server-timestamped, reorder-tolerant remote-ship DRAW history.
    /// Visual only: aim, targeting, collision and outbound state stay on wire truth.</summary>
    public const string RemoteTimelineKey = "remoteTimeline";

    /// <summary>The radial-visibility range in metres (native's dead-ahead
    /// distance, <c>WinTrek/WinTrek.cpp:323</c>).</summary>
    public const string ViewRangeKey = "viewRange";

    /// <summary>Every flag, in options-page order.</summary>
    public static IReadOnlyList<VisualFlag> Definitions { get; } = new[]
    {
        new VisualFlag(
            RadialVisibilityKey,
            "Radial visibility",
            "true distance, no far-plane pop-in",
            VisualFlagKind.Toggle,
            Hotkey: 'R'),
        new VisualFlag(
            RemoteTimelineKey,
            "Remote timeline",
            "server-time remote ship smoothing",
            VisualFlagKind.Toggle,
            Hotkey: 'T'),
        new VisualFlag(
            ViewRangeKey,
            "View range",
            "metres the radial rule draws to",
            VisualFlagKind.Range,
            DefaultValue: 10000f,
            MinValue: 1000f,
            MaxValue: 50000f,
            Step: 1000f),
    };

    /// <summary>The row for <paramref name="key"/>, or null for an unknown key.</summary>
    public static VisualFlag? Find(string key)
    {
        foreach (var definition in Definitions)
            if (string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase))
                return definition;
        return null;
    }
}

/// <summary>
/// P979 — the live <c>visuals.*</c> state: the player-profile <c>visuals</c>
/// group (flat <c>key: value</c> object in <c>player.json</c>), the
/// <c>--visuals key=on,...</c> launch override, and the object the flight render
/// reads every frame (so an options-page toggle applies at once).
/// <para><b>Missing = default OFF, unknown keys ignored.</b> A profile without
/// the group, or one carrying keys this build does not know, resolves to the
/// table defaults without an error. The launch override is the only place that
/// reports an unknown key (a console warning), because a hand-typed typo silently
/// doing nothing is worth saying out loud.</para>
/// </summary>
[JsonConverter(typeof(VisualOptionsJsonConverter))]
public sealed class VisualOptions
{
    private readonly Dictionary<string, bool> _toggles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _numbers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first flag's live value — the radial no-pop-in rule.</summary>
    public bool RadialVisibility => IsOn(VisualFlags.RadialVisibilityKey);

    /// <summary>The P985 remote-ship presentation timeline.</summary>
    public bool RemoteTimeline => IsOn(VisualFlags.RemoteTimelineKey);

    /// <summary>The radial rule's range in metres.</summary>
    public float ViewRange => Value(VisualFlags.ViewRangeKey);

    /// <summary>A toggle's live value; an unset/unknown key reads OFF.</summary>
    public bool IsOn(string key)
    {
        if (_toggles.TryGetValue(key, out bool on)) return on;
        return VisualFlags.Find(key)?.DefaultOn ?? false;
    }

    /// <summary>A numeric value; an unset/unknown key reads the table default (0).</summary>
    public float Value(string key)
        => _numbers.TryGetValue(key, out float value) ? value : VisualFlags.Find(key)?.DefaultValue ?? 0f;

    /// <summary>Set a toggle (unknown keys are ignored).</summary>
    public void SetToggle(string key, bool on)
    {
        if (VisualFlags.Find(key) is not { Kind: VisualFlagKind.Toggle }) return;
        _toggles[key] = on;
    }

    /// <summary>Set a numeric value, clamped to the table's range (unknown keys ignored).</summary>
    public void SetValue(string key, float value)
    {
        if (VisualFlags.Find(key) is not { Kind: VisualFlagKind.Range } definition) return;
        if (!float.IsFinite(value)) value = definition.DefaultValue;
        _numbers[key] = Math.Clamp(value, definition.MinValue, definition.MaxValue);
    }

    /// <summary>A copy of this object (the profile value and the effective value
    /// must not alias: an unrelated preferences save must not persist a launch
    /// override into the profile).</summary>
    public VisualOptions Clone()
    {
        var copy = new VisualOptions();
        foreach (var (key, on) in _toggles) copy._toggles[key] = on;
        foreach (var (key, value) in _numbers) copy._numbers[key] = value;
        return copy;
    }

    /// <summary>
    /// Apply a launch override specification: comma-separated <c>key=value</c>
    /// pairs, e.g. <c>radialVisibility=on,viewRange=12000</c>. Toggle values are
    /// <c>on/off</c> (also <c>true/false/1/0</c>); a bare <c>key</c> means on.
    /// Unknown keys and malformed values are appended to
    /// <paramref name="warnings"/> and ignored.
    /// </summary>
    public void ApplySpec(string? spec, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;
        foreach (var raw in spec.Split(','))
        {
            string item = raw.Trim();
            if (item.Length == 0) continue;
            int eq = item.IndexOf('=');
            string key = (eq < 0 ? item : item[..eq]).Trim();
            string value = eq < 0 ? "on" : item[(eq + 1)..].Trim();
            var definition = VisualFlags.Find(key);
            if (definition is null)
            {
                warnings.Add($"--visuals: unknown key '{key}' (ignored)");
                continue;
            }
            if (definition.Kind == VisualFlagKind.Toggle)
            {
                if (TryParseToggle(value, out bool on))
                    SetToggle(definition.Key, on);
                else
                    warnings.Add($"--visuals: '{key}={value}' is not on/off (ignored)");
            }
            else
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                    || !float.IsFinite(number))
                {
                    warnings.Add($"--visuals: '{key}={value}' is not a number (ignored)");
                    continue;
                }
                if (number < definition.MinValue || number > definition.MaxValue)
                    warnings.Add($"--visuals: '{key}={number}' clamped to [{definition.MinValue:0}, {definition.MaxValue:0}]");
                SetValue(definition.Key, number);
            }
        }
    }

    /// <summary>Parse <paramref name="spec"/> into a fresh option set (all other
    /// flags at their table defaults).</summary>
    public static VisualOptions Parse(string? spec, out IReadOnlyList<string> warnings)
    {
        var options = new VisualOptions();
        var list = new List<string>();
        options.ApplySpec(spec, list);
        warnings = list;
        return options;
    }

    /// <summary>
    /// Resolve the effective options at startup: the profile group (or the table
    /// defaults when absent), then the launch override applied on top.
    /// </summary>
    public static VisualOptions Resolve(VisualOptions? profile, string? launchSpec, out IReadOnlyList<string> warnings)
    {
        var options = profile?.Clone() ?? new VisualOptions();
        var list = new List<string>();
        options.ApplySpec(launchSpec, list);
        warnings = list;
        return options;
    }

    /// <summary>The one-line startup log of every resolved flag.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var definition in VisualFlags.Definitions)
            parts.Add(definition.Kind == VisualFlagKind.Toggle
                ? $"{definition.Key}={(IsOn(definition.Key) ? "on" : "off")}"
                : $"{definition.Key}={Format(Value(definition.Key))}");
        return string.Join(' ', parts);
    }

    /// <summary>How a value of this flag reads on the options page ("on"/"off"
    /// or "10000 m").</summary>
    public static string DescribeValue(VisualFlag definition, VisualOptions? options)
    {
        if (definition.Kind == VisualFlagKind.Toggle)
            return (options?.IsOn(definition.Key) ?? definition.DefaultOn) ? "on" : "off";
        return Format(options?.Value(definition.Key) ?? definition.DefaultValue) + " m";
    }

    /// <summary>Invariant integer formatting for the world-unit values.</summary>
    public static string Format(float value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static bool TryParseToggle(string value, out bool on)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on": case "true": case "1": case "yes": on = true; return true;
            case "off": case "false": case "0": case "no": on = false; return true;
            default: on = false; return false;
        }
    }

    /// <summary>Apply one JSON property (used by the converter; unknown keys ignored).</summary>
    internal void ApplyJson(string key, JsonElement value)
    {
        if (VisualFlags.Find(key) is not { } definition) return;
        if (definition.Kind == VisualFlagKind.Toggle)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.True: SetToggle(definition.Key, true); break;
                case JsonValueKind.False: SetToggle(definition.Key, false); break;
                case JsonValueKind.Number: SetToggle(definition.Key, value.GetDouble() != 0); break;
                case JsonValueKind.String:
                    if (TryParseToggle(value.GetString() ?? "", out bool on)) SetToggle(definition.Key, on);
                    break;
            }
            return;
        }
        double number;
        if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble();
        else if (value.ValueKind == JsonValueKind.String
                 && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            number = parsed;
        else return;
        if (double.IsFinite(number)) SetValue(definition.Key, (float)number);
    }
}

/// <summary>
/// P979 — <c>player.json</c>'s <c>visuals</c> group as a flat
/// <c>{ "radialVisibility": true, "viewRange": 12000 }</c> object. Unknown keys
/// are ignored on read (a profile written by a newer build loads here), and the
/// writer emits every table key with its resolved value so the group documents
/// itself.
/// </summary>
public sealed class VisualOptionsJsonConverter : JsonConverter<VisualOptions>
{
    public override VisualOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new VisualOptions();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return result;
        }
        using var document = JsonDocument.ParseValue(ref reader);
        foreach (var property in document.RootElement.EnumerateObject())
            result.ApplyJson(property.Name, property.Value);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, VisualOptions value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var definition in VisualFlags.Definitions)
        {
            if (definition.Kind == VisualFlagKind.Toggle)
                writer.WriteBoolean(definition.Key, value.IsOn(definition.Key));
            else
                writer.WriteNumber(definition.Key, value.Value(definition.Key));
        }
        writer.WriteEndObject();
    }
}
