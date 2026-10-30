using Allegiance.Core;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    internal readonly record struct TargetAction(string Id, string Family, StepDirection Direction);
    internal static readonly TargetAction[] RemainderTargetActions = new[]
    {
        "Target", "TargetEnemy", "TargetFriendly", "TargetEnemyBase", "TargetFriendlyBase",
        "TargetAlliedBase", "TargetWarp", "TargetFlag", "TargetArtifact",
    }.SelectMany(f => new[] { new TargetAction(f, f, StepDirection.Next),
        new TargetAction(f + "Nearest", f, StepDirection.Nearest),
        new TargetAction(f + "Prev", f, StepDirection.Previous) }).ToArray();

    // FindTarget common.cpp:505-510,566-614: nearest uses distance; cycling
    // walks the model list, and falls back to nearest if the current isn't in it.
    internal static AutopilotTarget? ChooseTarget(IReadOnlyList<CandidateObject> candidates,
        SimVec position, AutopilotTarget? current, StepDirection direction)
    {
        if (candidates.Count == 0) return null;
        int index = current is { } c ? candidates.ToList().FindIndex(x => x.Kind == c.Kind && x.Id == c.Id) : -1;
        CandidateObject selected;
        if (direction == StepDirection.Nearest || index < 0)
            selected = candidates.MinBy(x => (x.Position - position).LengthSquared());
        else selected = candidates[(index + (direction == StepDirection.Previous ? candidates.Count - 1 : 1)) % candidates.Count];
        return new AutopilotTarget(selected.Kind, selected.Id, selected.Name);
    }

    // WinTrek.cpp:10335-10357; common.cpp:266-277,337-340;
    // treasureIGC.cpp:128-149: flag payload -1 is a neutral artifact.
    internal static List<CandidateObject> GatherTreasureTargets(IReadOnlyDictionary<short, TreasureEntity> treasures,
        short sector, short? side, bool artifact, Func<short, short, bool>? allied = null) => treasures.Values
        .Where(t => t.ClusterId == sector && t.Code == TreasureCode.Flag
            && (artifact ? t.PayloadId < 0 : t.PayloadId >= 0 && side is { } own && t.PayloadId != own
                && !(allied?.Invoke(own, t.PayloadId) ?? false)))
        .Select(t => new CandidateObject(t.Position, TargetKind.Treasure, t.TreasureId,
            artifact ? $"artifact {t.TreasureId}" : $"flag side {t.PayloadId}", t.PayloadId < 0 ? null : t.PayloadId)).ToList();

    internal static bool BaseRelationMatches(ClientWorld world, short ownSide, short? side, string family, bool lifepod)
    {
        if (side is not { } other) return false;
        bool ally = world.AreAllied(ownSide, other);
        // WinTrek.cpp:8987-9023: without allies, AlliedBase falls back to FriendlyBase.
        return family == "TargetEnemyBase" ? other != ownSide && !ally
            : family == "TargetAlliedBase" && world.HasAllies(ownSide) ? ally
            : other == ownSide || (lifepod && ally);
    }

    // common.cpp:650-743: exhaust the local cluster first, then search known
    // warp destinations breadth-first. Remote nearest is measured from the
    // arrival aleph, never from unrelated coordinates in the starting sector.
    internal static AutopilotTarget? ChooseAcrossClusters(IReadOnlyList<CandidateObject> candidates,
        Func<CandidateObject, short> sectorOf, IReadOnlyDictionary<short, DataWarpIGC> warps,
        short sector, SimVec position, AutopilotTarget? current, StepDirection direction)
    {
        var local = ChooseTarget(candidates.Where(c => sectorOf(c) == sector).ToList(), position, current, direction);
        if (local is not null) return local;
        var visited = new HashSet<short> { sector };
        List<DataWarpIGC> Destinations(short from) => warps.Values
            .Where(w => w.ClusterId == from && warps.ContainsKey(w.DestinationId))
            .Select(w => warps[w.DestinationId]).ToList();
        var frontier = Destinations(sector);
        var next = new List<DataWarpIGC>();
        while (frontier.Count > 0)
        {
            int index = direction == StepDirection.Previous ? frontier.Count - 1 : 0;
            var arrival = frontier[index];
            frontier.RemoveAt(index);
            if (visited.Add(arrival.ClusterId))
            {
                var remote = ChooseTarget(candidates.Where(c => sectorOf(c) == arrival.ClusterId).ToList(),
                    Vec(arrival.Position), null, StepDirection.Nearest);
                if (remote is not null) return remote;
                next.AddRange(Destinations(arrival.ClusterId));
            }
            if (frontier.Count == 0) { frontier = next; next = new(); }
        }
        return null;
    }

    private short CandidateSector(CandidateObject candidate) => candidate.Kind == TargetKind.Probe
        ? _session.World.Probes[candidate.Id].ClusterId : _session.World.Stations[candidate.Id].ClusterId;

    internal static bool ShipRelationMatches(ClientWorld world, short side, CandidateObject candidate, string family)
    {
        if (candidate.Kind != TargetKind.Ship || candidate.SideId is not { } other) return false;
        bool friendly = other == side || world.AreAllied(side, other);
        return family == "TargetFriendly" ? friendly : !friendly &&
            (candidate.RoleBits & (DataHullTypeIGC.HabmRescue | DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder)) != 0;
    }

    private void SelectRemainderTarget(string family, StepDirection direction)
    {
        if (!TryOwnPose(out var position, out _, out _)) return;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own) || world.MySideId is not { } side) return;
            short sector = RenderSector(own.SectorId);
            // Source hull is the carrier when boarded (WinTrek.cpp:10215).
            var source = own.ParentId >= 0 && world.Ships.TryGetValue(own.ParentId, out var parent) ? parent : own;
            short hull = ShipRoleBits(source.HullId);
            bool pod = (hull & DataHullTypeIGC.HabmLifepod) != 0;
            List<CandidateObject> candidates;
            if (family is "TargetFlag" or "TargetArtifact")
                candidates = GatherTreasureTargets(world.Treasures, sector, side, family == "TargetArtifact", world.AreAllied);
            else if (family.EndsWith("Base", StringComparison.Ordinal))
            {
                var spec = family == "TargetEnemyBase" ? EnemyBaseSpec() : FriendlyBaseSpec(hull);
                candidates = GatherBaseCandidates(world.Stations, spec.IncludeProbes ? world.Probes : null, StationRoleBits, ProbeRoleBits)
                    .Where(x => (x.RoleBits & spec.RoleBitmask) != 0 &&
                        (x.Kind == TargetKind.Probe && (x.RoleBits & DataStationTypeIGC.SabmRescueAny) != 0
                        || BaseRelationMatches(world, side, x.SideId, family, pod))).ToList();
            }
            else
            {
                candidates = GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids, me, sector, ShipRoleBits, StationRoleBits);
                AnchorCommandCandidates(candidates);
                if (family == "TargetWarp") candidates = candidates.Where(x => x.Kind == TargetKind.Warp).ToList();
                else if (family is "TargetEnemy" or "TargetFriendly")
                    candidates = candidates.Where(x => ShipRelationMatches(world, side, x, family)).ToList();
                else
                {
                    // GetSimilarTargetMask WinTrek.cpp:267-320: preserve current kind
                    // and relation; default to enemy ship/station when no current.
                    if (_autoTarget is { } current && !(current.Kind == TargetKind.Ship && current.Id == me))
                    {
                        candidates.AddRange(world.Treasures.Values.Where(t => t.ClusterId == sector)
                            .Select(t => new CandidateObject(t.Position, TargetKind.Treasure, t.TreasureId, $"treasure {t.TreasureId}")));
                        candidates.AddRange(world.Probes.Values.Where(p => p.ClusterId == sector)
                            .Select(p => new CandidateObject(p.Position, TargetKind.Probe, p.ProbeId, $"probe {p.ProbeId}", p.SideId)));
                        var selected = candidates.FirstOrDefault(x => x.Kind == current.Kind && x.Id == current.Id);
                        bool IsFriendly(short? owner) => owner is { } other && (other == side || world.AreAllied(side, other));
                        candidates = candidates.Where(x => x.Kind == current.Kind &&
                            (x.Kind is not (TargetKind.Ship or TargetKind.Station or TargetKind.Probe) || IsFriendly(x.SideId) == IsFriendly(selected.SideId))).ToList();
                    }
                    else candidates = candidates.Where(x => x.Kind is TargetKind.Ship or TargetKind.Station
                        && x.SideId is { } other && other != side && !world.AreAllied(side, other)).ToList();
                }
            }
            _autoTarget = family.EndsWith("Base", StringComparison.Ordinal)
                ? ChooseAcrossClusters(candidates, CandidateSector, world.Warps, sector, position, _autoTarget, direction)
                : ChooseTarget(candidates, position, _autoTarget, direction);
            // P886b — native target keys SetTarget(m, c_cidDefault) (WinTrek.cpp:10149,
            // 9024 → SetCommand(c_cmdCurrent, ...) :8833), so selecting a target with a
            // target-family key writes the server c_cmdCurrent / Ship.CommandTarget — the
            // slot the training GetTargetCondition and the lead predictor read. The port
            // previously only updated the client reticle here (a P886b real-window finding:
            // M3 goal 12 "Press the B key to target the outpost" never advanced because B
            // never reached the server CommandTarget). Commit exactly like a player pick.
            if (_autoTarget is { } sel) CommitTargetForPlayer(sel);
            _targetNoMatchNoun = _autoTarget is null ? family : null;
            _targetNoMatchUntil = _nowSeconds + TargetNoMatchDisplaySeconds;
            Console.WriteLine($"[p642] {family}/{direction} -> {_autoTarget?.Name ?? "none"}");
        }
        RefreshWindowTitle();
    }

    private void HandleRemainderTargetKeys(KeyboardState keys, bool composing)
    {
        foreach (var action in RemainderTargetActions)
            if (RemainderTargetEdgeDown(keys, action.Id) && !composing) SelectRemainderTarget(action.Family, action.Direction);
        if (TargetEdgeDown(keys, "TargetSelf") && !composing) SelectSelfTarget();
        if (TargetEdgeDown(keys, "OccupyNextTurret") && !composing)
        {
            lock (_session.Gate)
                if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var own)
                    && NextTurretParent(own.ParentId) is { } parent) _session.SendBoardShip(parent);
        }
    }
    // WinTrek.cpp:10452-10464: re-board current parent; server picks the seat.
    internal static short? NextTurretParent(short parent) => parent >= 0 ? parent : null;

    /// <summary>Training-aware press edge for a remainder-target action. Outside training this is
    /// exactly the current binding (<see cref="TargetEdgeDown"/>). While a training mission runs it
    /// ALSO honours the action's NATIVE-default key — the key the native prompt names
    /// (defaultinputmap.mdl) — mirroring how <see cref="TrainingInputGate"/> already routes the
    /// mission's key press (P886). This is the P892b B-key fix: the port's shipped default map leaves
    /// <c>TargetEnemy</c>/<c>TargetFriendlyBase</c> unbound (F/B are rebound to ToggleCloak/Scoreboard),
    /// so a real-window B/F tap would otherwise never reach the client's
    /// <c>SelectRemainderTarget → CommitTargetForPlayer</c>, and the server <c>Ship.CommandTarget</c>
    /// that M3 goal 12's <c>GetTargetCondition(OT_station,1010)</c> reads would never be set
    /// (WinTrek.cpp:10149/9024; P886c measured zero <c>[p642]</c> edges for F and B).</summary>
    private bool RemainderTargetEdgeDown(KeyboardState keys, string action)
        => _targetPressEdges.Observe(action, RemainderTargetKeyDown(
            Rendering.KeyBindingStore.Instance.Map, keys, action,
            Allegiance.ClientCore.Training.TrainingController.IsTraining));

    /// <summary>Pure, testable predicate for <see cref="RemainderTargetEdgeDown"/>: true when the
    /// action's current binding is down, or — only while a training mission is active — its
    /// native-default key is down (training follows native prompts). Normal flight is untouched:
    /// <paramref name="training"/> must be true for the native-default fallback.</summary>
    internal static bool RemainderTargetKeyDown(Rendering.ActionMap map, KeyboardState keys, string action, bool training)
    {
        if (map.IsDownMostSpecific(keys, action)) return true;
        return training && map.ActionFilter?.Invoke(action) != false
            && TrainingInputGate.NativeDefaultKeyDown(keys, action);
    }

    /// <summary>While a training mission runs, a key the native prompt names means the native action,
    /// so the port's normal-flight action on the same key stands down: B targets the base without
    /// opening the scoreboard over the landing, F targets an enemy without toggling the cloak.</summary>
    internal static bool YieldsToTrainingTarget(KeyboardState keys, string action, bool training)
        => training && action switch
        {
            "Scoreboard" => TrainingInputGate.NativeDefaultKeyDown(keys, "TargetFriendlyBase"),
            "ToggleCloak" => TrainingInputGate.NativeDefaultKeyDown(keys, "TargetEnemy"),
            _ => false,
        };
}
