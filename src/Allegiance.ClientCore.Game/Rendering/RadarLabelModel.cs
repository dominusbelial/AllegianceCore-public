using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Resolved radar label block for one contact. Every field is already
/// gated by the native <c>bLabel</c>/<c>bStats</c> rules; null means native
/// suppresses that line. The renderer never re-applies visibility decisions.</summary>
public readonly record struct RadarLabel(
    string? Name,
    string? RangeText,
    float? Shield,
    float? Hull,
    float? Fill,
    int? RipcordSeconds,
    bool Eye,
    int? CommandId,
    bool CommandAccepted)
{
    public bool HasAny => Name is { Length: > 0 } || RangeText is not null
        || Shield is not null || Hull is not null || Fill is not null
        || RipcordSeconds is > 0 || Eye || CommandId is not null;
}

/// <summary>P783 — the native per-kind, per-LOD <em>label tier</em> for one
/// contact: which of the three label payloads native draws (name line, range
/// line, hull/shield/ore/fill stat bars). This is the pure
/// <c>kind × LOD × context → spec</c> result of the switch at
/// <c>WinTrek/radarimage.cpp:703-961</c>; <see cref="RadarLabelModel.Resolve"/>
/// turns it into a <see cref="RadarLabel"/> by attaching the raw readings.
/// Name is native <c>bLabel</c>, Stats is native <c>bStats</c>, Range is
/// <c>range &gt; 0</c> after the per-kind clip assignments.</summary>
public readonly record struct RadarLabelSpec(bool Name, bool Stats, bool Range)
{
    /// <summary>The all-suppressed spec (native leaves bLabel/bStats false and
    /// range zeroed).</summary>
    public static readonly RadarLabelSpec None = default;
}

/// <summary>
/// HUD-5 / P783 — pure port of native radar label/stat visibility
/// (<c>WinTrek/radarimage.cpp:703-980,1199-1441</c>). The radar snapshot stores
/// the raw per-contact readings; this resolver applies the native per-kind LOD
/// and important-mask rules that decide which of those readings become a label.
/// No graphics, no fonts — the same headless discipline as
/// <see cref="RadarBracketLayout"/> so the rules are unit-testable.
/// </summary>
public static class RadarLabelModel
{
    public const float RangeClipLabels = 1000f;   // radarimage.cpp:18

    /// <summary>HUD-5b — native <c>colorOther</c> brightness
    /// (radarimage.cpp:1094-1096): a contact carrying one of
    /// target/accepted/threat/highlight/flag/artifact keeps the full side colour,
    /// a queued order dims it to 0.75, everything else to 0.5. The dim applies to
    /// the brackets and the label, never to the icon itself (which uses
    /// <c>color</c>).</summary>
    public static float BracketBrightness(bool importantMask, bool queued)
        => importantMask ? 1f : queued ? 0.75f : 0.5f;

    /// <summary>The important-mask set native exempts from the brightness dim
    /// (<c>c_maskTarget | c_maskAccepted | c_maskThreat | c_maskHighlight |
    /// c_maskFlag | c_maskArtifact</c>, radarimage.cpp:1094). Note
    /// <c>c_maskQueued</c> and <c>c_maskSubject</c> are NOT in it.</summary>
    public static float BracketBrightness(in HudContact contact)
        => BracketBrightness(
            importantMask: contact.Selected || contact.AcceptedOrder || contact.Threat
                || contact.ThreatFlash || contact.Highlight || contact.CarriedFlag
                || contact.CarriedArtifact,
            queued: contact.QueuedOrder);

    /// <summary>Whether any native overlay bracket is drawn for this contact
    /// (radarimage.cpp:352-357: enemy/me/subject/flag/artifact/rip/targeted/
    /// last-fired). An accepted or queued order is NOT a bracket — native shows
    /// the command icon plus the brightened label (radarimage.cpp:1094-1096),
    /// which is why HUD-5b dropped the green/amber corner frames.</summary>
    public static bool HasOverlayBracket(in HudContact contact)
        => contact.NearestEnemy || contact.Subject || contact.CarriedFlag
           || contact.CarriedArtifact || contact.Ripcord || contact.Threat
           || contact.Highlight;

    /// <summary>Native <c>bRangeLabels = !CommandCamera</c>
    /// (radarimage.cpp:523-524): gates the per-contact range text and, with it,
    /// the nearest-enemy lookup (radarimage.cpp:582-583 — native passes NULL for
    /// <c>pmodelEnemy</c> whenever range labels are off).</summary>
    public static bool RangeLabelsEnabled(bool commandCamera) => !commandCamera;

