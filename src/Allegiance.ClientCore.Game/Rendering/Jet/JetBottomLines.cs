namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1040c — the two shared bottom-left lines' stated COMPACT forms.
/// <para><b>Why a compact form exists.</b> The cargo and flight-parts rows keep their native
/// fixed offsets (<c>Game1.Cargo.cs</c>/<c>Game1.InFlightUtility.cs:169</c>,
/// <c>Game1.FlightParts.cs:324</c>) and their text is the shared line the client already builds.
/// In the command view the CMD panel owns the bottom-centre band, so a row whose ink band meets
/// that panel is bounded to the space left of it; at 800×480 that is ~138 px and the full
/// <c>WEAPONS: GROUP  BOOST: ON  [1:Plasma]</c> / <c>CARGO 1/5  Ammo pack x3</c> cannot be shown
/// whole. The P1040b rule requires the selected weapon and the boost state to stay readable, so
/// these builders compose a shorter form FROM THE SAME LINE — nothing is invented, and a line
/// whose shape is not the one the shared builders produce is returned unchanged (never guessed).
/// </para>
/// <para>Both forms keep whole tokens: no word is cut, and the amount (<c>xN</c>) is dropped
/// rather than a name. The fitted ellipsis remains the last resort in
/// <see cref="JetHudRenderer"/>.</para>
/// </summary>
public static class JetBottomLines
{
    /// <summary>The shared flight-parts prefix (<c>Game1.FlightParts.cs:311</c>).</summary>
    public const string WeaponsPrefix = "WEAPONS: ";

    /// <summary>The shared boost field's marker (<c>Game1.FlightParts.cs:311</c>).</summary>
    public const string BoostMarker = "  BOOST: ";

    /// <summary>Compact word for the weapon field (<c>WEAPONS</c> → <c>WPN</c>).</summary>
    public const string WeaponsWord = "WPN";

    /// <summary>Compact word for the boost field (<c>BOOST</c> → <c>BST</c>).</summary>
    public const string BoostWord = "BST";

    /// <summary>
    /// P1040c — the flight-parts row's compact form: <c>WPN &lt;GROUP|SINGLE n&gt;
    /// [&lt;mount&gt;:&lt;weapon&gt;] BST &lt;ON|OFF&gt;</c>. The weapon field leads (it decides
    /// whether the trigger fires single mounts or the whole group), the first weapon mount (the
    /// selected weapon) follows whole, and the boost state closes the row — exactly the three
    /// readings the full line carries; the later mounts and the <c>PART:</c> selection are the
    /// fields that give way. A line that is not the shared shape is returned unchanged.
    /// <para><paramref name="withLabel"/> = false drops the row's own <c>WPN</c> label when the
    /// measured band cannot hold the labelled form: the three readings are unchanged, only the
    /// label gives way (the row's position and the bracket already identify it).</para>
    /// </summary>
    public static string FlightPartsCompact(string line, bool withLabel = true)
    {
        if (string.IsNullOrEmpty(line)) return line;
        if (!line.StartsWith(WeaponsPrefix, StringComparison.Ordinal)) return line;
        int boostAt = line.IndexOf(BoostMarker, StringComparison.Ordinal);
        string group = boostAt > 0
            ? line[WeaponsPrefix.Length..boostAt]
            : line[WeaponsPrefix.Length..].Trim();
        if (group.Length == 0) return line;
        string state = "";
        int mountsStart = line.Length;
        if (boostAt > 0)
        {
            int valueStart = boostAt + BoostMarker.Length;
            int next = line.IndexOf("  ", valueStart, StringComparison.Ordinal);
            state = (next > 0 ? line[valueStart..next] : line[valueStart..]).Trim();
            mountsStart = next > 0 ? next : line.Length;
        }
        string mount = "";
        int open = mountsStart < line.Length ? line.IndexOf('[', mountsStart) : -1;
        if (open >= 0)
        {
            int close = line.IndexOf(']', open + 1);
            if (close > open) mount = line[open..(close + 1)];
        }
        return string.Concat(withLabel ? WeaponsWord + " " : "", group,
            mount.Length > 0 ? " " + mount : "",
            state.Length > 0 ? " " + BoostWord + " " + state : "");
    }

    /// <summary>
    /// P1040c — the cargo row's compact form: the hold OCCUPANCY head (<c>CARGO …</c>) plus the
    /// first detail segment (the selected item), with a trailing per-slot amount (<c>xN</c>)
    /// dropped so the item name stays whole. The later slots and the selection prompt are the
    /// fields that give way. <paramref name="withDetail"/> = false keeps only the occupancy (the
    /// narrowest form that still carries the hold reading). A line without the shared
    /// double-space separator is returned unchanged (its head IS the occupancy).
    /// </summary>
    public static string CargoCompact(string line, bool withDetail = true)
    {
        if (string.IsNullOrEmpty(line)) return line;
        int head = line.IndexOf("  ", StringComparison.Ordinal);
        if (head <= 0) return line;
        if (!withDetail) return line[..head];
        string occupancy = line[..head];
        int detailStart = head + 2;
        int detailEnd = line.IndexOf("  ", detailStart, StringComparison.Ordinal);
        string detail = (detailEnd > 0 ? line[detailStart..detailEnd] : line[detailStart..]).Trim();
        // Drop a trailing " xN" per-slot amount: the count is the occupancy's job, the name is
        // the reading the compact form must keep.
        int amount = detail.LastIndexOf(" x", StringComparison.Ordinal);
        if (amount > 0 && IsAmount(detail[(amount + 2)..])) detail = detail[..amount];
        return detail.Length > 0 ? occupancy + "  " + detail : occupancy;
    }

    /// <summary>Is <paramref name="text"/> a bare amount (<c>3</c>, <c>12</c>)?</summary>
    private static bool IsAmount(string text)
        => text.Length > 0 && text.All(char.IsAsciiDigit);
}
