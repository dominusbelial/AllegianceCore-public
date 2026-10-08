using System;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The <em>kind</em> of a treasure's resolved native visual — which art and
/// name to show (<c>treasureIGC.cpp:118-155</c>). The <see cref="TreasureVisual.Gem"/>
/// kind is the fallback for an unresolvable case (a part/development id the
/// client's catalogue cannot resolve), matching the pre-P586 shared-gold-gem
/// placeholder.
/// </summary>
public enum TreasureVisualKind
{
    /// <summary>The procedural gold-gem placeholder — fallback for an
    /// unresolvable part/development (never for cash/powerup/flag/artifact,
    /// which are always resolvable).</summary>
    Gem,
    Part,        // a buyable part's own model (ammo/fuel/missile pack, gun, shield…)
    Development, // a development's own model
    Cash,        // model "acs14", icon "cashicon"
    Powerup,     // model "acs14", icon "iconpu"
    Flag,        // decal "biosflag", icon "flag", side-tinted
    Artifact,    // model "artifact", icon "articon"
}

/// <summary>
/// P586 — the resolved native visual of a treasure, ported from
/// <c>CtreasureIGC::Initialize</c> (<c>treasureIGC.cpp:118-155</c>). The wire
/// <c>DataTreasureIGC</c> carries no ModelName; the visual is DERIVED from
/// <c>treasureCode</c> + <c>treasureID</c>:
/// <list type="bullet">
/// <item><b>Part / Development</b> (<c>m_buyable</c>): <c>name =
/// m_buyable-&gt;GetModelName()</c>, <c>icon = GetIconName()</c>
/// (<c>treasureIGC.cpp:123-126</c>), display name = the part/development's own
/// <c>Name</c> (so the target box reads "Ammo Pack", "Fuel Pack", …).</item>
/// <item><b>Cash / Powerup</b>: model <c>"acs14"</c>, icon
/// <c>"cashicon"</c>/<c>"iconpu"</c> (<c>treasureIGC.cpp:153-155</c>).</item>
/// <item><b>Flag</b> with a real side (<c>treasureID != SIDE_TEAMLOBBY</c>):
/// the <c>"biosflag"</c> decal (billboard), icon <c>"flag"</c>, name
/// <c>"Flag"</c> (<c>treasureIGC.cpp:130-139</c>).</item>
/// <item><b>Artifact</b> (flag whose <c>treasureID == SIDE_TEAMLOBBY</c> =
/// <c>Side.ArtifactSentinelFlag</c>): model <c>"artifact"</c>, icon
/// <c>"articon"</c>, name <c>"Artifact"</c> (<c>treasureIGC.cpp:141-146</c>).</item>
/// </list>
/// An unresolvable part/development id resolves to <see cref="TreasureVisualKind.Gem"/>
/// (the pre-P586 placeholder) — the caller logs it once.
/// </summary>
public readonly record struct TreasureVisual(
    TreasureVisualKind Kind,
    string? ModelStem,     // native model stem ("acs14", "artifact", part ModelName…) or null for a flag decal
    string? TextureStem,   // texture-unit stem ("biosflag" for the flag decal; the model texture namespace otherwise)
    string IconName,       // radar/HUD icon ("cashicon","iconpu","flag","articon", or the part's IconName)
    string DisplayName,    // target-box / tooltip name ("Cash","Powerup","Flag","Artifact", or the part's Name)
    bool IsDecal)          // true = the flag's camera-facing "biosflag" decal (native LoadDecal)
{
    /// <summary>The fallback for an unresolvable case — the pre-P586 shared
    /// gold gem (same placeholder the renderer drew for every treasure).</summary>
    public static readonly TreasureVisual Gem =
        new(TreasureVisualKind.Gem, ModelStem: null, TextureStem: null,
            IconName: "", DisplayName: "Loot", IsDecal: false);
}

/// <summary>Pure static-code resolution of a treasure's native visual from its
/// wire <c>treasureCode</c> + <c>treasureID</c> (via the client's
/// <see cref="ShopCatalog"/>), with no graphics state — unit-testable.</summary>
public static class TreasureVisualResolver
{
    /// <summary>
    /// Resolve the native visual for a treasure instance. <paramref name="catalog"/>
    /// may be null/partial (client degraded without a loadable core), in which
    /// case an unresolvable part/development falls back to <see cref="TreasureVisual.Gem"/>
    /// while cash/powerup/flag/artifact still resolve (they need no core lookup).
    /// </summary>
    public static TreasureVisual Resolve(TreasureCode code, short treasureId, ShopCatalog? catalog)
    {
        switch (code)
        {
            case TreasureCode.Part:
            {
                // A buyable part treasure uses the PART'S OWN model/icon/name
                // (treasureIGC.cpp:123-126). Unresolvable part id → gem fallback.
                if (catalog is not null)
                {
                    foreach (var p in catalog.Parts)
                    {
                        if (p.PartId == treasureId && p.PartId != 0)
                            return new TreasureVisual(
                                TreasureVisualKind.Part,
                                Stem(p.ModelName), Stem(p.IconName),
                                p.IconName, p.Name, IsDecal: false);
                    }
                }
                return TreasureVisual.Gem;   // unresolvable part id — caller logs once
            }

            case TreasureCode.Development:
            {
                // A buyable development treasure uses the development's OWN
                // model/icon/name (treasureIGC.cpp:123-126). Unresolvable → gem.
                if (catalog?.Developments is { } devs && devs.TryGetValue(treasureId, out var dev))
                    return new TreasureVisual(
                        TreasureVisualKind.Development,
                        Stem(dev.ModelName), Stem(dev.IconName),
                        dev.IconName, dev.Name, IsDecal: false);
                return TreasureVisual.Gem;   // unresolvable development id — caller logs once
            }

            case TreasureCode.Cash:
                return new TreasureVisual(TreasureVisualKind.Cash, "acs14", "acs14bmp", "cashicon", "Cash", IsDecal: false);

            case TreasureCode.Powerup:
                return new TreasureVisual(TreasureVisualKind.Powerup, "acs14", "acs14bmp", "iconpu", "Powerup", IsDecal: false);

            case TreasureCode.Flag:
                // A real side → the "biosflag" decal, side-tinted (treasureIGC.cpp:130-139);
                // the sentinel SIDE_TEAMLOBBY (-2) → the "artifact" model (treasureIGC.cpp:141-146).
                if (treasureId != Side.ArtifactSentinelFlag)
                    // Real side → the "biosflag" decal (side-tinted), treasureIGC.cpp:130-139.
                    // Native names the decal "biosflag"; the artwork asset unit is
                    // biosflagbmp.mdl (MdlTextureDecoder loads <stem>.mdl).
                    return new TreasureVisual(TreasureVisualKind.Flag, ModelStem: null, "biosflagbmp", "flag", "Flag", IsDecal: true);
                return new TreasureVisual(TreasureVisualKind.Artifact, "artifact", "artifactbmp", "articon", "Artifact", IsDecal: false);

            default:
                return TreasureVisual.Gem;
        }
    }

    /// <summary>A model/icon name to a usable stem (trimmed), or null when blank
    /// — mirroring how the port treats a missing model name (no geometry).</summary>
    private static string? Stem(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
