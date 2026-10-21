namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>HUD-5b — one contact's resolved radar label block plus the strings
/// that render it. Built once per label change and reused while the contact's
/// label inputs stay the same, so the per-frame draw path allocates nothing for
/// a steady radar (107v107 = ~214 contacts every frame).</summary>
public readonly record struct RadarLabelView(
    RadarLabel? Label,
    string? NameLine,
    string? IconNameLine,
    string? NoEyeNameLine,
    string? IconNoEyeNameLine,
    string? RangeLine,
    string BlockKey,
    string[] Drawn);

/// <summary>
/// HUD-5b — per-contact frame-to-frame cache for <see cref="RadarLabelModel.Resolve"/>
/// and its formatted lines. The radar snapshot rebuilds its <see cref="HudContact"/>
/// list every frame (positions and opacity change constantly), but the
/// <em>label inputs</em> — name, stat fractions, command/eye/rip state and the
/// LOD mode — change rarely, and the rounded range only changes the range line.
/// Slots are indexed by contact position in the list; a slot is rebuilt only when
/// one of those inputs differs, so a steady frame allocates no
/// <c>RadarLabel</c>, string or <c>string[]</c> at all.
/// Pure and GPU-free, like the rest of the radar model code.
/// </summary>
public sealed class RadarLabelCache
{
    private struct Slot
    {
        public bool Valid;
        public HudContact Contact;
        public RadarMode Mode;
        public bool CommandCamera;
        public RadarLabelView View;
    }

    private Slot[] _slots = Array.Empty<Slot>();

    /// <summary>HUD-11 — diagnostics: how many times <see cref="Resolve"/> was
    /// called. The row-count pass in <see cref="HudElements"/> is pinned to one
    /// call per contact (not nine Count walks); tests read/clear this.</summary>
    internal int ResolveCalls { get; private set; }
    internal void ResetResolveCalls() => ResolveCalls = 0;

    /// <summary>Resolve (or reuse) the label block for the contact at
    /// <paramref name="index"/>. A negative index bypasses the cache.</summary>
    public RadarLabelView Resolve(int index, in HudContact contact, RadarMode mode, bool commandCamera)
    {
        ResolveCalls++;
        if (index >= 0 && index < _slots.Length)
        {
            ref var slot = ref _slots[index];
            if (slot.Valid && slot.Mode == mode && slot.CommandCamera == commandCamera)
            {
                if (SameLabelInputs(in slot.Contact, in contact))
                    return slot.View;
                if (SameLabelInputsIgnoringRange(in slot.Contact, in contact)
                    && (contact.Range is > 0) == (slot.Contact.Range is > 0))
                {
                    // Only the rounded range moved (the usual case for a flying
                    // contact): refresh the range line, keep the name line and the
                    // drawn-id array. Both strings come from the metre-keyed cache.
                    slot.Contact = contact;
                    var label = slot.View.Label;
                    if (label is { } l)
                    {
                        string? rangeText = contact.Range is > 0 ? RadarLabelModel.RangeText(contact.Range.Value) : null;
                        var updated = l with { RangeText = rangeText };
                        slot.View = slot.View with
                        {
                            Label = updated.HasAny ? updated : null,
                            RangeLine = contact.Range is > 0 ? RadarLabelModel.RangeLineText(contact.Range.Value) : null,
                        };
                    }
                    return slot.View;
                }
            }
        }
        var view = Build(contact, mode, commandCamera);
        if (index >= 0)
        {
            if (index >= _slots.Length)
                Array.Resize(ref _slots, Math.Max(index + 1, Math.Max(8, _slots.Length * 2)));
            _slots[index] = new Slot
            {
                Valid = true,
                Contact = contact,
                Mode = mode,
                CommandCamera = commandCamera,
                View = view,
            };
        }
        return view;
    }

    private static RadarLabelView Build(in HudContact contact, RadarMode mode, bool commandCamera)
    {
        var label = RadarLabelModel.Resolve(contact, mode, commandCamera);
        if (label is not { } l)
            return new RadarLabelView(null, null, null, null, null, null, "radar:none", Array.Empty<string>());
        // HUD-15/HUD-17 — four name-line variants, all cached so switching per
        // frame costs no string: the historical A<id>/Q<id> prefix (fallback when
        // the native command icon is missing) and the bare name used once the
        // icon art draws, each with and without the legacy '*' eye fallback (the
        // '*' stays only when the native eyebmp art is missing).
        var prefixed = FormatNameLine(l);
        return new RadarLabelView(
            l,
            prefixed,
            l.CommandId is null ? prefixed : FormatNameLine(l, includeCommand: false),
            FormatNameLine(l, includeCommand: true, includeEye: false),
            FormatNameLine(l, includeCommand: false, includeEye: false),
            contact.Range is > 0 ? RadarLabelModel.RangeLineText(contact.Range.Value) : null,
            "radar:" + (l.Name ?? l.RangeText ?? "label"),
            DrawnIds(l));
    }

