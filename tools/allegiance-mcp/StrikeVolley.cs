using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P801 — the volley requirement: how many bombers must release TOGETHER to
/// kill one station inside a single shield-regeneration window.
///
/// <para><b>Native rules this encodes (no constants changed):</b> the station's
/// damage matrices are <c>DamageConstants[dmgid][defid]</c> indexed by the
/// station TYPE's defense bytes (<c>src/Igc/stationIGC.cpp:138,159</c>;
/// <c>DataStationTypeIGC.DefenseTypeArmor/Shield</c>); shield damage is
/// <c>amount*dtmShield/maxShield</c> and an overflow becomes hull damage
/// <c>overflow*maxShield*dtmArmor/(maxArmor*dtmShield)</c>
/// (<c>src/Igc/stationIGC.cpp:159-193</c>). Regeneration is native
/// <c>CstationIGC::Update</c>: ARMOR regenerates while the armor is below full,
/// and only once armor is full does the SHIELD regenerate
/// (<c>src/Igc/stationIGC.cpp:192-210</c> — this is why a scratched garrison
/// does not heal its shield, and why widely spaced hits can never break one).
/// Missile/launcher data are the real <c>DataMissileTypeIGC.Power</c> and
/// <c>LoadTime</c>; a magazine's shots are spaced by that load time
/// (<c>src/Igc/missileigc.cpp</c>).</para>
///
/// <para><b>Pure.</b> No world, socket, bot or server state: it maps a station
/// profile + real bomber loadouts to a bomber count and can replay a release
/// schedule. Unit-tested against real PCore001 data.</para>
/// </summary>
internal static class StrikeVolley
{
    /// <summary>Native station defense/durability inputs, copied from a real
    /// <see cref="DataStationTypeIGC"/> so the planner stays core-free.</summary>
    internal readonly record struct StationProfile(
        float MaxArmor, float MaxShield, float ArmorRegen, float ShieldRegen,
        byte ArmorDefense, byte ShieldDefense, float[][] DamageConstants)
    {
        internal static StationProfile From(StaticCore core, DataStationTypeIGC t) => new(
            t.MaxArmorHitPoints, t.MaxShieldHitPoints, t.ArmorRegeneration, t.ShieldRegeneration,
            t.DefenseTypeArmor, t.DefenseTypeShield, core.Constants.DamageConstants);
    }

    /// <summary>One real weapon group on a bomber: a magazine with
    /// <paramref name="Shots"/> rounds loading every <paramref name="LoadTime"/>
    /// seconds, or a gun whose burst repeats at <c>LoadTime</c> for the window.</summary>
    internal sealed record Mount(byte DamageType, float Power, int Shots, float LoadTime);

    /// <summary>What one bomber really carries for the anti-base job.</summary>
    internal sealed record Loadout(short HullId, Mount[] Mounts, bool CloakCapable = false);

    /// <summary>The release schedule for one bomber: each round's time offset from
    /// the shared go time. Guns contribute their burst inside the window.</summary>
    internal sealed record Shot(double Time, float Power, byte DamageType);

    /// <summary>Planner output. <see cref="BombersRequired"/> already includes the
    /// landing and survival corrections described on <see cref="Compute"/>;
    /// <see cref="KillBombers"/> is the bare mathematical minimum (every round
    /// landing, full-health station). <see cref="SurvivalBombers"/> is the extra
    /// beyond that bare minimum (0 when the survival margin is off and the
    /// measured landing share is 1).</summary>
    internal readonly record struct Requirement(
        int BombersRequired, int KillBombers, int ShotsToKill, double WindowSeconds,
        double ShieldRefillSeconds, float ShieldDamagePerRound, float ArmorDamagePerRound,
        int LoadoutCount, int SurvivalBombers = 0, double LandingShare = 1, bool CanKill = true);

    /// <summary>P801 tunable: all rounds of one massed strike must land inside this
    /// window. 20 s covers a five-round SRM magazine (load time 4 s = 16 s of
    /// shots) and is far shorter than the garrison's ~50 s half-shield refill, so
    /// repairs cannot outpace the volley.</summary>
    internal const double VolleyWindowSeconds = 20;

