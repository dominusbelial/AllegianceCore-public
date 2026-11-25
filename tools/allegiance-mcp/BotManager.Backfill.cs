using Allegiance.Core;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.ClientCore.Session;
using System.Diagnostics;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly bool _backfill;
    private readonly string _backfillClientVersion;
    private readonly Dictionary<string, ActiveBot> _backfillFinaleMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly BackfillRetry _probeRetry = new();
    private readonly Dictionary<short, BackfillRetry> _commanderRetries = new();
    private bool _probeRecoveryLogged;
    private static double BackfillNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private sealed class BackfillRetryPendingException : Exception;

    internal void PrepareBackfill(int seats)
    {
        if (!_backfill) throw new InvalidOperationException("Backfill was not enabled at startup");
        // Same existing small-battle preset used by the owned skirmish driver.
        ConfigureMeetingEngagement(seats <= 6);
    }

    private static string? ProbeFailure(ConnectionSession probe)
    {
        if (probe.IsDisposed) return "probe disposed";
        if (probe.Reconnecting) return null; // the owned reconnect window gets first refusal
        if (probe.Lost) return "probe lost";
        if (!probe.Connected) return "probe disconnected";
        if (probe.Ack is not { FValidated: true }) return "probe not validated";
        return null;
    }

    /// <summary>Called by the driver's single tracked background task. A fresh
    /// probe stays private until seating/ready-up succeeds. Publication uses the
    /// behavior/research owner gates; the driver drains admissions before recovery.</summary>
    internal void MaintainBackfillSessions(bool recoverProbe)
    {
        if (_loopCts.IsCancellationRequested) return;
        var old = Probe;
        if (ProbeFailure(old) is { } reason)
        {
            if (!_probeRecoveryLogged)
            {
                Console.WriteLine($"[backfill] probe lost reason={reason}");
                _probeRecoveryLogged = true;
            }
            if (recoverProbe && _probeRetry.Due(BackfillNow))
            try
            {
                using var ownership = new PendingOwnership(DisposePeer);
                var fresh = ownership.Own(new ConnectionSession());
                fresh.World.CountOnlyProjectiles = true;
                fresh.World.ReckonRemotes = false;
                short side = ConnectProbe(fresh);
                _loopCts.Token.ThrowIfCancellationRequested();
                lock (_pauseGate)
                lock (_researchPauseGate)
                lock (_commanders)
                {
                    foreach (short key in _commanders.Where(p => ReferenceEquals(p.Value, old)).Select(p => p.Key).ToArray())
                        _commanders.Remove(key);
                    // Auto-pick may choose a different side after the outage.
                    // The probe still doubles as exactly one commander.
                    if (_commanders.Remove(side, out var replaced)) DisposePeer(replaced);
                    ProbeSide = side;
                    _probeReadyShipId = fresh.World.MyShipId!.Value;
                    _commanders[side] = fresh;
                    _commanderRetries.Remove(side); // this side was successfully seated by the probe
                    Probe = fresh;
                    ownership.Transfer();
                }
                DisposePeer(old);
                _probeRetry.Succeeded();
                _probeRecoveryLogged = false;
                Console.WriteLine($"[backfill] probe restored side={side} ship={fresh.World.MyShipId}");
            }
            catch (OperationCanceledException) when (_loopCts.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                double delay = _probeRetry.Failed(BackfillNow, Random.Shared.NextDouble());
                Console.WriteLine($"[backfill] probe retry in {delay:F1}s: {ex.Message}");
            }
        }
        // A probe's resume clears its world/briefing. Commander maintenance
        // must not depend on that stale briefing or on probe recovery succeeding.
        if (ProbeSide < 0) return;
        foreach (short side in ValidSides())
        {
            if (_loopCts.IsCancellationRequested) return;
            try { EnsureCommander(side); }
            catch (BackfillRetryPendingException) { } // other sides can still recover
        }
    }

    internal (long Generation, bool Ready, short[] Sides, BackfillPilot[] Pilots, string? NotReadyReason) BackfillRoster(string[] patterns)
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
        var probe = Probe;
        lock (probe.Gate)
        {
            var world = probe.World;
            string? reason = ProbeFailure(probe) ?? (probe.Reconnecting ? "probe reconnecting"
                : world.Mission?.Stage == Allegiance.Core.Wire.MissionStageMessage.StageOver ? "stage over"
                : !world.WorldReady || world.MySideId is not >= 0 ? "world not ready" : null);
            var names = world.Ships.Values.Where(s => s.Player is { IsHuman: true })
                .ToDictionary(s => s.Player!.CharacterName, s => s.ShipId, StringComparer.OrdinalIgnoreCase);
            return (world.MissionGeneration, reason is null,
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
                }).ToArray(), reason);
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