    /// <summary>HUD-17 — pick the cached name line for the art that actually
    /// drew this frame: the native command icon replaces the A/Q prefix and the
    /// native eye replaces the '*' fallback (radarimage.cpp:1199-1208,1333-1352).
    /// Pure so the four-way choice is unit-pinnable.</summary>
    public static string? SelectNameLine(in RadarLabelView view, bool commandArt, bool eyeArt)
        => (commandArt, eyeArt) switch
        {
            (true, true) => view.IconNoEyeNameLine,
            (true, false) => view.IconNameLine,
            (false, true) => view.NoEyeNameLine,
            _ => view.NameLine,
        };

    /// <summary>Native label line order: accepted/queued command icon, then the
    /// detection eye, then the name, then the friendly ripcord seconds
    /// (radarimage.cpp:1234-1310,1199-1208,1021-1034). The port's fallback for
    /// missing command art is an A/Q prefix and for missing eye art a '*' mark
    /// (<paramref name="includeCommand"/>/<paramref name="includeEye"/>); once
    /// the native art draws the caller uses the bare line.</summary>
    public static string? FormatNameLine(RadarLabel label, bool includeCommand = true, bool includeEye = true)
    {
        string prefix = includeCommand && label.CommandId is { } cid
            ? (label.CommandAccepted ? "A" : "Q") + cid + " " : "";
        string eye = includeEye && label.Eye ? "*" : "";
        string rip = label.RipcordSeconds is { } seconds ? $" {seconds}s" : "";
        if (label.Name is { Length: > 0 } name) return prefix + eye + name + rip;
        if (prefix.Length == 0 && eye.Length == 0 && rip.Length == 0) return null;
        return prefix.TrimEnd() + eye + rip;
    }

    private static string[] DrawnIds(RadarLabel label)
    {
        int count = (label.Name is not null ? 1 : 0)
            + (label.RangeText is not null ? 1 : 0)
            + (label.Hull is not null || label.Shield is not null ? 1 : 0)
            + (label.Fill is not null ? 1 : 0)
            + (label.Eye ? 1 : 0)
            + (label.RipcordSeconds is not null ? 1 : 0)
            + (label.CommandId is not null ? 2 : 0);
        if (count == 0) return Array.Empty<string>();
        var ids = new string[count];
        int i = 0;
        if (label.Name is not null) ids[i++] = "Radar name labels";
        if (label.RangeText is not null) ids[i++] = "Radar range labels";
        if (label.Hull is not null || label.Shield is not null) ids[i++] = "Radar hull and shield bars";
        if (label.Fill is not null) ids[i++] = "Radar ore and miner fill bars";
        if (label.Eye) ids[i++] = "Radar friendly detection eyes";
        if (label.RipcordSeconds is not null) ids[i++] = "Radar friendly ripcord seconds";
        if (label.CommandId is not null)
        {
            ids[i++] = "Radar command verb labels";
            // Native conveys accepted/queued orders through the command icon
            // beside the label (radarimage.cpp:1234-1310) and the bracket
            // brightness step — NOT an extra bracket (HUD-5b). The port draws the
            // A/Q text prefix, which marks these two parity rows.
            ids[i++] = label.CommandAccepted
                ? "Radar accepted order brackets"
                : "Radar queued order brackets";
        }
        return ids;
    }

    /// <summary>Everything <see cref="RadarLabelModel.Resolve"/> reads except the
    /// range. Position and opacity are deliberately excluded: they change every
    /// frame and cannot change the label.</summary>
    private static bool SameLabelInputs(in HudContact a, in HudContact b)
        => SameLabelInputsIgnoringRange(in a, in b) && a.Range == b.Range;

    private static bool SameLabelInputsIgnoringRange(in HudContact a, in HudContact b)
        => a.Kind == b.Kind && a.OnScreen == b.OnScreen
           && a.Name == b.Name
           && a.Shield == b.Shield && a.Hull == b.Hull && a.Fill == b.Fill
           && a.IsEnemy == b.IsEnemy && a.IsSelf == b.IsSelf && a.IsSameSide == b.IsSameSide
           && a.Selected == b.Selected && a.AcceptedOrder == b.AcceptedOrder
           && a.QueuedOrder == b.QueuedOrder && a.Threat == b.Threat
           && a.CarriedFlag == b.CarriedFlag && a.CarriedArtifact == b.CarriedArtifact
           && a.Highlight == b.Highlight && a.CommandId == b.CommandId
           && a.CommandAccepted == b.CommandAccepted
           && a.RipcordSeconds == b.RipcordSeconds && a.DetectionEye == b.DetectionEye;
}