    /// <summary>P801 tunable: how long a garrison takes to refill HALF of its
    /// shield (<c>0.5*maxShield/shieldRegen</c>: 5000/100 = 50 s) — the window
    /// beyond which a trickle can never accumulate damage.</summary>
    internal const double HalfShieldRefill = .5;

    /// <summary>P801 tunable: minimum meaningful magazine for planning (a hull
    /// with no station-damaging weapon is not a strike bomber). P959 raises the
    /// bound from 12 to 18 so the corrected wave can also be GROWN from spare
    /// fighters inside the same one-window mass (a full garrison with defenders
    /// needs more hulls than the old ideal-case arithmetic asked for).</summary>
    internal const int MaxPlannedBombers = 18;

    /// <summary>P959 — the lowest delivery share the planner will plan with. A
    /// wave that measurably lands less than a fifth of the rounds its hulls
    /// carry cannot be fixed by sizing alone (the press would need an
    /// unassembleable wing), so the requirement saturates there and the commit
    /// rule keeps holding/sending the whole wing instead of requesting more.</summary>
    internal const double LandingFloor = 0.2;

    /// <summary>Build one bomber's real anti-base loadout from an expanded loadout
    /// list (the parts the server actually granted). Missile launchers use their
    /// magazine amount and load time; station-damaging guns contribute their
    /// burst inside the volley window. A cloak part on the hull makes it a
    /// legal lone-wolf hull (the same <c>ET_Cloak</c>@0 gate
    /// <see cref="CloakPolicy"/> uses).</summary>
    internal static Loadout? FromParts(StaticCore core, short hullId, IReadOnlyList<ExpandedPartData>? parts)
    {
        if (!core.HullTypes.TryGetValue(hullId, out var hull) || parts is null) return null;
        var mounts = new List<Mount>();
        bool cloak = false;
        foreach (var part in parts)
        {
            if (part.Amount <= 0 || part.MountId >= 128) continue;   // cargo is not a firing mount
            // A turret mount fires only with a gunner aboard (weaponIGC.h:236-254), and a
            // gunner aims at ships, not the station: count FIXED mounts only, or a
            // turret bomber's unmanned guns make the strike requirement too low (P814b).
            if (part.MountId >= hull.MaxFixedWeapons && part.MountId < hull.MaxWeapons) continue;
            if (core.PartTypes.TryGetValue(part.PartId, out var type))
            {
                if (part.MountId == 0 && type.Type is DataPartTypeIGC.Cloak { MaxCloaking: > 0 })
                    cloak = true;
                if (part.MountId < 128 && type.Type is DataPartTypeIGC.Weapon weapon
                    && core.ProjectileTypes.TryGetValue(weapon.ProjectileTypeId, out var round)
                    && round.Power > 0)
                {
                    // A gun fires one burst slot per ready cycle; cap the count to
                    // the volley window so a fast gun cannot imply infinite damage.
                    int burst = Math.Max(1, (int)Math.Ceiling(VolleyWindowSeconds / Math.Max(.05f, weapon.DtimeReady)));
                    mounts.Add(new(round.DamageType, round.Power, burst, Math.Max(.05f, weapon.DtimeBurst)));
                }
                continue;
            }
            if (core.Launchers.TryGetValue(part.PartId, out var launcher)
                && core.MissileTypes.TryGetValue(launcher.ExpendabletypeId, out var missile)
                && missile.Power > 0)
                mounts.Add(new(missile.DamageType, missile.Power,
                    Math.Clamp((int)part.Amount, 0, 64), Math.Max(.1f, missile.LoadTime)));
        }
        if (mounts.Count == 0) return null;
        return new(hullId, mounts.ToArray(), cloak
            && core.PartTypes.Values.Any(p => p.Type is DataPartTypeIGC.Cloak { MaxCloaking: > 0 } && hull.CanMountPart(p, 0)));
    }

    /// <summary>Real per-round damage against this station: <c>power *
    /// DamageConstants[type][defense]</c> for armor and shield separately.</summary>
    internal static (float Armor, float Shield) RoundDamage(StationProfile station,
        byte damageType, float power)
    {
        var dc = station.DamageConstants;
        int row = damageType & 0x1f;
        if (row >= DataConstantsIGC.DmgidMax) return (0, 0);
        float At(byte def) => def >= DataConstantsIGC.DefidMax ? 1 : MathF.Max(0, dc[row][def]);
        return (power * At(station.ArmorDefense), power * At(station.ShieldDefense));
    }

