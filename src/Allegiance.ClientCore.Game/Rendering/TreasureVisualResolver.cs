using System;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The <em>kind</em> of a treasure's resolved native visual — which art and
/// name to show (<c>treasureIGC.cpp:118-155</c>). The <see cref="TreasureVisual.Gem"/>
/// kind is the fallback for an unresolvable case (a part/development id the
/// client's static core cannot resolve), matching the pre-P586 shared-gold-gem
/// placeholder.
/// </summary>
public enum TreasureVisualKind
{
    /// <summary>The procedural gold-gem placeholder — fallback for an
    /// unresolvable part/development (never for cash/powerup/flag/artifact,
    /// which are always resolvable).</summary>
    Gem,
    Part,        // a buyable part's own model (ammo/fuel/missile pack, gun, shield, launcher/dispenser…)
    Development, // a development's own model
    Cash,        // model "acs14", icon "cashicon"
    Powerup,     // model "acs14", icon "iconpu"
    Flag,        // decal "biosflag", icon "flag", side-tinted
    Artifact,    // model "artifact", icon "articon"
}

/// <summary>
/// P586/P586b — the resolved native visual of a treasure, ported from
/// <c>CtreasureIGC::Initialize</c> (<c>treasureIGC.cpp:118-155</c>). The wire
/// <c>DataTreasureIGC</c> carries no ModelName; the visual is DERIVED from
/// <c>treasureCode</c> + <c>treasureID</c>:
/// <list type="bullet">
/// <item><b>Part / Development</b> (<c>m_buyable</c>): <c>name =
/// m_buyable-&gt;GetModelName()</c>, <c>icon = GetIconName()</c>
/// (<c>treasureIGC.cpp:123-126</c>), display name = the part/development's own
/// <c>Name</c> (so the target box reads "Ammo Pack", "Fuel Pack", "Mine Pack
/// 1", …). The buyable spans EVERY <c>OT_partType</c> — a plain part AND a
/// compact launcher/dispenser (<c>m_buyable = GetPartType(treasureID)</c>,
/// <c>treasureIGC.cpp:118-126</c>); a launcher's <c>GetModelName()</c> returns
/// its expendable's <c>LauncherDef.modelName</c> (<c>launchertypeigc.h:59-67</c>),
/// carried here by the built mountable part's <c>ModelName</c>. No explicit
/// <c>TextureStem</c>: native passes <c>texture=NULL</c> for a buyable
/// (<c>Load(0, name, NULL, icon, …)</c>, <c>treasureIGC.cpp:154</c>), so the
/// texture resolves from the model's own texture namespace.</item>
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
    string? ModelStem,     // native model stem ("acs14", "artifact", part/launcher ModelName…) or null for a flag decal
    string? TextureStem,   // explicit texture-unit stem (null for part/development = the model's own namespace; "biosflagbmp" for the flag decal)
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
/// wire <c>treasureCode</c> + <c>treasureID</c> via the client's static core
/// (<see cref="ShopCatalog"/> → <see cref="ShopCatalog.Core"/>), with no
/// graphics state — unit-testable.</summary>
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
                // P586b — resolve from the FULL static core, not a filtered shop
                // view: a part treasure's buyable (treasureIGC.cpp:118-126) is
                // ANY OT_partType — a plain part OR a compact launcher/dispenser.
                if (catalog?.Core is { } core)
                {
                    // Plain part → its own model/icon/name (treasureIGC.cpp:123-126).
                    if (core.PartTypes.TryGetValue(treasureId, out var part) && part.PartId != 0)
                        return Buyable(part);
                    // Launcher part (missile/mine/chaff/probe) → the built
                    // mountable part's ModelName/IconName/Name (the expendable's
                    // LauncherDef; ClauncherTypeIGC::GetModelName =
                    // launcherDef->modelName, launchertypeigc.h:59-67).
                    if (core.TryBuildExpendableLauncher(treasureId, out var launcher))
                        return Buyable(launcher);
                    return TreasureVisual.Gem;   // unresolvable part id — caller logs once
                }
                // Degraded client: no loaded core → fall back to the catalogue's
                // flat part + launcher lists (still full, not tech-filtered).
                if (catalog is not null)
                {
                    foreach (var p in catalog.Parts)
                        if (p.PartId == treasureId && p.PartId != 0)
                            return Buyable(p);
                    if (catalog.Launchers is { } launchers)
                        foreach (var l in launchers)
                            if (l.Part.PartId == treasureId)
                                return Buyable(l.Part);
                }
                return TreasureVisual.Gem;
            }

            case TreasureCode.Development:
            {
                if (catalog?.Core?.Developments is { } coreDevs && coreDevs.TryGetValue(treasureId, out var coreDev))
                    return Buyable(coreDev);
                if (catalog?.Developments is { } devs && devs.TryGetValue(treasureId, out var dev))
                    return Buyable(dev);
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

    /// <summary>
    /// P586b — startup self-check: resolve every part type and launcher in the
    /// loaded core and report any that would fall back to the gem (the "0
    /// unresolved" invariant this change guarantees). This is the exact set the
    /// 15v15 showcase drops (mounted parts incl. launchers/dispensers, ammo,
    /// fuel); developments are resolved by <see cref="Resolve"/> too but never
    /// spawn as loot, so they are out of the self-check's scope. Returns the
    /// number that fell back (0 = healthy).
    /// </summary>
    public static int SelfCheck(ShopCatalog? catalog, Action<string> log)
    {
        if (catalog?.Core is not { } core)
        {
            log("[loot] self-check skipped (no static core loaded)");
            return 0;
        }
        int fallback = 0;
        foreach (var p in core.PartTypes.Values)
        {
            if (p.PartId == 0) continue;
            if (Resolve(TreasureCode.Part, p.PartId, catalog).Kind == TreasureVisualKind.Gem)
            {
                fallback++;
                log($"[loot] self-check: PART {p.PartId} ('{p.Name}') unresolved — would render the gold gem");
            }
        }
        foreach (var lid in core.Launchers.Keys)
        {
            if (Resolve(TreasureCode.Part, lid, catalog).Kind == TreasureVisualKind.Gem)
            {
                fallback++;
                log($"[loot] self-check: LAUNCHER {lid} unresolved — would render the gold gem");
            }
        }
        return fallback;
    }

    /// <summary>A buyable part/development treasure (treasureIGC.cpp:123-126):
    /// the buyable's own model + icon + name. TextureStem is null — native
    /// passes texture=NULL for a buyable (treasureIGC.cpp:154), so the model's
    /// OWN texture namespace supplies it (never the icon name).</summary>
    private static TreasureVisual Buyable(DataPartTypeIGC p) =>
        new(TreasureVisualKind.Part, Stem(p.ModelName), null, Stem(p.IconName) ?? "", p.Name, IsDecal: false);

    private static TreasureVisual Buyable(DataDevelopmentIGC d) =>
        new(TreasureVisualKind.Development, Stem(d.ModelName), null, Stem(d.IconName) ?? "", d.Name, IsDecal: false);

    /// <summary>A model/icon name to a usable stem (trimmed), or null when blank
    /// — mirroring how the port treats a missing model name (no geometry).</summary>
    private static string? Stem(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