    /// <summary>Native friendly detection eye (radarimage.cpp:1006-1019): shown
    /// only for a different SHIP of exactly the same side whose pilot is a real
    /// player (<c>GetPilotType() &gt;= c_ptPlayer</c>) and whose ShipStatus
    /// reports <c>GetDetected() == true</c>. <paramref name="detected"/> is that
    /// RAW per-ship ShipStatus bit (no inversion — this radar path reads
    /// <c>ss.GetDetected()</c> directly, unlike the own-ship console path).
    /// HUD-17: the server now populates the bit for same-side player ships
    /// (own-side copy), so the gate is fed real data.</summary>
    public static bool ShowDetectionEye(bool isSelf, bool sameSide, bool playerPiloted, bool detected)
        => !isSelf && sameSide && playerPiloted && detected;

    /// <summary>HUD-17 — the production radar call-site gate for one teammate
    /// entity, extracted so the whole native rule (same side, player-piloted,
    /// different ship, raw Detected bit) is unit-pinnable without a Game1
    /// render pass. <paramref name="mySide"/> is the local player's side;
    /// <paramref name="isSelf"/> excludes the player's own ship (native
    /// <c>pship != pshipSource</c>; we already know when we are eyed).</summary>
    public static bool ShowDetectionEyeFor(ShipEntity? ship, short mySide, bool isSelf)
        => ship is { } other
           && ShowDetectionEye(
               isSelf: isSelf,
               sameSide: other.SideId == mySide,
               playerPiloted: other.Player is { } pilot
                   && pilot.PilotType >= Allegiance.Core.Wire.PlayerInfo.PilotTypePlayer,
               detected: other.Detected);

    /// <summary>One candidate for the native nearest-enemy search
    /// (<c>FindTarget(pship, c_ttShip | c_ttEnemy | c_ttNearest)</c>,
    /// radarimage.cpp:582-583 / Igc/common.cpp:470-630): the squared centre
    /// distance from the own ship, whether the contact is an enemy
    /// (<c>c_ttEnemy</c>: not this side and not an ally) and whether the ship is
    /// visible (<c>GetVisibleF() &amp;&amp; CanSee()</c>).</summary>
    public readonly record struct NearestEnemyCandidate(short ShipId, bool Enemy, float DistanceSquared);