    /// <summary>The shots one bomber releases on one go time, in release order.</summary>
    internal static IEnumerable<Shot> Schedule(Loadout loadout)
    {
        foreach (var mount in loadout.Mounts)
            for (int i = 0; i < mount.Shots; i++)
                yield return new Shot(i * mount.LoadTime, mount.Power, mount.DamageType);
    }

    /// <summary>P959 — station-damaging rounds this loadout can put inside one
    /// volley window (the denominator of the measured landing share). Only rounds
    /// whose real damage matrix reaches the station count: a gun burst that the
    /// station's defense zeroes (or a dispensers/chaff mount) must not inflate the
    /// expectation to thousands of rounds.</summary>
    internal static int RoundsInWindow(Loadout loadout, StationProfile station, double window = VolleyWindowSeconds)
        => Schedule(loadout).Count(s => s.Time <= window && RoundDamage(station, s.DamageType, s.Power).Shield > 0);

    /// <summary>P959 — does this REAL granted loadout damage the station at all
    /// (the same both-pools gate <see cref="Compute"/> uses)? The arm path uses it
    /// to decide whether a live wing member already carries a strike loadout
    /// (fly it now) or must dock for a legal refit before it transits.</summary>
    internal static bool DamagesStation(StationProfile station, Loadout loadout)
    {
        foreach (var mount in loadout.Mounts)
        {
            if (mount.Power <= 0 || mount.Shots <= 0) continue;
            var (armor, shield) = RoundDamage(station, mount.DamageType, mount.Power);
            if (shield > 0 && (station.MaxShield == 0 || armor > 0)) return true;
        }
        return false;
    }

    /// <summary>Smallest number of these bombers that kills the station within
    /// <see cref="VolleyWindowSeconds"/>, plus one spare bomber when the roster
    /// can field it (the "margin": a reserve hull for a miss, a ram or a
    /// defender kill). Sizes every hull as the WEAKEST usable loadout in the list
    /// (P959: a real wing is mixed, so the conservative plan is the only honest
    /// one), and returns a requirement of at least 1 for a strike-capable side.
    ///
    /// <para><b>P896 survival margin.</b> <paramref name="survivalMargin"/> = 0
    /// is exactly the P801 behaviour above. Above 0 the wave also carries
    /// <c>ceil(margin × max(losses, defenders))</c> extra bombers, where
    /// <paramref name="losses"/> is how many of this side's strike bombers were
    /// lost on the previous wave and <paramref name="defenders"/> is how many live
    /// enemy ships this side can see in the target sector — the two ways a wave
    /// bleeds hulls before its rounds land (P896 §2). The total is bounded by the
    /// side's reserved bomber budget (<paramref name="hullsAvailable"/>, already
    /// capped by <c>ShowcaseFinale.Settings.Bombers</c>) and by
    /// <see cref="MaxPlannedBombers"/>, so a bigger wave can still be assembled
    /// inside the staging cap instead of out-waiting the deadline.</para>
    ///
    /// <para><b>P959 landing share.</b> <paramref name="landingShare"/> is the
    /// measured share of one committed bomber's rounds that really reach the
    /// station (released-and-alive × guided-and-hit, see
    /// <c>BotManager.FinaleLandingShare</c>). It divides the ideal hull count:
    /// the planner no longer assumes every magazine round lands. 1 (the default,
    /// and what an unmeasured wave uses) reproduces the arithmetic above exactly;
    /// the count is clamped to <see cref="LandingFloor"/>..1 and the result never
    /// exceeds <paramref name="hullsAvailable"/>.</para></summary>
    internal static Requirement Compute(StationProfile station,
        IReadOnlyList<Loadout?> loadouts, int hullsAvailable, double window = VolleyWindowSeconds,
        double survivalMargin = 0, int losses = 0, int defenders = 0, double landingShare = 1)
    {
        var requirement = ComputeCore(station, loadouts, hullsAvailable, window, survivalMargin, losses, defenders, landingShare);
        if (BotDecisionRecorder.IsEnabled)
            BotDecisionRecorder.Record("Finale", "VolleyRequirement",
                new { station, loadouts, hullsAvailable, window, survivalMargin, losses, defenders, landingShare }, requirement);
        return requirement;
    }

