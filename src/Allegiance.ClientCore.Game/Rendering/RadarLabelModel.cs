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

/// <summary>
/// HUD-5 — pure port of native radar label/stat visibility
/// (<c>WinTrek/radarimage.cpp:704-980,1199-1416</c>). The radar snapshot stores
/// the raw per-contact readings; this resolver applies the native per-kind LOD
/// and important-mask rules that decide which of those readings become a label.
/// No graphics, no fonts — the same headless discipline as
/// <see cref="RadarBracketLayout"/> so the rules are unit-testable.
/// </summary>
public static class RadarLabelModel
{
    public const float RangeClipLabels = 1000f;   // radarimage.cpp:15

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
    /// reports <c>GetDetected()</c>. <paramref name="detected"/> is the
    /// per-player ShipStatus reading; null means "not known to this client"
    /// (the port has no scanner simulation), which must NOT light the eye —
    /// HUD-5b removed the fake always-on eye that came from the server's
    /// constant <c>Detected = true</c> for teammates.</summary>
    public static bool ShowDetectionEye(bool isSelf, bool sameSide, bool playerPiloted, bool? detected)
        => !isSelf && sameSide && playerPiloted && detected == true;

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
    public static RadarLabel? Resolve(HudContact contact, RadarMode mode, bool commandCamera)
    {
        bool important = contact.Selected || contact.AcceptedOrder || contact.QueuedOrder
            || contact.Threat || contact.Highlight || contact.CarriedFlag || contact.CarriedArtifact;

        bool bLabel = important;
        bool bStats = important;
        int? range = commandCamera ? null : contact.Range;
        string? name = contact.Name;

        if (!important)
        {
            if (mode == RadarMode.Minimal)
            {
                // Native Minimal keeps only the own ship (radarimage.cpp:724-736);
                // every other contact loses both its icon and its label data.
                if (!contact.IsSelf)
                {
                    name = null;
                    range = null;
                }
            }
            else
            {
                switch (contact.Kind)
                {
                    case HudContactKind.Ship:
                        ApplyShip(mode, contact, ref bLabel, ref bStats, ref range, ref name);
                        break;
                    case HudContactKind.Station:
                        ApplyStation(mode, contact, ref bLabel, ref bStats, ref range);
                        break;
                    case HudContactKind.Warp:
                        bLabel = mode <= RadarMode.Default;                       // radarimage.cpp:954
                        if (mode == RadarMode.Target && (range ?? 0) >= RangeClipLabels)
                            range = null;                                          // radarimage.cpp:955-956
                        break;
                    case HudContactKind.Asteroid:
                        ApplyAsteroid(mode, contact, ref bLabel, ref bStats, ref range, ref name);
                        break;
                }
            }
        }

        // Native: !bLabel clears the name but may keep a bare range line
        // (radarimage.cpp:966-970). Stats are only computed when bStats.
        if (!bLabel) name = null;

        float? shield = bStats && contact.Shield is { } sh && sh <= 1f ? sh : null;
        float? hull = bStats && contact.Hull is { } hu && hu <= 1f ? hu : null;
        float? fill = bStats && contact.Fill is { } fl && fl <= 1f ? fl : null;

        var result = new RadarLabel(
            name is { Length: > 0 } ? name : null,
            range is > 0 ? RangeText(range.Value) : null,
            shield, hull, fill,
            contact.RipcordSeconds, contact.DetectionEye, contact.CommandId, contact.CommandAccepted);

        return result.HasAny ? result : null;
    }

    private static void ApplyShip(RadarMode mode, HudContact contact,
        ref bool bLabel, ref bool bStats, ref int? range, ref string? name)
    {
        // radarimage.cpp:738-778.
        switch (mode)
        {
            case RadarMode.All:
                bLabel = bStats = true;
                if (!contact.OnScreen && contact.IsSameSide) range = null;
                break;
            case RadarMode.Default:
                if (contact.OnScreen || (range ?? 0) < RangeClipLabels) bLabel = true;
                if (contact.IsSameSide) range = null;
                else bStats = true;
                break;
            case RadarMode.Target:
                if (!contact.IsEnemy)
                {
                    if (!contact.IsSelf)
                    {
                        name = null;
                        range = null;
                    }
                }
                else
                {
                    if (contact.OnScreen) bStats = true;
                    if ((range ?? 0) < RangeClipLabels) bLabel = true;
                }
                break;
        }
    }

    private static void ApplyStation(RadarMode mode, HudContact contact,
        ref bool bLabel, ref bool bStats, ref int? range)
    {
        // radarimage.cpp:782-810 (non-pedestal branch). Pedestal 1000 m clipping
        // is an icon-presence rule owned by the radar-contacts row; labels follow
        // the same 1000 m range clip here for the All/Default tiers.
        switch (mode)
        {
            case RadarMode.All:
                bLabel = bStats = true;   // radarimage.cpp:798
                break;
            case RadarMode.Default:
                if ((range ?? 0) >= RangeClipLabels || !contact.OnScreen) range = null;
                break;
            case RadarMode.Target:
                range = null;
                break;
        }
    }

    private static void ApplyAsteroid(RadarMode mode, HudContact contact,
        ref bool bLabel, ref bool bStats, ref int? range, ref string? name)
    {
        // radarimage.cpp:903-930. Generic rocks ('A' or unnamed) only get a label
        // in All for named special rocks; otherwise the rock label is suppressed.
        bool generic = string.IsNullOrEmpty(name) || name[0] == 'A';
        if (generic)
        {
            if ((range ?? 0) >= RangeClipLabels || (range ?? 0) == 0)
            {
                name = null;
                range = null;
            }
        }
        else if (mode == RadarMode.All)
        {
            bLabel = bStats = true;
        }
    }
}
