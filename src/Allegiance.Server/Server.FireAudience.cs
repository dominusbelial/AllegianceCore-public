using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    // Native relays missile/expendable fire to the sector (FedSrv.CPP:5505,
    // 5781), alongside sector ship updates (FedSrv.CPP:4148-4169). There is no
    // unconditional spotted-on-fire rule: Activate changes signature instead
    // (weaponIGC.h:103-125). The thin client needs the pose dependency ordered
    // ahead of the effect, independent of replaceable HEAVY/LIGHT frames.
    private sealed class EmissionViewer(Ship viewer, Sector sector)
    {
        internal readonly Ship Viewer = viewer;
        internal readonly Sector Sector = sector;
        internal readonly short? Side = viewer.SideId;
        internal readonly Dictionary<short, EmissionPose> Poses = new();
    }

    private readonly record struct EmissionPose(Ship Ship, double At);
    private readonly Dictionary<ConnectionId, EmissionViewer> _emissionViewers = new();
    private readonly Dictionary<Missile, HashSet<ConnectionId>> _missileViewers = new();
    private readonly Dictionary<Probe, HashSet<ConnectionId>> _probeViewers = new();
    private long _fireSent, _fireNeverPosed, _fireInitialPoses, _fireRefreshPoses, _fireHidden;

    internal static bool ShipPoseVisible(Ship viewer, Ship subject, bool admin = false)
        => admin || viewer.SideId == subject.SideId || !subject.IsCloaked
            || viewer.SideId is not { } side || subject.SeenBySide(side);

    /// <summary>P866 — native <c>CFSMission::GetGroupRealSides()</c>
    /// (<c>FedSrv/fsmission.h:361</c>): the mission's "All real teams" group.
    /// Membership is maintained by <c>CFSShip::SetSide</c>
    /// (<c>FedSrv/fsship.cpp:1073-1078</c>): a connection is ADDED when its player
    /// is seated on a side and REMOVED when the player is on
    /// <c>SIDE_TEAMLOBBY</c> (<c>fsship.cpp:1076</c>). So "whole mission" in
    /// native means every seated player of the mission — never a team-lobby
    /// (unseated) connection, and never a connection of another mission.
    /// The port's team-lobby state is <c>Ship.SideId == null</c> (never seated) or
    /// <c>SideTeamLobby</c> (-2, the park <see cref="MoveToTeamLobby"/> uses), so
    /// both are excluded here. Reused by every KILL/MINE broadcast whose native
    /// rule is <c>SendMessages(GetGroupRealSides())</c>.</summary>
    private List<ConnectionId> RealSideAudience(Mission? mission = null)
    {
        var targets = new List<ConnectionId>();
        foreach (var kv in _peerShips)
        {
            short? side = kv.Value.Ship.SideId;
            if (side is null || side == SideTeamLobby) continue;
            if (mission is not null && !ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            targets.Add(kv.Key);
        }
        return targets;
    }

    /// <summary>P866 — the mission that owns <paramref name="sector"/>. A KILL/MINE
    /// notice is about an object whose owner sector never changes (a mine never
    /// moves, a probe is anchored, a missile lives in its launch sector), even when
    /// its LAUNCHER later warps into another game / changes side (P697's ghost
    /// battle). Native derives the group from the mission the object lives in, so
    /// the whole-mission audience must be that mission's real sides — never the
    /// launcher's CURRENT mission.</summary>
    private Mission? MissionOfSector(Sector sector) =>
        _missions.FirstOrDefault(mission => mission.Sectors.Contains(sector));

    // Side IDs are mission-local; persistent side-wide object knowledge must
    // not spill into another game's equally numbered side/sector.
    private bool PeerInMission(ConnectionId peer, Sector sector)
        => _peerShips.TryGetValue(peer, out var live)
            && live.Ship.Mission?.Sectors.Contains(sector) == true;

    private bool HasAdminView(ConnectionId peer)
        => _spectateSector.ContainsKey(peer) && IsSpectatorAdmin(peer);

    private bool EmissionVisible(ConnectionId peer, Ship source, Sector sector)
        => _peerShips.TryGetValue(peer, out var live) && PeerInSector(peer, sector)
            && source.Sector == sector && live.Ship.Mission == source.Mission
            && ShipPoseVisible(live.Ship, source, HasAdminView(peer));

    // Scoped to ONE fan-out: no callback mutates the source between recipients.
    // Pose/status are identical; export identity retains its existing side gate.
    // Native FedSrv.CPP:5505 forwards one fire message to the sector; the reliable
    // pose dependency is the port's existing thin-client adaptation.
    private sealed class EmissionPackets
    {
        internal BroadcastPacket? Status, Pose;
    }

    private int BroadcastShipEmission(Ship source, Sector sector, byte[] packet)
    {
        int sent = 0;
        var shared = TestLegacySurgeProjection ? null : new EmissionPackets();
        foreach (var peer in _peerShips.Keys)
            if (PeerInSector(peer, sector) && SendShipEmission(peer, source, sector, packet, shared)) sent++;
        return sent;
    }

    private void RememberProbeViewer(ConnectionId peer, Probe probe)
    {
        if (!_probeViewers.TryGetValue(probe, out var viewers)) _probeViewers[probe] = viewers = new();
        viewers.Add(peer);
    }

    private int BroadcastProbeEmission(Probe probe, byte[] packet)
    {
        // An armed probe is the actual emitter, independently of its deployer
        // (native probeigc.cpp:500). Never revive a departed/dead parent ship.
        if (!probe.OwnerSector.Models.Contains(probe)) return 0;
        int sent = 0;
        foreach (var peer in _peerShips)
        {
            if (!PeerInSector(peer.Key, probe.OwnerSector) || !PeerInMission(peer.Key, probe.OwnerSector)) continue;
            bool known = _probeViewers.TryGetValue(probe, out var viewers) && viewers.Contains(peer.Key);
            if (!known && !HasAdminView(peer.Key) && peer.Value.Ship.SideId != probe.SideId
                && (peer.Value.Ship.SideId is not { } side || !probe.SeenBySide(side))) continue;
            if (!known)
            {
                var export = ExportProbe.FromProbe(probe, (uint)TickIndex,
                    (probe.Launcher as Ship)?.Id ?? -1, probe.SideId ?? -1);
                SendPeer(peer.Key, PackBroadcast(Message(export)));
                RememberProbeViewer(peer.Key, probe);
                _fireInitialPoses++;
            }
            if (!_probeViewers[probe].Contains(peer.Key)) _fireNeverPosed++;
            _fireSent++;
            SendPeer(peer.Key, packet);
            sent++;
        }
        return sent;
    }

    // Sim-thread only, including calls during inbound dispenser commands and
    // Mission.Update before the identity/pose broadcast suffix. Key zero uses
    // the reliable FIFO; a later coalesced pose cannot replace this dependency.
    private bool SendShipEmission(ConnectionId peer, Ship source, Sector sector, byte[] packet, EmissionPackets? shared = null)
    {
        if (!EmissionVisible(peer, source, sector))
        {
            _fireHidden++;
            return false;
        }
        // A projectile may hit after its launcher died. Preserve the already
        // known source without sending a pose that would revive a dead ship.
        if (source.Fraction <= 0 || source.StationId is not null || source.ParentShip is not null)
        {
            if (!_emissionViewers.TryGetValue(peer, out var prior)
                || !prior.Poses.TryGetValue(source.Id, out var known) || known.Ship != source)
                return false;
        }
        else EnsureEmissionPose(peer, source, sector, shared);
        var state = _emissionViewers[peer];
        // Count deliveries, not unique sim events: one round sent to ten viewers
        // contributes ten. This tests reliable source-pose history at enqueue,
        // never TCP acknowledgement or client rendering.
        if (!state.Poses.TryGetValue(source.Id, out var pose) || pose.Ship != source)
            _fireNeverPosed++;
        _fireSent++;
        SendPeer(peer, packet);
        return true;
    }

    private void EnsureEmissionPose(ConnectionId peer, Ship source, Sector sector, EmissionPackets? shared = null)
    {
        var viewer = _peerShips[peer].Ship;
        if (!_emissionViewers.TryGetValue(peer, out var state)
            || state.Viewer != viewer || state.Sector != sector || state.Side != viewer.SideId)
            _emissionViewers[peer] = state = new(viewer, sector);
        double now = UpdateCadenceNow;
        bool initial = !state.Poses.TryGetValue(source.Id, out var last) || last.Ship != source;
        // Reliable refresh bounds active-source age even with the floor disabled
        // or with HEAVY packets superseded by latest-wins queue coalescing.
        if (!initial && now - last.At < 0.5) return;
        if (initial)
        {
            SendPeer(peer, CrossSideExportPacket(source, viewer.SideId ?? -1));
            var status = shared?.Status ?? PackBroadcast(Message(MakeCrossSideShipStatus(source, sector.Id, spotted: true)));
            if (shared is not null) shared.Status = status;
            SendPeer(peer, status);
            _fireInitialPoses++;
        }
        else _fireRefreshPoses++;
        var pose = shared?.Pose;
        if (pose is null)
        {
            pose = PackBroadcast(Message(new SingleShipUpdate { OidTarget = source.Id,
                Ship = ServerShipUpdate.FromModel(source.Id, source, (uint)TickIndex) }));
            if (shared is not null) shared.Pose = pose;
        }
        SendPeer(peer, pose);
        state.Poses[source.Id] = new(source, now);
    }

    private void PruneEmissionPoses()
    {
        foreach (var pair in _probeViewers)
            if (pair.Value.Count == 0 || !pair.Key.OwnerSector.Models.Contains(pair.Key))
                _probeViewers.Remove(pair.Key);
        foreach (var pair in _missileViewers)
            if (pair.Value.Count == 0 || pair.Key.Launcher is not Ship launcher
                || launcher.Mission is null || !_missions.Contains(launcher.Mission))
                _missileViewers.Remove(pair.Key);
        foreach (var pair in _emissionViewers)
        {
            var state = pair.Value;
            if (!_peerShips.TryGetValue(pair.Key, out var live) || state.Viewer != live.Ship
                || state.Sector != BroadcastSectorFor(pair.Key, live.Sector) || state.Side != live.Ship.SideId)
            {
                _emissionViewers.Remove(pair.Key);
                continue;
            }
            foreach (var entry in state.Poses)
            {
                var ship = entry.Value.Ship;
                if (ship.Sector != state.Sector || ship.Fraction <= 0 || ship.StationId is not null
                    || ship.ParentShip is not null || !ShipPoseVisible(state.Viewer, ship, HasAdminView(pair.Key)))
                    state.Poses.Remove(entry.Key);
            }
        }
    }

    internal string TakeFireAudienceReport()
    {
        string report = $"[fire-audience] sent={_fireSent} neverposed={_fireNeverPosed} initialPoses={_fireInitialPoses} refreshPoses={_fireRefreshPoses} hidden={_fireHidden}";
        _fireSent = _fireNeverPosed = _fireInitialPoses = _fireRefreshPoses = _fireHidden = 0;
        return report;
    }
}