    private static Requirement ComputeCore(StationProfile station,
        IReadOnlyList<Loadout?> loadouts, int hullsAvailable, double window,
        double survivalMargin, int losses, int defenders, double landingShare)
    {
        // A mount only counts when the core's matrix says it damages BOTH the
        // station's shield and (after the shield is down) its armor — the same
        // gate ResearchSiegeNeedsResupply uses for a sustainable station weapon.
        bool Damages(Mount m)
        {
            var (armor, shield) = RoundDamage(station, m.DamageType, m.Power);
            return m.Power > 0 && m.Shots > 0 && shield > 0 && (station.MaxShield == 0 || armor > 0);
        }
        // P959 — plan with the WEAKEST usable loadout first. A wing is mixed in
        // practice (the rig's waves flew 2-round SRM Anti-Base stealth bombers while
        // the planner had been sized on whichever 5-round hull happened to be armed
        // that second), so sizing every hull as the weakest member is the only
        // conservative reading of "these bombers": the strongest-first plan asked for
        // 3 hulls that really carried 2-round magazines.
        var usable = loadouts.OfType<Loadout>()
            .Where(l => l.Mounts.Length > 0 && l.Mounts.Any(Damages))
            .OrderBy(TotalDamage).ThenBy(l => l.HullId).ToArray();
        if (usable.Length == 0) return new(1, 1, 0, window,
            ShieldRefillSeconds(station), 0, 0, 0, 0, 1);
        int available = Math.Clamp(hullsAvailable <= 0 ? usable.Length : hullsAvailable, 1, MaxPlannedBombers);

        int kill = 0;
        float cleanShieldDamage = 0, cleanArmorDamage = 0;
        int shotsToKill = 0;
        for (int n = 1; n <= available; n++)
        {
            // P959 — every hull in the search carries the WEAKEST usable magazine:
            // sizing a mixed wing by its best hull asked for 3 bombers while the
            // wave that formed carried 2-round SRM Anti-Base magazines.
            var group = Enumerable.Range(0, n).Select(_ => usable[0]).ToArray();
            var result = Simulate(station, group, window);
            if (n == 1) { cleanShieldDamage = result.FirstShieldDamage; cleanArmorDamage = result.FirstArmorDamage; }
            if (!result.Killed) continue;
            kill = n;
            shotsToKill = result.ShotsFired;
            break;
        }
        bool canKill = kill > 0;
        if (kill == 0)
        {
            // Even the whole roster cannot kill it inside one window: ask for the
            // whole roster, released together, rather than a stream that cannot
            // concentrate any better.
            kill = available;
            var group = Enumerable.Range(0, available).Select(_ => usable[0]).ToArray();
            shotsToKill = Simulate(station, group, window).ShotsFired;
        }
        int ideal = kill < available ? kill + 1 : kill;
        // P959 — the planner's ideal count assumes every magazine round lands on
        // the station. Reality does not: hulls die on the run-in and missiles are
        // spent/missed, so a wave of `ideal` hulls delivers only `landing` of
        // them. Divide by the measured share (floored, never above 1) so the
        // requirement predicts a KILL instead of a scratch.
        double landing = double.IsFinite(landingShare) ? Math.Clamp(landingShare, LandingFloor, 1) : 1;
        int landed = (int)Math.Ceiling(ideal / landing);
        // P896 — survival margin: expected losses on the run in (observed last-wave
        // losses) or the defenders standing in the target sector, whichever is
        // bigger, scaled by the setting; never past the reserved budget.
        int survival = survivalMargin <= 0 ? 0
            : (int)Math.Ceiling(survivalMargin * Math.Max(0, Math.Max(losses, defenders)));
        int total = Math.Clamp(landed + survival, 1, available);
        return new(total, Math.Max(1, kill), shotsToKill, window,
            ShieldRefillSeconds(station), cleanShieldDamage, cleanArmorDamage, usable.Length,
            Math.Max(0, total - ideal), landing, canKill);
    }

    private static float TotalDamage(Loadout l) => l.Mounts.Sum(m => m.Power * m.Shots);

    internal static double ShieldRefillSeconds(StationProfile s)
        => s.ShieldRegen <= 0 ? double.PositiveInfinity : HalfShieldRefill * s.MaxShield / s.ShieldRegen;