    /// <summary>Native nearest-enemy selection: the smallest squared distance
    /// wins, ties keep the FIRST candidate (native replaces only on
    /// <c>d &lt; distance</c>, Igc/common.cpp:608/621), enemies only, visible
    /// only. Returns null when nobody qualifies.</summary>
    public static short? NearestEnemy(IReadOnlyList<NearestEnemyCandidate> candidates)
    {
        short? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.Enemy) continue;
            if (candidate.DistanceSquared < bestDistance)
            {
                bestDistance = candidate.DistanceSquared;
                best = candidate.ShipId;
            }
        }
        return best;
    }

    /// <summary>Formatted range digits, cached by metre value so a steady frame
    /// reuses the same string instance instead of allocating
    /// <c>int.ToString()</c> per contact per frame (HUD-5b: ~214 contacts in a
    /// 107v107 fight). Native prints <c>int(separation + 0.5f)</c>
    /// (radarimage.cpp:626-636,1209).</summary>
    public static string RangeText(int metres)
    {
        if (metres <= 0) return "0";
        if (_rangeText.TryGetValue(metres, out var cached)) return cached;
        var text = metres.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_rangeText.Count >= RangeTextCacheLimit) _rangeText.Clear();
        _rangeText[metres] = text;
        return text;
    }

    /// <summary>The rendered range line ("1234 m", what the label block draws),
    /// cached by metre value for the same reason as <see cref="RangeText"/>.</summary>
    public static string RangeLineText(int metres)
    {
        if (metres <= 0) return "0 m";
        if (_rangeLineText.TryGetValue(metres, out var cached)) return cached;
        var text = RangeText(metres) + " m";
        if (_rangeLineText.Count >= RangeTextCacheLimit) _rangeLineText.Clear();
        _rangeLineText[metres] = text;
        return text;
    }

    private const int RangeTextCacheLimit = 4096;
    private static readonly Dictionary<int, string> _rangeText = new();
    private static readonly Dictionary<int, string> _rangeLineText = new();

    /// <summary>Resolve the label block native would draw for
    /// <paramref name="contact"/> in <paramref name="mode"/>. Command camera
    /// suppresses range (native <c>bRangeLabels</c>, radarimage.cpp:523,627-629).</summary>
    public static RadarLabel? Resolve(in HudContact contact, RadarMode mode, bool commandCamera)
    {
        var spec = Spec(contact, mode, commandCamera);

        // Native: !bLabel clears the name but may keep a bare range line
        // (radarimage.cpp:967-974). Stats are only computed when bStats (:977).
        string? name = spec.Name && contact.Name is { Length: > 0 } ? contact.Name : null;
        int? range = spec.Range ? contact.Range : null;

        float? shield = spec.Stats && contact.Shield is { } sh && sh <= 1f ? sh : null;
        float? hull = spec.Stats && contact.Hull is { } hu && hu <= 1f ? hu : null;
        float? fill = spec.Stats && contact.Fill is { } fl && fl <= 1f ? fl : null;

        var result = new RadarLabel(
            name,
            range is > 0 ? RangeText(range.Value) : null,
            shield, hull, fill,
            contact.RipcordSeconds, contact.DetectionEye, contact.CommandId, contact.CommandAccepted);

        return result.HasAny ? result : null;
    }

    /// <summary>P783 — THE native label-tier table: one pure function mapping
    /// <c>kind × LOD × context</c> to the label spec native's per-kind block
    /// produces (<c>WinTrek/radarimage.cpp:703-961</c>). Context is the flag set
    /// <see cref="HudContact"/> carries for that contact (important mask, exact
    /// same side vs allied, on-screen, missile aim, treasure usefulness).
    /// Command camera zeroes the range line exactly like native's
    /// <c>bRangeLabels</c> gate (:523-524,626-636) but never changes a clip.
    /// </summary>
    public static RadarLabelSpec Spec(in HudContact contact, RadarMode mode, bool commandCamera)
    {
        // radarimage.cpp:703-712 — an important contact (target, accepted/queued
        // order, highlight, threat, flag, artifact) takes bLabel = bStats = true
        // and SKIPS every per-kind tier below.
        if (IsImportant(contact))
            return new RadarLabelSpec(true, true, RangeLineShown(contact, commandCamera));

        // radarimage.cpp:724-731 — Minimal suppresses icon+range for every
        // non-self contact and leaves bLabel/bStats false for ALL of them (the
        // own ship keeps its icon but still gets no label).
        if (mode == RadarMode.Minimal) return RadarLabelSpec.None;

        bool withinClip = WithinRangeClip(contact);
        bool sameSide = contact.IsSameSide;
        bool sameOrAllied = sameSide || contact.IsAllied;
        bool range = RangeLineShown(contact, commandCamera);

        switch (contact.Kind)
        {
            case HudContactKind.Ship:
                // radarimage.cpp:736-780.
                return mode switch
                {
                    // :740-745 — label+stats for every ship; a same-side ship
                    // that is off screen loses its range (the bracket carries it).
                    RadarMode.All => new(true, true, range && !(!contact.OnScreen && sameSide)),
                    // :747-757 — label only on screen or within the clip; the
                    // exact own side loses its range, everyone else gets stats.
                    RadarMode.Default => new(
                        contact.OnScreen || withinClip,
                        !sameSide,
                        range && !sameSide),
                    // :758-777 — friendly/allied ships leave the radar (icon off,
                    // range 0, no label); enemies keep the range, get stats only
                    // on screen and the name only within the clip.
                    RadarMode.Target => sameOrAllied
                        ? RadarLabelSpec.None
                        : new(withinClip, contact.OnScreen, range),
                    _ => RadarLabelSpec.None,
                };

            case HudContactKind.Station:
                // radarimage.cpp:782-817. A pedestal station clips at the label
                // range in every LOD (icon + label + range); the non-pedestal
                // tiers follow the mode switch.
                if (IsPedestal(contact))
                    return new(false, false, withinClip && range);
                return mode switch
                {
                    RadarMode.All => new(true, true, range),        // :796-800
                    RadarMode.Default => new(false, false,           // :801-806
                        range && contact.OnScreen && withinClip),
                    RadarMode.Target => RadarLabelSpec.None,         // :807-813 (range = 0)
                    _ => RadarLabelSpec.None,
                };

            case HudContactKind.Warp:
                // radarimage.cpp:952-957 — name in All+Default, range clipped
                // only in Target.
                return mode switch
                {
                    RadarMode.All or RadarMode.Default => new(true, false, range),
                    RadarMode.Target => new(false, false, withinClip && range),
                    _ => RadarLabelSpec.None,
                };

            case HudContactKind.Missile:
                // radarimage.cpp:819-832 — a missile never has a name or stats;
                // outside All only a missile aimed at the own side keeps its
                // range (native drops the icon and the range otherwise).
                return mode switch
                {
                    RadarMode.All => new(false, false, range),
                    _ => new(false, false, contact.MissileTargetsOwnSide && range),
                };

            case HudContactKind.Probe:
            case HudContactKind.Mine:
                // radarimage.cpp:834-875. All: stats for every probe/mine and a
                // name only for an ON-SCREEN enemy; same side/ally keeps its
                // range unless it is both off screen and beyond the clip.
                // Default: no label, same-side/ally range clipped at 1000 m.
                // Target: only a within-clip enemy keeps its range.
                return mode switch
                {
                    RadarMode.All => new(
                        Name: !sameOrAllied && contact.OnScreen,
                        Stats: true,
                        Range: range && !(sameOrAllied && !contact.OnScreen && !withinClip)),
                    RadarMode.Default => new(false, false, range && !(sameOrAllied && !withinClip)),
                    RadarMode.Target => new(false, false, range && !sameOrAllied && withinClip),
                    _ => RadarLabelSpec.None,
                };

            case HudContactKind.Treasure:
            case HudContactKind.Tech:
                // radarimage.cpp:877-899. A useful treasure/tech gets its name in
                // All only, its range always except the Target clip; a NOT-useful
                // treasure has no label at all (it only exists on the radar in
                // All — see RadarLod.ShowTreasure).
                bool useful = contact.TreasureUseful || contact.IsTech;
                if (!useful)
                    return mode == RadarMode.All
                        ? new RadarLabelSpec(false, false, range)
                        : RadarLabelSpec.None;
                return mode switch
                {
                    RadarMode.All => new(true, false, range),
                    RadarMode.Default => new(false, false, range),
                    RadarMode.Target => new(false, false, withinClip && range),
                    _ => RadarLabelSpec.None,
                };

            case HudContactKind.Asteroid:
                // radarimage.cpp:901-950. A generic rock ('A…' or unnamed) never
                // gets a label, only its range within the clip; a named special
                // rock gets name+stats (hull + ore) in All, a bare range in
                // Default, and the Target clip.
                bool generic = string.IsNullOrEmpty(contact.Name) || contact.Name[0] == 'A';
                return mode switch
                {
                    RadarMode.All => generic
                        ? new RadarLabelSpec(false, false, withinClip && range)
                        : new RadarLabelSpec(true, true, range),
                    // Default clips only the GENERIC branch (:922-935); a named
                    // special rock keeps its bare range line at any distance.
                    RadarMode.Default => generic
                        ? new RadarLabelSpec(false, false, withinClip && range)
                        : new RadarLabelSpec(false, false, range),
                    RadarMode.Target => new(false, false, withinClip && range),
                    _ => RadarLabelSpec.None,
                };
        }

        return RadarLabelSpec.None;
    }

    /// <summary>Native <c>bLabel</c>'s important-mask term
    /// (radarimage.cpp:704-710): the current/accepted/queued order, the
    /// highlight, a threat, and the carried flag/artifact. Those contacts keep
    /// the full label+stats in every LOD (and the full colour, :1094-1096).</summary>
    public static bool IsImportant(in HudContact contact)
        => contact.Selected || contact.AcceptedOrder || contact.QueuedOrder
           || contact.Threat || contact.Highlight || contact.CarriedFlag
           || contact.CarriedArtifact;

    /// <summary>The native clip test <c>separation &lt; rangeClipLabels</c>
    /// (radarimage.cpp:18,749,773,786,803,843,859,866,883,909,939,955) against
    /// the ADJUSTED surface separation — native subtracts both radii before the
    /// LOD switch (<c>separation -= radiusSource + pmodel-&gt;GetRadius()</c>,
    /// :626-636), so this is NOT the number the range line prints. Falls back to
    /// the range value when the snapshot has no adjusted separation (headless
    /// fixtures), which is what the pre-P783 model compared.</summary>
    public static bool WithinRangeClip(in HudContact contact)
        => (contact.ClipSeparation ?? contact.Range ?? 0f) < RangeClipLabels;

    /// <summary>The native pedestal-station capability
    /// (<c>GetBaseStationType()-&gt;HasCapability(c_sabmPedestal)</c>,
    /// radarimage.cpp:784), which puts the station on the 1000 m clip in every
    /// LOD instead of the per-mode tiers.</summary>
    private static bool IsPedestal(in HudContact contact) => contact.IsPedestalStation;

    /// <summary>Native draws the range line only when <c>range &gt; 0</c>
    /// (radarimage.cpp:1209) — always false in the command camera
    /// (<c>bRangeLabels</c>, :523-529) and for the own ship (:626-628).</summary>
    private static bool RangeLineShown(in HudContact contact, bool commandCamera)
        => !commandCamera && contact.Range is > 0;
}
