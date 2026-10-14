using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>Native damage flags (igc.h:558-560) and side immunity shared by
/// explosion recipients. The damage matrix masks flags (missionigc.h:155).</summary>
internal static class BlastDamage
{
    internal const byte Mask = 0x1f;
    internal const byte NoWarn = 0x20;
    internal const byte NoDebris = 0x40;

    internal static short? SideOf(Model? model) => model switch
    {
        Ship ship => ship.SideId,
        Probe probe => probe.SideId,
        Missile missile => SideOf(missile.Launcher),
        Mine mine => mine.SideId,
        _ => null
    };

    internal static bool Suppressed(Mission? mission, short? side, Model? launcher, float amount)
        => launcher is not null && amount >= 0 && mission?.AllowFriendlyFire != true
            && (mission?.AreSidesFriendly(side, SideOf(launcher))
                ?? (side is not null && side == SideOf(launcher)));
}