    internal readonly record struct VolleyResult(bool Killed, double KilledAt, int ShotsFired,
        float ShieldBefore, float ShieldAfter, float ArmorBefore, float ArmorAfter,
        float FirstShieldDamage, float FirstArmorDamage);

    /// <summary>Replay a simultaneous group release against the native station
    /// damage/regen rules. Used by the requirement search and directly by the
    /// loopback proof (massed vs trickle schedules).</summary>
    internal static VolleyResult Simulate(StationProfile station,
        IReadOnlyList<Loadout> group, double window)
    {
        var shots = new List<(double At, Loadout Owner, Shot Shot)>();
        foreach (var loadout in group)
            foreach (var shot in Schedule(loadout))
                if (shot.Time <= window) shots.Add((shot.Time, loadout, shot));
        shots.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Shot.Power.CompareTo(b.Shot.Power));
        return Replay(station, shots.Select(s => (At: s.At, Owner: s.Owner, Shot: s.Shot)).ToArray(), window);
    }

    /// <summary>Replay an explicit release schedule (time → bomber loadout → shot).
    /// The loopback test uses this for the massed and the trickle runs.</summary>
    internal static VolleyResult Replay(StationProfile station,
        IReadOnlyList<(double At, Loadout Owner, Shot Shot)> schedule, double horizon)
    {
        float shield = station.MaxShield > 0 ? 1f : 0f;
        float armor = 1f;
        double now = 0;
        int fired = 0;
        float firstShield = 0, firstArmor = 0;
        double killedAt = double.NaN;
        float shieldBefore = shield, armorBefore = armor;

        void Regenerate(double until)
        {
            if (until <= now) return;
            float dt = (float)(until - now);
            if (armor < 1f && station.MaxArmor > 0)
                armor = MathF.Min(1f, armor + dt * station.ArmorRegen / station.MaxArmor);
            else if (armor >= 1f && shield < 1f && station.MaxShield > 0)
                shield = MathF.Min(1f, shield + dt * station.ShieldRegen / station.MaxShield);
            now = until;
        }

        foreach (var (at, owner, shot) in schedule.OrderBy(s => s.At))
        {
            if (at > horizon) break;
            Regenerate(at);
            shieldBefore = shield; armorBefore = armor;
            var (armorDamage, shieldDamage) = RoundDamage(station, shot.DamageType, shot.Power);
            if (shieldDamage <= 0 || station.MaxShield <= 0) continue;
            fired++;
            if (fired == 1) { firstShield = shieldDamage; firstArmor = armorDamage; }
            float absorb = shieldDamage / station.MaxShield;
            if (shield >= absorb)
                shield -= absorb;
            else
            {
                float overflow = absorb - shield;
                if (armorDamage > 0 && armor > 0 && station.MaxArmor > 0)
                    armor -= overflow * station.MaxShield * armorDamage / (station.MaxArmor * shieldDamage);
                shield = 0;
                if (armor <= 0)
                {
                    armor = 0;
                    killedAt = at;
                    break;
                }
            }
        }
        if (double.IsNaN(killedAt)) Regenerate(horizon);
        return new(!double.IsNaN(killedAt), killedAt, fired,
            shieldBefore, shield, armorBefore, armor, firstShield, firstArmor);
    }

    /// <summary>P801 finale feed rule: a bomber releases ONLY when it was in the
    /// committed wave and the shared go time has arrived. A relaunch that missed
    /// the wave (or a bomber still gathering) waits for the next mass — it never
    /// attacks alone. Pure so the finale staging rule is unit-testable.</summary>
    internal static bool ShouldRelease(double now, double? goTime, bool inCommittedWave, bool alreadyReleased)
    {
        bool release = alreadyReleased || inCommittedWave && goTime is { } go && now >= go;
        if (BotDecisionRecorder.IsEnabled)
            BotDecisionRecorder.Record("Finale", "ShouldRelease",
                new { now, goTime, inCommittedWave, alreadyReleased }, new { release });
        return release;
    }

    /// <summary>Convenience for the caller: the station a real core type describes.</summary>
    internal static StationProfile? Profile(StaticCore core, short stationTypeId)
        => core.StationTypes.TryGetValue(stationTypeId, out var type) ? StationProfile.From(core, type) : null;
}
