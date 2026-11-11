using Allegiance.Core;
using Allegiance.Core.Collision;
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
    private long _fireCulled;

    // ─────────────── P993 — broadcast.fireAudienceCull (default OFF) ───────────────

    /// <summary>P993 — <c>broadcast.fireAudienceCull</c>, default OFF. When on, a
    /// <c>PROJECTILE_INSTANCE(12)</c> delivery (ship round or probe round) also
    /// requires the recipient to be able to DRAW the round: its own ship within
    /// the client's own <see cref="ProjectileDrawRadius.WorldUnits"/> (5 900 u)
    /// reach of the spawn point, plus the travel margin of
    /// <see cref="ProjectileDrawRangeKeeps"/>. OFF touches nothing — the audience
    /// test, the pose dependency and every frame stay byte-identical.
    /// <para><b>Native intent.</b> Native never sends a round to anyone: every
    /// client creates its own rounds from the fire bits in the ship update it
    /// receives (<c>Igc/weaponIGC.cpp:142-348</c>), and its local impact debris is
    /// itself gated on the object being rendered (<c>Igc/projectileIGC.cpp:193</c>,
    /// <c>pModel-&gt;GetVisibleF()</c>). The port's <c>12</c> relay has no native
    /// rule to match, so it is made to follow the port renderer's own visibility
    /// instead — the same reasoning P697 used to scope the relay to the shooter's
    /// sector. <c>PROJECTILE_HULL_HIT(213)</c> is a port-only message and a bot
    /// input stream, so its audience is deliberately untouched.</para></summary>
    private bool _broadcastFireAudienceCull;

    /// <summary>P993 — resolve <c>broadcast.fireAudienceCull</c> from config +
    /// <c>ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL</c> (env wins, strict grammar,
    /// the same shape as the other <c>broadcast.*</c> switches) and print the
    /// active value so a rig A/B is always labelled. Default OFF.</summary>
    private void ResolveFireAudienceCull()
    {
        _broadcastFireAudienceCull = ResolveFireAudienceCullFlag(
            _config.Broadcast?.FireAudienceCull ?? false,
            Environment.GetEnvironmentVariable("ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL"));
        Console.WriteLine(_broadcastFireAudienceCull
            ? $"[boot] broadcast.fireAudienceCull = true — PROJECTILE_INSTANCE(12) is relayed only to viewers whose own ship is within the client's {ProjectileDrawRadius.WorldUnits:0} u projectile draw radius of the round plus its travel margin; the shooter's own peer and admin free-camera views are always kept; PROJECTILE_HULL_HIT(213)'s audience is unchanged"
            : "[boot] broadcast.fireAudienceCull = false — PROJECTILE_INSTANCE(12) is relayed to the whole sector audience (today's wire)");
    }

    internal static bool ResolveFireAudienceCullFlag(bool configured, string? environment)
        => environment?.Trim().ToLowerInvariant() switch
        {
            null or "" => configured,
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new InvalidDataException("ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL must be 0/1 or false/true"),
        };

    /// <summary>P993 — what a <c>PROJECTILE_INSTANCE(12)</c> delivery needs from the
    /// round to decide the cull: where it spawned, how fast it travels and how long
    /// it lives. <c>Projectile.Velocity</c> is constant for the round's whole life
    /// (<c>Projectile.OnUpdate</c> only integrates it), and
    /// <c>Projectile.ProjectileType.Lifespan</c> is an UPPER bound on the life
    /// because the only override SHORTENS it (<c>Weapon.cs</c> clamps the blast
    /// intercept's <c>shotLifespan</c> to <c>&lt;= lifespan</c>,
    /// <c>Igc/weaponIGC.cpp:273-288</c>). Deliberately not the remaining lifetime:
    /// an upper bound keeps the rule conservative and needs no sim clock.</summary>
    internal readonly record struct EmissionCullOrigin(Vector3 Spawn, float Speed, float Lifespan)
    {
        internal static EmissionCullOrigin FromProjectile(Projectile projectile)
            => new(projectile.Position, projectile.Velocity.Length(), projectile.ProjectileType.Lifespan);
    }

    /// <summary>P993 — the draw-radius rule itself, pure, and shared by the relay
    /// and the tests. Keep the delivery iff the round's spawn point is within
    /// <c>R + (|v_round| + speedLimit_viewer) · lifespan</c> of the viewer's own
    /// ship, with <c>R = </c> <see cref="ProjectileDrawRadius.WorldUnits"/>.
    /// <para><b>Why that margin is exactly right (proof).</b> The renderer draws a
    /// round at local time <c>t</c> iff <c>|round(t) - viewer(t)| &lt;= R</c>
    /// (<c>Game1.Rendering.cs</c> bolt loop, <c>ClientWorld.DiagnoseDraw</c>). Over
    /// the round's life <c>t</c> in <c>[0, lifespan]</c>:</para>
    /// <list type="bullet">
    /// <item><c>|round(t) - spawn| = |v_round| * t</c> — the velocity is constant,
    /// so the path is the straight segment of length
    /// <c>|v_round| * lifespan</c>;</item>
    /// <item><c>|viewer(t) - viewer(0)| &lt;= speedLimit * t</c> — the viewer's own
    /// speed is bounded by <see cref="Ship.MovementSpeedLimit"/> (max speed incl.
    /// the afterburner thrust ratio, +10 % integration/wire margin, +1; P655, the
    /// same bound the movement envelope enforces).</item>
    /// </list>
    /// <para>So <c>|round(t) - viewer(t)| &gt;= |spawn - viewer(0)| - (|v_round| +
    /// speedLimit) * t</c>; if the round is ever DRAWN (<c>&lt;= R</c>) then
    /// <c>|spawn - viewer(0)| &lt;= R + (|v_round| + speedLimit) * lifespan</c>. The
    /// test is therefore a NECESSARY condition for a drawn round: no round the
    /// client would draw is ever culled. It is not sufficient (a round flying away
    /// is kept), which is the safe direction, and the measured fixtures show what
    /// that overshoot costs.</para>
    /// <para><b>Why this rule and not the closest approach.</b> Both are correct
    /// under constant velocities, but the closest approach (project the relative
    /// velocity onto the spawn offset, clamp <c>t</c> to the lifespan) needs a
    /// division and two branches per (shot, recipient) pair, while this form is one
    /// squared-distance compare against a limit computed once per shot plus a
    /// multiply per recipient: no square roots, no division, and the overshoot is
    /// only the round's own travel (the same quantity the brief's "max projectile
    /// speed x lifespan" margin names), never the whole 5 900 u radius.</para></summary>
    internal static bool ProjectileDrawRangeKeeps(
        Vector3 viewerPosition, float viewerSpeedLimit, in EmissionCullOrigin origin)
    {
        float limit = ProjectileDrawRadius.WorldUnits + (origin.Speed + viewerSpeedLimit) * origin.Lifespan;
        return (origin.Spawn - viewerPosition).LengthSquared() <= limit * limit;
    }

    /// <summary>P993 — the per-recipient decision for one round. The recipient
    /// kinds that are NEVER culled:
    /// <list type="bullet">
    /// <item><b>The shooter's own peer</b> — its own copy is what the client's
    /// own-shot echo de-dups against (P462) and what the MCP/bot own-round counter
    /// reads (<c>ClientWorld.TakeOwnProjectileCount</c>); native's shooter always
    /// sees its own round too. (It is inside the margin by construction, so this
    /// is belt-and-braces — and it keeps the own-echo contract obvious.)</item>
    /// <item><b>Admin free-camera spectators</b> (<see cref="HasAdminView"/>) — the
    /// client anchors the bolt test on its own ship pose, but an admin's free
    /// camera is a debug view whose point is seeing a sector unfiltered (P612),
    /// and a distance test against the admin's own ship would silently blank the
    /// sector being watched.</item>
    /// </list>
    /// Every other recipient kind is decided by the same draw-range rule against
    /// its own ship position, because that is the anchor the client uses whatever
    /// its state: a FLYING ship, a DOCKED one (station view — the renderer's
    /// <c>anchorPos = meUp.Position</c> is not gated on dock state), a BOARDED
    /// gunner or OBSERVER child (<c>ParentShip != null</c>, whose pose follows the
    /// parent) and a LIFEPOD (a flying ship like any other) are the same case: the
    /// renderer draws rounds around that pose, so a round beyond its reach cannot
    /// produce a pixel.</summary>
    private bool FireAudienceCullKeeps(ConnectionId peer, Ship viewer, Ship? source, in EmissionCullOrigin origin)
        => FireAudienceCullKeeps(
            cull: _broadcastFireAudienceCull,
            shooterOwnPeer: source is not null && ReferenceEquals(viewer, source),
            adminView: HasAdminView(peer),
            viewerPosition: viewer.Position,
            viewerSpeedLimit: viewer.MovementSpeedLimit(),
            origin);

    /// <summary>P993 — the decision without any server state, so the tests pin the
    /// whole rule (including the two never-cull recipient kinds) as a pure
    /// function. See <see cref="FireAudienceCullKeeps(ConnectionId, Ship, Ship?, in EmissionCullOrigin)"/>
    /// for the per-recipient-kind table this implements.</summary>
    internal static bool FireAudienceCullKeeps(bool cull, bool shooterOwnPeer, bool adminView,
        Vector3 viewerPosition, float viewerSpeedLimit, in EmissionCullOrigin origin)
        => !cull || shooterOwnPeer || adminView
            || ProjectileDrawRangeKeeps(viewerPosition, viewerSpeedLimit, origin);

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

    private int BroadcastShipEmission(Ship source, Sector sector, byte[] packet, Projectile? projectile = null)
    {
        int sent = 0;
        var shared = TestLegacySurgeProjection ? null : new EmissionPackets();
        // P993 — ONE cull origin per shot (never per recipient): the spawn point,
        // the round's constant speed and its lifespan bound. A null projectile is
        // every other caller (213, mine/missile exports, deployments), which keeps
        // today's audience exactly; with the flag off nothing is even computed.
        bool cull = projectile is not null && _broadcastFireAudienceCull;
        var origin = cull ? EmissionCullOrigin.FromProjectile(projectile!) : default;
        foreach (var peer in _peerShips.Keys)
            if (PeerInSector(peer, sector) && SendShipEmission(peer, source, sector, packet, shared, cull, origin)) sent++;
        return sent;
    }

    private void RememberProbeViewer(ConnectionId peer, Probe probe)
    {
        if (!_probeViewers.TryGetValue(probe, out var viewers)) _probeViewers[probe] = viewers = new();
        viewers.Add(peer);
    }

    private int BroadcastProbeEmission(Probe probe, byte[] packet, Projectile? projectile = null)
    {
        // An armed probe is the actual emitter, independently of its deployer
        // (native probeigc.cpp:500). Never revive a departed/dead parent ship.
        if (!probe.OwnerSector.Models.Contains(probe)) return 0;
        // P993 — probe ROUNDS are culled exactly like ship rounds (the deployer's
        // own peer is kept like any shooter); a probe HIT (213) passes no
        // projectile and keeps today's audience.
        var origin = projectile is not null && _broadcastFireAudienceCull ? EmissionCullOrigin.FromProjectile(projectile) : default;
        bool cull = projectile is not null && _broadcastFireAudienceCull;
        var launcher = probe.Launcher as Ship;
        int sent = 0;
        foreach (var peer in _peerShips)
        {
            if (!PeerInSector(peer.Key, probe.OwnerSector) || !PeerInMission(peer.Key, probe.OwnerSector)) continue;
            // P993 — decided BEFORE the first-sight export: a viewer that cannot
            // draw the round must not be remembered as a probe viewer either (that
            // memory is what suppresses a later probe export to it).
            if (cull && !FireAudienceCullKeeps(peer.Key, peer.Value.Ship, launcher, origin))
            {
                _fireCulled++;
                continue;
            }
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
    private bool SendShipEmission(ConnectionId peer, Ship source, Sector sector, byte[] packet, EmissionPackets? shared = null,
        bool cull = false, EmissionCullOrigin origin = default)
    {
        if (!EmissionVisible(peer, source, sector))
        {
            _fireHidden++;
            return false;
        }
        // P993 — the audience select for a PROJECTILE_INSTANCE(12) delivery ends
        // here for a viewer that cannot draw the round, BEFORE EnsureEmissionPose:
        // a culled (viewer, shooter) pair must not drag in the first-sight
        // EXPORT(26)/SHIP_STATUS(90) or the 0.5 s SINGLE_SHIP_UPDATE(78) refresh.
        if (cull && !FireAudienceCullKeeps(peer, _peerShips[peer].Ship, source, origin))
        {
            _fireCulled++;
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
        string report = $"[fire-audience] sent={_fireSent} neverposed={_fireNeverPosed} initialPoses={_fireInitialPoses} refreshPoses={_fireRefreshPoses} hidden={_fireHidden} culled={_fireCulled}";
        _fireSent = _fireNeverPosed = _fireInitialPoses = _fireRefreshPoses = _fireHidden = _fireCulled = 0;
        return report;
    }
}
