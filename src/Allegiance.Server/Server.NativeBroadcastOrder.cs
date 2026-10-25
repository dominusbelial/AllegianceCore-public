using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;

namespace Allegiance.Server;

public sealed partial class Server
{
    private bool _nativeBroadcastOrder;
    // Snapshot membership, rather than Stage after Update: countdowns and game
    // rotation must neither duplicate a send nor suppress a new mission's burst.
    private readonly HashSet<Mission> _preUpdateShipMissions = new();
    private int _shipBroadcastPhase; // 0 = all, 1 = before Update, 2 = after Update

    internal static bool ResolveNativeBroadcastOrderFlag(bool configured, string? environment)
        => environment?.Trim().ToLowerInvariant() switch
        {
            null or "" => configured,
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new InvalidDataException("ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER must be 0/1 or false/true"),
        };

    private void ResolveNativeBroadcastOrder()
    {
        _nativeBroadcastOrder = ResolveNativeBroadcastOrderFlag(_config.Sim.NativeBroadcastOrder,
            Environment.GetEnvironmentVariable("ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER"));
        _preUpdateShipMissions.Clear();
        _shipBroadcastPhase = 0;
        Console.WriteLine($"[boot] sim.nativeBroadcastOrder = {_nativeBroadcastOrder} (EXPERIMENTAL)");
        TickProfiler.SetLabel($"{TickProfiler.Label}; sim.nativeBroadcastOrder={_nativeBroadcastOrder}");
    }

    private bool IncludeShipBroadcastMission(Mission? mission) => _shipBroadcastPhase switch
    {
        1 => mission is not null && _preUpdateShipMissions.Contains(mission),
        2 => mission is null || !_preUpdateShipMissions.Contains(mission),
        _ => true,
    };

    private void BroadcastBeforeMissionUpdate()
    {
        if (!_nativeBroadcastOrder) return;
        _preUpdateShipMissions.Clear();
        foreach (var mission in _missions)
            if (mission.Stage == MissionStage.Started) _preUpdateShipMissions.Add(mission);
        if (_preUpdateShipMissions.Count == 0) return;
        _shipBroadcastPhase = 1;
        try
        {
            // Design: native MoveShips sends before Update (FedSrv.CPP:4237-4254).
            // Keep identities before statuses/poses, and use the existing encode
            // caches once against the previous completed tick. TickIndex advances
            // later in SimPass; the pose retains that previous tick's timestamp.
            StartBroadcastProjectionMeasurement();
            Step("crossSideExportSweep", SweepCrossSideExports);
            Step("crossSideShipStatusSweep", SweepCrossSideShipStatuses);
            Step("shipDetection", SweepShipDetection);
            Step("broadcastShipStates", () => BroadcastShipStates((uint)TickIndex));
            Step("broadcastShipsUpdate", () => BroadcastShipsUpdate((uint)TickIndex));
        }
        finally
        {
            _shipBroadcastPhase = 0;
            // Native flushes the ship send before Update. Publish this detached
            // phase now instead of retaining its first-burst allocations across
            // collision simulation, then capture subsequent sim events/statics
            // in their own ordered snapshot. The flag-off path is unchanged.
            if (_buildingBroadcastSnapshot is not null)
            {
                CompleteBroadcastSnapshot();
                BeginBroadcastSnapshot();
            }
            // Simulation callbacks may need a NEW emission pose while TickIndex
            // still has the pre-update value. Never reuse that tick's old pose.
            // Already queued packets own detached values (P668).
            _sharedHeavy.Clear();
            _sharedLight.Clear();
            _tierHeavy.Clear();
        }
    }

    private void BroadcastAfterMissionUpdate(Action project)
    {
        if (!_nativeBroadcastOrder) { project(); return; }
        _shipBroadcastPhase = 2;
        try { project(); }
        finally { _shipBroadcastPhase = 0; }
    }

    private readonly Dictionary<Sector, short[]> _inactiveTurretRelays = new();

    /// <summary>
    /// P822 (SIM-0470) — native's <c>rgActiveTurrets</c> as a cluster-wide gather
    /// (FedSrv.CPP:3774-3792), cached per sector for the duration of ONE
    /// ship-update pass so every viewer of that sector relays the same list and
    /// the pending one-shot marks are read once. Cleared at the start and end of
    /// each pass, like <see cref="_inactiveTurretRelays"/>.
    /// </summary>
    private readonly Dictionary<Sector, Ship[]> _activeTurretCandidates = new();

    private short[] CollectInactiveTurretRelay(Sector sector)
    {
        // Design: native FedSrv.CPP:3791-3794,4009-4020. The same stopped
        // gunner IDs go to EVERY cluster player, including the gunner itself;
        // no age gate. Observers and unboarded ships are never turret updates.
        // This cluster-wide list is identical for every viewer. Collect once
        // per projection, including when sector workers run in parallel.
        // P822 — the weapons test is native's *else*-branch test, so it applies
        // only to gunners whose last update kind is NOT active
        // (FedSrv.CPP:3783-3791: `else if ((stateM & (oneWeaponIGC|allWeaponsIGC)) == 0)`).
        // A gunner with a still-pending fresh active update is relayed as ACTIVE
        // even if its weapons state is clear; a gunner whose pending mark was
        // consumed (or whose update aged out) and whose weapons are off is
        // inactive — the full native selection, mutually exclusive by
        // construction.
        lock (_snapshotCaptureGate)
        {
            if (_inactiveTurretRelays.TryGetValue(sector, out var cached)) return cached;
            List<short>? ids = null;
            foreach (var model in sector.Models)
                if (model is Ship { IsNpc: false, Dead: false, StationId: null,
                        ParentShip: { StationId: null }, TurretId: not null } gunner
                    && !_pendingTurretRelays.Contains(gunner)
                    && (gunner.StateM & ShipControlState.WeaponsMask) == 0)
                    (ids ??= new()).Add(gunner.Id);
            var result = ids?.ToArray() ?? [];
            _inactiveTurretRelays.Add(sector, result);
            return result;
        }
    }
}
