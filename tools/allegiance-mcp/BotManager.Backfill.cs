using Allegiance.Core;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly bool _backfill;
    private readonly string _backfillClientVersion;
    private readonly Dictionary<string, ActiveBot> _backfillFinaleMembers = new(StringComparer.OrdinalIgnoreCase);

    internal void PrepareBackfill(int seats)
    {
        if (!_backfill) throw new InvalidOperationException("Backfill was not enabled at startup");
        // Same existing small-battle preset used by the owned skirmish driver.
        ConfigureMeetingEngagement(seats <= 6);
        foreach (short side in ValidSides()) EnsureCommander(side);
    }

    internal (long Generation, bool Ready, short[] Sides, BackfillPilot[] Pilots) BackfillRoster(string[] patterns)
    {
        // Snapshot owned combat clients before the probe gate, matching the
        // behavior loop's lock order. Commander/research peers live only in
        // _commanders and never enter this removal inventory.
        Dictionary<string, (bool Protected, string Role)> owned;
        lock (_gate) owned = _bots.Where(IsParticipating).ToDictionary(b => b.Name, b =>
        {
            var hull = Catalog.RepresentativeCore().HullTypes[b.HullId];
            bool economic = b.Behavior != "combat" || hull.HasCapability(DataHullTypeIGC.HabmMiner)
                || hull.HasCapability(DataHullTypeIGC.HabmBuilder);
            return (economic, b.Role);
        }, StringComparer.OrdinalIgnoreCase);
        lock (Probe.Gate)
        {
            var world = Probe.World;
            var names = world.Ships.Values.Where(s => s.Player is { IsHuman: true })
                .ToDictionary(s => s.Player!.CharacterName, s => s.ShipId, StringComparer.OrdinalIgnoreCase);
            return (world.MissionGeneration, Probe.Connected && !Probe.Lost && !Probe.Reconnecting
                && world.WorldReady && world.MySideId is >= 0 && world.Mission?.Stage != Allegiance.Core.Wire.MissionStageMessage.StageOver,
                world.SideRoster?.Sides.Where(s => s.SideId >= 0).Select(s => s.SideId).ToArray() ?? [],
                world.ScoreRoster.Select(p =>
                {
                    bool ours = owned.TryGetValue(p.CharacterName, out var local);
                    bool leader = names.TryGetValue(p.CharacterName, out short id) && world.TeamLeaderShipIdFor(p.SideId) == id;
                    return new BackfillPilot(p.CharacterName, p.SideId, BotNameRules.IsBot(patterns, p.CharacterName),
                        !ours || local.Protected || leader, ours ? local.Role : "commander/external", ours,
                        // Port-only orchestration: unknown readiness retains the
                        // old count; an explicit PLAYER_READY(false) frees a seat.
                        !names.TryGetValue(p.CharacterName, out short readyId)
                            || !world.Roster.ReadyByShipId.TryGetValue(readyId, out bool ready) || ready);
                }).ToArray());
        }
    }

    internal void ArmBackfillFinale()
    {
        string[] names;
        lock (_pauseGate)
        lock (_gate)
        {
            foreach (var bot in _bots.Where(b => IsParticipating(b) && b.Behavior == "combat"))
                _backfillFinaleMembers[bot.Name] = bot;
            names = _backfillFinaleMembers.Keys.ToArray();
        }
        // Use the existing showcase controller, including pending map discovery
        // and its automatic re-arm across rotations. No timer/strategy is ported.
        ConfigureFinale(new ShowcaseFinale.Settings(), names);
    }

    internal string[] BackfillFinaleCoverage()
    {
        lock (_pauseGate)
        lock (_gate) return _bots.Where(b => IsParticipating(b) &&
            (b.Finale is { } run && (!run.Rotated || b.FinaleResetObserved && b.LifeGeneration > run.Generations.GetValueOrDefault(b))
                || _pendingFinales?.Any(p => p.Names.Contains(b.Name)) == true)).Select(b => b.Name).ToArray();
    }

    private void InheritBackfillFinale(string name)
    {
        // An external seat replacement inherits the vacant combat slot in the
        // existing controller. Preserve its clock and discovery/re-arm state;
        // otherwise repeated human visits could leave an entirely new fleet
        // outside the showcase's small-fleet exploration/finale lifecycle.
        lock (_pauseGate)
        lock (_researchPauseGate)
        lock (_gate)
        {
            if (_backfillFinaleMembers.Count == 0) return; // initial fleet not armed yet
            var fresh = _bots.Single(b => b.Name == name);
            var departed = _backfillFinaleMembers.Values.FirstOrDefault(b => b.Side == fresh.Side && !IsParticipating(b));
            if (departed is null) return;
            if (_pendingFinales is { } pending)
                for (int i = 0; i < pending.Count; i++)
                {
                    var p = pending[i];
                    pending[i] = (p.Settings, p.Names.Select(n => n == departed.Name ? fresh.Name : n).ToArray(), p.PendingAt);
                }
            foreach (var run in _finales ?? [])
                lock (run.Sync)
                {
                    int slot = Array.IndexOf(run.Bots, departed);
                    if (slot < 0) continue;
                    run.Bots[slot] = fresh;
                    run.Generations.Remove(departed);
                    run.InitialKills.Remove(departed);
                    run.RearmBlockedSince.Remove(departed);
                    // A new connection starts its own generation counter even
                    // when joining a later server match. If re-arm is waiting,
                    // this launched fresh seat already belongs to that new world.
                    bool afterRotation = run.Rotated && !run.NextRunCreated;
                    run.Generations[fresh] = fresh.LifeGeneration - (afterRotation ? 1 : 0);
                    run.InitialKills[fresh] = fresh.Kills;
                    fresh.FinaleResetObserved = afterRotation;
                    fresh.Finale = run;
                }
            departed.Finale = null;
            _backfillFinaleMembers.Remove(departed.Name);
            _backfillFinaleMembers[fresh.Name] = fresh;
        }
    }

    internal bool QuitBackfillCombat(string name)
    {
        ActiveBot? bot;
        lock (_gate) bot = _bots.FirstOrDefault(b => b.Name == name && b.Behavior == "combat");
        if (bot?.Cs is not { } peer) return false;
        var hull = Catalog.RepresentativeCore().HullTypes[bot.HullId];
        if (hull.HasCapability(DataHullTypeIGC.HabmMiner) || hull.HasCapability(DataHullTypeIGC.HabmBuilder)) return false;
        lock (Probe.Gate) if (Probe.World.TeamLeaderShipIdFor(bot.Side) == bot.ShipId) return false;
        bot.Inactive = true;
        peer.SendLogoff(); // native voluntary departure bypasses reconnect parking (FedSrv.CPP:5787-5802)
        WaitUntil(() =>
        {
            lock (Probe.Gate) return !Probe.World.ScoreRoster.Any(p => p.CharacterName == name);
        }, 3000);
        RemoveInactiveBot(bot, "backfill yielded combat seat");
        return true;
    }

    internal string SpawnBackfillCombat(short side)
    {
        // Reuse the existing mixed-fleet roles and the live tech/wallet selector;
        // no grants, simulated NPCs or new combat strategy.
        int ordinal;
        lock (_gate) ordinal = _bots.Count(b => IsParticipating(b) && b.Side == side);
        var role = ordinal % 4 == 0 ? Showcase.HullRole.Scout
            : ordinal % 4 == 3 ? Showcase.HullRole.Bomber : Showcase.HullRole.Fighter;
        var core = Catalog.RepresentativeCore();
        var hull = Showcase.RepresentativeHullOfRole(core, role)
            ?? Showcase.RepresentativeHullOfRole(core, Showcase.HullRole.Fighter)
            ?? throw new InvalidOperationException("No player combat hull in the loaded core");
        var parts = Catalog.NativePreferredLoadout(hull.HullId, hull.Name, 0).Parts;
        var spawned = SpawnBot(hull.HullId, hull.Name, side, parts, "combat", role: role.ToString());
        InheritBackfillFinale(spawned.Name);
        return spawned.Name;
    }
}
