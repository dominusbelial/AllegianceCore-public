using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P276 — pure (MonoGame-free) layout of the <em>part-info / connector
/// compatibility</em> detail panel for the docked/hangar loadout screen, the
/// follow-up to P268's shop list. Mirrors the native
/// <c>PartInfoPane</c>/<c>ConnectorPane</c> (<c>WinTrek/loadout.h:22,29,44</c>):
/// for the currently-highlighted shop row it shows the real numeric stats of
/// that hull/part plus — for a part — whether it actually fits the player's
/// current hull (the connector/hardpoint compatibility question the P268 list
/// never answered). Kept in the same discipline as <see cref="ShopLayout"/>:
/// pure, headless-testable, every string printable ASCII 32..126.
///
/// <para><b>Real stats, not placeholders.</b> Every figure comes from the
/// decoded core record (<see cref="DataHullTypeIGC"/>/<see cref="DataPartTypeIGC"/>),
/// selecting the fields the native stat grid could show. Cost/Time/description
/// mirror the native pane's <c>DrawDescription</c> lines
/// (<c>loadout.cpp:1455-1456</c>: "Cost: $N" / "Time: N Sec.").</para>
///
/// <para><b>Compatibility</b> is the real native mount test —
/// <see cref="DataHullTypeIGC.CanMountPart"/>, a port of
/// <c>IhullTypeIGC::CanMount</c> (<c>src/Igc/hullTypeIGC.h:199-213</c>) — so the
/// "FITS / DOES NOT FIT" verdict uses the same per-hardpoint weapon mask and
/// <c>pmEquipment[et]</c> single-mount mask the engine itself uses.</para>
/// </summary>
public static class PartInfoLayout
{
    /// <summary>Build the detail rows for the shop row the cursor currently rests on.</summary>
    public static IReadOnlyList<ShopLayout.Row> Build(ShopModel m)
    {
        var rows = new List<ShopLayout.Row>();
        if (m.CursorOnHull && m.CursorHull is { } hull)
            BuildHull(rows, hull);
        else if (m.CursorPart is { } part)
            BuildPart(rows, m, part);
        else
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted, "(no shop item selected)"));
        return rows;
    }

    private static void BuildHull(List<ShopLayout.Row> rows, in DataHullTypeIGC h)
    {
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Title, $"HULL - {ShopLayout.AsciiSafe(h.Name)}"));
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Info,
            $"cost: ${h.Price}   build: {h.TimeToBuild}s"));
        if (Desc(h.Description) is { Length: > 0 } d)
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted, d));

        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Section, "STATS"));
        Add(rows, "mass", Num(h.Mass));
        Add(rows, "signature", Num(h.Signature));
        Add(rows, "max speed", Num(h.Speed));
        Add(rows, "turn (yaw/pitch/roll)",
            $"{Num(h.MaxTurnRates[0])}/{Num(h.MaxTurnRates[1])}/{Num(h.MaxTurnRates[2])}");
        Add(rows, "thrust", Num(h.Thrust));
        Add(rows, "hit points", Num(h.HitPoints));
        Add(rows, "max energy", Num(h.MaxEnergy));
        Add(rows, "recharge rate", Num(h.RechargeRate));
        Add(rows, "scanner range", Num(h.ScannerRange));
        Add(rows, "max fuel", Num(h.MaxFuel));
    }

    private static void BuildPart(List<ShopLayout.Row> rows, ShopModel m, in DataPartTypeIGC p)
    {
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Title, $"{ShopLayout.AsciiSafe(p.Name)} [{ShopLayout.Cat(p)}]"));
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Info,
            $"cost: ${p.Price}   build: {p.TimeToBuild}s"));
        if (Desc(p.Description) is { Length: > 0 } d)
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted, d));

        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Section, "STATS"));
        Add(rows, "mass", Num(p.Mass));
        Add(rows, "signature", Num(p.Signature));

        // Subtype-specific real fields (DataPartTypeIGC.Subtype).
        switch (p.Type)
        {
            case DataPartTypeIGC.Weapon w:
                Add(rows, "energy/shot", Num(w.EnergyPerShot));
                Add(rows, "fire ready", $"{Num(w.DtimeReady)}s");
                Add(rows, "burst delay", $"{Num(w.DtimeBurst)}s");
                Add(rows, "dispersion", Num(w.Dispersion));
                Add(rows, "ammo/shot", w.CAmmoPerShot.ToString(CultureInfo.InvariantCulture));
                break;
            case DataPartTypeIGC.Shield s:
                Add(rows, "max strength", Num(s.MaxStrength));
                Add(rows, "regen rate", Num(s.RateRegen));
                break;
            case DataPartTypeIGC.Cloak c:
                Add(rows, "energy/s", Num(c.EnergyConsumption));
                Add(rows, "max cloaking", Num(c.MaxCloaking));
                Add(rows, "on rate", Num(c.OnRate));
                Add(rows, "off rate", Num(c.OffRate));
                break;
            case DataPartTypeIGC.Afterburner ab:
                Add(rows, "fuel/s", Num(ab.FuelConsumption));
                Add(rows, "max thrust", Num(ab.MaxThrust));
                break;
            case DataPartTypeIGC.Pack pk:
                Add(rows, "pack amount", pk.Amount.ToString(CultureInfo.InvariantCulture));
                break;
        }

        // Connector/hardpoint compatibility vs the hull the shop will actually
        // launch. P335: once the player has chosen a different hull, the server
        // (P334) evaluates every requested part against THAT new hull via
        // CanMount and DROPS a part that doesn't fit — so judging the part
        // against the pre-dock CURRENT hull would tell a hull-swapping player a
        // part "fits" when the server will silently drop it. Judge against
        // EffectiveHullId (== current when no hull is chosen) and label it.
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Section, "COMPATIBILITY"));
        var effective = m.EffectiveHull;
        string label = m.WillSwapHull ? "chosen hull" : "current hull";
        if (effective is { } hull)
        {
            bool fits = hull.CanMountPart(p);
            rows.Add(ShopLayout.Row.Of(
                fits ? ShopLayout.Kind.HighlightFit : ShopLayout.Kind.HighlightNoFit,
                fits
                    ? $"FITS {label} ({ShopLayout.AsciiSafe(hull.Name)})"
                    : $"DOES NOT FIT {label} ({ShopLayout.AsciiSafe(hull.Name)})"));
            // P335 — when a hull swap is pending, a part that can't mount the
            // new hull will be DROPPED on launch (the server's CanMount drop),
            // so make that consequence explicit rather than ambiguous.
            if (m.WillSwapHull && !fits)
                rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted,
                    "  -> dropped on launch (new hull's hardpoints)"));
        }
        else
        {
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted,
                $"(unknown hull #{m.EffectiveHullId} - fit unchecked)"));
        }
    }

    private static void Add(List<ShopLayout.Row> rows, string label, string value) =>
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Info, $"{label,-16} {value}"));

    /// <summary>Trim to a single ASCII line for the compact panel; blank if empty.</summary>
    private static string Desc(string raw) =>
        raw is null || raw.Length == 0 ? "" : ShopLayout.AsciiSafe(raw);

    /// <summary>Compact, culture-stable numeric formatting for real core stats.</summary>
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
