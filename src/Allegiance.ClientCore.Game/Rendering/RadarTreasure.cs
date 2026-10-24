using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>HUD-4 — pure port of native's treasure usefulness classification
/// (radarimage.cpp:437-511, <c>UsefulTreasure</c>): a treasure either gets no
/// radar icon (not useful), the ordinary treasure icon (useful part), or the
/// tech icon (useful tech). Game1 resolves the part catalogue/dev-tech inputs
/// and this helper applies the native switch.</summary>
public enum RadarTreasureUse
{
    NotUseful,
    UsefulPart,
    UsefulTech,
}

public static class RadarTreasure
{
    /// <param name="code">The treasure's wire code (<see cref="TreasureCode"/>).</param>
    /// <param name="partEquipmentType">For a Part treasure, the resolved part's
    /// <see cref="EquipmentType"/>; null when the part cannot be resolved (the
    /// port then shows it as a useful part rather than silently hiding it).</param>
    /// <param name="partEffectsSatisfied">For a Part treasure, whether the part's
    /// effect techs are already researched by the player's side (native
    /// <c>ppt-&gt;GetEffectTechs() &lt;= GetDevelopmentTechs()</c>).</param>
    /// <param name="packUseful">For a Pack treasure, whether the pack is useful to
    /// the player's own ship (fuel pack needs an afterburner; ammo pack needs an
    /// ammo weapon).</param>
    /// <param name="launcherMountable">For a launcher-type Part treasure (chaff/
    /// magazine/dispenser), whether the own hull can mount it at mount 0 — the
    /// native <c>IsLauncherType(et) &amp;&amp; pship-&gt;GetCluster() &amp;&amp;
    /// pship-&gt;GetHullType()-&gt;CanMount(ppt, 0)</c> gate (radarimage.cpp:503-508).
    /// Ignored for non-launcher parts.</param>
    /// <param name="developmentObsolete">P805 — for a Development treasure, the
    /// LIVE native obsolete test <c>pd-&gt;IsObsolete(GetDevelopmentTechs())</c>
    /// (radarimage.cpp:458-461; <c>IdevelopmentIGC::IsObsolete</c>,
    /// developmentigc.h:110-113): a tech-only development whose effect techs the
    /// side already fully holds is <b>not useful</b> and loses its radar icon in
    /// every LOD but All.</param>
    public static RadarTreasureUse Usefulness(
        TreasureCode code,
        EquipmentType? partEquipmentType = null,
        bool partEffectsSatisfied = true,
        bool packUseful = true,
        bool launcherMountable = true,
        bool developmentObsolete = false)
    {
        return code switch
        {
            // Cash/flag/powerup are always useful parts to an in-flight ship
            // (radarimage.cpp:443-450); a DEVELOPMENT treasure is useful only
            // while not obsolete (radarimage.cpp:452-463).
            TreasureCode.Development => developmentObsolete
                ? RadarTreasureUse.NotUseful
                : RadarTreasureUse.UsefulPart,
            TreasureCode.Cash or TreasureCode.Flag or TreasureCode.Powerup => RadarTreasureUse.UsefulPart,
            TreasureCode.Part => partEquipmentType switch
            {
                EquipmentType.Pack => packUseful ? RadarTreasureUse.UsefulPart : RadarTreasureUse.NotUseful,
                null => RadarTreasureUse.UsefulPart,
                _ => partEffectsSatisfied
                    ? (IsLauncherType(partEquipmentType.Value) && launcherMountable
                        ? RadarTreasureUse.UsefulPart
                        : RadarTreasureUse.NotUseful)
                    : RadarTreasureUse.UsefulTech,
            },
            _ => RadarTreasureUse.NotUseful,
        };
    }

    /// <summary>Native <c>IlauncherTypeIGC::IsLauncherType</c>
    /// (src/Igc/igc.h:3879-3885): chaff launcher, magazine (missile launcher)
    /// and dispenser (mine/probe launcher).</summary>
    public static bool IsLauncherType(EquipmentType et) =>
        et is EquipmentType.ChaffLauncher or EquipmentType.Magazine or EquipmentType.Dispenser;
}
