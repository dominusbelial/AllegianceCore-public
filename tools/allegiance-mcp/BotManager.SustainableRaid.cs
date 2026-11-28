using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // This is a pilot-private grant, mount state and known route. A side-wide
    // station export is never sufficient to infer that this pilot can navigate.
    private static SustainableRaid.Fit? ObserveRaidFit(ActiveBot bot, ClientWorld world, ShipEntity own)
    {
        if (bot.Behavior != "combat" || own.IsNotVisible || own.Latest is not { } pose
            || world.MissionCore is not { } core || world.ResearchBriefing?.Plan.CoreVersion != core.Version
            || !core.HullTypes.TryGetValue(own.HullId, out var hull)) return null;
        bool reserved = bot.FinaleBomberActive || bot.FinaleBomberReserved || bot.FinaleRefitting
            || bot.IsExplorer || bot.MedevacScout || IsSupport(bot) || bot.ReconTarget is not null
            || bot.DefenseSector is not null || bot.FinaleEscort || bot.Role.Equals("scout", StringComparison.OrdinalIgnoreCase)
            || Showcase.ClassifyHull(hull) == Showcase.HullRole.Scout;
        var loadout = SustainableRaid.Mounted(core, own.HullId, bot.Granted, own.Loadout,
            Math.Max(0, world.OwnMissilesFired - bot.FinaleMissileLaunchBase), pose.AmmoFraction, pose.EnergyFraction, world.GlobalAttributesFor(bot.Side));
        short? home = null;
        var reachable = new Dictionary<short, double>();
        double remaining = world.Mission is { GameLengthSeconds: > 0 } mission && world.MissionElapsedSeconds() is { } elapsed
            ? mission.GameLengthSeconds - elapsed : double.PositiveInfinity;
        foreach (var station in world.Stations.Values)
        {
            if (station.SideId == bot.Side && station.ClusterId == own.SectorId && hull.MaxFixedWeapons > 0
                && core.StationTypes.TryGetValue(station.StationTypeId, out var st) && st.HasCapability(DataStationTypeIGC.SabmRestart))
                home ??= station.StationId;
            if (!reserved && loadout is not null && station.SideId >= 0 && station.SideId != bot.Side
                && !world.AreAllied(bot.Side, station.SideId) && station.Position is { Length: 3 })
            {
                double travel = FinaleTravelSeconds(core, world, own, station.ClusterId, Vec3(station.Position));
                if (double.IsFinite(travel) && travel + StrikeVolley.VolleyWindowSeconds <= remaining)
                    reachable.Add(station.StationId, travel);
            }
        }
        return new(loadout, reachable, home, reserved);
    }

    private static SustainableRaid.MemberState RaidMemberState(ActiveBot bot, ShipEntity own)
    {
        if (own.IsDead || bot.IsPod || Catalog.IsLifepodHull(own.HullId))
            return SustainableRaid.MemberState.Lost;
        if (own.StationId >= 0 || own.ParentId >= 0 || bot.FinaleBomberActive || bot.FinaleBomberReserved || bot.FinaleRefitting)
            return SustainableRaid.MemberState.Withdrawn;
        // The recovery driver sets this on its explicit damaged-hull return.
        // A low health sample or a homeward position alone cannot supply that cause.
        return bot.KillRecovering ? SustainableRaid.MemberState.Lost : SustainableRaid.MemberState.Flying;
    }

    private static SustainableRaid.Observation? ObserveRaid(ClientWorld world, ResearchPlan plan, StaticCore core,
        bool finaleTriggered, Dictionary<short, SustainableRaid.MemberObservation>? members)
    {
        if (!world.ServerClockSeeded) return null;
        var (batch, at) = world.LatestStationObservation;
        return new(at, batch?.Entries.Select(e => e.StationId).ToHashSet() ?? [],
            finaleTriggered || world.FinaleClockArmed, world.StationDestroyer, plan.Name, members,
            t => SustainableRaid.StationProfile(core, t.StationTypeId, world.GlobalAttributesFor(t.Side)));
    }

    private readonly Dictionary<short, (ClientWorld World, Action Callback)> _raidEndCallbacks = [];

    private sealed record RaidOrderExpiry(string Epoch, long Generation, long Acceptance, ResearchTactics.Order Order);

    private void UpdateRaidOrders(ClientWorld world, short side, string epoch, ResearchTactics policy,
        SustainableRaid.Wing? previous, ActiveBot[] bots, ResearchTactics.Order[] orders)
    {
        foreach (var expired in policy.TakeReleasedRaidOrders())
        {
            if (orders.Contains(expired)) continue;
            var bot = Array.Find(bots, b => b.ShipId == expired.Recipient);
            if (bot is not null) Volatile.Write(ref bot.ExpiredRaidOrder,
                new(epoch, bot.LifeGeneration, Volatile.Read(ref bot.OrderAcceptance), expired));
        }
        if (ReferenceEquals(previous, policy.ActiveRaid)) return;
        if (_raidEndCallbacks.Remove(side, out var old)) old.World.GameOverReceived -= old.Callback;
        if (policy.ActiveRaid is { } active)
        {
            // The server may rotate between two research passes. Close on the
            // old commander's own GAME_OVER receipt while its evidence survives.
            void MissionEnded()
            {
                world.GameOverReceived -= MissionEnded;
                if (world.ResearchBriefing?.Epoch != epoch || !ReferenceEquals(policy.ActiveRaid, active)) return;
                var target = active.Station;
                if (world.Stations.TryGetValue(target.Id, out var station)) target = target with { Side = station.SideId };
                var terminal = new SustainableRaid.Observation(world.SimulationTimeSeconds, [], true,
                    world.StationDestroyer, world.ResearchBriefing.Plan.Name);
                policy.Tick(world.SimulationTimeSeconds, false, [], [target], raid: terminal);
                LogRaidEvents(policy, epoch, side);
            }
            world.GameOverReceived += MissionEnded;
            _raidEndCallbacks[side] = (world, MissionEnded);
        }
    }

    private static void LogRaidEvents(ResearchTactics policy, string epoch, short side)
    {
        foreach (var e in policy.TakeRaidEvents())
            Console.WriteLine(FormattableString.Invariant($"[ordinary-raid] epoch={epoch} side={side} target={e.Target} stage={e.Stage} sim={e.At:F2} {e.Detail}"));
    }
}
