using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>
    /// P884b — retire every per-WORLD ship-keyed cache of <b>one</b> mission at its
    /// game-over rotation, without touching another mission's state (P883b: a
    /// server can host several worlds).
    /// <para><b>Native intent.</b> Native never rotates an object: it resets the
    /// SAME <c>CfsMission</c> (<c>FedSrv/fsmission.cpp:3490-3527</c>,
    /// <c>m_pMission-&gt;ResetMission()</c> at <c>:3527</c>), and
    /// <c>CmissionIGC::ResetMission</c> moves every ship of the world to the
    /// NULL cluster and terminates every cluster and side bucket
    /// (<c>Igc/missionigc.cpp:3804-3828</c>). Per-ship native state is therefore
    /// gone with the reset — e.g. <c>CFSShip::m_atu</c>
    /// (<c>FedSrv/fsship.h:541</c>) lives on the ship object that the next
    /// generation replaces. The port assembles a fresh <see cref="Mission"/>
    /// instead, so every server cache keyed by a ship id (or holding a
    /// <see cref="Ship"/>) must be dropped here or a retired world's ids/objects
    /// survive the match that created them.</para>
    /// <para><b>Why the complement, not the mission's ship list.</b> A ship that
    /// died earlier in the match is no longer in any registry, so
    /// <see cref="Mission.GetShips"/> cannot name it — but its latch entries are
    /// exactly the ones that leak. The only ids that must be KEPT are the ids of
    /// ships that are still alive in some <b>other</b> mission (plus the peers
    /// still seated in one); every other subject is retired world state. That
    /// also makes the sweep <b>per mission</b>: on a multi-mission server the
    /// other worlds' latches are untouched, unlike a server-wide
    /// <c>Clear()</c>.</para>
    /// </summary>
    private void RetirePerWorldShipState(Mission retired)
    {
        var keep = new HashSet<short>();
        foreach (var mission in _missions)
            if (!ReferenceEquals(mission, retired))
                foreach (var ship in mission.GetShips())
                    keep.Add(ship.Id);
        // A seated peer is the authority for its OWN current ship: during the
        // two-phase rotation the migrant's fresh ship already lives in `fresh`
        // (which is not yet in _missions), and a peer of another mission must
        // never have its latch dropped.
        foreach (var live in _peerShips.Values)
            if (!ReferenceEquals(live.Ship.Mission, retired))
                keep.Add(live.Ship.Id);

        static bool Keep(HashSet<short> keep, short id) => keep.Contains(id);

        // P884's three per-world caches. P884 emptied them server-wide, which is
        // correct on the single-mission server it was measured on but would wipe
        // every other mission's latches here (forcing needless re-EXPORTs /
        // re-STATUSes and losing the first-pose guarantee for their peers).
        _teammatePositionSent.RemoveWhere(e => !Keep(keep, e.ShipId));
        foreach (var k in _crossSideShipStatusSeen.Keys.Where(k => !Keep(keep, k.ShipId)).ToList())
            _crossSideShipStatusSeen.Remove(k);
        foreach (var id in _teammateLoadoutCache.Keys.Where(id => !Keep(keep, id)).ToList())
            _teammateLoadoutCache.Remove(id);

        // HUD-17 own-side Detected baseline, likewise per ship (was server-wide).
        foreach (var id in _shipDetected.Keys.Where(id => !Keep(keep, id)).ToList())
            _shipDetected.Remove(id);
        _shipDetectedChanged.RemoveWhere(id => !Keep(keep, id));

        // P803/P822 — native m_atu (FedSrv/fsship.h:541) is per SHIP: a retired
        // gunner's stored ACTIVE_TURRET_UPDATE must not be relayed (or kept) in
        // the fresh world. The one-shot projection latch holds Ship references.
        foreach (var id in _lastTurretUpdates.Keys.Where(id => !Keep(keep, id)).ToList())
            _lastTurretUpdates.Remove(id);
        _pendingTurretRelays.RemoveWhere(s => !Keep(keep, s.Id));

        // SIM-0662/0667 — the allied-dock pending sets hold Ship references and
        // are drained by the dock callback that armed them, in the same tick
        // (native calls AlliedRearm/AlliedLifepodDocked synchronously inside
        // ChangeStation, FedSrv.CPP:13264-13308). A game over between the arm and
        // the drain must not leave a retired Ship reachable — and through its
        // Ship.Mission, a retired world.
        _alliedRearmPending.RemoveWhere(s => !Keep(keep, s.Id));
        _alliedPodRedirectPending.RemoveWhere(s => !Keep(keep, s.Id));

        // Dropping the entries is not enough: a .NET dictionary/hash-set keeps the
        // backing arrays of the widest moment it ever saw, and a match's latches
        // peak at peers x ships-at-once. Left alone, each match whose peak exceeds
        // the last one re-grows those arrays in doublings and the floor ratchets up
        // every match without ever returning (the rig's shape). Trim them back to
        // the live population at the rotation boundary — the other missions' entries
        // on a multi-mission server are all that survive, and the fresh world
        // re-allocates its own small arrays as it repopulates.
        TrimRetiredLatchCapacity();
    }

    /// <summary>P884b — release the PEAK capacity of every per-world (recipient,
    /// subject) latch after this world's entries have been swept. Called from
    /// <see cref="RetirePerWorldShipState"/> (rotation) once the migration's
    /// <see cref="RunMigrationLatchSweep"/> has removed the retired world's
    /// entries, and again when a rotation finishes its deferred sweep
    /// (<see cref="TrimRetiredLatchCapacity"/> from <see cref="RunMigrationLatchSweep"/>)
    /// so a large migration that spans several ticks is covered too.</summary>
    private void TrimRetiredLatchCapacity()
    {
        // Only reallocate when the peak is meaningfully larger than the live
        // population (a multi-mission server keeps another world's entries): a
        // set that is already near its live size is not worth a fresh array
        // every intermission.
        static bool Worth(int capacity, int count) => capacity > 2 * count + 64;
        void TrimHash<T>(HashSet<T> set)
        {
            int count = set.Count;
            if (Worth(set.EnsureCapacity(0), count)) set.TrimExcess();
        }
        void TrimDict<TKey, TValue>(Dictionary<TKey, TValue> map) where TKey : notnull
        {
            int count = map.Count;
            if (Worth(map.EnsureCapacity(0), count)) map.TrimExcess();
        }
        TrimHash(_teammateExportSent);
        TrimHash(_playerInfoSent);
        TrimHash(_teammatePositionSent);
        TrimHash(_sameSideNpcExportSent);
        TrimHash(_crossSideMineExportSent);
        TrimHash(_buildingEffectExportSent);
        TrimHash(_stationIdentitySent);
        _crossSideDiscoverySent.TrimExcessIfWorthwhile();
        _crossSideExportSent.TrimExcessIfWorthwhile();
        TrimDict(_lastSameSideNpcStatusTick);
        TrimDict(_teammateLoadoutVersionSent);
        TrimDict(_lastTeammateStatusTick);
        TrimDict(_crossSideShipStatusTick);
        TrimDict(_crossSideShipStatusSeen);
        TrimDict(_teammateLoadoutCache);
        TrimDict(_shipDetected);
        TrimDict(_lastTurretUpdates);
    }
}
