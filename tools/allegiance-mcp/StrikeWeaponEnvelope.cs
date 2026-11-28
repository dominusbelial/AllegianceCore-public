using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal static class StrikeWeaponEnvelope
{
    internal enum Kind { Guided, Dumbfire, Gun }

    internal readonly record struct Weapon(Kind Kind, short Part, byte Mount, float Range, float Cycle);

    // Igc/magazineigc.cpp:127-148 measures centre distance for both missile types.
    // Igc/weaponIGC.cpp:213-221 and weaponIGC.h:165-172 scale gun speed and life.
    // weaponIGC.h:104-114,179-182 uses dtimeBurst for activation and refire.
    internal static Weapon? Select(StaticCore core, DataHullTypeIGC hull, IReadOnlyList<ExpandedPartData> parts,
        StrikeVolley.StationProfile station, GlobalAttributeSet ga, long missilesUsed, float ammo, float energy)
    {
        Weapon? gun = null;
        foreach (var part in parts)
        {
            if (part.Amount <= 0 || part.MountId >= 128) continue;
            if (part.MountId == 0 && part.Amount > missilesUsed
                && core.Launchers.TryGetValue(part.PartId, out var launcher)
                && core.MissileTypes.TryGetValue(launcher.ExpendabletypeId, out var m)
                && !m.HasWarpBombCapability && Damages(m.DamageType, m.Power))
            {
                // Dumbfire coasts until activation (missileigc.cpp:104,363-369,395-404).
                // Use stationary-launch reach. A pilot need not rely on closing velocity.
                float burn = m.MaxLock == 0 ? Math.Clamp(m.Lifespan - m.ReadyTime, 0, m.Lifespan) : m.Lifespan;
                float range = m.InitialSpeed * m.Lifespan + .5f * m.Acceleration * burn * burn;
                if (range > 1) return new(m.MaxLock == 0 ? Kind.Dumbfire : Kind.Guided,
                    part.PartId, 0, range, Math.Max(.1f, m.LoadTime));
            }
            if (part.MountId >= hull.MaxFixedWeapons || part.FractionMounted < ExpandedPartData.FullyMounted
                || !core.PartTypes.TryGetValue(part.PartId, out var type) || type.Type is not DataPartTypeIGC.Weapon w
                || !core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var round)
                || !Damages(round.DamageType, round.Power) || ammo < w.CAmmoPerShot || energy < w.EnergyPerShot) continue;
            float reach = round.Speed * round.Lifespan
                * (w.CAmmoPerShot > 0 ? ga.Get(GlobalAttributeSet.SpeedAmmo) : 1)
                * (w.EnergyPerShot > 0 ? ga.Get(GlobalAttributeSet.LifespanEnergy) : 1);
            if (reach > (gun?.Range ?? 1))
                gun = new(Kind.Gun, part.PartId, part.MountId, reach, Math.Max(.05f, w.DtimeBurst));
        }
        return gun;

        bool Damages(byte type, float power)
        {
            var damage = StrikeVolley.RoundDamage(station, type, power);
            return damage.Armor > 0 && (station.MaxShield <= 0 || damage.Shield > 0);
        }
    }

    internal static bool Ready(Weapon weapon, short station, float distance, float alignment,
        bool currentStation, MissileLockState feedback, bool gunTargetConfirmed, bool cycleReady)
    {
        if (!currentStation || !cycleReady || !(distance > 1 && distance < weapon.Range)) return false;
        if (weapon.Kind == Kind.Gun) return gunTargetConfirmed && alignment >= MathF.Cos(BotManager.AttackMaxOffAngle);
        return feedback.TargetType == IgcObjectType.Station && feedback.TargetId == station && feedback.IsReady
            && (weapon.Kind == Kind.Guided ? feedback.IsFullLock : alignment >= MathF.Cos(BotManager.AttackMaxOffAngle));
    }

    internal const double ReadyWait = 8;
    internal const double OrderRetry = 2;

    // Compare the selected, remaining grants over the same window as the mass
    // planner. Discount each round's damage by the measured landing share so
    // mixed gun/missile volleys still have to break both regenerating pools.
    internal static bool Sufficient(StrikeVolley.StationProfile station,
        IReadOnlyList<StrikeVolley.Loadout> ready, double landingShare, int shotsNeeded)
    {
        double share = double.IsFinite(landingShare) ? Math.Clamp(landingShare, StrikeVolley.LandingFloor, 1) : 1;
        if (ready.Count == 0 || ready.Sum(l => StrikeVolley.RoundsInWindow(l, station)) * share < shotsNeeded)
            return false;
        var delivered = ready.Select(l => l with
        {
            Mounts = l.Mounts.Select(m => m with { Power = (float)(m.Power * share) }).ToArray()
        }).ToArray();
        return StrikeVolley.Simulate(station, delivered, StrikeVolley.VolleyWindowSeconds).Killed;
    }

    internal struct ReadinessWait
    {
        private double? _qualifiedAt;

        internal List<short>? Members(List<short> arrived, List<short> ready, bool sufficient,
            double now, ref string? reason)
        {
            if (reason is null)
            {
                _qualifiedAt = null;
                return null;
            }
            if (reason == "deadline-forced") return arrived;
            _qualifiedAt ??= now;
            if (sufficient)
            {
                reason = "weapon-ready";
                return ready;
            }
            if (now - _qualifiedAt < ReadyWait) return null;
            reason = "readiness-timeout";
            return arrived;
        }
    }

    internal static Vector3 Lane(Vector3 outward, bool cloak, float range)
    {
        if (outward.LengthSquared() < 1) outward = new Vector3(1, 0, 0);
        outward = outward.Normalize();
        if (cloak)
        {
            var lateral = Vector3.CrossProduct(outward, new Vector3(0, 1, 0));
            if (lateral.LengthSquared() < .001f) lateral = Vector3.CrossProduct(outward, new Vector3(1, 0, 0));
            outward = (outward + .5f * lateral.Normalize()).Normalize();
        }
        return outward * (.85f * range);
    }

    internal static int Movement(Vector3 position, Vector3 velocity, Orientation orientation, Vector3 goal)
    {
        var correction = goal - position - 2 * velocity;
        return correction.LengthSquared() < 20 * 20 ? 0 : SiegeNavigation.DirectionButtons(correction, orientation);
    }

    internal static bool Spent(long? atRelease, long accepted) => atRelease is { } start && accepted > start;
}
