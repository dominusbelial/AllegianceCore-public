using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // P801 — finale feed + strike massing.
    //
    // Finale bombers used to fly to the target independently (each relaunch
    // attacked on its own). That is the trickle the rig measured: one SRM
    // Anti-Base hit takes a garrison shield 1.0 -> 0.5, the shield refills before
    // the next bomber arrives, and a 20k-armor garrison never dies. Here every
    // finale bomber that reaches the target sector holds at a standoff point
    // just outside the station until the volley requirement from
    // StrikeVolley.cs is assembled, then all of them take their attack order on
    // one shared go time. A late relaunch waits for the NEXT wave instead of
    // attacking alone. Escorts (P778a) and support repair (P791) are untouched.

    /// <summary>P801 — just outside a station's defences: 6.5 km from the hull,
    /// which is inside a bomber's missile reach but far enough that the group is
    /// not already trading fire while it forms up.</summary>
    internal const float FinaleStrikeStandoff = 3500f;

    /// <summary>P801 — lone wolves hold further out and laterally offset, so
    /// their approach lane is visibly different from the visible element's.</summary>
    internal const float FinaleLoneWolfStandoff = 5000f;
    internal const float FinaleLoneWolfLateral = 2500f;

    /// <summary>P801 — after this long without the computed volley the finale
    /// releases whatever is assembled (one group, never a stream) and logs the
    /// fallback. The same cap the wave policy uses.</summary>
    internal const double FinaleStagingHardCapSeconds = 180;

    /// <summary>P801 — a wave's release window. A relaunch that arrives after
    /// this waits for a fresh mass instead of joining a spent volley.</summary>
    internal const double FinaleWaveWindowSeconds = 20;

    /// <summary>Proxy radius for "the lone wolf arrived unseen": no visible enemy
    /// ship inside this radius of its release point while the cloak was held.
    /// (The real detection rule is signature vs scanner range — Ship.EffectiveSignature
    /// feeding the sector sweep, shipIGC.h:353; this is a bounded, honest proxy,
    /// not a claim of native invisibility.)</summary>
    internal const float LoneWolfDetectedRadius = 2500f;

    /// <summary>P801 — mark up to two cloak-capable strike hulls as lone wolves
    /// when their real purchased loadout carries a cloak. Called whenever the
    /// finale assigns strike hardware (relaunch and research refit).</summary>
    private static void AssignFinaleLoneWolf(ActiveBot bot, FinaleRun run, short hullId,
        IReadOnlyList<Allegiance.Core.Wire.ExpandedPartData>? parts)
    {
        if (bot.Finale is not { } assigned || !ReferenceEquals(assigned, run)) return;
        lock (run.Sync)
        {
            if (!run.Plans.TryGetValue(bot.Side, out var plan) || plan.AssignedLoneWolves >= ResearchTactics.MaxLoneWolves) return;
            var loadout = StrikeVolley.FromParts(Catalog.RepresentativeCore(), hullId, parts);
            if (loadout is not { CloakCapable: true }) return;
            bot.FinaleLoneWolf = true;
            plan.AssignedLoneWolves++;
        }
    }

    /// <summary>P801 — the volley requirement for this finale target, from the
    /// real granted loadouts and the real station type.</summary>
    private static StrikeVolley.Requirement FinaleVolleyRequirement(FinaleRun run, FinalePlan plan,
        short side, IReadOnlyList<ActiveBot> armed)
    {
        var core = Catalog.RepresentativeCore();
        if (!core.StationTypes.TryGetValue(plan.TargetTypeId, out var stationType))
            return new(ResearchTactics.StrikeGroupSize, ResearchTactics.StrikeGroupSize, 0,
                StrikeVolley.VolleyWindowSeconds, 0, 0, 0, 0);
        var profile = StrikeVolley.StationProfile.From(core, stationType);
        var loadouts = armed.Select(b => StrikeVolley.FromParts(core, ActualHullId(b), b.Parts)).ToArray();
        int roster = Math.Max(armed.Count, run.Bots.Count(b => b.Side == side
            && b.FinaleBomberReserved && !b.IsPod));
        return StrikeVolley.Compute(profile, loadouts, roster);
    }

    /// <summary>P801 — per-run coordinator, called once a second from
    /// <see cref="UpdateFinales"/> under the run lock. Computes the requirement,
    /// watches assembly, sets the shared go time, samples station health for the
    /// [strike] outcome and logs one bounded line per stage edge.</summary>
    private void FinaleStrikeMassTick(FinaleRun run, ActiveBot[] bots, double now)
    {
        foreach (var (side, plan) in run.Plans.OrderBy(kv => kv.Key))
        {
            if (!run.Sides.Contains(side) || run.Rotated || run.GameOverAt is not null) continue;
            // A bomber that already released this life is SPENT (its magazine is
            // empty): it must not be re-counted into another mass until it
            // relaunches (ResetNewLife clears FinaleStrikeSpent).
            var armed = run.Bots.Where(b => b.Side == side && b.FinaleBomberActive && b.FinaleGrantVerified
                && !b.IsPod && !b.FinaleStrikeSpent && IsParticipating(b)).ToArray();
            if (plan.KilledAt is not null)
            {
                if (!plan.OutcomeLogged)
                {
                    plan.OutcomeLogged = true;
                    Console.WriteLine($"[strike] side={side} finale end target=station#{plan.TargetId} sector={plan.Sector} "
                        + $"outcome=station-killed hitsInWindow={plan.HitsInWindow} "
                        + $"shield={Fraction(plan.ShieldAtGo)}->{Fraction(plan.ShieldNow)} "
                        + $"hull={Fraction(plan.HullAtGo)}->{Fraction(plan.HullNow)} "
                        + $"required={plan.RequiredBombers} loneWolves={plan.AssignedLoneWolves}");
                }
                continue;
            }
            if (armed.Length == 0) continue;
            plan.StagingSince ??= now;
            var requirement = FinaleVolleyRequirement(run, plan, side, armed);
            int roster = Math.Max(armed.Length, run.Bots.Count(b => b.Side == side
                && b.FinaleBomberReserved && !b.IsPod));
            int required = Math.Clamp(requirement.BombersRequired, 1, Math.Max(1, roster));
            plan.RequiredBombers = required;

            // `assembled` = armed and in the target sector (they are holding at the
            // standoff point). Reading each own world keeps per-side knowledge.
            var assembled = new List<short>();
            foreach (var bot in armed)
            {
                if (bot.Cs is not { } cs) continue;
                lock (cs.Gate)
                    if (cs.World.Ships.TryGetValue(bot.ShipId, out var own) && !own.IsDead
                        && own.StationId < 0 && own.SectorId == plan.Sector && own.Latest is not null
                        && cs.World.Stations.TryGetValue(plan.TargetId, out var station)
                        && station.Position is { Length: 3 } sp
                        && (own.Latest.Position - new Vector3(sp[0], sp[1], sp[2])).Length()
                            <= (bot.FinaleLoneWolf ? FinaleLoneWolfStandoff : FinaleStrikeStandoff)
                                + ResearchTactics.RallyGatherRadius)
                        assembled.Add(bot.ShipId);
            }

            if (plan.GoTime is null)
            {
                bool quota = assembled.Count >= required;
                bool fallback = now - plan.StagingSince >= FinaleStagingHardCapSeconds && assembled.Count > 0;
                if (quota || fallback)
                    CommitFinaleWave(run, plan, side, assembled, now,
                        quota ? "massed" : "staging-timeout", armed);
            }
            else if (now >= plan.GoTime + FinaleWaveWindowSeconds)
            {
                // The window is spent. If un-released bombers remain (a fresh
                // relaunch), form a NEW mass rather than letting them attack alone.
                bool waiting = armed.Any(b => !b.FinaleReleaseLogged && !plan.GoMembers.Contains(b.ShipId));
                if (waiting)
                {
                    plan.GoTime = null;
                    plan.GoMembers.Clear();
                    plan.MassLogged = false;
                    plan.StagingSince = now;
                    plan.HitsInWindow = 0;
                    plan.HullAtGo = plan.ShieldAtGo = -1;
                }
            }

            // Sample the target's live shield/hull at 1 Hz while a wave is out.
            if (plan.GoTime is { } go && now - plan.LastSampleAt >= 1)
            {
                plan.LastSampleAt = now;
                if (TrySampleStation(run, bots, side, plan, out float hull, out float shield))
                {
                    if (plan.ShieldAtGo < 0) { plan.ShieldAtGo = shield; plan.HullAtGo = hull; }
                    else if (now <= go + FinaleWaveWindowSeconds + 15
                        && (shield < plan.ShieldNow - .001f || hull < plan.HullNow - .001f))
                        plan.HitsInWindow++;
                    plan.ShieldNow = shield;
                    plan.HullNow = hull;
                }
            }
        }
    }

    private void CommitFinaleWave(FinaleRun run, FinalePlan plan, short side, List<short> members,
        double now, string reason, ActiveBot[] armed)
    {
        plan.GoTime = now + ResearchTactics.StrikeGoLeadSeconds;
        plan.GoMembers.Clear();
        foreach (short id in members) plan.GoMembers.Add(id);
        plan.HullAtGo = plan.ShieldAtGo = -1;
        foreach (var bot in armed)
            if (plan.GoMembers.Contains(bot.ShipId)) { bot.FinaleReleaseLogged = false; bot.FinaleReleaseAt = null; }
        if (TrySampleStation(run, armed, side, plan, out float hull, out float shield))
        {
            plan.HullAtGo = plan.HullNow = hull;
            plan.ShieldAtGo = plan.ShieldNow = shield;
        }
        if (!plan.MassLogged)
        {
            plan.MassLogged = true;
            Console.WriteLine($"[strike] side={side} finale commit required={plan.RequiredBombers} "
                + $"assembled={members.Count} visible={members.Count - plan.AssignedLoneWolves} "
                + $"loneWolves={plan.AssignedLoneWolves} target=station#{plan.TargetId} sector={plan.Sector} "
                + $"goIn={ResearchTactics.StrikeGoLeadSeconds:F0}s wait={now - (plan.StagingSince ?? now):F1}s "
                + $"stationHull={Fraction(hull)} stationShield={Fraction(shield)} reason={reason}");
        }
    }

    private static bool TrySampleStation(FinaleRun run, IReadOnlyList<ActiveBot> bots, short side,
        FinalePlan plan, out float hull, out float shield)
    {
        hull = shield = -1;
        foreach (var bot in bots)
        {
            if (bot.Side != side || bot.Cs is not { } cs) continue;
            lock (cs.Gate)
                if (cs.World.StationLive.TryGetValue(plan.TargetId, out var live))
                {
                    hull = live.HullFraction;
                    shield = live.ShieldFraction;
                    return true;
                }
        }
        return false;
    }

    private static string Fraction(float value) => value < 0
        ? "-1"
        : value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>P801 — hold a finale bomber at the staging point until its wave's
    /// shared go time, then release it. Returns true when the tick is owned by
    /// the hold (no attack/transit decision this tick). Only in the target
    /// sector: elsewhere the ordinary finale transit flies it there. A released
    /// bomber falls through to the normal station attack, so P778a push and
    /// support repair keep working on the group.</summary>
    private bool HoldFinaleStrike(ActiveBot bot, double now)
    {
        if (!bot.FinaleBomberActive || bot.IsPod || bot.Finale is not { Rotated: false } run) return false;
        if (run.TriggeredAt is null || run.GameOverAt is not null || !run.Sides.Contains(bot.Side)) return false;
        if (!run.Plans.TryGetValue(bot.Side, out var plan) || plan.KilledAt is not null) return false;
        if (bot.Cs is not { } cs || CsSector(cs) != plan.Sector) return false;

        double? go;
        bool member;
        lock (run.Sync) { go = plan.GoTime; member = plan.GoMembers.Contains(bot.ShipId); }

        if (go is { } release && member)
        {
            if (StrikeVolley.ShouldRelease(now, go, member, bot.FinaleReleaseLogged))
            {
                if (!bot.FinaleReleaseLogged)
                {
                    bot.FinaleReleaseLogged = true;
                    bot.FinaleReleaseAt = now;
                    bot.FinaleStrikeSpent = true;   // magazine spent: wait for a relaunch before the next wave
                    bool cloaked = bot.CloakHeld;
                    float nearest = NearestVisibleEnemy(cs, bot.ShipId, bot.Side, out _);
                    bool undetected = bot.FinaleLoneWolf && cloaked && nearest > LoneWolfDetectedRadius;
                    Console.WriteLine($"[strike] side={bot.Side} finale release ship={bot.ShipId} "
                        + $"role={(bot.FinaleLoneWolf ? "lone-wolf" : "visible")} "
                        + $"delta={now - release:F1}s cloak={cloaked.ToString().ToLowerInvariant()} "
                        + $"nearestEnemy={(float.IsPositiveInfinity(nearest) ? "-1" : nearest.ToString("F0"))} "
                        + $"undetected={undetected.ToString().ToLowerInvariant()}");
                }
                return false;   // released: the normal station attack runs this tick
            }
            HoldAtFinaleStandoff(bot, cs, plan, now);
            return true;
        }

        // Not in the committed wave (a relaunch, or still gathering): hold.
        HoldAtFinaleStandoff(bot, cs, plan, now);
        return true;
    }

    private void HoldAtFinaleStandoff(ActiveBot bot, ConnectionSession cs, FinalePlan plan, double now)
    {
        Vector3 station;
        lock (cs.Gate)
        {
            if (!cs.World.Stations.TryGetValue(plan.TargetId, out var target)
                || target.Position is not { Length: 3 } p) return;
            station = new Vector3(p[0], p[1], p[2]);
        }
        if (!TryCsPose(cs, bot.ShipId, out var pos, out _)) return;
        var outward = pos - station;
        if (outward.LengthSquared() < 1f) outward = new Vector3(1, 0, 0);
        outward = outward.Normalize();
        float standoff = bot.FinaleLoneWolf ? FinaleLoneWolfStandoff : FinaleStrikeStandoff;
        Vector3 hold = station + outward * standoff;
        if (bot.FinaleLoneWolf)
        {
            // A lateral lane offset keeps the wolf's approach visibly distinct
            // from the visible element's line to the station.
            var lateral = Vector3.CrossProduct(outward, new Vector3(0, 1, 0));
            if (lateral.LengthSquared() < 1e-3f) lateral = Vector3.CrossProduct(outward, new Vector3(1, 0, 0));
            hold += lateral.Normalize() * FinaleLoneWolfLateral;
        }
        bot.TargetId = null;
        bot.OrderedTargetId = null;
        CombatFlyTo(bot, hold, 60f, 900f, 0);
    }

    private static float NearestVisibleEnemy(ConnectionSession cs, short shipId, short side, out short sector)
    {
        float best = float.PositiveInfinity;
        sector = -1;
        if (!TryCsPose(cs, shipId, out var pos, out _)) return best;
        lock (cs.Gate)
        {
            sector = cs.World.Ships.TryGetValue(shipId, out var own) ? own.SectorId : (short)-1;
            foreach (var ship in cs.World.Ships.Values)
                if (ship.SideId >= 0 && ship.SideId != side && !ship.IsDead && !ship.IsNotVisible
                    && ship.SectorId == sector && ship.Latest is { } pose)
                    best = MathF.Min(best, (pose.Position - pos).Length());
        }
        return best;
    }
}
