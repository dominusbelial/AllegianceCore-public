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
    //
    // P959 — the four defects that kept the wave from killing anything:
    //  1. the requirement was bounded by the hulls ALIVE at that second (pods
    //     excluded), so a depleted side "proved" a 1-4 bomber kill;
    //  2. the reserved wing was never ordered anywhere at the arm (only a
    //     RELAUNCH made a bot a strike bomber), so the wing was never together;
    //  3. the standoff hold flew toward its own radial hold point, i.e. nose away
    //     from the station, so the native missile lock never built and no bomber
    //     could fire when released;
    //  4. the 180 s staging cap released whatever was assembled (2) against a
    //     full-health garrison.
    // See docs/reports/FINALE_STRIKE_MASS_P959.md.

    /// <summary>P801 — just outside a station's defences: 3.5 km from the hull,
    /// which is inside a bomber's missile reach but far enough that the group is
    /// not already trading fire while it forms up.</summary>
    internal const float FinaleStrikeStandoff = 3500f;

    /// <summary>P801 — lone wolves hold further out and laterally offset, so
    /// their approach lane is visibly different from the visible element's.</summary>
    internal const float FinaleLoneWolfStandoff = 5000f;
    internal const float FinaleLoneWolfLateral = 2500f;

    /// <summary>P959 — how long a mass may keep forming before the coordinator logs
    /// a stall. It never commits on its own any more: P801's flat cap released
    /// whatever was assembled, typically two bombers, against a full-health
    /// garrison, they died, the shield regenerated, and the next wave repeated it.
    /// Only the deadline math can now force a short wave.</summary>
    internal const double FinaleStagingHardCapSeconds = 180;

    /// <summary>P801 — a wave's release window. A relaunch that arrives after
    /// this waits for a fresh mass instead of joining a spent volley.</summary>
    internal const double FinaleWaveWindowSeconds = 20;

    /// <summary>P959 — how long after the release window the coordinator keeps
    /// sampling the target before it closes the wave (missile flight + the last
    /// magazine rounds). The same tail the P801 hit counter used.</summary>
    internal const double FinaleWaveTailSeconds = 15;

    /// <summary>A wave that cannot reach its requirement may still be forced by the
    /// deadline, but never smaller than this (or the whole requirement when it is
    /// smaller).</summary>
    internal const int FinaleMinimumStrike = 2;

    /// <summary>Proxy radius for "the lone wolf arrived unseen": no visible enemy
    /// ship inside this radius of its release point while the cloak was held.
    /// (The real detection rule is signature vs scanner range — Ship.EffectiveSignature
    /// feeding the sector sweep, shipIGC.h:353; this is a bounded, honest proxy,
    /// not a claim of native invisibility.)</summary>
    internal const float LoneWolfDetectedRadius = 2500f;

    /// <summary>P959 — is this side's strike armed (finale trigger or the early
    /// pressure wave)? Every strike path uses this instead of <c>run.Sides</c> so
    /// the early wave runs through exactly the same machinery.</summary>
    private static bool FinaleSideArmed(FinaleRun run, short side)
        => run.Sides.Contains(side) || run.EarlySides.Contains(side);

    /// <summary>P801 — mark up to two cloak-capable strike hulls as lone wolves
    /// when their real granted loadout carries a cloak. Called whenever the
    /// finale assigns strike hardware (relaunch and research refit).</summary>
    private static void AssignFinaleLoneWolf(ActiveBot bot, FinaleRun run, short hullId,
        IReadOnlyList<Allegiance.Core.Wire.ExpandedPartData>? parts)
    {
        if (bot.Finale is not { } assigned || !ReferenceEquals(assigned, run)) return;
        lock (run.Sync)
        {
            if (!run.Plans.TryGetValue(bot.Side, out var plan)) return;
            var loadout = StrikeVolley.FromParts(Catalog.RepresentativeCore(), hullId, parts);
            // A relaunch re-enters here: the wolf keeps its role only if this hull can still cloak.
            bot.FinaleLoneWolf = false;
            if (loadout is not { CloakCapable: true }) { plan.AssignedLoneWolves = LiveLoneWolves(run, bot.Side); return; }
            // Rig 221 batch 23b: a plain counter was bumped again on every relaunch and never released
            // when a wolf died or lost its cloak, so it sat at 2 and the commit line printed visible=-1.
            if (LiveLoneWolves(run, bot.Side) >= ResearchTactics.MaxLoneWolves) return;
            bot.FinaleLoneWolf = true;
            plan.AssignedLoneWolves = LiveLoneWolves(run, bot.Side);
        }
    }

    private static int LiveLoneWolves(FinaleRun run, short side) =>
        run.Bots.Count(b => b.Side == side && b.FinaleLoneWolf && b.FinaleBomberReserved && !b.IsPod);

    /// <summary>P959 — put one wing member on strike duty: role, target sector,
    /// entry route, and no leftover defence/objective goal. Shared by the arm, the
    /// wing-growth path, pre-arm and relaunch so a wing member is ordered exactly
    /// the same way wherever it joins.</summary>
    private static void OrderFinaleWingBot(ActiveBot bot, FinaleRun run, FinalePlan plan, double now)
    {
        bot.FinaleOriginal ??= new(bot.HullId, bot.HullName, bot.Parts, bot.WeaponPartId,
            bot.LauncherPartId, bot.MissileReadyTime, bot.WingmanBehaviour, bot.TargetSector, bot.RouteIndex, bot.Role);
        if (!bot.FinaleEscort) bot.Role = "bomber";
        bot.UseStaging = false;
        bot.MeetingEngagement = false;
        bot.IsExplorer = false;
        bot.ReconTarget = null;
        bot.TargetSector = plan.Sector;
        bot.TargetId = null;
        bot.OrderedTargetId = null;
        bot.RoutePath = null;
        bot.RouteIndex = -1;
        bot.Arrived = false;
        bot.ArrivedAt = null;
        bot.StagingSector = -1;
        bot.StagePhase = StageReleasedPhase;
        bot.ReleasedAt = now;
        EndDefense(bot);
        bot.Path.Clear();
        bot.PathGoal = -1;
        AssignResearchSiegeRoute(bot, run, plan);
    }

    /// <summary>P959 — is the PRE-ARM window open (reserve the wing and start its
    /// legal refits)? Pure so the setting's off path is unit-testable: 0 = off.</summary>
    internal static bool FinalePrearmDue(double prearmMinutes, double afterMinutes, double ageSeconds)
        => prearmMinutes > 0 && ageSeconds >= Math.Max(0, (afterMinutes - Math.Min(prearmMinutes, afterMinutes)) * 60);

    /// <summary>P959 — is the one early pressure wave due? Pure: 0 = off (the old
    /// timeline, byte-identical behaviour), one wave only, never after the finale
    /// itself would have armed.</summary>
    internal static bool FinaleEarlyPressureDue(double earlyMinutes, double afterMinutes, double ageSeconds,
        bool alreadyArmed)
        => earlyMinutes > 0 && !alreadyArmed
            && ageSeconds >= earlyMinutes * 60 && ageSeconds < afterMinutes * 60;

    /// <summary>P959 — may this live wing member fly as a strike bomber right now?
    /// Only when its REAL granted hardware damages the target (the granted hull must
    /// be the hull those parts are mounted on). Everything else refits legally first.</summary>
    internal static bool FinaleWingMemberReady(bool loadoutDamages, bool grantedHullMatches)
        => loadoutDamages && grantedHullMatches;

    /// <summary>P959 — the wing size this wave needs: at least the corrected
    /// requirement, never below what is already reserved, capped by the wing cap
    /// (<c>Settings.MaxWing</c>) the side can grow to from its spare fighters.
    /// Pure so the growth cap is unit-testable.</summary>
    internal static int FinaleWingTarget(int required, int reserved, int cap)
        => Math.Clamp(Math.Max(required, reserved), 1, Math.Max(1, cap));

    /// <summary>P959 — grow the reserved wing from the side's spare combat hulls
    /// when the corrected requirement cannot be met by the configured wing. Every
    /// added hull is an ordinary bot: it keeps its legal purchased loadout, docks
    /// and buys a legal strike loadout through the normal refit path, and joins the
    /// same mass. No grant, tech bypass or model insertion.</summary>
    private void GrowFinaleWing(FinaleRun run, FinalePlan plan, short side, int target, ActiveBot[] bots, double now)
    {
        int reserved = run.Bots.Count(b => b.Side == side && b.FinaleBomberReserved);
        if (reserved >= target) return;
        var core = Catalog.RepresentativeCore();
        var profile = StrikeVolley.Profile(core, plan.TargetTypeId);
        int added = 0;
        foreach (var bot in bots.Where(b => b.Side == side && !b.FinaleBomberReserved && !b.FinaleEscort
                && !b.IsPod && IsParticipating(b) && !IsSupport(b))
            .OrderByDescending(b => IsFinaleFighter(b)).ThenByDescending(b => ResearchFinale(b)).ThenBy(b => b.ShipId))
        {
            if (reserved + added >= target) break;
            bot.FinaleBomberReserved = true;
            bool ready = profile is { } stationProfile
                && FinaleGrantedStrikeLoadout(bot) is { } loadout
                && FinaleWingMemberReady(StrikeVolley.DamagesStation(stationProfile, loadout),
                    bot.Granted.HullId == ActualHullId(bot));
            if (ready)
            {
                // Already carries a legal strike loadout: fly it to the wing now.
                bot.FinaleBomberActive = bot.FinaleGrantVerified = true;
                bot.WingmanBehaviour |= Showcase.WbbmUseMissiles;
                OrderFinaleWingBot(bot, run, plan, now);
            }
            else
            {
                // Refit FIRST (legal dock-and-buy); the relaunch orders it onto the wing.
                bot.FinaleBomberActive = false;
                bot.FinaleRefitCheckAt = now;
            }
            added++;
        }
        if (added > 0)
            Console.WriteLine($"[strike] side={side} finale wing grow reserved={reserved + added} target={target} "
                + $"added={added} cap={run.Settings.MaxWing} required={plan.RequiredBombers} (P959)");
    }

    /// <summary>P801 — the volley requirement for this finale target, from the
    /// real granted loadouts and the real station type.
    ///
    /// <para>P896 — the wave also carries the survival extra
    /// (<see cref="ShowcaseFinale.Settings.SurvivalMargin"/>, now default 1) sized
    /// from this side's observed losses on the previous wave and the defenders it
    /// can see in the target sector. P959 — the budget is the WING the side can
    /// field (the whole reserved wing, grown to <c>Settings.MaxWing</c> from spare
    /// fighters), never the hulls that happen to be alive this second, and the
    /// planner divides the ideal hull count by the measured landing share so
    /// "required" predicts a kill instead of an ideal-case scratch.</para></summary>
    private static StrikeVolley.Requirement FinaleVolleyRequirement(FinaleRun run, FinalePlan plan,
        short side, IReadOnlyList<ActiveBot> armed)
    {
        var core = Catalog.RepresentativeCore();
        if (!core.StationTypes.TryGetValue(plan.TargetTypeId, out var stationType))
            return new(ResearchTactics.StrikeGroupSize, ResearchTactics.StrikeGroupSize, 0,
                StrikeVolley.VolleyWindowSeconds, 0, 0, 0, 0, 0, 1);
        var profile = StrikeVolley.StationProfile.From(core, stationType);
        var loadouts = armed.Select(b => StrikeVolley.FromParts(core, ActualHullId(b), b.Parts)).ToArray();
        int combatants = run.Bots.Count(b => b.Side == side && !IsSupport(b) && IsParticipating(b));
        int cap = Math.Clamp(Math.Min(run.Settings.MaxWing, Math.Max(combatants, run.Settings.Bombers)),
            1, StrikeVolley.MaxPlannedBombers);
        // P959 — plan against the CAP the side may grow to, not the hulls alive now
        // (that was the 1-4 bomber "requirement") and not the reserved count alone
        // (a depleted wing could never ask for the hulls it needs).
        int budget = cap;
        plan.TargetMaxShield = stationType.MaxShieldHitPoints;
        plan.TargetMaxArmor = stationType.MaxArmorHitPoints;
        var requirement = StrikeVolley.Compute(profile, loadouts, budget, StrikeVolley.VolleyWindowSeconds,
            run.Settings.SurvivalMargin, plan.WaveLosses, plan.DefenderCount, plan.LandingShare);
        plan.ShieldDamagePerRound = requirement.ShieldDamagePerRound;
        plan.ArmorDamagePerRound = requirement.ArmorDamagePerRound;
        plan.PlannerKill = requirement.KillBombers;
        plan.ShotsNeeded = requirement.ShotsToKill;
        plan.WingCap = cap;
        return requirement;
    }

    /// <summary>P896 — how many live enemy ships this side can see in the target
    /// sector (the biggest count any of its own worlds reports). Fog honesty: an
    /// unseen defender is not counted, so the wave is never sized from knowledge
    /// the side does not have.</summary>
    private static int FinaleDefendersInSector(FinaleRun run, short side, FinalePlan plan)
    {
        int best = 0;
        foreach (var bot in run.Bots)
        {
            if (bot.Side != side || bot.IsPod || bot.Cs is not { } cs) continue;
            int seen = 0;
            lock (cs.Gate)
                foreach (var ship in cs.World.Ships.Values)
                    if (ship.SideId >= 0 && ship.SideId != side && !ship.IsDead && !ship.IsGone
                        && !ship.IsNotVisible && ship.SectorId == plan.Sector
                        && !Catalog.IsLifepodHull(ship.HullId)) seen++;
            best = Math.Max(best, seen);
        }
        return best;
    }

    /// <summary>P801 — per-run coordinator, called once a second from
    /// <see cref="UpdateFinales"/> under the run lock. Computes the requirement,
    /// watches assembly, sets the shared go time, samples station health for the
    /// [strike] outcome and logs one bounded line per stage edge.</summary>
    private void FinaleStrikeMassTick(FinaleRun run, ActiveBot[] bots, double now)
    {
        foreach (var (side, plan) in run.Plans.OrderBy(kv => kv.Key))
        {
            if (!FinaleSideArmed(run, side) || run.Rotated || run.GameOverAt is not null
                || (HasMultipleEnemies && !MultiSideActive(side))) continue;
            // P896 — a plan armed with no target (blind side after the P831 grace) has
            // nothing to strike; ResolveFinaleTargets fills it in once the side sees one.
            if (plan.TargetId == FinalePlan.NoTarget) continue;
            // P959 — close the previous wave before any new mass starts: log what it
            // really delivered (the diagnosis table's per-commit line) and fold the
            // measured landing share into the next requirement.
            CloseFinaleWave(plan, side, now);
            // P959 — the early pressure wave is ONE wave per side: once its window has
            // closed (and the finale has not taken over) the wing returns to ordinary
            // duty, so the finale later re-arms a fresh wing.
            if (plan.EarlyWave && plan.WaveEndLogged && run.TriggeredAt is null)
            {
                EndFinaleEarlyPressure(run, plan, side, now);
                continue;   // this side is back on ordinary duty; no new mass this tick
            }
            // A bomber that already released this life is SPENT (its magazine is
            // empty): it must not be re-counted into another mass until it
            // relaunches (ResetNewLife clears FinaleStrikeSpent). P973 — the wave also
            // counts only hulls that really carry station-damaging hardware: finale
            // DUTY (FinaleBomberActive, P733's fallback) is not the same question as
            // "can this hull hurt the station", and a fallback fighter must never be
            // sized, staged or released as a strike bomber (P959's rule, kept where a
            // real strike loadout exists on the same side).
            var armed = run.Bots.Where(b => b.Side == side && b.FinaleBomberActive && b.FinaleGrantVerified
                && !b.IsPod && !b.FinaleStrikeSpent && IsParticipating(b)
                && FinaleCarriesStrikeHardware(b, plan)).ToArray();
            if (plan.KilledAt is not null)
            {
                if (!plan.OutcomeLogged)
                {
                    plan.OutcomeLogged = true;
                    Console.WriteLine($"[strike] side={side} finale end target=station#{plan.TargetId} sector={plan.Sector} "
                        + $"outcome=station-killed hitsInWindow={plan.HitsInWindow} "
                        + $"shield={Fraction(plan.ShieldAtGo)}->{Fraction(plan.ShieldNow)} "
                        + $"hull={Fraction(plan.HullAtGo)}->{Fraction(plan.HullNow)} "
                        + $"required={plan.RequiredBombers} loneWolves={plan.AssignedLoneWolves} "
                        + $"wing={plan.WingCap} landing={plan.LandingShare:F2} "
                        + $"evidence={plan.KilledEvidence ?? "none"}");
                }
                continue;
            }
            if (armed.Length == 0)
            {
                // P959 — a side whose whole reserved wing is refitting for a legal
                // strike loadout (or was armed with hardware that cannot hurt the
                // station) still reports the P896 hold instead of silently doing
                // nothing: it never commits a wave that provably cannot damage.
                var core = Catalog.RepresentativeCore();
                if (core.StationTypes.TryGetValue(plan.TargetTypeId, out var targetType))
                {
                    var profile = StrikeVolley.StationProfile.From(core, targetType);
                    var wing = run.Bots.Where(b => b.Side == side && b.FinaleBomberReserved && !b.IsPod
                        && IsParticipating(b)).ToArray();
                    bool damaging = wing.Any(b => StrikeVolley.FromParts(core, ActualHullId(b), b.Parts) is { } l
                        && StrikeVolley.DamagesStation(profile, l));
                    if (wing.Length > 0 && !damaging && now - plan.LastNoStrikeLogAt >= FinaleStagingLogSeconds)
                    {
                        plan.LastNoStrikeLogAt = now;
                        Console.WriteLine($"[strike] side={side} finale holding: none of {wing.Length} wing bomber(s) "
                            + $"carries a station-damaging loadout for station#{plan.TargetId} (P896) — not committing");
                    }
                }
            }
            else if (plan.GoTime is null || plan.WaveEndLogged)
            {
                plan.AssignedLoneWolves = LiveLoneWolves(run, side);
                var threats = ObserveFinaleThreats(run, side, plan, now);
                plan.SectorDefenders = threats.Sector;
                plan.EngagingDefenders = threats.Engaging;
                plan.DefenderCount = run.Settings.SurvivalMargin <= 0 ? 0 : run.Settings.ReachableStrike
                    ? threats.Engaging : FinaleDefendersInSector(run, side, plan);
                var requirement = FinaleVolleyRequirement(run, plan, side, armed);
                // P959 — the requirement is the wing's, not the live body count's. A
                // depleted roster no longer shrinks it to "whatever relaunched" (rig:
                // required 2-4; 123 of 126 commits on home/garrison stations had <=4).
                plan.RequiredBombers = requirement.BombersRequired;
                plan.SurvivalBombers = requirement.SurvivalBombers;

                // P896 — never commit a wave that provably cannot damage the target. The
                // P801 planner already drops loadouts whose real damage matrix does not
                // break BOTH pools (StrikeVolley.Compute -> Damages, LoadoutCount 0); when
                // every armed bomber is in that state (rig b59: a side whose bomber/
                // anti-base tech gate was not met yet flew its old hull — the NT-1/SC-5
                // finding), committing would only feed defenders free kills and leave the
                // station at hull=1.000/shield=1.000 for the next wave. The deadline is
                // untouched: this side holds its wing and strikes as soon as a real
                // anti-base loadout is granted.
                if (requirement.LoadoutCount == 0)
                {
                    if (now - plan.LastNoStrikeLogAt >= FinaleStagingLogSeconds)
                    {
                        plan.LastNoStrikeLogAt = now;
                        Console.WriteLine($"[strike] side={side} finale holding: none of {armed.Length} armed bomber(s) "
                            + $"carries a station-damaging loadout for station#{plan.TargetId} (P896) — not committing");
                    }
                }
                else
                {
                    // P959 — grow the wing from spare fighters when the corrected
                    // requirement is bigger than the wing the side reserved.
                    GrowFinaleWing(run, plan, side, FinaleWingTarget(plan.RequiredBombers,
                        run.Bots.Count(b => b.Side == side && b.FinaleBomberReserved), plan.WingCap), bots, now);
                }
            }

            if (run.Settings.ReachableStrike && plan.GoTime is null)
            {
                plan.Demand = ReachableFinaleDemand(run, plan, side, armed, now);
                // The reachable subset owns BOTH the commit law and its feedback.
                // Keeping the all-armed planner here logged contradictory kill
                // counts and converted observed pool drops with the wrong round.
                var activePlanner = plan.Demand.Planner;
                plan.PlannerKill = activePlanner.KillBombers;
                plan.ShotsNeeded = activePlanner.ShotsToKill;
                plan.ShieldDamagePerRound = activePlanner.ShieldDamagePerRound;
                plan.ArmorDamagePerRound = activePlanner.ArmorDamagePerRound;
                plan.RequiredBombers = plan.Demand.Required;
                plan.ReachableBombers = plan.Demand.Reachable;
                plan.MinimumUseful = plan.Demand.MinimumUseful;
                plan.SurvivalBombers = plan.Demand.ThreatExtra;
                if (!plan.Demand.ReachableKill && TryRetargetReachableFinale(run, plan, side, armed, now))
                    continue;
            }

            // `assembled` = armed and in the target sector (they are holding at the
            // standoff point). Reading each own world keeps per-side knowledge.
            var assembled = new List<short>();
            foreach (var bot in armed)
            {
                if (bot.Cs is not { } cs) continue;
                if (run.Settings.ReachableStrike && (StrikeVolley.Profile(Catalog.RepresentativeCore(), plan.TargetTypeId) is not { } targetProfile
                    || FinaleGrantedStrikeLoadout(bot) is not { } realLoadout
                    || !StrikeVolley.DamagesStation(targetProfile, realLoadout))) continue;
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
                // Rig 221 batch 23b: the clock started when the first bomber was armed at home, so the
                // transit used up the cap and 25 of 31 finale waves committed one bomber at a time
                // (staging-timeout, assembled=1). The cap now counts from the first arrival at the
                // standoff, and P959 removes the flat cap from the commit decision: only the deadline
                // can force a short wave, and a wave below the requirement never goes alone.
                if (now - plan.LastStagingLogAt >= FinaleStagingLogSeconds)
                {
                    plan.LastStagingLogAt = now;
                    LogFinaleStaging(run, plan, side, armed, assembled.Count, plan.RequiredBombers, now);
                }
                plan.StagingSince = assembled.Count == 0 ? null : plan.StagingSince ?? now;
                if (assembled.Count > plan.AssemblyPeak)
                {
                    plan.AssemblyPeak = assembled.Count;
                    plan.LastAssemblyGrowthAt = now;
                }
                string? commit = run.Settings.ReachableStrike
                    ? FinaleAssembly.CommitReason(assembled.Count, plan.Demand, plan.StagingSince,
                        plan.LastAssemblyGrowthAt, now, ReachableFinaleDeadline(run, plan), run.Settings.PlateauSeconds)
                    : FinaleCommitReason(assembled.Count, plan.RequiredBombers, plan.StagingSince, now, plan.DeadlineAt);
                if (commit is { } reason) CommitFinaleWave(run, plan, side, assembled, now, reason, armed);
            }
            else if (plan.WaveEndLogged && now >= plan.GoTime + FinaleWaveWindowSeconds + FinaleWaveTailSeconds)
            {
                // The window is spent. If un-released bombers remain (a fresh
                // relaunch), form a NEW mass rather than letting them attack alone.
                bool waiting = armed.Any(b => !b.FinaleReleaseLogged && !plan.GoMembers.Contains(b.ShipId));
                if (waiting)
                {
                    plan.GoTime = null;
                    plan.GoMembers.Clear();
                    plan.MassLogged = false;
                    plan.StagingSince = null;
                    plan.LastAssemblyGrowthAt = null;
                    plan.AssemblyPeak = 0;
                    plan.HitsInWindow = 0;
                    plan.HullAtGo = plan.ShieldAtGo = -1;
                    if (HasMultipleEnemies)
                    {
                        // A new wave is a new strategic commitment. Re-resolve on
                        // the next coordinator pass, never while a wave is in flight.
                        plan.TargetId = FinalePlan.NoTarget;
                        plan.ObservedTarget = false;
                    }
                }
            }

            // Sample the target's live shield/hull at 1 Hz while a wave is out; the
            // measured drop is converted into ROUNDS (P959) so the wave-end line can
            // compare delivered rounds with what the magazines could have delivered.
            if (plan.GoTime is { } go && now - plan.LastSampleAt >= 1)
            {
                plan.LastSampleAt = now;
                if (TrySampleStation(run, bots, side, plan, out float hull, out float shield))
                {
                    if (plan.ShieldAtGo < 0) { plan.ShieldAtGo = shield; plan.HullAtGo = hull; }
                    else if (now <= go + FinaleWaveWindowSeconds + FinaleWaveTailSeconds)
                    {
                        float shieldDrop = plan.ShieldNow >= 0 ? plan.ShieldNow - shield : 0;
                        float hullDrop = plan.HullNow >= 0 ? plan.HullNow - hull : 0;
                        if (shieldDrop > .001f || hullDrop > .001f)
                        {
                            plan.HitsInWindow++;
                            plan.WaveRounds += FinaleRoundsOf(shieldDrop, hullDrop,
                                plan.TargetMaxShield, plan.TargetMaxArmor,
                                plan.ShieldDamagePerRound, plan.ArmorDamagePerRound);
                            plan.WaveShieldAfter = shield;
                            plan.WaveHullAfter = hull;
                        }
                    }
                    plan.ShieldNow = shield;
                    plan.HullNow = hull;
                }
            }
        }
    }

    /// <summary>P959 — measured rounds a shield/hull drop represents, from the real
    /// station pools and the planner's real per-round damage. A round that overflows
    /// the shield drops both pools, so the bigger of the two implied counts is used
    /// (never zero for a real hit). Pure so the measurement is unit-testable.</summary>
    internal static double FinaleRoundsOf(float shieldDrop, float hullDrop,
        float maxShield, float maxArmor, float shieldPerRound, float armorPerRound)
    {
        double fromShield = shieldPerRound > 0 && maxShield > 0 ? shieldDrop * maxShield / shieldPerRound : 0;
        double fromHull = armorPerRound > 0 && maxArmor > 0 ? hullDrop * maxArmor / armorPerRound : 0;
        return Math.Max(0, Math.Max(fromShield, fromHull));
    }

    /// <summary>P959 — close the wave that released at <c>plan.GoTime</c>: one
    /// bounded <c>[strike] … finale wave end</c> line with the per-commit diagnosis
    /// (required/committed/released/alive/hits/rounds/station pools/defenders/target
    /// of the planner), then fold the measured landing share into the EWMA used by
    /// the next requirement. Pure bookkeeping: no world or socket reads.</summary>
    private static void CloseFinaleWave(FinalePlan plan, short side, double now)
    {
        if (plan.WaveEndLogged || plan.GoTime is not { } go) return;
        if (now < go + FinaleWaveWindowSeconds + FinaleWaveTailSeconds && plan.KilledAt is null) return;
        if (plan.KilledAt is not null)
        {
            plan.WaveShieldAfter = plan.WaveHullAfter = 0;
            plan.ShieldNow = plan.HullNow = 0;
        }
        plan.WaveEndLogged = true;
        int committed = plan.WaveCommitted;
        int released = plan.WaveReleased;
        // P959 — delivery = rounds the wave really landed against the rounds the kill
        // needs (not against every magazine round it could have fired: a wave that has
        // already killed the station stopped firing, and white-space rounds nobody
        // needed must not look like losses).
        double needed = Math.Max(1, plan.ShotsNeeded);
        double measured = plan.KilledAt is not null ? 1
            : Math.Clamp(plan.WaveRounds / needed, StrikeVolley.LandingFloor, 1);
        double prior = plan.LandingShare;
        plan.LandingShare = plan.WavesMeasured == 0 ? measured : .5 * prior + .5 * measured;
        plan.WavesMeasured++;
        plan.ReleaseLosses = plan.WaveKilledBeforeRelease;
        Console.WriteLine($"[strike] side={side} finale wave end target=station#{plan.TargetId} sector={plan.Sector} "
            + $"required={plan.RequiredBombers} committed={committed} released={released} "
            + $"killedBeforeRelease={Math.Max(0, committed - released)} hits={plan.HitsInWindow} "
            + $"rounds={plan.WaveRounds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"/{plan.WaveExpectedRounds} "
            + $"shield={Fraction(plan.WaveShieldBefore)}->{Fraction(plan.WaveShieldAfter)} "
            + $"hull={Fraction(plan.WaveHullBefore)}->{Fraction(plan.WaveHullAfter)} "
            + $"defenders={plan.DefenderCount} engaging={plan.EngagingDefenders} sectorDefenders={plan.SectorDefenders} "
            + $"releaseLosses={plan.ReleaseLosses} plannerKill={plan.PlannerKill} shotsNeeded={plan.ShotsNeeded} "
            + $"landing={plan.LandingShare.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} "
            + $"wing={plan.WingCap}");
        plan.WaveCommitted = plan.WaveReleased = 0;
        plan.WaveExpectedRounds = 0;
        plan.WaveRounds = 0;
        plan.WaveShieldBefore = plan.WaveShieldAfter = -1;
        plan.WaveHullBefore = plan.WaveHullAfter = -1;
        plan.HitsInWindow = 0;
    }

    /// <summary>P959 — end the one early pressure wave: unreserve the wing, restore
    /// each live member's ordinary role/order while keeping the hardware it really
    /// bought (<paramref name="currentHardware"/>), and clear the strike state so the
    /// finale can re-arm the same bots later.</summary>
    private static void EndFinaleEarlyPressure(FinaleRun run, FinalePlan plan, short side, double now)
    {
        plan.EarlyWave = false;
        run.EarlySides.Remove(side);
        plan.GoTime = null;
        plan.GoMembers.Clear();
        plan.StagingSince = null;
        int released = 0;
        foreach (var bot in run.Bots)
        {
            if (bot.Side != side || (!bot.FinaleBomberReserved && !bot.FinaleEscort) || bot.IsPod) continue;
            RestoreFinaleLoadout(bot, currentHardware: true);
            bot.FinaleBomberReserved = bot.FinaleBomberActive = bot.FinaleEscort = false;
            bot.FinaleLoneWolf = false;
            bot.FinaleReleaseLogged = false;
            bot.FinaleStrikeSpent = false;
            bot.FinaleRefitting = bot.FinaleRefitLaunchPending = false;
            bot.FinalePreparationRefitUsed = false;
            bot.TargetSector = null;
            bot.RoutePath = null;
            bot.RouteIndex = -1;
            released++;
        }
        Console.WriteLine($"[strike] side={side} early pressure wave complete t={now - run.Created:F0}s "
            + $"released={released} wave={plan.WaveCommitted} (P959)");
    }

    /// <summary>Cadence of the bounded <c>[strike] … finale staging</c> line.</summary>
    internal const double FinaleStagingLogSeconds = 60;

    /// <summary>P801 follow-up telemetry (rig 221 batch 25: one finale commit in a whole finale, side 1
    /// never assembled two bombers) — where the side's reserved finale bombers are while a wave forms:
    /// pods, docked/refitting, armed but not yet in the target sector (by sector), in the sector, at the
    /// standoff. One line per side per minute.</summary>
    private static void LogFinaleStaging(FinaleRun run, FinalePlan plan, short side, ActiveBot[] armed,
        int assembled, int required, double now)
    {
        int reserved = 0, pods = 0, docked = 0, unarmed = 0, inSector = 0;
        var elsewhere = new SortedDictionary<short, int>();
        foreach (var bot in run.Bots)
        {
            if (bot.Side != side || !bot.FinaleBomberReserved) continue;
            reserved++;
            if (bot.IsPod) { pods++; continue; }
            if (!armed.Contains(bot)) { unarmed++; continue; }
            if (bot.Cs is not { } cs) continue;
            short sector; bool atStation;
            lock (cs.Gate)
            {
                if (!cs.World.Ships.TryGetValue(bot.ShipId, out var own)) continue;
                sector = own.SectorId; atStation = own.StationId >= 0;
            }
            if (atStation) docked++;
            else if (sector == plan.Sector) inSector++;
            else elsewhere[sector] = elsewhere.GetValueOrDefault(sector) + 1;
        }
        int escorts = 0, escortsInSector = 0, misordered = 0;
        foreach (var bot in run.Bots.Where(b => b.Side == side && (b.FinaleBomberReserved || b.FinaleEscort)))
        {
            if (bot.TargetSector != plan.Sector && !bot.IsPod && bot.FinaleBomberActive) misordered++;
            if (!bot.FinaleEscort || bot.IsPod || bot.Cs is not { } cs) continue;
            escorts++;
            lock (cs.Gate)
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var own) && own.StationId < 0
                    && own.SectorId == plan.Sector && !own.IsDead) escortsInSector++;
        }
        string where = elsewhere.Count == 0 ? "-" : string.Join(",", elsewhere.Select(kv => $"{kv.Key}:{kv.Value}"));
        Console.WriteLine($"[strike] side={side} finale staging reserved={reserved} armed={armed.Length} "
            + $"assembled={assembled} required={required} inSector={inSector} docked={docked} pods={pods} "
            + $"unarmed={unarmed} elsewhere={where} reachable={plan.ReachableBombers} minimum={plan.MinimumUseful} "
            + $"engaging={plan.EngagingDefenders} sectorDefenders={plan.SectorDefenders} "
            + $"escorts={escortsInSector}/{escorts} misordered={misordered} target=station#{plan.TargetId} sector={plan.Sector} "
            + $"staging={(plan.StagingSince is { } since ? $"{now - since:F0}s" : "-")}");
    }

    /// <summary>P801 follow-up — when a finale wave forming at the standoff commits:
    /// "massed" once the volley is assembled; otherwise only the DEADLINE can force
    /// a short wave, and only while the release window still lets its rounds land
    /// before the match ends. P959 removes P801's flat 180 s "staging-timeout": it
    /// released two bombers against a full-health garrison, which can only feed
    /// defenders and leave the station regenerated for the next wave.</summary>
    internal static string? FinaleCommitReason(int assembled, int required, double? stagingSince, double now,
        double? deadlineAt = null, double window = FinaleWaveWindowSeconds, double lead = ResearchTactics.StrikeGoLeadSeconds)
    {
        if (assembled <= 0 || stagingSince is not { } since) return null;
        if (assembled >= required) return "massed";
        if (deadlineAt is { } deadline && double.IsFinite(deadline)
            && now + lead + window + FinaleWaveTailSeconds >= deadline
            && assembled >= Math.Min(required, FinaleMinimumStrike))
            return "deadline-forced";
        return null;
    }

    /// <summary>P801 follow-up — may a finale bomber use the ordinary cloak policy?
    /// Outside the target sector (transit) yes. In it, the visible element never
    /// cloaks (it draws the defenders) and a lone wolf cloaks only once its wave
    /// is committed, so it holds with full energy.</summary>
    internal static bool FinaleCloakAllowed(bool inTargetSector, bool loneWolf, bool committedMember) =>
        !inTargetSector || (loneWolf && committedMember);

    private void CommitFinaleWave(FinaleRun run, FinalePlan plan, short side, List<short> members,
        double now, string reason, ActiveBot[] armed)
    {
        plan.GoTime = now + ResearchTactics.StrikeGoLeadSeconds;
        plan.GoMembers.Clear();
        foreach (short id in members) plan.GoMembers.Add(id);
        plan.HullAtGo = plan.ShieldAtGo = -1;
        // P959 — per-wave accounting start: what this wave committed, what its
        // magazines could deliver inside one window (the landing-share denominator),
        // and the station state at the go time.
        plan.WaveCommitted = members.Count;
        plan.WaveKilledBeforeRelease = 0;
        plan.WaveReleased = 0;
        plan.WaveRounds = 0;
        plan.WaveExpectedRounds = 0;
        plan.WaveEndLogged = false;
        var core = Catalog.RepresentativeCore();
        var profile = StrikeVolley.Profile(core, plan.TargetTypeId);
        foreach (var bot in armed)
            if (plan.GoMembers.Contains(bot.ShipId) && profile is { } stationProfile
                && (run.Settings.ReachableStrike ? FinaleGrantedStrikeLoadout(bot)
                    : StrikeVolley.FromParts(core, ActualHullId(bot), bot.Parts)) is { } loadout)
                plan.WaveExpectedRounds += StrikeVolley.RoundsInWindow(loadout, stationProfile);
        foreach (var bot in armed)
            if (plan.GoMembers.Contains(bot.ShipId)) { bot.FinaleReleaseLogged = false; bot.FinaleReleaseAt = null; }
        if (TrySampleStation(run, armed, side, plan, out float hull, out float shield))
        {
            plan.HullAtGo = plan.HullNow = hull;
            plan.ShieldAtGo = plan.ShieldNow = shield;
            plan.WaveShieldBefore = shield;
            plan.WaveHullBefore = hull;
        }
        int lastWaveLosses = plan.WaveLosses;
        plan.WaveLosses = 0;   // P896 — the next wave measures the losses of THIS one
        if (!plan.MassLogged)
        {
            plan.MassLogged = true;
            int wolves = armed.Count(b => b.FinaleLoneWolf && plan.GoMembers.Contains(b.ShipId));
            Console.WriteLine($"[strike] side={side} finale commit required={plan.RequiredBombers} "
                + $"assembled={members.Count} visible={members.Count - wolves} "
                + $"loneWolves={wolves} target=station#{plan.TargetId} sector={plan.Sector} "
                + $"goIn={ResearchTactics.StrikeGoLeadSeconds:F0}s wait={now - (plan.StagingSince ?? now):F1}s "
                + $"stationHull={Fraction(hull)} stationShield={Fraction(shield)} reason={reason} "
                + $"plannerKill={plan.PlannerKill} shotsNeeded={plan.ShotsNeeded} "
                + $"wing={plan.WingCap} landing={plan.LandingShare.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} "
                + $"reachable={plan.ReachableBombers} minimum={plan.MinimumUseful} engaging={plan.EngagingDefenders} "
                + $"sectorDefenders={plan.SectorDefenders} memberIds={string.Join(',', members)} t={now - run.Created:F1} "
                // P896 — only when the survival margin is on, so a default (0) run's
                // lines stay byte-identical to the AIX baseline.
                + (run.Settings.SurvivalMargin > 0
                    ? $" survival={plan.SurvivalBombers}"
                        + $"(margin={run.Settings.SurvivalMargin:F2},losses={(run.Settings.ReachableStrike ? plan.ReleaseLosses : lastWaveLosses)},defenders={plan.DefenderCount})"
                    : ""));
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
        if (run.TriggeredAt is null && run.EarlySides.Count == 0) return false;
        if (run.GameOverAt is not null || !FinaleSideArmed(run, bot.Side)) return false;
        if (!run.Plans.TryGetValue(bot.Side, out var plan) || plan.KilledAt is not null
            || plan.TargetId == FinalePlan.NoTarget) return false;
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
                    lock (run.Sync) run.Plans[bot.Side].WaveReleased++;
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

    /// <summary>P801/P959 — hold position on the radial standoff line, nose ON the
    /// station. The orientation matters: native missile lock only builds while the
    /// target is inside the missile's <c>LockAngle</c> of the nose
    /// (<c>magazineigc.cpp:148-167</c>), and the bot's fire rule needs a full lock
    /// before it spends a round (P558). P801 flew toward the radial hold point —
    /// i.e. nose pointing AWAY from the station — so the measured lock never left
    /// 0.000 (21,058 <c>[missilelock] target 2081 lock 0.000</c> lines in the P896
    /// r-f-m0 log) and no bomber could fire the instant it was released. Facing the
    /// station during the hold makes every wave member locked and ready at go.</summary>
    private void HoldAtFinaleStandoff(ActiveBot bot, ConnectionSession cs, FinalePlan plan, double now)
    {
        Vector3 station;
        lock (cs.Gate)
        {
            if (!cs.World.Stations.TryGetValue(plan.TargetId, out var target)
                || target.Position is not { Length: 3 } p) return;
            station = new Vector3(p[0], p[1], p[2]);
        }
        if (!TryCsPose(cs, bot.ShipId, out var pos, out var orient)) return;
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
        // A real imminent collision still owns the tick (native dodge pre-empts
        // every goal, P489/P553).
        if (TryDodgeGoal(cs, bot, pos, out var dodgeGoal)) { CombatFlyTo(bot, dodgeGoal, 15f, 200f, 0); return; }
        var toStation = station - pos;
        if (toStation.LengthSquared() > 1f)
        {
            var (yaw, pitch, _) = SteerTowardDirection(orient, toStation.Normalize());
            // Hold the ring: drift in when outside it, back out when inside, level
            // otherwise. Throttle is along the nose, which now points at the station.
            float distance = (pos - station).Length();
            float throttle = distance > standoff + 300f ? .6f : distance < standoff - 300f ? -.6f : 0f;
            cs.CommandControls(new ControlData(yaw, pitch, 0f, throttle), CloakApproach(bot, cs, 0));
        }
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
