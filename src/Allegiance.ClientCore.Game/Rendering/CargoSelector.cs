using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P626 — pure in-flight cargo/inventory selection model.
///
/// Native holds a "selected cargo part" — <c>mountSelected</c> over the ship's
/// cargo bay mounts <c>-1..-c_maxCargo</c> — advanced by <c>TK_NextCargo</c>
/// via <c>WinTrekClient::NextCargoPart</c> (trekigc.cpp:2620-2642) and read by
/// <c>GetCargoPart</c> (trekigc.cpp:2603-2618); <c>TK_DropCargo</c> drops it
/// (WinTrek.cpp:9698-9730) and <c>TK_Reload</c> tops up pools from it
/// (WinTrek.cpp:9674-9695). <c>TK_NextPart/PrevPart</c> (igc.h:232-233) are the
/// same "select next/previous part from inventory" family over the identical
/// selection. This helper models that selection as a circular index over the
/// ship's <em>occupied</em> cargo mounts only (dropping an empty slot is
/// meaningless; native also prefers a real part), exposing the pure
/// next/previous arithmetic so the Game1 keys and the unit tests share ONE rule.
/// </summary>
public static class CargoSelector
{
    /// <summary>True iff the given expanded-part entry is a cargo item, i.e.
    /// carries a negative mount (encoded as a high-bit byte on the wire).</summary>
    public static bool IsCargoMount(byte mountId) => mountId >= 0x80;

    /// <summary>Signed mount of a cargo entry (high-bit byte → negative).</summary>
    public static sbyte SignedMount(byte mountId) => unchecked((sbyte)mountId);

    /// <summary>Encode a negative cargo mount (in -1..-5) to its wire byte.</summary>
    public static byte EncodeMount(int mount) => unchecked((byte)mount);

    /// <summary>The occupied cargo mounts (negative), sorted first-slot-first
    /// (<c>-1</c> before <c>-5</c>, native's ascending mount order), from a list
    /// of cargo <see cref="Allegiance.Core.Wire.ExpandedPartData"/> entries.</summary>
    public static IReadOnlyList<sbyte> OccupiedMounts(System.Collections.Generic.IReadOnlyList<Allegiance.Core.Wire.ExpandedPartData> cargo)
    {
        var mounts = new List<sbyte>();
        foreach (var e in cargo)
            if (IsCargoMount(e.MountId))
                mounts.Add(SignedMount(e.MountId));
        mounts.Sort();          // -5 .. -1 => ascending: -5,-4,-3,-2,-1
        return mounts;
    }

    /// <summary>The first (highest-mount, i.e. -1) occupied cargo mount, or
    /// <c>-1</c> when cargo is empty.</summary>
    public static int FirstOccupied(System.Collections.Generic.IReadOnlyList<sbyte> mounts)
        => mounts.Count == 0 ? -1 : mounts[^1];

    /// <summary>Circular NEXT occupied mount from <paramref name="current"/>
    /// (native's <c>NextCargoPart</c> direction — forward through the bay,
    /// wrapping). Returns <c>-1</c> when cargo is empty.</summary>
    public static int Next(int current, System.Collections.Generic.IReadOnlyList<sbyte> mounts)
    {
        if (mounts.Count == 0) return -1;
        if (current < 0)
        {
            // Find the first occupied mount strictly "after" (less-than in native
            // descent) current; wrap to the first occupied.
            var after = mounts.Where(m => m < current).OrderByDescending(m => m).ToList();
            return after.Count > 0 ? after[0] : mounts[^1];
        }
        return mounts[^1];      // no current selection -> first occupied
    }

    /// <summary>Circular PREVIOUS occupied mount from <paramref name="current"/>.
    /// Returns <c>-1</c> when cargo is empty.</summary>
    public static int Prev(int current, System.Collections.Generic.IReadOnlyList<sbyte> mounts)
    {
        if (mounts.Count == 0) return -1;
        if (current < 0)
        {
            var before = mounts.Where(m => m > current).OrderBy(m => m).ToList();
            return before.Count > 0 ? before[0] : mounts[0];
        }
        return mounts[0];
    }
}
