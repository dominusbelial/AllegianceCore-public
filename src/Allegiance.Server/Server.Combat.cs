using System.Diagnostics;
using Allegiance.Core.Profiling;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
// Disambiguate the LoadoutChange KIND enum (Sim) from the Allegiance.Core.Wire.LoadoutChange
// codec — both are in scope here; the enum is the natural meaning in the
// gameplay broadcast handler, the wire codec is reached as Allegiance.Core.Wire.LoadoutChange.
using LoadoutChange = Allegiance.Core.Sim.LoadoutChange;

namespace Allegiance.Server;

/// <summary>
/// Ordnance/kill/death/eject broadcast and win-condition members of <see cref="Server"/>
/// (P330 stage-4 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>
/// type, so private field/method access across the split files is unaffected.
/// </summary>
public sealed partial class Server
{

    /// <summary>Relay a real shot to eligible viewers in its sector, ordering the source
    /// identity/pose first. Native simulates guns locally (weaponIGC.cpp:142-318);
    /// the port transports the result to its thin clients.
    /// </summary>
    private void BroadcastProjectileInstance(Projectile p)
    {
        if (p.OriginProbe is Probe origin)
        {
            BroadcastProbeEmission(origin, FedPacket.Pack(ProjectileInstance.FromProjectile(p, (uint)TickIndex).Encode()));
            return;
        }
        if (p.Launcher is not Ship shooter)
        {
            if (ConsoleLog.Verbose) ConsoleLog.Write("[projectile] ProjectileCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            if (ConsoleLog.Verbose) ConsoleLog.Write($"[projectile] shooter {shooter.Id} has no side — no PROJECTILE_INSTANCE broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as BroadcastShipStates stamps ServerShipUpdate.Time).
        uint timeCreate = (uint)TickIndex;

        var msg = ProjectileInstance.FromProjectile(p, timeCreate);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = BroadcastShipEmission(shooter, p.OwnerSector, packet);
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[projectile] ship {shooter.Id} fired -> PROJ_INST(12) type {p.ProjectileType.ProjectileTypeId} " +
                                                  $"pos {p.Position} vel {p.Velocity} fwd {p.Forward} life {p.ProjectileType.Lifespan} -> {n} peer(s) (same-side + cross-side)");
    }

    /// <summary>2026-10-15 — fire and hit relays are scoped to the shooter's sector.
    /// Native creates gun rounds in the local simulation (weaponIGC.cpp:290-303).
    /// PROJECTILE_INSTANCE is declared CS (Inc/Messages.h:131), but FedSrv has no
    /// corresponding relay; each native client simulates its own cluster. The port's relay used side-wide audiences (every
    /// teammate holding the shooter's export; every connection of a side with any
    /// peer in the sector), so a peer in another sector received the shots too and the
    /// distance-only client draw put them at the same coordinates in the viewer's
    /// sector — the "ghost battles" seen in every sector (Domingo, clash51).</summary>
    private bool PeerInSector(ConnectionId conn, Sector sector)
        => _peerShips.TryGetValue(conn, out var ps) && BroadcastSectorFor(conn, ps.Sector) == sector;

    /// <summary>
    /// P762 — relay a non-projectile <c>AddHullHit</c> (the drilling builder's
    /// contact debris, <c>shipIGC.cpp:888-890</c>) through the same
    /// <see cref="ProjectileHullHit"/> wire message P364 uses, with source id
    /// -1 because native's call has no launcher. The target supplies the
    /// cluster/radius/id the client sizes and places the spark with; the
    /// audience is the builder's side and sector (the peers who can see the
    /// build). Runs on the sim thread.
    /// </summary>
    private void BroadcastHullHit(Asteroid target, Vector3 worldImpact, Vector3 worldNormal)
    {
        if (target.Sector is not { } sector) return;
        short targetId = target.Data.AsteroidId;
        float radius = target.Radius;
        var msg = new ProjectileHullHit
        {
            TargetId = targetId,
            ClusterId = sector.Id,
            Radius = radius,
            Position = worldImpact,
            Normal = worldNormal,
            AbsorbedByShield = false,
            SourceId = -1,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = SendToSectorPeerSides(sector, packet);
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[hull-hit] target {targetId} sector {sector.Id} -> PROJ_HULL_HIT(213) -> {n} peer(s)");
    }

    /// <summary>
    /// Send a packet to every peer physically in <paramref name="sector"/>
    /// (plus the peers of a builder's side? — this helper's audience is the
    /// sector itself, matching the simulation's visibility of the build).
    /// </summary>
    private int SendToSectorPeerSides(Sector sector, byte[] packet)
    {
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Sector != sector) continue;
            SendPeer(kv.Key, packet);
            n++;
        }
        return n;
    }

    /// <summary>Relay a projectile impact to the same sector/source audience as creation.
    /// Native delivers its in-process hull hit at projectileIGC.cpp:220-226.
    /// </summary>
    private void BroadcastProjectileHullHit(Projectile p, Vector3 worldImpact, Vector3 worldNormal,
        float targetRadius, short targetId, short clusterId, bool absorbedByShield)
    {
        var shooter = p.Launcher as Ship;
        var origin = p.OriginProbe as Probe;
        if (shooter is null && origin is null) return;
        if (origin is null && shooter?.SideId is null)
        {
            if (ConsoleLog.Verbose) ConsoleLog.Write($"[projectile-hit] shooter {shooter?.Id} has no side — no PROJECTILE_HULL_HIT broadcast");
            return;
        }

        var msg = new ProjectileHullHit
        {
            TargetId = targetId,
            ClusterId = clusterId,
            Radius = targetRadius,
            Position = worldImpact,
            Normal = worldNormal,
            AbsorbedByShield = absorbedByShield,   // P463 — shield-hit discriminator
            SourceId = shooter?.Id ?? -1,                 // P539b — attribute the hit to the real shooter
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = origin is not null ? BroadcastProbeEmission(origin, packet)
            : BroadcastShipEmission(shooter!, p.OwnerSector, packet);
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[projectile-hit] ship {shooter?.Id} hit target {targetId} sector {clusterId} " +
                                                  $"absorbedByShield={absorbedByShield} pos {worldImpact} normal {worldNormal} radius {targetRadius} -> PROJ_HULL_HIT(213) -> {n} peer(s) (same-side + cross-side)");
    }

    /// <summary>
    /// P623 — unicast a real <c>VICTIM_DAMAGED(217)</c> <see cref="VictimDamaged"/>
    /// to the STRUCK ship's own peer alone: the port's wire analogue of native's
    /// in-process DamageBucket population (which a pure-renderer client cannot do
    /// for itself). Unlike every other damage message here, this is a single-peer
    /// targeted send — only the victim needs to know who is hurting it, so it is
    /// zero-cost to every other client and leaks nothing beyond the participant.
    /// <paramref name="attackerShipId"/>/<paramref name="victimShipId"/> are the
    /// ship ids and <paramref name="damage"/> the hull/shield amount the attacker's
    /// round dealt (drives the client's severity-ranked hostile window). Runs on
    /// the sim thread (same one that owns <c>_peerShips</c>), so no extra locking —
    /// mirroring <see cref="BroadcastProjectileHullHit"/>.
    /// </summary>
    private void BroadcastVictimDamaged(short attackerShipId, short victimShipId, float damage)
    {
        var msg = new VictimDamaged { AttackerId = attackerShipId, Damage = damage };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.Id == victimShipId)
            {
                SendPeer(kv.Key, packet);
                n++;
                break;   // one controlling peer per ship — unicast, not broadcast
            }
        }
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[victim-damaged] ship {victimShipId} took {damage:0.##} from ship {attackerShipId} -> VICTIM_DAMAGED(217) -> {n} peer(s) (victim only)");
    }

    /// <summary>
    /// Handle a collision that a sector resolver just resolved
    /// (<see cref="ServerHost.CollisionEvent"/>, raised from
    /// <c>Sector.ResolveShipShipCollisions</c> /
    /// <c>ResolveShipAsteroidCollisions</c> / <c>ResolveShipStationCollisions</c>
    /// right after the bounce/damage on the sim thread — P463c) by broadcasting a
    /// real <c>COLLISION_EVENT(216)</c> <see cref="CollisionEvent"/> to the peers
    /// who can see <b>either</b> party. This is the wire that lets a pure-renderer
    /// client play the positional collision sound and feed its own ship's impact
    /// into P449b's camera jiggle — the port of native's same-process
    /// <c>PlaySoundEffect(collisionSound, …)</c> / <c>PlayVisualEffect(effectJiggle,
    /// …)</c> (<c>shipIGC.cpp:834,1004,1189-1195</c>). Audience reuses the P364/P375
    /// visibility rules, united over both parties: each party's own peer plus its
    /// same-side teammates who hold its <c>ExportShip(26)</c>, plus the P369
    /// cross-side viewers in the party's sector — so an observer hears the crunch
    /// whenever they can see either colliding ship. <paramref name="party2"/> is
    /// null for an asteroid/station party (a rock/base is never an exported body,
    /// contributes no audience and no jiggle), in which case the message carries
    /// party2 id NA(-1). Runs on the sim thread, so no extra locking — mirroring
    /// <see cref="BroadcastProjectileHullHit"/>.
    /// </summary>
    private void BroadcastCollision(Ship party1, Ship? party2, short clusterId,
        Vector3 impact, float jiggle1, float jiggle2)
    {
        var msg = new Allegiance.Core.Wire.CollisionEvent
        {
            Party1Id = party1.Id,
            Party2Id = party2?.Id ?? -1,
            ClusterId = clusterId,
            Jiggle1 = jiggle1,
            Jiggle2 = jiggle2,
            Position = impact,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        var audience = new HashSet<ConnectionId>();
        AddCollisionPartyAudience(party1, audience);
        if (party2 is { } p2) AddCollisionPartyAudience(p2, audience);

        int n = 0;
        foreach (var conn in audience)
        {
            SendPeer(conn, packet);
            n++;
        }
        Console.WriteLine($"[collision] ship {party1.Id}" +
                          (party2 is { } p ? $" x ship {p.Id}" : $" x {ClassifyStaticBody(clusterId, impact)}") +
                          $" sector {clusterId} pos {impact} jiggle({jiggle1},{jiggle2}) -> COLLISION_EVENT(216) -> {n} peer(s) (union of both parties)");
    }

    /// <summary>P553 — collision-death measurement aid (pure logging, no physics):
    /// name the STATIC body a ship collided with when it is not another ship
    /// (the wire carries party2 = null for both a rock and a base, so the raw
    /// log reads "rock/base"). The sector's runtime stations and mineable
    /// asteroids are scanned for one whose collision sphere contains the impact
    /// point — a base when it is inside a <see cref="Station"/> (Center, Radius),
    /// otherwise an asteroid when inside a <see cref="Asteroid"/> (its record
    /// position, Radius) — yielding "asteroid {id}" / "station {id}". Deterministic
    /// at resolve time (the body is still present unless the impact killed it).
    /// Unknown bodies fall back to the unambiguous "rock/base" label.</summary>
    private string ClassifyStaticBody(short clusterId, Vector3 impact)
    {
        foreach (var m in Missions)
        {
            foreach (var sec in m.Sectors)
            {
                if (sec.Id != clusterId) continue;
                foreach (var st in sec.StationRuntimes)
                {
                    if ((impact - st.Center).LengthSquared() <= st.Radius * st.Radius)
                        return $"station {st.StationId}";
                }
                foreach (var ast in sec.Mineable)
                {
                    var ap = ast.Data.Position;
                    if (ap is not { Length: 3 }) continue;
                    var c = new Vector3(ap[0], ap[1], ap[2]);
                    if ((impact - c).LengthSquared() <= ast.Radius * ast.Radius)
                        return $"asteroid {ast.Data.AsteroidId}";
                }
                return "rock/base";
            }
        }
        return "rock/base";
    }

    /// <summary>Collect the connections who can see a single collision party:
    /// the party's OWN peer always (a pilot always hears/feels their own
    /// collision, even for a side-less synthetic body), plus — when the party
    /// carries a side — its same-side teammates who hold the party's
    /// <c>ExportShip(26)</c> (<see cref="_teammateExportSent"/>) and the P369
    /// cross-side viewers in the party's sector. The exact P364/P375 audience
    /// rules, gathered into <paramref name="audience"/> (union-safe).</summary>
    private void AddCollisionPartyAudience(Ship party, HashSet<ConnectionId> audience)
    {
        foreach (var kv in _peerShips)
        {
            if (ReferenceEquals(kv.Value.Ship, party)) { audience.Add(kv.Key); continue; }
            if (party.SideId is not { } side) continue;   // no side -> no teammate/cross-side reach
            if (kv.Value.Ship.SideId == side
                && _teammateExportSent.Contains((kv.Key, party.Id)))
                audience.Add(kv.Key);
        }
        if (party.SideId is { } ps && party.Sector is { } sec)
            foreach (var (conn, _) in CrossSideViewers(party, sec))
                audience.Add(conn);
    }

    /// <summary>Relay a missile launch to the sector, with a reliable source pose first.
    /// Native forwards FIRE_MISSILE to GetGroupSectorFlying (FedSrv.CPP:5505).
    /// </summary>
    private void BroadcastFireMissile(Missile m)
    {
        if (m.Launcher is not Ship shooter)
        {
            Console.WriteLine("[missile] MissileCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[missile] launcher {shooter.Id} has no side — no FIRE_MISSILE broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as BroadcastProjectileInstance stamps timeCreate=(uint)TickIndex).
        uint timeFired = (uint)TickIndex;

        var msg = new FireMissile
        {
            Lock = m.LockFraction,
            TimeFired = timeFired,
            MissileTypeId = m.MissileType.ExpendableTypeId,
            LauncherId = shooter.Id,
            ClusterId = m.OwnerSector.Id,
            TargetType = (int)EquipmentTarget.TypeOf(m.Target),
            TargetId = m.Target?.Id ?? -1,
            BDud = m.IsDud,
            // Exactly one missile per fire — this slice always sends a single
            // missile, never a salvo (see class doc).
            Entries =
            [
                new MissileLaunchData(m.Position, m.Forward, m.Velocity, m.Id),
            ],
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        var viewers = _missileViewers[m] = new();
        foreach (var peer in _peerShips.Keys)
            if (PeerInSector(peer, m.OwnerSector) && SendShipEmission(peer, shooter, m.OwnerSector, packet))
            {
                viewers.Add(peer);
                n++;
            }
        Console.WriteLine($"[missile] ship {shooter.Id} fired -> FIRE_MISSILE(70) type {m.MissileType.ExpendableTypeId} " +
                          $"pos {m.Position} vel {m.Velocity} fwd {m.Forward} life {m.MissileType.Lifespan} -> {n} peer(s) (same-side + cross-side)");
    }

    /// <summary>Remove a known missile and play its expiry explosion for the
    /// recorded viewers still in its sector, including after launcher departure.
    /// Native missile expiry explodes at its current position (missileigc.cpp:531).</summary>
    private void BroadcastMissileDestroyed(Missile m)
    {
        if (m.Launcher is not Ship shooter)
        {
            Console.WriteLine("[missile] MissileDestroyedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[missile] launcher {shooter.Id} has no side — no MISSILE_DESTROYED broadcast");
            return;
        }

        var msg = new MissileDestroyed
        {
            Position = m.Position,              // where it expired, not where it launched
            ClusterId = m.OwnerSector.Id,
            MissileId = m.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Creation and late tracking both record recipients. Cleanup must still
        // reach them after a cloak/warp/death, without revealing a new source or
        // playing an explosion to someone who never knew this missile.
        // Ordered destruction outranks the best-effort position lane. Replace
        // its pending batch BEFORE enqueueing destruction, or a stale 205 can
        // follow 83 and rehydrate the terminated missile in a late-viewer client.
        // Native termination is final (clintlib/appmsg.cpp:1790-1803); 205 is a
        // port-only stream. Other live missiles receive their next normal batch.
        byte[] clearedPositions = FedPacket.Pack(new MissileUpdate { Records = [] }.Encode());
        var targets = new List<ConnectionId>();
        if (_missileViewers.Remove(m, out var viewers))
            foreach (var peer in viewers)
                if (PeerInSector(peer, m.OwnerSector))
                {
                    SendPeerCoalescing(peer, clearedPositions, CoalesceMissiles);
                    SendPeer(peer, packet);
                    targets.Add(peer);
                }
        int n = targets.Count;
        Console.WriteLine($"[missile] ship {shooter.Id} expired missile {m.Id} -> MISSILE_DESTROYED(83) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} peer(s) (same-side + cross-side)");

        // P263 — the missile's impact/expiry blast VFX to the same audience.
        // Native Explode() plays the c_etMissile explosion wherever the missile
        // terminates (missileigc.cpp:531, scale = GetRadius()); the port emits
        // it server-side as the explosion category + position + velocity so the
        // fireball sprites ride the missile's momentum. P375 — `targets` now
        // includes the cross-side viewers, so they see the blast too.
        if (targets.Count > 0)
            SendExplosionEvent(targets, ExplosionType.Missile, m.OwnerSector.Id, m.Position, m.Velocity, m.MissileType.Radius);
    }

    // Explicit PROMOTE/SWAP_PART echoes apply their own mutations on clients.
    // Publishing intermediate snapshots first would erase PROMOTE's parent or
    // make SWAP_PART undo an already-applied swap. Sim-thread scoped, with finally
    // restoration at the command handlers. Native FedSrv.CPP:13530-13555 does
    // not queue LOADOUT_CHANGE from these low-level site events.
    private int _loadoutCommandDepth;

    /// <summary>
    /// Handle a ship loadout mutation (<see cref="ServerHost.LoadoutChange"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>ChangeHull</c>/<c>MountPart</c>/
    /// <c>TryRemovePart</c>/<c>ClearLoadout</c>/<c>AddChildShip</c>/
    /// <c>RemoveChildShip</c> right where the mutation lands) by broadcasting a
    /// real <c>FM_S_LOADOUT_CHANGE(96)</c> <see cref="Allegiance.Core.Wire.LoadoutChange"/> to the
    /// same-side peers who can see the changed ship. The message carries the
    /// ship's <b>current whole</b> loadout (hull id + every mounted part) plus its
    /// boarded-child list — built fresh at broadcast time exactly like native
    /// <c>CFSShip::QueueLoadoutChange</c>'s per-event <c>ExportShipLoadout</c>
    /// (<c>FedSrv/fsship.cpp:708-735</c>), not a delta. Scope is <b>same-side
    /// only</b>, matching every prior per-ship broadcast (P58/P62/P63/P85/P86/P88/
    /// P90/P94 are all same-side only): the changed ship's own peer (who always
    /// sees it) plus same-side teammates who have already received that ship's
    /// <c>ExportShip(26)</c> — the exact <c>_teammateExportSent</c>-gated idiom
    /// <see cref="BroadcastExportMine"/> uses. Cross-side/enemy ship visibility is
    /// intentionally not attempted (the port has no cross-side <c>ExportShip</c>;
    /// matching that scope, an enemy does not receive LOADOUT_CHANGE). Runs on the
    /// thread that raised the site event (the mission tick mutating the ship, or a
    /// direct caller of a Ship mutation method), reaching the sim-thread-owned
    /// <c>_peerShips</c> map the same way the other broadcasts do.
    /// </summary>
    private void BroadcastWeaponReady(Ship ship, string partName)
    {
        // Native weaponIGC.h:44-46, cloakigc.cpp:75-76, shieldIGC.cpp:82-83,
        // afterburnerIGC.cpp:119-120 use local IGC audio/text. Carry them over
        // the existing chat transport. WinTrek/trekigc.cpp:2994-2996 limits
        // text to the owner; :4975-4982 also lets a boarded crew hear its source.
        var message = new ChatMessage
        {
            SidSender = -1, ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = ship.Id, VoiceOver = WeaponReadyCue.SoundId,
            CommandID = OrderChange.CidNone, OidTarget = -1, OtTarget = -1,
            Message = $"{partName} ready."
        };
        var packet = FedPacket.Pack(new[] { message.Encode() });
        foreach (var (connection, peer) in _peerShips)
        {
            if (ReferenceEquals(peer.Ship, ship)) _host.Send(connection, packet);
            else if (ReferenceEquals(peer.Ship.ParentShip, ship))
            {
                // The recipient identifier belongs to this individual crew member.
                message.OidRecipient = peer.Ship.Id;
                message.Message = string.Empty;
                _host.Send(connection, FedPacket.Pack(new[] { message.Encode() }));
            }
        }
    }

    /// <summary>HUD-20 — native's <c>PassengerData.turretID</c> is a <c>Mount</c>
    /// whose <c>NA</c> (-1, 0xFF on the wire) marks an observer
    /// (<c>Inc/Messages.h:30-34</c>; <c>clintlib/appmsg.cpp:2064-2068,2073</c>).
    /// The old <c>?? 0</c> sent every observer as mount 0, so a client could not
    /// tell an observer from a gunner.</summary>
    internal static PassengerData PassengerRecord(Ship child) =>
        new(child.Id, (byte)(child.TurretId ?? -1));

    private void BroadcastLoadoutChange(IShipIgc pship, IPartIgc ppart, LoadoutChange lc)
    {
        if (pship is not Ship ship)
        {
            Console.WriteLine("[loadout] LoadoutChangeEvent with a non-Ship — ignored");
            return;
        }
        if (ship.SideId is not { } shipSide)
        {
            Console.WriteLine($"[loadout] ship {ship.Id} has no side — no LOADOUT_CHANGE broadcast");
            return;
        }

        // P648 — a HULL change (SetBaseHullType, e.g. a post-death relaunch
        // buying a new ship) means this ship's identity hull changed for
        // CROSS-SIDE viewers too. Enemies never receive LOADOUT_CHANGE (this
        // broadcast is same-side only), so the one-shot cross-side EXPORT latch
        // is their ONLY prompt hull-freshness channel besides the slow P369
        // self-heal (30 s). Drop the latch for this ship so the next
        // SweepCrossSideExports re-sends the fresh hull/identity to every enemy
        // peer that can see it — the task scope-2 rule "enemies learn a ship
        // when it is visible to them, as native does" (native re-exports a ship
        // when it becomes visible again; here we re-export promptly on the hull
        // change that marks the relaunch). Safe: SweepCrossSideExports only
        // sends to a side that has a seated peer IN the ship's current sector,
        // so a re-export never reaches an enemy who isn't actually seeing it.
        if (lc == LoadoutChange.HullChange)
            _crossSideExportSent.RemoveWhere(e => e.ShipId == ship.Id);

        if (_loadoutCommandDepth > 0) return;

        // The changed ship's CURRENT whole loadout (hull + every part), rebuilt
        // fresh at broadcast time (native ExportShipLoadout, FedSrv/fsship.cpp:710),
        // plus one PassengerData per boarded child (fsship.cpp:726-733). sidShip is
        // the ship's own real id — the non-force broadcast-to-observers path.
        var msg = new Allegiance.Core.Wire.LoadoutChange
        {
            HullId = ship.HullType.HullId,
            SidShip = ship.Id,
            // Launch repair uses 255 for fully armed runtime parts. Native
            // ExpandedPartData stores BytePercentage(1) == 240 (Utility.h:1311-
            // 1327; shipIGC.h:819). Keep the final post-launch inventory,
            // including refilled packs, but publish its native full-mount byte.
            Parts = ship.ExportInventoryForAck()
                .Select(p => p.FractionMounted == byte.MaxValue
                    ? p with { FractionMounted = ExpandedPartData.FullyMounted } : p)
                .ToArray(),
            Passengers = ship.ChildShips.Select(PassengerRecord).ToArray(),
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Changed ship's own peer always receives it (that peer owns it).
            // Teammates receive it only once they already hold the ship's
            // ExportShip — the exact _teammateExportSent gate BroadcastExportMine
            // uses. Everyone else (cross-side enemies, same-side peers who never
            // got the export) is excluded.
            bool isOwner = ReferenceEquals(kv.Value.Ship, ship);
            bool isVisibleTeammate = !isOwner
                                     && kv.Value.Ship.SideId == shipSide
                                     && _teammateExportSent.Contains((kv.Key, ship.Id));
            if (isOwner || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[loadout] ship {ship.Id} {lc} -> LOADOUT_CHANGE(96) hull {msg.HullId} " +
                          $"{msg.Parts.Count} part(s) {msg.Passengers.Count} passenger(s) -> {n} same-side peer(s)");
    }

    /// <summary>
    /// P361 — relay a ship's AUTO reload (raised from <c>Sim/Ship.cs</c>
    /// <c>TryAutoReloadAmmo</c> via <see cref="ServerHost.AutoReloadTriggered"/>
    /// when the ship's weapon fire drove its ammo to zero) as a sector-wide
    /// <c>RELOAD(129)</c> broadcast, exactly like the MANUAL
    /// <see cref="HandleReload"/> relay (<c>FedSrv.CPP:6651-6652</c>). On success
    /// (a cargo spare was consumed) the applied <c>ReloadData</c> entries are
    /// broadcast with <c>shipID</c> filled in so every peer flying in the same
    /// sector observes the cargo→pool transfer. On failure (no spare pack in the
    /// cargo hold — native's "Ammo depleted." case) there is nothing to relay
    /// (the ship stayed at zero), so no <c>RELOAD</c> is sent; the outcome is
    /// still exposed to P362 via <c>Ship.LastAutoReloadOutcome</c>. Runs on the
    /// simulation thread (inside the tick that fired the empty weapon), the same
    /// threading model as every other inbound/outbound broadcast.
    /// </summary>
    private void BroadcastAutoReload(IShipIgc pship, IReadOnlyList<ReloadData> entries, bool success)
    {
        if (pship is not Ship ship)
        {
            Console.WriteLine("[autoreload] AutoReloadTriggered with a non-Ship — ignored");
            return;
        }
        if (!success)
        {
            Console.WriteLine($"[autoreload] ship {ship.Id}: ammo hit zero, no cargo spare — Ammo depleted (no RELOAD relay)");
            return;
        }
        if (ship.Sector is null)
        {
            Console.WriteLine($"[autoreload] ship {ship.Id} has no sector — no RELOAD relay");
            return;
        }
        var msg = new Reload { Entries = entries, ShipID = ship.Id };
        SendSectorWide(ship.Sector, msg.Encode());
        Console.WriteLine($"[autoreload] ship {ship.Id}: pool/launcher emptied -> auto-reload {entries.Count} cargo entry/entries applied -> RELOAD(129) shipID={ship.Id} sector {ship.Sector.Id} wide");
    }

    /// <summary>
    /// P787a — relay a launcher auto-reload FAILURE (raised from <c>Sim/Ship.cs</c>
    /// <c>TryAutoReloadLauncher</c> via <see cref="ServerHost.LauncherReloadFailed"/>
    /// when a fire empties a missile/chaff/dispenser launcher and no spare
    /// launcher of that equipment type remains in cargo) as a sector-wide
    /// <c>DROP_PART(33)</c> broadcast — the port of native's reload-failure leg
    /// (<c>clintlib.cpp:3685-3695</c>: <c>FM_CS_DROP_PART</c> + terminating the
    /// empty launcher). The sim has already dropped the empty launcher; this
    /// relay lets every peer — including the own ship's pure-renderer client —
    /// observe the launcher gone and play the type-specific depleted cue
    /// (salMissilesDepletedSound / salChaffDepletedSound / salDispenserEmptySound).
    /// Unlike a MANUAL DROP_PART, no part treasure is created (native's empty-
    /// launcher guard, <c>FedSrv.CPP:6323</c>). Runs on the simulation thread
    /// inside the tick that emptied the launcher, like every other broadcast.
    /// </summary>
    private void BroadcastLauncherDepleted(IShipIgc pship, EquipmentType et, byte mount)
    {
        if (pship is not Ship ship)
        {
            Console.WriteLine("[launcher] LauncherReloadFailed with a non-Ship — ignored");
            return;
        }
        if (ship.Sector is null)
        {
            Console.WriteLine($"[launcher] ship {ship.Id} has no sector — no DROP_PART relay");
            return;
        }
        var msg = new DropPart { ShipID = ship.Id, Et = et, Mount = mount };
        SendSectorWide(ship.Sector, msg.Encode());
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[launcher] ship {ship.Id}: {et}@{(int)mount} emptied, no cargo spare -> empty launcher dropped -> DROP_PART(33) shipID={ship.Id} sector {ship.Sector.Id} wide (depleted)");
    }

    /// <summary>Export a newly deployed field to its owning side (including NPC layers).
    /// A viewer in the deployment sector receives the live emitter pose first.
    /// Cross-side discovery is independent, through SweepCrossSideMineExports.
    /// </summary>
    private void BroadcastExportMine(Mine m)
    {
        if (m.Launcher is not Ship layer)
        {
            Console.WriteLine("[mine] MineCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (layer.SideId is not { } layerSide)
        {
            Console.WriteLine($"[mine] layer {layer.Id} has no side — no EXPORT(mine) broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as the other broadcasts stamp timeCreate=(uint)TickIndex).
        uint timeCreate = (uint)TickIndex;
        var msg = ExportMine.FromMine(m, timeCreate, launcherId: layer.Id, sideId: layerSide);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // P591d — the owning side sees its own field: every same-side seated peer,
        // regardless of the launcher being a connected peer (an NPC layer drone
        // has no peer export, and the field must still reach the side). Enemies
        // are served by the P453 cross-side sweep once spotted.
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.SideId != layerSide || !PeerInMission(kv.Key, m.OwnerSector)) continue;
            if (PeerInSector(kv.Key, m.OwnerSector))
            {
                if (SendShipEmission(kv.Key, layer, m.OwnerSector, packet)) n++;
            }
            else { SendPeer(kv.Key, packet); n++; } // persistent own-side map knowledge
        }
        Console.WriteLine($"[mine] ship {layer.Id} deployed -> EXPORT(26)/OT_mine {m.MineType.ExpendableTypeId} " +
                          $"pos {m.Position} life {m.MineType.Lifespan} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a mine that just expired (<see cref="ServerHost.MineDestroyedEvent"/>,
    /// raised from <c>Sim/Mine.cs</c> <c>OnUpdate</c> alongside
    /// <c>sector.Remove(mine)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_MINE_DESTROYED(84)</c> <see cref="MineDestroyed"/> message — the
    /// \"this mine blew up\" notice (<c>Messages.h:603-606</c>) — to the same
    /// same-side audience that saw it deploy (<see cref="BroadcastExportMine"/>).
    /// This is P94's destruction half, reached by modelling the mine's own
    /// expiry as the analogue of a ship's death (<c>Ship.ReceiveDamage</c> →
    /// <c>KillShipEvent</c>, <c>Sim/Ship.cs:494</c>), exactly as
    /// <see cref="BroadcastMissileDestroyed"/> does for missiles. Because a mine
    /// never moves, the position carried (<c>m.Position</c>) is the <b>same</b>
    /// deployment position the creation export carried — the observable
    /// \"the mine didn't move\" contract this task's verification asserts
    /// explicitly. Same-side only, same <c>_teammateExportSent</c>-gated
    /// audience, same sim-thread-no-lock reasoning as the other broadcasts.
    /// </summary>
    private void BroadcastMineDestroyed(Mine m)
    {
        var msg = new MineDestroyed
        {
            ClusterId = m.OwnerSector.Id,
            MineId = m.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // P453 — the destroy audience is the union of the same-side deploy
        // audience (P591d: the owning side's seated peers, exactly
        // BroadcastExportMine's side-wide shape) and every cross-side peer that
        // was ever sent this mine's EXPORT (the _crossSideMineExportSent latch
        // from SweepCrossSideMineExports). A client must never keep a phantom
        // field after the mine is destroyed or expires, so the cross-side reach
        // rides the latch and is served even when the launcher is unusual (log-only).
        Ship? layer = m.Launcher as Ship;
        short? ownerSide = m.SideId; // ownership belongs to the field, not its deployer
        bool hasOwner = ownerSide is not null;
        if (!hasOwner)
            Console.WriteLine("[mine] MineDestroyedEvent for an unowned field — same-side audience skipped, cross-side latch still served");

        int n = 0;
        var targets = new List<ConnectionId>();
        foreach (var kv in _peerShips)
        {
            // P591d — same-side = the owning side (matches the new
            // BroadcastExportMine audience), not gated on the launcher being a
            // connected peer.
            bool sameSide = hasOwner && kv.Value.Ship.SideId == ownerSide;
            bool gotCrossSideExport = _crossSideMineExportSent.Contains((kv.Key, m.Id));
            if ((sameSide || gotCrossSideExport) && PeerInMission(kv.Key, m.OwnerSector))
            {
                SendPeer(kv.Key, packet);
                targets.Add(kv.Key);
                n++;
            }
        }
        Console.WriteLine($"[mine] ship {layer?.Id ?? -1} destroyed/expired mine {m.Id} -> MINE_DESTROYED(84) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} peer(s)");
        // P574 — deliberately NO explosion here. The centre "field died"
        // c_etMine blast this P263 block previously sent is a misattribution:
        // the native line it cited (shipIGC.h:2024) is the PER-HIT pop inside
        // ApplyMineDamage, not the field-death path. Native draws nothing when a
        // mine is used up or expires — KillMineEvent just terminates the mine
        // (clintlib.cpp:2954-2958; the game-site igc.h:4845 default is empty) and
        // CmineIGC::Update → Terminate (mineigc.cpp:150-156) does likewise. The
        // per-hit pop is now broadcast by <see cref="BroadcastMineHit"/> at P2.
    }

    /// <summary>
    /// P574 — broadcast the per-hit <c>c_etMine</c> pop for a live ship's mine
    /// hit, the port of native <c>ApplyMineDamage</c>'s explosion pair
    /// (<c>shipIGC.h:2021-2024</c>): throttled to one per 0.5 s per ship (the
    /// sim raises <see cref="ServerHost.MineHitEvent"/> only when the
    /// <c>m_timeLastMineExplosion</c> gate passes) and placed at the native
    /// <c>m_maxMineP2</c> — just ahead of the ship along its path plus the
    /// 10-unit random offset (<c>mineigc.h:129-132</c>) — NOT the field centre.
    /// Sent as <c>EXPLOSION_EVENT(207)</c> scale 1.5 to the peers whose ship is
    /// in the same sector as the mine (the "sees the sector's explosions"
    /// audience, the same same-sector filter the ship-death blast uses). Runs on
    /// the sim thread (inside the sector tick that applied the hit), so the
    /// sim-thread-owned peer map needs no lock.
    /// </summary>
    private void BroadcastMineHit(Mine m, Vector3 p1, Vector3 p2)
    {
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Sector, m.OwnerSector))
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0)
        {
            Console.WriteLine($"[mine] ship {m.Launcher?.Id ?? -1} hit by mine {m.Id}: pop at {p2} sector {m.OwnerSector.Id} -> 0 peer(s)");
            return;
        }
        SendExplosionEvent(targets, ExplosionType.Mine, m.OwnerSector.Id, p2, Vector3.Zero, 1.5f);
        Console.WriteLine($"[mine] ship {m.Launcher?.Id ?? -1} hit by mine {m.Id}: c_etMine pop at {p2} sector {m.OwnerSector.Id} -> {targets.Length} peer(s)");

        // P574 — the mine's current strength fraction (native SetMineStrength,
        // mineigc.h:142,180) so the client thins the decal field to match the
        // pop ("the field wears down" half). Same same-sector audience.
        var strength = new MineStrength
        {
            ClusterId = m.OwnerSector.Id,
            MineId = m.Id,
            Strength = m.Fraction,
        };
        byte[] sp = FedPacket.Pack(new[] { strength.Encode() });
        foreach (var t in targets) SendPeer(t, sp);
        Console.WriteLine($"[mine] mine {m.Id} strength -> {m.Fraction:F3} (MINE_STRENGTH) -> {targets.Length} peer(s)");
    }

    /// <summary>
    /// P263 — broadcast a real <see cref="Wire.ExplosionEvent"/>(207) to the
    /// given peers so the MonoGame client renders the blast VFX. This is the
    /// port's server-authoritative replacement for native's
    /// <c>IclusterSite::AddExplosion(position, forward, right, velocity, scale,
    /// type)</c> callback (<c>clusterIGC.cpp:407</c>): the sim decides WHAT died
    /// (the type category) and WHERE, and the thin render client plays the
    /// matching sprite preset. Runs on the sim thread (same one that owns
    /// _peerShips and the caller's targets), so no extra locking.
    /// </summary>
    /// <remarks><c>scale</c> is the native 2 * GetRadius() audio-tier value
    /// (<c>trekigc.cpp:183</c>, tiers &gt;100 / &gt;10 / else at
    /// <c>trekigc.cpp:94-104</c>); the rendered size comes from the type preset,
    /// not this. Logged for the live-proof seam.</remarks>
    private void SendExplosionEvent(
        IEnumerable<ConnectionId> targets,
        int type,
        short clusterId,
        Vector3 position,
        Vector3 velocity,
        float scale)
    {
        var msg = new ExplosionEvent
        {
            Type = type,
            ClusterId = clusterId,
            Scale = scale,
            Position = position,
            Velocity = velocity,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var t in targets) { SendPeer(t, packet); n++; }
        Console.WriteLine($"[explosion] type {type} cluster {clusterId} scale {scale} at {position} -> EXPLOSION_EVENT(207) -> {n} peer(s)");
    }

    /// <summary>Export a newly deployed probe to its owning side, including NPC deployers.
    /// A viewer in the deployment sector receives the emitter identity/pose first;
    /// remote teammates retain their existing persistent object knowledge.
    /// </summary>
    private void BroadcastExportProbe(Probe p)
    {
        if (p.Launcher is not Ship deployer)
        {
            Console.WriteLine("[probe] ProbeCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (deployer.SideId is not { } deployerSide)
        {
            Console.WriteLine($"[probe] deployer {deployer.Id} has no side — no EXPORT(probe) broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as the mine/other broadcasts stamp timeCreate=(uint)TickIndex).
        uint timeCreate = (uint)TickIndex;
        var msg = ExportProbe.FromProbe(p, timeCreate, launcherId: deployer.Id, sideId: deployerSide);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.SideId == deployerSide && PeerInMission(kv.Key, p.OwnerSector))
            {
                if (PeerInSector(kv.Key, p.OwnerSector))
                {
                    if (SendShipEmission(kv.Key, deployer, p.OwnerSector, packet)) { RememberProbeViewer(kv.Key, p); n++; }
                }
                else { SendPeer(kv.Key, packet); RememberProbeViewer(kv.Key, p); n++; } // persistent own-side map knowledge
            }
        }
        Console.WriteLine($"[probe] ship {deployer.Id} deployed -> EXPORT(26)/OT_probe {p.ProbeType.ExpendableTypeId} " +
                          $"pos {p.Position} life {p.ProbeType.Lifespan} side {deployerSide} -> {n} same-side peer(s)");
    }

    /// <summary>Remove a probe for its owning side, matching creation including
    /// NPC deployers. Cleanup does not depend on the deployer remaining alive,
    /// on its current side, or on a connected-player export latch.</summary>
    private void BroadcastProbeDestroyed(Probe p)
    {
        _probeViewers.Remove(p, out var viewers);
        var packet = FedPacket.Pack(new ProbeDestroyed
        {
            ClusterId = p.OwnerSector.Id,
            ProbeId = p.Id,
        }.Encode());
        int sent = 0;
        foreach (var peer in _peerShips)
            if ((peer.Value.Ship.SideId == p.SideId || viewers?.Contains(peer.Key) == true)
                && PeerInMission(peer.Key, p.OwnerSector)) { SendPeer(peer.Key, packet); sent++; }
        Console.WriteLine($"[probe] ship {(p.Launcher as Ship)?.Id ?? -1} expired probe {p.Id} -> PROBE_DESTROYED(85) " +
                          $"pos {p.Position} sector {p.OwnerSector.Id} -> {sent} same-side peer(s)");
    }

    /// <summary>
    /// Handle a warp bombed by a missile (<see cref="ServerHost.WarpBombEvent"/>,
    /// raised from <c>Sector.ResolveWarpBombTriggers</c> on the sim thread when a
    /// live <see cref="Missile"/> reaches a warp, P101) by broadcasting a real
    /// <c>FM_S_WARP_BOMB(188)</c> <see cref="WarpBomb"/> message — the
    /// "aleph destabilized" notice (<c>Inc/Messages.h:969-973</c>) that a missile
    /// hit a warp and will explode there at the captured simulation deadline.
    /// Native FedSrv/FedSrv.CPP:12551-12558 uses loaded slot 32 and uint
    /// milliseconds. Capture at the collision chunk so time dilation and later
    /// core edits cannot disagree with the scheduled blast.
    /// <para><b>WHOLE MISSION — the one genuine behavioural departure from every
    /// prior ordnance broadcast in this project.</b> Native sends <c>WARP_BOMB</c>
    /// to <c>GetGroupRealSides()</c> (<c>FedSrv.CPP:12558</c>) — all real sides,
    /// not a same-side gate. Every prior ordnance broadcast
    /// (<c>BroadcastProjectileInstance</c>/<c>BroadcastFireMissile</c>/
    /// <c>BroadcastMineDestroyed</c>/<c>BroadcastProbeDestroyed</c>, P88/P90/P94/P99)
    /// is same-side only; this one reaches <b>every</b> connected peer, on any
    /// side. Do not "fix" it to same-side to match the neighbours. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void BroadcastWarpBomb(ResolvedWarp warp, Missile missile)
    {
        var mission = (missile.Launcher as Ship)?.Mission;
        if (mission is null || !mission.Sectors.Any(s => s.Warps.Contains(warp))) return;
        double timeExplosion = missile.WarpBombTime ??
            mission.Sectors.First(s => s.Warps.Contains(warp)).LastUpdate +
            mission.Core.Constants.FloatConstants[32];
        var msg = new WarpBomb
        {
            TimeExplosion = (uint)(timeExplosion * 1000.0), // native Time, same clock as PING
            WarpIdBombed = warp.Warp.WarpId,
            ExpendableIdMissile = missile.MissileType.ExpendableTypeId,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides()).
        var targets = _peerShips.Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[warpbomb] warp {warp.Warp.WarpId} bombed by missile {missile.Id} " +
                          $"(missileType {missile.MissileType.ExpendableTypeId}) -> WARP_BOMB(188) " +
                          $"timeExplosion ms {msg.TimeExplosion} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a combat-killed asteroid (<see cref="ServerHost.KillAsteroidEvent"/>,
    /// raised from <c>Model.Sector.ResolveCombatCollisions</c> on the sim thread
    /// when a real ordnance hit crosses the rock's <see cref="Asteroid.Fraction"/>
    /// to zero, P102) by broadcasting a real <c>FM_S_ASTEROID_DESTROYED(86)</c>
    /// <see cref="AsteroidDestroyed"/> message — the &quot;this asteroid was
    /// destroyed here&quot; notice (<c>Messages.h:613-617</c>).
    /// <para><b>WHOLE MISSION.</b> Native sends <c>ASTEROID_DESTROYED</c> from the
    /// plain <c>(IAsteroidIgc, bool)</c> overload to <c>GetGroupRealSides()</c>
    /// (<c>FedSrv.CPP:12418</c>) — all real sides, not a same-side gate, the same
    /// audience P101 settled for <c>WARP_BOMB</c>. Do not &quot;fix&quot; it to
    /// same-side to match the neighbouring ship/probe/mine broadcasts. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.
    /// <c>explodeF</c> is true from the only call site this task implements (combat
    /// death), transmitted verbatim.</para>
    /// </summary>
    private void BroadcastStaleAsteroidDestroyed(short asteroidId, short sectorId, Side side)
    {
        var packet = FedPacket.Pack(new AsteroidDestroyed
            { AsteroidId = asteroidId, ClusterId = sectorId, ExplodeF = false }.Encode());
        foreach (var peer in _peerShips)
            if (ReferenceEquals(peer.Value.Ship.Mission, side.Mission) && peer.Value.Ship.SideId == side.Id)
                SendPeer(peer.Key, packet);
    }

    private void BroadcastAsteroidDestroyed(Asteroid a, bool explodeF)
    {
        var msg = new AsteroidDestroyed
        {
            ClusterId = a.Data.ClusterId,
            AsteroidId = a.Data.AsteroidId,
            ExplodeF = explodeF,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides(), FedSrv.CPP:12418).
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[asteroid] asteroid {a.Data.AsteroidId} '{a.Data.Name}' destroyed (explodeF={explodeF}) " +
                          $"=> ASTEROID_DESTROYED(86) sector {a.Data.ClusterId} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a combat-destroyed station (<see cref="ServerHost.KillStationEvent"/>,
    /// raised from <c>Model.Sector.ResolveCombatCollisions</c> on the sim thread
    /// when a real ordnance hit crosses the station's
    /// <see cref="Station.ArmorFraction"/> to zero, P125) by, in the native order
    /// (<c>FedSrvSiteBase::KillStationEvent</c>, <c>FedSrv.CPP:12237-12279</c>):
    /// <list type="number">
    /// <item><b>VacateStation</b> (<c>fsmission.cpp:4783</c>) — kick every ship
    /// docked at the dead base out through the real undock path
    /// (<see cref="PerformUndock"/>: clear <see cref="Ship.StationId"/>, push
    /// clear of the (now-gone) dock radius, start the undock debounce, re-send
    /// FM_S_SET_CLUSTER). Reuses the existing undock/eject helper rather than
    /// writing new ejection logic — the task's explicit instruction (P105/P107/
    /// P110 helpers).</item>
    /// <item><b>Broadcast <c>FM_S_STATION_DESTROYED(59)</c></b>
    /// (<see cref="Wire.StationDestroyed"/>, <c>Messages.h:436-439</c>) with
    /// <c>stationID</c> from the station and <c>launcher</c> = the credited
    /// ship's id, or <c>NA</c> when the launcher was not a ship
    /// (<c>FedSrv.CPP:12277-12278</c>).</item>
    /// </list>
    /// <para><b>WHOLE MISSION.</b> Native sends <c>STATION_DESTROYED</c> to
    /// <c>GetGroupRealSides()</c> (<c>FedSrv.CPP:12279</c>) — all real sides, not
    /// a same-side gate, the same audience P102 settled for <c>ASTEROID_DESTROYED</c>.
    /// Do not &quot;fix&quot; it to same-side. The score/Steam bookkeeping in the
    /// native function (<c>KillBase</c>, <c>AwardBaseKillOrCapture</c>,
    /// <c>AddBaseKill</c>) is confirmed N/A — no score or Steam system exists
    /// anywhere in this port. Runs on the sim thread (same one that owns
    /// <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void BroadcastStationDestroyed(Station station, Model? launcher)
    {
        using var scope = TickProfiler.Enter("combat.stationEvents");
        short launcherId = -1;                     // NA
        if (launcher is Ship s) launcherId = s.Id;

        // P583 — the credited launcher's base kill (native KillBase(true),
        // FedSrv.CPP:12252). A turret-gunner child's +0.5 (KillBase(false),
        // FedSrv.CPP:12264) is N/A — this port has no child/turret ships.
        // Only a live peer's score is credited (an ownerless NPC launcher has
        // no score object).
        if (launcher is Ship launcherShip && LiveOf(launcherShip)?.PlayerScore is { } baseKillScore)
        {
            baseKillScore.KillBase(true);
            Console.WriteLine($"[scoring] launcher ship {launcherShip.Id} base kill +1 (player peer)");
        }

        // P752 — the LAUNCHER's side base-kill counter (native
        // psideLauncher->AddBaseKill(), FedSrv.CPP:12269-12273): ANY launcher
        // that has a side credits it (ship/missile/mine/probe — native reads
        // plauncher->GetSide() after the OT_ship-only score block), not only
        // Ship launchers. Feeds the game-over side table's cBaseKills column
        // (fsmission.cpp:3077).
        if (launcher is { } anyLauncher && EquipmentTarget.SideOf(anyLauncher) is { } bkSideId
            && station.Mission?.Sides.FirstOrDefault(s => s.Id == bkSideId) is { } bkSide)
            bkSide.BaseKills++;

        // 1. Vacate every ship docked at the destroyed station (native
        //    VacateStation, fsmission.cpp:4783) — reuse PerformUndock, the real
        //    undock/eject path (StationId cleared + launch reposition + cooldown
        //    + FM_S_SET_CLUSTER). The dead station is already gone from the
        //    sector's Stations list (removed in the combat pass), so
        //    PerformUndock's reposition lookup finds nothing and simply skips the
        //    launch-reposition — the ship is still correctly ejected.
        //    P799 — GHOSTS ARE NOT VACATED (fsmission.cpp:4796-4802): native's
        //    VacateStation skips IsGhost ships in the eject loop and instead
        //    re-docks every ghost of that side at another restart-capable base
        //    (fsmission.cpp:4805-4849: `pship->SetStation(ps)`); ghosts cannot
        //    launch, so undocking one would strand it in space forever. If the
        //    side has NO alternate restart base the ghost keeps its (destroyed)
        //    station pointer — native's "don't worry: side will be eliminated"
        //    branch (fsmission.cpp:4789) — because losing the last restart
        //    station fires SideLostRestartStation → DeactivateSide, which
        //    force-kills the docked ghosts.
        VacateStation(station);

        // 2. Whole-mission broadcast: every connected peer with a seated ship,
        //    regardless of side (GetGroupRealSides(), FedSrv.CPP:12279).
        var msg = new StationDestroyed
        {
            StationId = station.StationId,
            Launcher = launcherId,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        var note = $"[station] station {station.StationId} destroyed " +
                   $"(launcher {launcherId}) => STATION_DESTROYED(59) -> {targets.Length} peer(s) [WHOLE MISSION]";
        Console.WriteLine(note);
        Notify(note);                     // spectator event feed (same text)

        foreach (var t in targets)
            SendPeer(t, packet);

        // P263 — the station's destruction blast VFX. Native picks the category
        // by the station's victory-flag capability on STATION_DESTROYED
        // (appmsg.cpp:525-528: c_sabmFlag ? c_etLargeStation : c_etSmallStation)
        // and the port emits it server-side to the same whole-mission audience.
        // The scale is the native 2 * GetRadius() audio tier; the rendered size
        // comes from the type preset (350u large / 100u small-station), so a
        // nominal station radius suffices here.
        int stationType = station.Type.HasCapability(DataStationTypeIGC.SabmFlag)
            ? ExplosionType.LargeStation
            : ExplosionType.SmallStation;
        SendExplosionEvent(targets, stationType, station.ClusterId, station.Center, Vector3.Zero, 100f);
    }

    /// <summary>
    /// P451 — the defending team's base-under-attack alert, the port of
    /// <c>FedSrvSiteBase::DamageStationEvent</c> (<c>FedSrv.CPP:12183-12233</c>),
    /// reached from <see cref="ServerHost.DamageStationEvent"/> (raised inside
    /// <see cref="Station.ReceiveDamage"/>, the port of the <c>pigc-&gt;DamageStationEvent</c>
    /// at <c>stationIGC.cpp:169</c>). In the native order:
    /// <list type="number">
    /// <item><b>Gates</b> (<c>FedSrv.CPP:12185</c>): the hit must deal real damage
    /// (<c>amount &gt;= 1.0f</c>), come from a real launcher, and carry no
    /// <c>c_dmgidNoWarn</c> flag (<c>0x20</c>, <c>igc.h:559</c>). A launcher without
    /// a side (never seated) sends nothing (<c>FedSrv.CPP:12187-12188</c>).</item>
    /// <item><b>Throttle</b> (<c>FedSrv.CPP:12190</c>): at most one report per station
    /// per 10 s, tracked on the station (<see cref="Station.LastDamageReport"/>);
    /// a hit inside the window sends nothing and does not reset the stamp.</item>
    /// <item><b>Cross-side team alert</b> (<c>FedSrv.CPP:12192-12212</c>): when the
    /// launcher's side differs from the station's, the DEFENDING side only gets a
    /// team <see cref="Wire.ChatMessage"/>: <c>"%s in %s under attack by %s"</c>
    /// (or <c>"...critically damaged and under attack by %s"</c> once the PRE-hit
    /// hull fraction is below 0.30, <c>FedSrv.CPP:12199</c>), with <c>VoiceOver</c>
    /// set to the station type's <c>UnderAttackSound</c>/<c>CriticalSound</c>.
    /// Alliances are absent in this port (no <c>CHAT_ALLIES</c>, no "(Ally)"
    /// prefix — the "<c>%s</c>" ally slot is elided) and <c>ct</c> is therefore
    /// always <c>CHAT_TEAM</c>.</item>
    /// <item><b>Same-side "Watch your fire"</b> (<c>FedSrv.CPP:12221-12228</c>): when
    /// the launcher is on the station's own side and is a player, that pilot alone
    /// gets a private <c>"Watch your fire"</c> with <c>droneWatchFireSound</c>
    /// (<c>= 1325</c>; igc.h:456-463 enum from 1000, sounds.h:377 is entry 325).
    /// </item>
    /// </list>
    /// <para>Scoped to the launcher's mission (the attacking ship's mission is the
    /// mission whose sector the station sits in, the same bridge the sector uses for
    /// KillStationEvent), so a peer on the defending side id in ANOTHER mission is
    /// never alerted. Runs on the sim thread (same one that owns <c>_peerShips</c>),
    /// so no extra locking.</para>
    /// </summary>
    private const byte DmgIdNoWarn = 0x20;                 // c_dmgidNoWarn (igc.h:559)
    private const short DroneWatchFireSound = 1324;        // Igc/sounds.h:377; igc.h:456-464 (1001 + ordinal)
    // P751 — ship under-attack voice lines (sounds.h:391-394, NativeVoiceCatalog.Generated.cs:918-921).
    private const short DroneUnderAttackSound = 1338;
    private const short MinerUnderAttackSound = 1339;
    private const short CarrierUnderAttackSound = 1340;
    private const short MinerCriticalSound = 1341;

    private void BroadcastStationDamaged(Station station, Model? launcher, byte type, float amount)
    {
        // Native gates (FedSrv.CPP:12185): real damage, a real launcher, no no-warn bit.
        if (amount < 1.0f) return;
        if (launcher is not Ship ls) return;
        if ((type & DmgIdNoWarn) != 0) return;
        if (ls.SideId is not { } launcherSide) return;     // unseated launcher -> no alert (FedSrv.CPP:12187-12188)
        if (ls.Mission is not { } mission) return;         // scoping bridge: the attacking mission

        // Per-station 10 s throttle (FedSrv.CPP:12190). Only a report that passes the
        // window sends; native stamps LastDamageReport only after a passing block
        // (FedSrv.CPP:12231), so an in-window hit neither sends nor extends the window.
        double now = SimNow;
        if (now - station.LastDamageReport <= 10.0) return;

        short pside = station.SideId;
        if (pside != launcherSide)
        {
            // Cross-side: team alert to the DEFENDING side with the station type's
            // voice line. Critical once the PRE-hit hull fraction drops below 0.30
            // (the raise precedes the damage mutation, so ArmorFraction is pre-hit,
            // exactly as native reads GetFraction() from line 169 before line 171).
            // P846 — the native #ALLY branches (FedSrv.CPP:12192-12229): an ALLIED
            // launcher still triggers the alert (pside != psideLauncher) but the
            // text carries the "(Ally)" prefix, the chat target becomes CHAT_ALLIES
            // when the defending side has allies, the audience includes those
            // allies, and an allied PLAYER launcher also gets the private
            // "Watch your fire" (FedSrv.CPP:12222-12228).
            bool allied = AlliedSides(mission, pside, launcherSide);
            bool hasAllies = StationSideHasAllies(mission, pside);
            bool critical = station.ArmorFraction < 0.30f;
            short voice = critical ? station.Type.CriticalSound : station.Type.UnderAttackSound;

            string stationName = station.Data.Name ?? $"station {station.StationId}";
            string sectorName = mission.Sectors.FirstOrDefault(s => s.Id == station.ClusterId)?.Name ?? "?";
            string launcherName = PeerName(ls) ?? $"Ship {ls.Id}";
            string allyPrefix = allied ? "(Ally) " : "";
            string text = critical
                ? $"{stationName} in {sectorName} critically damaged and under attack by {allyPrefix}{launcherName}"
                : $"{stationName} in {sectorName} under attack by {allyPrefix}{launcherName}";

            var msg = new ChatMessage
            {
                Message = text,
                ChatTarget = (int)(hasAllies ? ChatTarget.Allies : ChatTarget.Team),
                OidRecipient = pside,   // the defending side id (SendChatf's oidRecipient)
                VoiceOver = voice,
            };
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

            int n = 0;
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId is not { } peerSide) continue;
                // Defending side, plus every side allied to it when the alert is
                // addressed to CHAT_ALLIES (Server.Callbacks' own audience rule).
                if (peerSide != pside && !(hasAllies && AlliedSides(mission, pside, peerSide))) continue;
                SendPeer(kv.Key, packet);
                n++;
            }
            Console.WriteLine($"[station] '{text}' (voice {voice}, target {(hasAllies ? "ALLIES" : "TEAM")}) -> side {pside} [{n} peer(s)] [under attack]");

            // Allied player launcher: the private friendly-fire whisper, sender
            // NULL (the station), exactly like the same-side branch below.
            if (allied) SendStationWatchYourFire(ls);
        }
        else
        {
            // Same-side launcher: private "Watch your fire" to that pilot alone
            // (FedSrv.CPP:12221-12228), only when the launcher is a player (an
            // ownerless NPC has no _peerShips entry).
            SendStationWatchYourFire(ls);
        }

        station.LastDamageReport = now;
    }

    /// <summary>P846 — the station-damage private friendly-fire whisper
    /// (<c>SendChat(NULL, CHAT_INDIVIDUAL, launcher-&gt;GetObjectID(),
    /// droneWatchFireSound, "Watch your fire")</c>, <c>FedSrv.CPP:12226,12264</c>).
    /// The station is the sender, so <c>sidSender</c> is NA. Only a peer launcher
    /// has a connection to receive it.</summary>
    private void SendStationWatchYourFire(Ship launcher)
    {
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, launcher)) continue;
            var msg = new ChatMessage
            {
                Message = "Watch your fire",
                SidSender = -1, // native station warning has NULL sender (FedSrv.CPP:12226)
                ChatTarget = (int)ChatTarget.Individual,
                OidRecipient = launcher.Id,
                VoiceOver = DroneWatchFireSound,
            };
            SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
            Console.WriteLine($"[station] 'Watch your fire' -> ship {launcher.Id} [(allied) launcher]");
            break;
        }
    }

    /// <summary>P846 — native's <c>station-&gt;GetSide()-&gt;GetAllies() != NA</c>
    /// test (<c>FedSrv.CPP:12196</c>): the defending side belongs to an ally
    /// group, so the alert goes out as <c>CHAT_ALLIES</c> (whether or not another
    /// member is online — native reads the group id, not the membership).</summary>
    private static bool StationSideHasAllies(Mission mission, short sideId)
        => mission.Sides.FirstOrDefault(s => s.Id == sideId)?.AllyGroup is not null;

    /// <summary>
    /// P751b — handle a damaging hit on a ship (<see cref="ServerHost.DamageShipEvent"/>,
    /// raised from <c>Ship.ReceiveDamage</c> at shipIGC.cpp:398 and shipIGC.cpp:1312).
    /// Port of <c>FedSrvSiteBase::DamageShipEvent</c> (FedSrv.CPP:12089-12178).
    /// <para>Native gates (FedSrv.CPP:12091): real damage (<c>amount &gt;= 1.0f</c>),
    /// a real launcher, and no <c>c_dmgidNoWarn</c> bit. The 10 s throttle is
    /// per-ship (<c>ship-&gt;GetLastDamageReport()</c>), updated only after a
    /// passing report.</para>
    /// <para>For NPC ships the defending side receives a team "Under attack" chat
    /// with a pilot-type-specific voice line; for same-side or allied launcher
    /// attacks the launcher pilot receives a private "Watch your fire".</para>
    /// <para>Runs on the sim thread (same one that owns <c>_peerShips</c>), scoped
    /// to the ship's mission.</para>
    /// </summary>
    private void BroadcastShipDamaged(Ship ship, Model? launcher, double now, byte type, float amount)
    {
        // Native gates (FedSrv.CPP:12091): real damage, a real launcher, no no-warn bit.
        if (amount < 1.0f) return;
        if (launcher is null) return;
        if ((type & DmgIdNoWarn) != 0) return;
        if (ship.Mission is not { } mission) return;       // scoping bridge

        // Non-ship launchers (probes, station turrets) must still trigger the
        // alert: read the side from the launcher model (FedSrv.CPP:12095).
        if (EquipmentTarget.SideOf(launcher) is not { } launcherSide) return;
        if (ship.SideId is not { } pside) return;

        // Per-ship 10 s throttle (FedSrv.CPP:12103,12158). The exact stamp
        // behaviour differs for NPC and player victims; see below.
        // P751b: native gates on pcfs->IsPlayer(); the brief instructs us to
        // gate on the victim's pilot type (drones/miners/builders are NPCs).
        bool victimIsPlayer = ship.PilotType >= Ship.PilotTypePlayer;

        if (now - ship.LastDamageReport <= 10.0) return;

        bool sameSide = pside == launcherSide;
        // P846 — native's allied test is `pside->AlliedSides(pside, psideLauncher)`
        // (FedSrv.CPP:12116,12178), i.e. the PRODUCTION alliance model
        // (Side.AllyGroup, fed by the lobby). The port's skirmish/test pairs are
        // also honoured so a harness can express the same alliance.
        bool allied = !sameSide &&
            (AlliedSides(mission, pside, launcherSide) ||
             mission.AlliedSidePairs.Contains((pside, launcherSide)) ||
             mission.AlliedSidePairs.Contains((launcherSide, pside)));
        var launcherShip = launcher as Ship;

        if (!victimIsPlayer)
        {
            // NPC victim branch (FedSrv.CPP:12098-12171).
            if (sameSide)
            {
                // Same-side: private "Watch your fire" to a player launcher only
                // (FedSrv.CPP:12161-12171).
                SendWatchYourFire(launcherShip, ship);
            }
            else
            {
                // Cross-side (enemy or allied): team "Under attack" on the
                // defender's side (FedSrv.CPP:12120-12145).
                SendNpcUnderAttackAlert(ship, launcher, launcherSide, allied, mission, pside);

                // Allied player launcher: also whisper "Watch your fire"
                // (FedSrv.CPP:12131-12145).
                if (allied && launcherShip is not null)
                    SendWatchYourFire(launcherShip, ship);
            }

            // Native stamps the NPC throttle regardless of whether a chat was sent
            // (FedSrv.CPP:12172, just after the if/else block).
            ship.LastDamageReport = now;
        }
        else
        {
            // Player victim branch (FedSrv.CPP:12174-12185): only an allied
            // cross-side hit whispers "Watch your fire", and the throttle is
            // stamped only when that whisper is sent.
            if (!sameSide && allied && launcherShip is not null)
            {
                SendWatchYourFire(launcherShip, ship);
                ship.LastDamageReport = now;
            }
        }
    }

    /// <summary>P751b — send the private <c>"Watch your fire"</c> drone line to
    /// <paramref name="launcher"/> when it is a player ship. Native routes through
    /// <c>SendChat(ship, CHAT_INDIVIDUAL, launcher-&gt;GetObjectID(),
    /// droneWatchFireSound, "Watch your fire")</c> (FedSrv.CPP:12135-12138,
    /// 12161-12168). The sender is the victim ship.</summary>
    private void SendWatchYourFire(Ship? launcher, Ship victim)
    {
        if (launcher is null) return;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, launcher)) continue;
            var msg = new ChatMessage
            {
                Message = "Watch your fire",
                SidSender = victim.Id,
                ChatTarget = (int)ChatTarget.Individual,
                OidRecipient = launcher.Id,
                VoiceOver = DroneWatchFireSound,
            };
            SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
            Console.WriteLine($"[shipdmg] 'Watch your fire' -> ship {launcher.Id} [sender={victim.Id}]");
            break;
        }
    }

    /// <summary>P751b — send the team <c>"Under attack by ... in ..."</c> alert
    /// for an NPC ship. Mirrors <c>SendChatf(ship, CHAT_TEAM,
    /// ship-&gt;GetSide()-&gt;GetObjectID(), underAttackSound,
    /// "Under attack by %s%s in %s", strAllies, GetModelName(launcher),
    /// ship-&gt;GetCluster()-&gt;GetName())</c> (FedSrv.CPP:12120-12130). The sender is
    /// the victim ship; the "(Ally)" prefix is included for allied launchers.</summary>
    private void SendNpcUnderAttackAlert(Ship ship, Model launcher, short launcherSide, bool allied, Mission mission, short pside)
    {
        short voice;
        switch (ship.PilotType)
        {
            case Ship.PilotTypeCarrier:
                voice = CarrierUnderAttackSound;
                break;
            case Ship.PilotTypeMiner:
                voice = ship.Fraction < 0.5f ? MinerCriticalSound : MinerUnderAttackSound;
                break;
            case Ship.PilotTypeBuilder:
                // Native reads the constructor station type's under-attack sound
                // via GetBaseData()->GetConstructorUnderAttackSound() (FedSrv.CPP:12124).
                voice = ship.BuilderStationType is { } bst
                    ? bst.ConstructorUnderAttackSound
                    : DroneUnderAttackSound;
                break;
            default:
                voice = DroneUnderAttackSound;
                break;
        }

        string launcherName = LauncherDisplayName(launcher);
        string sectorName = ship.Sector?.Name ?? "?";
        string allyPrefix = allied ? "(Ally) " : "";
        string text = $"Under attack by {allyPrefix}{launcherName} in {sectorName}";

        var msg = new ChatMessage
        {
            Message = text,
            SidSender = ship.Id,
            ChatTarget = (int)ChatTarget.Team,
            VoiceOver = voice,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId != pside) continue;
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[shipdmg] '{text}' (voice {voice}) -> side {pside} [{n} peer(s)] [under attack]");
    }

    /// <summary>P751b — produce a chat label for any launcher model. Ships use the
    /// peer name like native <c>GetModelName</c>; non-ship launchers fall back to
    /// a type+id label so probes and station turrets can still trigger the alert
    /// (FedSrv.CPP:12095 allows any model launcher).</summary>
    private string LauncherDisplayName(Model launcher)
        => launcher switch
        {
            Ship s => PeerName(s) ?? $"Ship {s.Id}",
            Probe p => $"Probe {p.Id}",
            Mine m => $"Mine {m.Id}",
            Missile mi => $"Missile {mi.Id}",
            Treasure t => $"Treasure {t.Id}",
            EquipmentTarget et => et.ObjectType switch
            {
                IgcObjectType.Station => $"Station {et.Id}",
                IgcObjectType.Warp => $"Warp {et.Id}",
                IgcObjectType.Asteroid => $"Asteroid {et.Id}",
                _ => $"Model {et.Id}",
            },
            _ => $"Model {launcher.Id}",
        };

    // Native fsmission.cpp:4783-4850: eject real pilots and relocate ghosts.
    // Capture calls this after its victory check, before the captor docks
    // (fsship.cpp:686-695); destruction uses the same operation.
    private void VacateStation(Station station)
    {
        foreach (var kv in _peerShips.ToArray())
        {
            if (kv.Value.Ship.StationId != station.StationId || !ReferenceEquals(kv.Value.Ship.Mission, station.Mission)) continue;
            if (IsGhost(kv.Value))
            {
                var ghostMission = kv.Value.Ship.Mission;
                var ghostSide = kv.Value.Ship.SideId;
                var altBase = ghostMission is { } gm && ghostSide is { } gs
                    ? FindNearestRestartStation(gm, gs, station.ClusterId, station.Center)
                    : null;
                if (altBase is { } dest)
                {
                    var destSector = ghostMission!.Sectors.First(s => s.Id == dest.ClusterId);
                    kv.Value.Ship.StationId = dest.StationId;                       // pship->SetStation(ps), fsmission.cpp:4846
                    kv.Value.Ship.LastDockTime = _simNow;
                    _peerShips[kv.Key] = _peerShips[kv.Key] with { Sector = destSector };  // status frames follow the new dock
                    // Acknowledge the relocation like the manual teleport path does
                    // (FM_S_TELEPORT_ACK, FedSrv.CPP:7054-7131): native VacateStation
                    // sends no message (the docked SHIP_STATUS carries the new
                    // station), but the port's own-ship status latch benefits from
                    // the explicit ack. Port adaptation, stated plainly.
                    SendPeer(kv.Key, FedPacket.Pack(new[] { new TeleportAck { StationId = dest.StationId, BNewHull = false }.Encode() }));
                    Console.WriteLine($"[station] ghost ship {kv.Value.Ship.Id} relocated dock {station.StationId} (destroyed) -> restart base {dest.StationId} '{dest.Name}' (fsmission.cpp:4805-4849)");
                }
                else
                {
                    Console.WriteLine($"[station] ghost ship {kv.Value.Ship.Id} keeps its destroyed station {station.StationId} — no alternate restart base; side elimination will collect it (fsmission.cpp:4789)");
                }
                continue;
            }
            PerformUndock(kv.Key, kv.Value);
        }
    }

    /// <summary>
    /// Handle a captured station (<see cref="ServerHost.CaptureStationEvent"/>,,
    /// raised from <c>Model.Sector.ResolveDockCollisions</c> on the sim thread
    /// when a hostile ship with a <c>c_habmBoard</c> hull docks an enemy base,
    /// P126) by broadcasting a real <c>FM_S_STATION_CAPTURE(58)</c>
    /// (<see cref="Wire.StationCapture"/>, <c>Messages.h:429-434</c>) with
    /// <c>stationID</c> from the station, <c>sidOld</c> = the station's OLD
    /// owner (still current here because the sector raises this seam BEFORE
    /// re-siding), <c>sidNew</c> = the capturing ship's side, and
    /// <c>shipIDCredit</c> = the capturing ship's id — the native field order of
    /// <c>CFSShip::CaptureStation</c> (<c>fsship.cpp:625-628</c>).
    /// <para><b>WHOLE MISSION.</b> Native sends <c>STATION_CAPTURE</c> to
    /// <c>GetMission()-&gt;GetGroupRealSides()</c> (<c>fsship.cpp:629</c>) — all
    /// real sides, the same audience as <see cref="BroadcastStationDestroyed"/>'s
    /// STATION_DESTROYED and P102's ASTEROID_DESTROYED. Do not &quot;fix&quot; it
    /// to same-side.</para>
    /// <para>After re-siding and the victory check, a non-winning capture
    /// vacates the old active owner's docked ships (fsship.cpp:686-695,
    /// fsmission.cpp:4783-4850). The captor docks afterwards. This callback only
    /// broadcasts the pre-reside ownership change and updates capture scores;
    /// OnVictoryByStationsComputed performs that post-check vacate.</para>
    /// </summary>
    private void BroadcastStationCapture(Ship ship, Station station)
    {
        // P583 — the capturing pilot's base capture (native CaptureBase(true),
        // fsship.cpp:600, inside CFSShip::CaptureStation). Child +0.5 credits
        // N/A (no child ships). Only a live peer is credited.
        if (LiveOf(ship)?.PlayerScore is { } capScore)
        {
            capScore.CaptureBase(true);
            Console.WriteLine($"[scoring] ship {ship.Id} base capture +1 (player peer)");
        }

        // P752 — the CAPTURING ship's side base-capture counter (native
        // pside->AddBaseCapture(), fsship.cpp:617), feeding the game-over side
        // table's cBaseCaptures column (fsmission.cpp:3078).
        if (ship.SideId is { } bcSideId
            && station.Mission?.Sides.FirstOrDefault(s => s.Id == bcSideId) is { } bcSide)
            bcSide.BaseCaptures++;

        var msg = new StationCapture
        {
            StationId = station.StationId,
            SidOld = station.SideId,             // the OLD owner (seam raised pre-reside)
            SidNew = ship.SideId!.Value,         // the capturing side (ship is sided to dock)
            ShipIdCredit = ship.Id,              // the capturing ship (fsship.cpp:628)
        };

        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        var note = $"[station] station {station.StationId} captured: side {msg.SidOld} -> {msg.SidNew} " +
                   $"by ship {msg.ShipIdCredit} => STATION_CAPTURE(58) -> {targets.Length} peer(s) [WHOLE MISSION]";
        Console.WriteLine(note);
        Notify(note);                   // spectator event feed (same text)

        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// P191 — the server half of the conquest/territory win conditions, driven
    /// by the <see cref="ServerHost.VictoryByStationsComputed"/> seam the mission
    /// raises after every station capture/kill recomputes
    /// (<see cref="Mission.RecomputeConquestAndTerritory"/>). Two responsibilities,
    /// matching native <c>CheckForVictoryByStationKill</c>'s tail
    /// (<c>fsmission.cpp:3685-3697</c> then the <c>GameOver</c> at the capture /
    /// kill call sites, <c>fsship.cpp:688-691</c> / <c>FedSrv.CPP:12345</c>):
    /// <list type="number">
    /// <item><b>GAME_STATE(155) whole-mission broadcast</b>, sent once per recompute
    /// and ONLY when at least one side's <c>ConquestPercent</c>/<c>TerritoryCount</c>
    /// actually changed (the native <c>bChange</c> gate, <c>fsmission.cpp:3685</c>),
    /// scoped to THIS mission's peers. <c>nFlags</c>/<c>nArtifacts</c> are always
    /// all-zero (native never sets them — see the <see cref="Wire.GameState"/> codec
    /// remarks).</item>
    /// <item><b>Win declaration via the P178 path</b>: when the recompute produced
    /// a winner, <see cref="DeclareGameOver"/> flips the mission Over and broadcasts
    /// GAME_OVER(62) — the single shared game-over mechanism, never a second one.
    /// Guarded against an already-Over mission (defensive; the recompute's own
    /// stage gate already prevents a post-win recompute).</item>
    /// </list>
    /// Runs on the sim thread (same one that owns <c>_peerShips</c>), so no extra
    /// locking.
    /// </summary>
    private void OnVictoryByStationsComputed(Mission mission, bool changed, Side? winner, StationVictory? victory = null)
    {
        if (changed)
        {
            var msg = GameState.ForMission(mission);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

            // Whole-mission audience, scoped to THIS mission.
            var targets = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
                .Select(kv => kv.Key)
                .ToArray();

            var note = $"[gamestate] mission #{_missions.IndexOf(mission)} -> GAME_STATE(155) " +
                       $"conquest[{string.Join(",", msg.Conquest)}] territory[{string.Join(",", msg.Territory)}] " +
                       $"(nFlags/nArtifacts all 0) -> {targets.Length} peer(s) [WHOLE MISSION]";
            Console.WriteLine(note);
            Notify(note);

            foreach (var t in targets)
                SendPeer(t, packet);
        }

        if (winner is null && victory is { Captured: true } capture)
        {
            // Native CheckForVictoryByStationKill deactivates a side with no
            // restart base before CaptureStation tests its old active flag
            // (fsmission.cpp:3698-3715; fsship.cpp:693-695).
            OnSideLostRestartStation(mission, capture.OldSide);
            if (mission.Sides.Any(side => side.Id == capture.OldSide && side.Active))
                VacateStation(capture.Station);
        }

        if (winner is not null && mission.Stage != MissionStage.Over)
        {
            string winnerName = winner.Name ?? $"side{winner.Id}";
            DeclareGameOver(mission, winner.Id,
                victory is null ? $"{winnerName} won by achieving the conquest/territory victory objective."
                    : mission.StationVictoryReason(winner, victory.Station, victory.Launcher,
                        victory.Launcher is Ship actor ? PeerName(actor) : null, victory.Captured, victory.Built));
        }
    }

    /// <summary>
    /// P302 — the server half of the prosperity/artifact win conditions, driven
    /// by the <see cref="ServerHost.ProsperityArtifactComputed"/> seam the
    /// world mission raises after a team-money development completes
    /// (<see cref="CompleteDevelopment"/>) or an artifact is secured
    /// (<see cref="OnShipDocked"/>), matching native's
    /// <c>FedSrvSiteBase::DevelopmentCompleted</c> win (FedSrv.CPP:11274-11279)
    /// and the artifact leg of <c>CheckForVictoryByFlags</c>
    /// (fsmission.cpp:3888-3894). It mirrors <see cref="OnVictoryByStationsComputed"/>
    /// exactly: on a change it broadcasts a whole-mission GAME_STATE(155) (its
    /// <c>nFlags</c>/<c>nArtifacts</c> arrays carry the real counters), and on
    /// a winner it declares the mission over via the single shared P178
    /// <see cref="DeclareGameOver"/> path. Runs on the sim thread (same one
    /// that owns <c>_peerShips</c>), so no extra locking.
    /// </summary>
    private void OnProsperityArtifactComputed(Mission mission, bool changed, Side? winner)
    {
        if (changed)
        {
            var msg = GameState.ForMission(mission);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

            // Whole-mission audience, scoped to THIS mission (P191 idiom).
            var targets = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
                .Select(kv => kv.Key)
                .ToArray();

            var note = $"[gamestate] mission #{_missions.IndexOf(mission)} -> GAME_STATE(155) " +
                       $"nFlags[{string.Join(",", msg.NFlags)}] nArtifacts[{string.Join(",", msg.NArtifacts)}] " +
                       $"(conquest/territory unchanged) -> {targets.Length} peer(s) [WHOLE MISSION]";
            Console.WriteLine(note);
            Notify(note);

            foreach (var t in targets)
                SendPeer(t, packet);
        }

        if (winner is not null && mission.Stage != MissionStage.Over)
        {
            string winnerName = winner.Name ?? $"side{winner.Id}";
            DeclareGameOver(mission, winner.Id,
                $"{winnerName} won by achieving the prosperity/artifact victory objective.");
        }
    }

    /// <summary>
    /// P303 — the server half of the flags/kills win conditions, driven by the
    /// <see cref="ServerHost.FlagsKillsComputed"/> seam the world mission raises
    /// after a captured enemy flag is secured (<see cref="OnShipDocked"/>) or a
    /// side earns a credited kill (<see cref="BroadcastKillShip"/>), matching
    /// native's flag leg of <c>CheckForVictoryByFlags</c> (fsmission.cpp:3883-3887,
    /// FedSrv.CPP:13149) and <c>CheckForVictoryByKills</c> (fsmission.cpp:3749-3760,
    /// FedSrv.CPP:11872). It mirrors <see cref="OnProsperityArtifactComputed"/>
    /// exactly: on a change it broadcasts a whole-mission GAME_STATE(155) (its
    /// <c>nFlags</c> array carries the real counters; native GAME_STATE has no
    /// kills field, so a kills-win change still broadcasts the current
    /// flags/artifacts verbatim), and on a winner it declares the mission over
    /// via the single shared P178 <see cref="DeclareGameOver"/> path. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.
    /// </summary>
    private void OnFlagsKillsComputed(Mission mission, bool changed, Side? winner)
    {
        if (changed)
        {
            var msg = GameState.ForMission(mission);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

            // Whole-mission audience, scoped to THIS mission (P191 idiom).
            var targets = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
                .Select(kv => kv.Key)
                .ToArray();

            var note = $"[gamestate] mission #{_missions.IndexOf(mission)} -> GAME_STATE(155) " +
                       $"nFlags[{string.Join(",", msg.NFlags)}] nArtifacts[{string.Join(",", msg.NArtifacts)}] " +
                       $"(conquest/territory unchanged) -> {targets.Length} peer(s) [WHOLE MISSION]";
            Console.WriteLine(note);
            Notify(note);

            foreach (var t in targets)
                SendPeer(t, packet);
        }

        if (winner is not null && mission.Stage != MissionStage.Over)
        {
            string winnerName = winner.Name ?? $"side{winner.Id}";
            DeclareGameOver(mission, winner.Id,
                $"{winnerName} won by achieving the flags/kills victory objective.");
        }
    }

    /// <summary>
    /// P583 — classify a killed ship into the native <c>KillShip</c> victim
    /// class bucketing (<c>igc.h:5675-5706</c>, switched on
    /// <c>pship-&gt;GetPilotType()</c>). The port has no per-ship pilot-type
    /// attribute (native assigns it at ship creation, shipIGC.cpp:103), so it
    /// proxies the class from the hull's ability bits — the same capability
    /// basis the port already uses to recognise Builder/Miner hulls elsewhere
    /// (P285). Carrier → <see cref="PlayerScore.VictimClass.Carrier"/>;
    /// mining → <see cref="PlayerScore.VictimClass.Miner"/>; builder →
    /// <see cref="PlayerScore.VictimClass.Builder"/>; everything else (fighters,
    /// scouts, interceptors, bombers, HTTs — and LAYERS, which have no
    /// distinguishing ability bit in native either) → Player. The Layer→Player
    /// collapse is immaterial to the score: both pay <c>c_pointsLayer</c>=10 =
    /// <c>c_pointsPlayer</c>=10, only the displayed breakdown differs.
    /// </summary>
    private static PlayerScore.VictimClass ClassifyVictim(Ship victim)
    {
        if (victim.HullType.HasCapability(DataHullTypeIGC.HabmCarrier)) return PlayerScore.VictimClass.Carrier;
        if (victim.HullType.HasCapability(DataHullTypeIGC.HabmMiner)) return PlayerScore.VictimClass.Miner;
        if (victim.HullType.HasCapability(DataHullTypeIGC.HabmBuilder)) return PlayerScore.VictimClass.Builder;
        return PlayerScore.VictimClass.Player;
    }

    /// <summary>
    /// Handle a simulated ship death (<see cref="ServerHost.KillShipEvent"/>, raised
    /// from <c>Ship.ReceiveDamage</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_KILL_SHIP(105)</c> <see cref="KillShip"/> message to the peers that
    /// belong to the victim's mission. This is the live half of the (unit-tested) combat slice:
    /// the projectiles and collision pass already kill the ship in simulation; this
    /// tells sector peers about combat effects and the whole mission about actual
    /// deletion (native AnnounceExit, fsship.cpp:196-223). Runs on the sim thread (the site raises it inside the mission
    /// tick, same thread that owns <see cref="_peerShips"/> and broadcasts ship
    /// states), so no extra locking is needed.
    /// </summary>
    private void BroadcastKillShip(
        double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2)
    {
        using var scope = TickProfiler.Enter("combat.shipEvents");
        // Resolve the victim and (if it is a ship) the credited attacker to ids.
        if (ship is not Ship victim)
        {
            Console.WriteLine("[combat] KillShipEvent with a non-Ship victim — ignored");
            return;
        }
        if (victim.HullType.HasCapability(DataHullTypeIGC.HabmLifepod))
            _podTrips.End(victim, now, victim.LifepodTimeToDie is { } deadline && now > deadline
                && launcher is null ? "oxygen" : "death");
        // Native keeps the credited model's identity even when no kill is awarded
        // (FedSrv.CPP:11803-11809,11908-11912). Turrets are Ships too.
        var (killerType, killerId, killerSideId) = launcher switch
        {
            Ship attacker => (IgcObjectType.Ship, attacker.Id, attacker.SideId),
            Station killerStation => (IgcObjectType.Station, killerStation.StationId, (short?)killerStation.SideId),
            Probe probe => (IgcObjectType.Probe, probe.Id, probe.SideId),
            Mine mine => (IgcObjectType.Mine, mine.Id, mine.SideId),
            _ => ((IgcObjectType)(-1), (short)-1, (short?)null),
        };

        // ── P411: dead-ship loot drops (FedSrv.CPP:11686-11762) ──
        // Native KillShipEvent runs its treasure block INSIDE the `pshipParent ==
        // NULL` root-ship guard (FedSrv.CPP:11644), BEFORE the bKilled/eject
        // decision (FedSrv.CPP:11770) — so a destroyed ROOT ship drops its
        // parts/ammo/fuel/flag loot regardless of whether the pilot subsequently
        // ejects or truly dies. A child/turret ship (parented) gets no independent
        // drop. The dying ship's full state (Parts/Ammo) is still intact here
        // (ReceiveDamage raises KillShipEvent before anything clears it).
        if (victim.ParentShip is null)
            DropKillShipLoot(victim, now);

        // ── P107: the bKilled gate (FedSrv.CPP:11770-11785) ──
        // First, the owning peer — the port's IsPlayer() proxy (native
        // FedSrv.CPP:11772 pfsShip->IsPlayer()). Hoisted above the kill
        // broadcasts where P105 originally resolved it, because the eject
        // gate needs it. Non-player (bot) ships have no owning peer.
        ConnectionId? owner = null;
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, victim)) { owner = kv.Key; break; }

        // bLifepod = (pshipParent == NULL) && baseHull.HasCapability(c_habmLifepod)
        //   → port simplifications: no boarded/child ships yet (cluster E), so the
        //     (pshipParent==NULL) conjunct drops; the "already a lifepod" half is
        //     a real capability test on the CURRENT base hull (bit c_habmLifepod =
        //     0x04, igc.h:728; helper DataHullTypeIGC.HasCapability).
        // bKilled = (!bEjectPods) || bLifepod || (amount < 0.0f)
        //   → bEjectPods is always enabled (MissionParams not decoded in this port —
        //     stated plainly in IN_SPACE_EJECT_P107.md); no caller passes a negative
        //     amount into KillShipEvent (combat amounts are > 0, suicide = 1.0; the
        //     negative-amount repair path in Ship.ReceiveDamage never reaches the
        //     death event — it repairs and returns c_drNoDamage).
        //   → a non-player (bot) ship is always bKilled (native else branch,
        //     FedSrv.CPP:11781-11785).
        bool bLifepod = victim.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        bool bKilled = (owner is null) || bLifepod || amount < 0.0f;
        // Preserve the port's missing-eject-data death fallback, deciding it
        // before KILL_SHIP so its death flag and scoring stay consistent.
        if (!bKilled && (victim.Sector is null || victim.Mission is not { } ejectMission
            || ejectMission.Sides.FirstOrDefault(s => s.Id == victim.SideId) is not { } ejectSide
            || !ejectMission.Core.HullTypes.ContainsKey(ejectSide.Civilization.Lifepod)))
            bKilled = true;

        // ── P278: reset the dying ship's own Kill Bonus on a REAL death ──
        // Native FedSrv.CPP:11783-11787: `if (bKilled) pship->SetExperience(0.0f)`.
        // AFS's "if your own pod is destroyed" ⇔ bKilled here: under this
        // port's always-enabled eject pods, a player ship only dies for real
        // when it IS a lifepod (or amount<0, unreachable in combat) — so
        // destroying the pilot's eject pod resets the streak to 0, exactly the
        // AFS trigger. The !bKilled eject branch (below) deliberately does NOT
        // reset it (an ejection is not a lost streak).
        if (bKilled)
            victim.ResetKillBonus();

        // P746 — award credit BEFORE changing the victim into a pod. Native's
        // amount != 0 block includes KILL_SHIP, AddKill, rating, experience,
        // partial kills and the kills win check (FedSrv.CPP:11790-11915), then
        // branches to AddEjection or AddDeath (:11918-11921,12026-12032).
        // Keep the port's single-killer class credit (no shared damage buckets
        // here) and peer-only rating storage; neither depends on bKilled.
        var killMission = victim.Mission;
        var killerSide = killMission?.Sides.FirstOrDefault(s => s.Id == killerSideId);
        bool killCredit = amount != 0 && !bLifepod && killerSide is not null
            && killerSideId != victim.SideId;

        // ── P752: the game-over assist credit ──
        // Native FedSrvSiteBase::KillShipEvent walks the victim's damage track
        // AFTER the top (kill-credit) bucket and credits the FIRST enemy,
        // non-allied, player-piloted ship still in the mission with an assist
        // (FedSrv.CPP:11637-11670: `pfsAssist->GetPlayerScoreObject()->AddAssist();
        // break;`). Non-ship/allied/own-side/NPC buckets are skipped, and the
        // walk stops at the first player hit — exactly one assist per kill.
        if (killMission is { } assistMission && victim.DamageTrack is { } track)
        {
            foreach (var dealer in track.DealersAfterTop)
            {
                if (dealer is not Ship assistShip
                    || !ReferenceEquals(assistShip.Mission, assistMission)
                    || assistShip.SideId == victim.SideId
                    || AlliedSides(assistMission, victim.SideId, assistShip.SideId))
                    continue;
                ConnectionId? assistOwner = null;
                foreach (var kv in _peerShips)
                    if (ReferenceEquals(kv.Value.Ship, assistShip)) { assistOwner = kv.Key; break; }
                if (assistOwner is null)
                    continue;   // native gates on pfsAssist->IsPlayer()
                _peerShips[assistOwner.Value].PlayerScore.AddAssist();
                Console.WriteLine($"[scoring] assist ship {assistShip.Id} (peer) +1 (second-highest enemy damage on victim {victim.Id})");
                break;
            }
        }
        Side? killsWinner = null;
        bool killsChanged = false;
        if (killCredit)
        {
            if (launcher is Ship attackingShip)
            {
                ConnectionId? killerOwner = null;
                foreach (var kv in _peerShips)
                    if (ReferenceEquals(kv.Value.Ship, attackingShip)) { killerOwner = kv.Key; break; }

                if (killerOwner is not null)
                {
                    var killerScore = _peerShips[killerOwner.Value].PlayerScore;
                    killerScore.AddKill();
                    Console.WriteLine($"[scoring] killer ship {attackingShip.Id} kill +1 (player peer)");
                    if (owner is not null && killMission!.Core.Constants.FloatConstants is { } fc
                        && fc.Length > PlayerScore.CombatRatingDivideIndex)
                    {
                        PlayerScore.AdjustCombatRating(
                            fc[PlayerScore.CombatRatingAddIndex], fc[PlayerScore.CombatRatingDivideIndex],
                            killerScore, _peerShips[owner.Value].PlayerScore);
                        Console.WriteLine("[scoring] combat rating adjusted on both sides");
                    }

                    // Native partial-kill shares (FedSrv.CPP:11826-11861) are
                    // awarded below, for every enemy bucket — the killer's own
                    // share included — not a flat 1.0 here.
                }

                // P846 — native's partial-kill loop: after the credited ship's
                // AddKill/rating, EVERY enemy ship bucket of the victim's damage
                // track earns `KillShip(pship, bucketTotal/totalDamage)`
                // (FedSrv.CPP:11841-11861), so a shared kill splits the class
                // counter by damage share. Pre-P846 the port credited only the
                // top dealer with a flat 1.0 and left the helper dealers at 0.
                AwardPartialKillShares(victim, killMission!);

                // Experience is ship-owned; allied kills do not raise it
                // (FedSrv.CPP:11823-11824), even though they earn AddKill.
                if (!AlliedSides(killMission!, attackingShip.SideId, victim.SideId))
                    attackingShip.AddKillBonus();
            }

            // Ship AddKill cascades to its side in native (fsship.h:44-48,
            // Igc/shipIGC.h:1778-1782). Non-ships call side AddKill directly
            // (FedSrv.CPP:11867-11869). Increment exactly once for either path.
            killerSide!.Kills++;
            // Real kill-credit observability (training host M6 kill goals, TR-4): raised after the credit, so a
            // subscriber sees exactly the kills the side counter records (never an ejected lifepod kill).
            KillCreditGranted?.Invoke(launcher is Ship creditShip ? creditShip.Id : -1, victim.Id);
            if (killMission!.KillsEnabled)
            {
                (killsWinner, killsChanged) = killMission.RecomputeFlagsAndKills();
            }
        }

        // Native sends one KILL_SHIP to GetGroupMission for every nonzero
        // event, including ejections (FedSrv.CPP:11791-11798,11914-11915).
        // Effects/EJECT remain sector-scoped; ship deletion is death-only.
        if (amount != 0)
        {
            var msg = new KillShip
            {
                ShipId = victim.Id, TypeCredit = (short)killerType, IdCredit = killerId,
                SideIdKiller = killerSideId ?? -1,
                BKillCredit = killCredit, BDeathCredit = bKilled,
            };
            var killTargets = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, victim.Mission))
                .Select(kv => kv.Key).ToArray();
            byte[] packet = FedPacket.Pack(msg.Encode());
            foreach (var t in killTargets) SendPeer(t, packet);
            Console.WriteLine($"[combat] ship {victim.Id} — KILL_SHIP(105) bKillCredit={killCredit} bDeathCredit={bKilled} -> {killTargets.Length} mission peer(s)");
        }

        // Native checks kills victory before eject/death (FedSrv.CPP:11872-11880),
        // but processes game-over later (fsmission.cpp:2691-2695,2508-2511). The port's
        // callback immediately rotates the mission and migrates peers. Publish
        // only after this event's wire messages and victim score are complete.
        try
        {
            if (!bKilled)
            {
                _peerShips[owner!.Value].PlayerScore.AddEjection();
                Console.WriteLine($"[scoring] ship {victim.Id} ejection +1 (player peer)");
                // P752 — the victim's SIDE ejection counter (native
                // pside->AddEjection(), sideigc.h:479-483), feeding the game-over
                // side table's cEjections column (fsmission.cpp:3076).
                if (victim.Mission is { } ejMission && victim.SideId is { } ejSideId
                    && ejMission.Sides.FirstOrDefault(s => s.Id == ejSideId) is { } ejSide)
                    ejSide.Ejections++;
                if (EjectSurvivor(victim, launcher, p1, p2))
                    return;
            }

            // True death (including the existing missing-eject-data fallback) only.
            // Native AddDeath follows AnnounceExit (FedSrv.CPP:12025-12032).
            if (owner is not null)
            {
                _peerShips[owner.Value].PlayerScore.AddDeath();
                Console.WriteLine($"[scoring] victim ship {victim.Id} death +1 (player peer)");
            }
            if (victim.Mission is { } deathMission && victim.SideId is { } victimSideId)
            {
                var victimSide = deathMission.Sides.FirstOrDefault(s => s.Id == victimSideId);
                if (victimSide is not null)
                    victimSide.Deaths++;
            }

            // Peers whose ship is in the same sector as the victim can see the kill.
            var targets = _peerShips
                .Where(kv => kv.Value.Ship.Sector == victim.Sector)
                .Select(kv => kv.Key)
                .ToArray();

            Console.WriteLine($"[combat] ship {victim.Id} killed by {(killerId < 0 ? "(none)" : $"ship {killerId}")} " +
                              $"at sim-t {now:R} — death effects -> {targets.Length} sector peer(s)");
            // Native AnnounceExit sends true deletion to the mission, even when nobody is
            // in the victim's sector (fsship.cpp:196-223; FedSrv.CPP:12025). The sector-only
            // kill/effect audience must not leave an off-sector constructor alive in team state.
            var deleteTargets = _peerShips.Where(kv => ReferenceEquals(kv.Value.Ship.Mission, victim.Mission))
                .Select(kv => kv.Key).ToArray();
            int deleteReason = amount == 0 ? ShipDelete.SdrTerminate : ShipDelete.SdrKilled;
            var deletePacket = FedPacket.Pack(new ShipDelete { ShipId = victim.Id, Sdr = deleteReason }.Encode());
            if (targets.Length > 0)
            {
                // P263 — the ship's death blast VFX to the same same-sector audience.
                // Native picks the category by the victim base hull's fighter capability
                // on KILL_SHIP (appmsg.cpp:2853-2857: c_habmFighter ? c_etSmallShip :
                // c_etBigShip) — the exact split the port reproduces here. Scale is the
                // native 2 * GetRadius() = hull Length (shipRadius = 0.5*Length).
                int shipType = victim.HullType.HasCapability(DataHullTypeIGC.HabmFighter)
                    ? ExplosionType.SmallShip
                    : ExplosionType.BigShip;
                SendExplosionEvent(targets, shipType, victim.Sector!.Id, victim.Position, victim.Velocity, victim.HullType.Length);
            }
            foreach (var target in deleteTargets) SendPeer(target, deletePacket);
            Console.WriteLine($"[combat] ship {victim.Id} — SHIP_DELETE(20) {(deleteReason == ShipDelete.SdrKilled ? "SDR_KILLED" : "SDR_TERMINATE")} -> {deleteTargets.Length} mission peer(s)");
            // Finalization is simulation work, independent of whether anyone witnessed this sector.
            // In particular, a quiet layer consumption must remove the live unit (FedSrv.CPP:13842-13860).

            // P105 death-respawn, hooked at the exact seam P103 wired (ShipKilled →
            // this method) and sharing the same trigger. The native function
            // (FedSrv.CPP:12026-12074) turns a player's true death into a docked
            // Lifepod at a friendly station plus a private ENTER_LIFEPOD(166) to
            // that player only; bots just stay dead (native else:
            // FedSrv.CPP:12080-12085). Three guards reproduce it faithfully:
            //  (1) owner — only a peer-owned ship is a player (this port's player/
            //      bot distinction IS peer ownership; a ship with no owning peer in
            //      _peerShips is a bot/unowned ship and is NOT respawned);
            //  (2) a friendly station anywhere (FindFriendlyStation — the simplified
            //      stand-in for native's nearest-search + GetBase fallback);
            //  (3) implicit — FindFriendlyStation returns null when SideId is null,
            //      so a side-less ship is never respawned either.
            // No friendly station ⇒ no respawn, exactly like native's
            // `if (pstationDest)` guard (FedSrv.CPP:12055): the ship simply stays
            // gone, whatever SHIP_DELETE already said. (`owner` was resolved above,
            // before the P107 eject gate; only the guard remains here.)
            if (owner is null)
            {
                Console.WriteLine($"[lifepod] ship {victim.Id} has no owning peer (bot/non-player) — no respawn, stays dead");
                FinalizeDeadShip(victim);   // P323 M6 — really leave the sector & mark dead (native deletes the drone, FedSrv.CPP:12084)
                // HUD-20/P775 — the drone is gone for good (native deletes it,
                // FedSrv.CPP:12084): free its one-shot PLAYERINFO latch so the
                // set does not grow with every dead drone and a later ship that
                // reuses the id (AllocateShipId wraps) is announced afresh.
                if (victim.IsNpc) _playerInfoSent.RemoveWhere(e => e.ShipId == victim.Id);
                return;
            }

            // P455 — an eliminated (inactive) side cannot respawn. Native's lifepod
            // respawn needs a restart-capable friendly station (the FindTarget at
            // FedSrv.CPP:12034-12038 carries c_sabmRestart); a deactivated side has
            // none (DeactivateSide runs exactly when it lost its last restart
            // station and sets Side.Active=false FIRST, fsmission.cpp:5325), so its
            // force-killed players stay dead instead of respawning as lifepods. Gate
            // on Side.Active so eliminated players see exactly what native shows.
            if (victim.Mission is { } killMission2 && victim.SideId is { } victimSide2 &&
                killMission2.Sides.FirstOrDefault(s => s.Id == victimSide2) is { Active: false })
            {
                Console.WriteLine($"[lifepod] ship {victim.Id} is on an inactive (eliminated) side — no respawn, stays dead");
                FinalizeDeadShip(victim);
                return;
            }

            DataStationIGC? station = FindFriendlyStation(victim);
            if (station is null)
            {
                Console.WriteLine($"[lifepod] player ship {victim.Id} side {(victim.SideId?.ToString() ?? "(none)")} has no friendly station anywhere — no respawn (native FedSrv.CPP:12055 guard)");
                FinalizeDeadShip(victim);   // P323 M6 — the ghost-ship fix: remove the dead ship server-side (it used to keep flying/mining/broadcasting)
                return;
            }
            var dest = station.Value;                             // non-null capture past the guard
            var mission = victim.Mission!;
            // A valid station's ClusterId always names a sector of this mission
            // (DataStationIGC decoding guarantees it) — the station's own cluster
            // is where the Lifepod docks (native SetCluster(pcluster)).
            var destSector = mission.Sectors.First(s => s.Id == dest.ClusterId);
            var side = mission.Sides.FirstOrDefault(s => s.Id == victim.SideId!.Value);
            if (side is null)
            {
                Console.WriteLine($"[lifepod] player ship {victim.Id} side {victim.SideId} not in mission sides — no respawn");
                FinalizeDeadShip(victim);   // P323 M6 — same ghost-ship finalization as the no-station branch
                return;
            }
            if (!mission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var lp))
            {
                Console.WriteLine($"[lifepod] player ship {victim.Id} side {victim.SideId}: lifepod hull {side.Civilization.Lifepod} not in core — no respawn");
                FinalizeDeadShip(victim);   // P323 M6 — same ghost-ship finalization as the no-station branch
                return;
            }

            MoveShipToSector(victim, destSector, announceExit: false);   // P307: SetCluster — a REAL list move out of the old sector into the station's cluster (FedSrv.CPP:12045→12067); dock-bound → no flying-exit announce
            victim.ChangeHull(lp);           // SetBaseHullType(pht) — side's Lifepod hull (FedSrv.CPP:12052)
            // P653d: native SetBaseHullType restores m_fraction to 1 even when
            // assigning the same pod hull (shipIGC.cpp:1352-1359,1435). ChangeHull
            // is a hull-only primitive in this port, so restore the restart here,
            // just as EjectSurvivor does. Otherwise lethal damage leaves a docked
            // pod Dead and every subsequent purchase/boarding/teleport is refused.
            victim.SetFraction(1.0f);
            victim.StationId = dest.StationId; // SetStation + ShipStatusDocked (FedSrv.CPP:12064-12065)
            victim.LastDockTime = _simNow;     // P381: dock clock (lifepod respawn is a dock latch)

            // Keep the owning peer's broadcast bookkeeping coherent: the status
            // frames (BroadcastShipStates / MakeOwnShipStatus) derive Sector from
            // the LiveShip snapshot, so point it at the station's cluster too —
            // the next SHIP_STATUS(90) shows the Lifepod docked there.
            var conn = owner.Value;                               // non-null capture past the guard
            _peerShips[conn] = _peerShips[conn] with { Sector = destSector };

            var enter = EnterLifepod.ForPlayer(victim.Id);
            SendPeer(conn, FedPacket.Pack(new[] { enter.Encode() }));   // FM_S_ENTER_LIFEPOD(166) → own peer only
            Console.WriteLine($"[lifepod] player ship {victim.Id} -> docked Lifepod (hull {lp.HullId}) at station {dest.StationId} '{dest.Name}' " +
                              $"in sector '{destSector?.Name ?? "?"}' ; ENTER_LIFEPOD(166) -> own peer only");

            // P799 — native ghost elimination (FedSrv.CPP:12068-12072): right after
            // the (possibly ghost) player's lifepod restart, `if (pship->IsGhost()
            // && !m_pfsMission->HasPlayers(pside, false)) m_pfsMission->
            // DeactivateSide(pside);` — a side whose every connected player is a
            // ghost (all out of lives) is eliminated and falls through native's
            // game-over path (SIDE_INACTIVE + "is no more" chat + the
            // win-by-elimination check, i.e. this port's P455 DeactivateSide flow,
            // which may declare the last-side-standing winner or a side-only game
            // over for the eliminated side). Unlimited lives never ghosts, so this
            // is dead code for pre-P799 configs.
            if (IsGhost(_peerShips[conn]) && !SideHasNonGhostPlayers(mission, side.Id))
            {
                Console.WriteLine($"[lives] ship {victim.Id} is a ghost ({_peerShips[conn].PlayerScore.Deaths} deaths > lives {mission.Lives}) and side {side.Id} has no non-ghost players left — deactivating side (FedSrv.CPP:12070)");
                DeactivateSide(mission, side);
            }
        }
        finally
        {
            if (killsChanged || killsWinner is not null)
                killMission!.Site.FlagsKillsComputed(killMission, killsChanged, killsWinner);
        }
    }

    /// <summary>
    /// P846 — native's partial-kill share credit (<c>FedSrv.CPP:11826-11861</c>).
    /// Once a ship dies, native walks EVERY bucket of the victim's damage track
    /// (not only the credit bucket) and calls
    /// <c>pfsShip-&gt;GetPlayerScoreObject()-&gt;KillShip(pship, bucketTotal / totalDamage)</c>
    /// for each enemy ship in the same mission, where <c>totalDamage</c> is the
    /// sum of all buckets whose side differs from the victim's. The credited
    /// killer's own class counter therefore receives its <b>damage share</b>, not
    /// a flat +1, and every other enemy contributor receives theirs.
    /// <para>Port adaptations, stated plainly: (1) only <see cref="Ship"/> launchers
    /// are recorded in this port's track (<c>Ship.ReceiveDamage</c>, P549), so a
    /// bucket is always an <c>OT_ship</c> model — native's missile-launcher buckets
    /// (which suppress player credit entirely) are a native quirk this port does
    /// not reproduce; (2) score objects exist only for peers here, so a
    /// damage-dealing NPC has no scoreboard to credit (native's
    /// <c>GetPlayerScoreObject()</c> exists for drones but is never persisted);
    /// (3) native's parent-ship divisor branch
    /// (<c>pfsParent-&gt;...KillShip(pship, share/divisor)</c>) has no counterpart
    /// because this port has no child/turret ships.</para>
    /// </summary>
    private void AwardPartialKillShares(Ship victim, Mission mission)
    {
        if (victim.DamageTrack is not { } track || victim.SideId is not { } victimSide) return;
        var buckets = track.Buckets;
        float totalDamage = 0f;
        foreach (var (model, damage) in buckets)
            if (model is Ship dealer && dealer.SideId is { } dealerSide && dealerSide != victimSide)
                totalDamage += damage;
        if (totalDamage <= 0f) return;   // native's `if (totalDamage != 0.0f)` guard

        var victimClass = ClassifyVictim(victim);
        foreach (var (model, damage) in buckets)
        {
            if (model is not Ship dealer || dealer.SideId is not { } dealerSide) continue;
            if (!ReferenceEquals(dealer.Mission, mission)) continue;            // pmodel->GetMission() == pmission
            if (dealerSide == victimSide) continue;                             // pmodel->GetSide() != pside
            if (AlliedSides(mission, victimSide, dealerSide)) continue;        // #ALLY
            ConnectionId? owner = null;
            foreach (var kv in _peerShips)
                if (ReferenceEquals(kv.Value.Ship, dealer)) { owner = kv.Key; break; }
            if (owner is null) continue;                                       // no score object for NPCs
            float share = damage / totalDamage;
            _peerShips[owner.Value].PlayerScore.KillShip(victimClass, share);
            Console.WriteLine($"[scoring] partial-kill share {share:0.###} ({damage:0.#}/{totalDamage:0.#}) for ship {dealer.Id} (victim class {victimClass})");
        }
    }

    /// <summary>
    /// P411 — the dead-ship loot drop block, the port of the treasure section of
    /// native <c>KillShipEvent</c> (<c>FedSrv.CPP:11686-11762</c>). Called for a
    /// ROOT ship (guarded by the caller on <c>ParentShip is null</c>, matching
    /// native's <c>pshipParent == NULL</c> gate at <c>FedSrv.CPP:11644</c>).
    /// Rolls, in order: one 1-in-5 chance per mounted part, one 1-in-5 chance for
    /// the ship's remaining ammo (if any), one 1-in-5 chance for fuel (if any),
    /// then the carried flag's jettison/respawn. The dying ship's state is still
    /// intact (ReceiveDamage raised KillShipEvent before clearing anything).
    /// Runs on the sim thread (same as <see cref="BroadcastKillShip"/>).
    /// </summary>
    private void DropKillShipLoot(Ship victim, double now)
    {
        using var scope = TickProfiler.Enter("combat.treasure");
        var sector = victim.Sector;
        var mission = victim.Mission;
        if (sector is null || mission is null) return;
        uint time0 = (uint)(now * 1000);

        // (1) PARTS — 1-in-5 per still-mounted part (FedSrv.CPP:11686-11696):
        //     `if (randomInt(0,4) == 0) CreateTreasure(..., p, p->GetPartType(), p1, 100.0f, 60.0f)`
        //     then `p->Terminate()` either way. Snapshot the list (native walks a
        //     removal-safe first() loop); the dying ship's loadout is cleared by the
        //     death/respawn path (ChangeHull to Lifepod / FinalizeDeadShip), so no
        //     manual termination is needed here. dv=100.0f, lifespan=60.0f are the
        //     kill-ship call's arguments (FedSrv.CPP:11694) — passed through the
        //     reused CreateDropTreasure path unchanged.
        foreach (var part in victim.Parts.ToArray())
        {
            if (_rand.Next(5) == 0)   // randomInt(0,4)==0 — uniform pick in [0,4], p=1/5
            {
                var treasure = victim.CreateDropTreasure(part.Type, part.Amount, now, time0, dv: 100.0f, lifespan: 60.0f);
                SendTreasureSpawnSectorWide(sector, treasure);
                Console.WriteLine($"[loot] ship {victim.Id}: part {part.Type.PartId} amount {part.Amount} -> part treasure obj={treasure.Data.ObjectId} sector {sector.Id}");
            }
        }

        // (2) AMMO — 1-in-5 if ammo > 0 (FedSrv.CPP:11699-11713): the treasure's
        //     part type is the mission's ammo-pack (native `pmission->GetAmmoPack()`;
        //     the port's faithful stand-in is the core's c_packAmmo part type),
        //     amount = the ship's remaining ammo, dv=100.0f, lifespan=30.0f
        //     (FedSrv.CPP:11709); ammo is zeroed either way (FedSrv.CPP:11711).
        short ammo = victim.Ammo;
        if (ammo > 0)
        {
            DataPartTypeIGC? ammoPack = null;
            foreach (var p in mission.Core.PartTypes.Values)
                if (p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0)   // c_packAmmo (igc.h:549)
                {
                    ammoPack = p;
                    break;
                }
            if (ammoPack is { } ap && _rand.Next(5) == 0)
            {
                var treasure = victim.CreateDropTreasure(ap, ammo, now, time0, dv: 100.0f, lifespan: 30.0f);
                SendTreasureSpawnSectorWide(sector, treasure);
                Console.WriteLine($"[loot] ship {victim.Id}: ammo {ammo} -> ammo-pack treasure obj={treasure.Data.ObjectId} sector {sector.Id}");
            }
            victim.Ammo = 0;   // pship->SetAmmo(0), FedSrv.CPP:11711
        }

        // (3) FUEL — native FedSrv.CPP:11714-11728 mirrors ammo with
        //     `pmission->GetFuelPack()` and dv/lifespan 100/30, casting the
        //     ship's float fuel to short for the treasure amount, and zeroing
        //     the ship's fuel EITHER WAY (pship->SetFuel(0.0f),
        //     FedSrv.CPP:11727). P412 makes Ship.Fuel a real live pool, so the
        //     fuel > 0 guard can finally be true and a fuel treasure rolls with
        //     the same 1-in-5 odds as ammo.
        float fuel = victim.Fuel;
        if (fuel > 0)
        {
            DataPartTypeIGC? fuelPack = null;
            foreach (var p in mission.Core.PartTypes.Values)
                if (p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 1)   // c_packFuel (igc.h:550)
                {
                    fuelPack = p;
                    break;
                }
            if (fuelPack is { } fp && _rand.Next(5) == 0)
            {
                var treasure = victim.CreateDropTreasure(fp, (short)fuel, now, time0, dv: 100.0f, lifespan: 30.0f);
                SendTreasureSpawnSectorWide(sector, treasure);
                Console.WriteLine($"[loot] ship {victim.Id}: fuel {fuel:F1} -> fuel-pack treasure obj={treasure.Data.ObjectId} sector {sector.Id}");
            }
        }
        victim.SetFuel(0.0f);   // pship->SetFuel(0.0f), FedSrv.CPP:11727 (either way)

        // (4) FLAG — FedSrv.CPP:11730-11762: a carried flag is either respawned at
        //     its home base (if the death position is outside the universe radius) or
        //     dropped as a c_tcFlag treasure where the ship died; either way the
        //     carrier stops carrying it and a whole-mission GAIN_FLAG(NA, ship) fires.
        if (victim.Flag is { } sidFlag)
        {
            victim.Flag = null;   // pship->SetFlag(NA), FedSrv.CPP:11735

            if (IsOutsideUniverse(mission, victim.Position))
                RespawnCarriedFlag(mission, sidFlag);
            else
            {
                var ft = victim.CreateFlagTreasure(sidFlag, now, time0);
                SendTreasureSpawnSectorWide(sector, ft);
                Console.WriteLine($"[loot] ship {victim.Id}: jettisoned flag of side {sidFlag} as treasure obj={ft.Data.ObjectId} sector {sector.Id}");
            }

            // Native FedSrv.CPP:11755-11760: whole-mission announce the carrier no
            // longer holds the flag (sideidFlag = NA).
            BroadcastGainFlag(mission, GainFlag.NA, victim.Id, bIsTreasureDocked: false, parttypeidDocked: 0);
        }
    }

    /// <summary>
    /// P411 — the native universe-radius test (<c>FedSrv.CPP:11737-11743</c>):
    /// <c>p.x² + p.y² + p.z²/lm² &gt; (1.5·radiusUniverse)²</c> with
    /// <c>lm = FloatConstant(c_fcidLensMultiplier)</c> and <c>radius =
    /// FloatConstant(c_fcidRadiusUniverse)</c> (indices 0 and 1, igc.h:59-60).
    /// If the constants are missing/non-positive, defaults to <c>false</c> (inside
    /// the universe → the DROP leg), which is the practically-universal real-game
    /// outcome anyway — ships cannot leave the universe radius.
    /// </summary>
    private static bool IsOutsideUniverse(Mission mission, Vector3 p)
    {
        var fc = mission.Core.Constants.FloatConstants;
        if (fc is null || fc.Length <= 1) return false;
        float lm = fc[0];
        float radius = fc[1];
        if (lm <= 0f || radius <= 0f) return false;
        float r = 1.5f * radius;
        return p.X * p.X + p.Y * p.Y + p.Z * p.Z / (lm * lm) > r * r;
    }

    private void SendFlagSecureChat(Mission mission, short side, short cue, string text)
    {
        var message = new ChatMessage
        {
            SidSender = -1, ChatTarget = (int)ChatTarget.Team, OidRecipient = side,
            VoiceOver = cue, CommandID = OrderChange.CidNone, OtTarget = -1, OidTarget = -1, Message = text,
        };
        var packet = FedPacket.Pack([message.Encode()]);
        foreach (var (connection, peer) in _peerShips)
            if (ReferenceEquals(peer.Ship.Mission, mission) && peer.Ship.SideId == side) SendPeer(connection, packet);
    }

    /// <summary>
    /// P411 — respawn a jettisoned flag at its home base, the port of native
    /// <c>RespawnFlag</c>'s real-side leg (<c>FedSrv.CPP:13842-13944</c>) reached
    /// from the <c>KillShipEvent</c> flag branch when the death position is outside
    /// the universe. For a REAL side: pick a random eligible pedestal station of the
    /// flag's own side (capability <c>c_sabmPedestal</c>, with no flag of that side
    /// already within native's 300-unit "spitting distance", FedSrv.CPP:13883-13905)
    /// and spawn a c_tcFlag treasure above it (FedSrv.CPP:13921-13941). The
    /// SIDE_TEAMLOBBY artifact (-2) delegates to GenerateTreasure in a random
    /// neutral cluster (FedSrv.CPP:13847-13868).
    /// </summary>
    private void RespawnCarriedFlag(Mission mission, short sidFlag, Side? capturer = null)
    {
        if (sidFlag == Side.ArtifactSentinelFlag)   // SIDE_TEAMLOBBY = -2 (igc.h:514)
        {
            mission.RespawnArtifact(_simNow);
            return;
        }

        var candidates = new List<(Sector sector, DataStationIGC station, DataStationTypeIGC type)>();
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)   // static DataStationIGC records
            {
                if (station.SideId != sidFlag) continue;
                if (!mission.Core.StationTypes.TryGetValue(station.StationTypeId, out var stN)) continue;
                if (!stN.HasCapability(DataStationTypeIGC.SabmPedestal)) continue;   // c_sabmPedestal (igc.h:758)
                Vector3 center = station.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero;
                bool nearExistingFlag = sector.Models.OfType<Treasure>().Any(t =>
                    t.Code == TreasureCode.Flag && t.Data.TreasureId == sidFlag &&
                    (t.Position - center).LengthSquared() < 300.0f * 300.0f);          // FedSrv.CPP:13886-13897
                if (!nearExistingFlag)
                    candidates.Add((sector, station, stN));
            }

        if (candidates.Count == 0)
        {
            // Native FedSrv.CPP:13906-13920: a secure passes the capturer;
            // death/expiry passes NULL. No eligible pedestal after a capture wins.
            if (capturer is not null && mission.Stage != MissionStage.Over)
                DeclareGameOver(mission, capturer.Id,
                    $"{capturer.Name} wins. Team owning captured flag has no stations left.");
            Console.WriteLine($"[loot] flag {sidFlag}: no eligible pedestal station to respawn at — left unspawned");
            return;
        }

        var (sec, st, stype) = candidates[_rand.Next(candidates.Count)];
        var sp = st.Position;
        // p0 = station position with z raised by radius + c_fFlagOffset (FedSrv.CPP:13932-13933; igc.h:27).
        var p0 = new Vector3(sp[0], sp[1], sp[2] + stype.Radius + 50.0f);
        var data = new DataTreasureIGC(
            P0: [p0.X, p0.Y, p0.Z],
            V0: [0f, 0f, 0f],
            Lifespan: 10.0f * 24.0f * 3600.0f,           // 10 days (FedSrv.CPP:13927)
            Time0: (uint)(_simNow * 1000.0),
            ObjectId: mission.NextObjectId(),            // GenerateNewTreasureID() (FedSrv.CPP:13930)
            TreasureId: sidFlag,
            ClusterId: st.ClusterId,                     // pstation->GetCluster()->GetObjectID() (FedSrv.CPP:13926)
            Amount: 0,
            TreasureCode: TreasureCode.Flag,             // c_tcFlag (FedSrv.CPP:13923)
            CreateNow: false);                           // createNow = false (FedSrv.CPP:13928)
        var flag = new Treasure(data.ObjectId, sec, data, _simNow, mission);
        sec.Add(flag);
        SendTreasureSpawnSectorWide(sec, flag);
        Console.WriteLine($"[loot] flag {sidFlag} respawned above pedestal station {st.StationId} in sector {sec.Id} obj={flag.Data.ObjectId}");
    }

    /// <summary>
    /// P323 M6 — finalize a true death server-side when there is no respawn
    /// ("stays dead"). Native's KillShipEvent removes a dying player ship from
    /// its cluster unconditionally (<c>pship-&gt;SetCluster(NULL)</c>,
    /// FedSrv.CPP:12045) BEFORE deciding whether a station exists to respawn at
    /// (and a bot ship is deleted outright, FedSrv.CPP:12084); the port's combat
    /// death path achieves the same by zeroing the hull fraction in
    /// <see cref="Ship.ReceiveDamage"/> (Ship.cs:3199) and removing the dead
    /// ship in <see cref="Sector.ResolveCombatCollisions"/>
    /// (<c>if (ship.Dead) _models.Remove(ship)</c>, Sector.cs:1579). The
    /// oxygen-death / direct-KillShipEvent path (Ship.OnUpdate) performs NEITHER
    /// of those, and BroadcastKillShip's "no respawn, stays dead" branches used
    /// to log and return WITHOUT removing anything — leaving the ghost ship
    /// alive in its sector's <c>_models</c>, still flying / mining / shooting /
    /// broadcast every tick (a permanent server/client desync). This helper
    /// applies the missing finalization (match the port's death contract: mark
    /// Dead and leave the sector) so the "stays dead" log's claim is actually
    /// true. Idempotent: safe on an already-dead / already-removed ship.
    /// </summary>
    private static void FinalizeDeadShip(Ship ship)
    {
        ship.SetFraction(0f);                 // Dead == true (the port's death contract)
        if (ship.Sector is { } sec) sec.Terminate(ship);   // leave the sector's _models (native SetCluster(NULL)); P754b: Terminate, not a move
        ship.Sector = null;
    }

    /// <summary>
    /// P105's deliberately simplified stand-in for the native
    /// <c>FindTarget(..., c_ttFriendly | c_ttStation | c_ttNearest |
    /// c_ttAnyCluster, ..., c_sabmRestart)</c> nearest-friendly-station search
    /// (<c>FedSrv.CPP:12034-12038</c>). Native picks the spatially-nearest
    /// friendly station in any cluster, falling back to the mission base
    /// (<c>GetBase(pside)</c>); this port has <b>neither</b> a spatial
    /// nearest-search nor a "mission base" concept (confirmed — grep finds
    /// neither in <c>Sector.cs</c>/<c>Mission.cs</c>), so it returns <b>any</b>
    /// station whose <see cref="DataStationIGC.SideId"/> equals the ship's
    /// <see cref="Ship.SideId"/>, first-found in the mission's deterministic
    /// sector-then-station order. Stated plainly (not a silent drop): "best"
    /// station is out of scope — any one friendly match is fine, per the task.
    /// Returns <c>null</c> when no friendly station exists anywhere, mirroring
    /// native's no-<c>pstationDest</c> path (no respawn). Runs on the sim
    /// thread (called from <see cref="BroadcastKillShip"/>).
    /// </summary>
    private DataStationIGC? FindFriendlyStation(Ship ship)
    {
        if (ship.SideId is not { } sideId || ship.Mission is not { } mission)
            return null;
        // P550 — the debug-garrison preference (P393) has NO native equivalent
        // (the synthetic garrison is a port invention for the P379 debug arena),
        // so per the task it is retained ONLY behind the existing debug flag,
        // and never for a MapGen-sourced mission (where the generator placed the
        // real home stations and no synthetic garrison exists). For production
        // missions this branch is skipped entirely and the native
        // nearest-restart search below governs.
        var fromMapGen = MissionCameFromMapGen(mission);
        if (_config.LiveSettings?.DebugMode == true)
        {
            foreach (var sector in mission.Sectors)
                if (FindDebugGarrisonStation(sector, sideId, fromMapGen) is { } garrison)
                    return garrison;
        }
        // P550 — the native respawn station choice (FedSrv.CPP:12034-12038):
        // the spatially NEAREST friendly, restart-capable station in the ship's
        // own cluster, with the GetBase fallback (both c_sabmRestart-filtered).
        return FindNearestRestartStation(mission, sideId, ship.Sector?.Id, ship.Position);
    }

    /// <summary>
    /// P550 — the native respawn station-choice port
    /// (<c>FedSrv.CPP:12034-12038</c>, <c>FindTarget(pship, c_ttFriendly |
    /// c_ttStation | c_ttNearest | c_ttAnyCluster, …, c_sabmRestart)</c>):
    /// the spatially <b>nearest</b> friendly station, in <b>any</b> cluster,
    /// whose station type carries <c>c_sabmRestart</c> — the <c>c_sabmRestart</c>
    /// filter is exactly what the pre-P550 port lacked (it took the first
    /// friendly station with no restart test). Native's <c>c_ttAnyCluster</c>
    /// leg searches the ship's own cluster then adjacent clusters via a 1–2-hop
    /// warp BFS (<c>common.cpp:696-715</c>); the port collapses that to a direct
    /// mission-wide nearest-Euclidean scan, which always returns at least the
    /// same base native would (a faithful superset, stated plainly). When no
    /// restart-capable friendly station exists anywhere, falls back to native's
    /// <c>GetBase(pside)</c> (<c>fsmission.cpp:1433-1444</c>): the FIRST friendly
    /// station in mission order carrying <c>c_sabmRestart</c>. Returns
    /// <c>null</c> only when no restart-capable friendly station exists at all
    /// (native's no-<c>pstationDest</c> → no respawn, <c>FedSrv.CPP:12055</c>).
    /// Pure data over <c>mission.Sectors</c>/<c>mission.Core.StationTypes</c>,
    /// so it is unit-testable without a server.
    /// </summary>
    internal static DataStationIGC? FindNearestRestartStation(Mission mission, short sideId, short? ownSectorId, Vector3 position)
    {
        // Pass 1 — the ship's OWN cluster, nearest by squared distance (native
        // FindTarget's primary scan runs over pcluster->GetStations(),
        // common.cpp:511-536; positions are comparable within one sector).
        DataStationIGC? nearest = null;
        float best2 = float.MaxValue;
        if (ownSectorId is { } osid)
        {
            var own = mission.Sectors.FirstOrDefault(s => s.Id == osid);
            if (own is not null)
                foreach (var station in own.Stations)
                {
                    if (station.SideId != sideId) continue;                                       // friendly only (c_ttFriendly)
                    if (!mission.Core.StationTypes.TryGetValue(station.StationTypeId, out var t)) continue;
                    if (!t.HasCapability(DataStationTypeIGC.SabmRestart)) continue;               // c_sabmRestart filter
                    if (station.Position is not { Length: 3 } p) continue;
                    float dx = position.X - p[0], dy = position.Y - p[1], dz = position.Z - p[2];
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < best2) { best2 = d2; nearest = station; }
                }
            if (nearest is not null) return nearest;                                             // c_ttNearest hit in-cluster
        }

        // Pass 2 — the GetBase fallback (FedSrv.CPP:12035, fsmission.cpp:1433-1444):
        // the FIRST friendly restart-capable station in mission order. This is also
        // the any-cluster leg — station positions are SECTOR-LOCAL, so a cross-sector
        // Euclidean compare is meaningless; native picks the any-cluster destination
        // by warp-hop distance and here the port's fallback is native's GetBase scan
        // (a superset that returns the first restart-capable friendly station).
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)
            {
                if (station.SideId != sideId) continue;
                if (!mission.Core.StationTypes.TryGetValue(station.StationTypeId, out var t)) continue;
                if (!t.HasCapability(DataStationTypeIGC.SabmRestart)) continue;
                return station;
            }
        return null;
    }

    // ── P799 — native lives limit (ghosts) ──────────────────────────────

    /// <summary>
    /// P799 — the native ghost test (<c>Igc/shipIGC.h:2465-2469</c>):
    /// <c>IsGhost() = m_nDeaths &gt; GetMyMission()-&gt;GetMissionParams()-&gt;iLives</c>.
    /// The port's death counter is the owning peer's <see cref="PlayerScore.Deaths"/>
    /// (native <c>CFSPlayer::AddDeath</c> → <c>m_cDeaths</c>, incremented at
    /// <c>FedSrv.CPP:12032</c>) and the mission budget is
    /// <see cref="Allegiance.Core.Model.Mission.Lives"/> (= wire iLives). With the
    /// unlimited sentinel (0x7fff) this is never true — the pre-P799 behaviour for
    /// every production mission.
    /// </summary>
    internal bool IsGhost(LiveShip live) =>
        live.Ship.Mission is { } m && live.PlayerScore.Deaths > m.Lives;

    /// <summary>
    /// P799 — native <c>HasPlayers(pside, fCountGhosts=false)</c>
    /// (<c>fsmission.cpp:1335-1361</c>): does <paramref name="side"/> still own at
    /// least one connected, non-ghost player? The ghost half of the count is
    /// exactly <see cref="IsGhost"/>; a ghost bot/unowned ship has no player score
    /// of its own and never counts as a live player (native counts CFSPayers, not
    /// ships). Runs on the sim thread — same ownership as <see cref="IsGhost"/>.
    /// </summary>
    internal bool SideHasNonGhostPlayers(Mission mission, short sideId)
    {
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission) || kv.Value.Ship.SideId != sideId)
                continue;
            if (kv.Value.PlayerScore.Deaths <= mission.Lives)
                return true;
        }
        return false;
    }

    /// <summary>
    /// P107 — the in-space eject: the <c>!bKilled</c> branch of the native
    /// <c>KillShipEvent</c> (<c>FedSrv.CPP:11918-12022</c>). The dying player's
    /// ship survives, weakened, as a drifting Lifepod hull <b>still in the
    /// same sector</b> — not removed, not killed, no respawn. The caller
    /// (<see cref="BroadcastKillShip"/>) has already decided <c>!bKilled</c>
    /// via the <c>FedSrv.CPP:11770-11785</c> gate; this method only guards the
    /// data it needs (side/civilization/lifepod hull resolvable) and performs
    /// the port-relevant slice of the eject mechanics:
    /// <list type="bullet">
    /// <item>hull swap to the side's civilization's Lifepod hull
    /// (<c>pship-&gt;SetBaseHullType(pht)</c>, <c>FedSrv.CPP:11942</c>);</item>
    /// <item>reposition/reorient/velocity per the native math — an outward
    /// burst away from the killer's position when available, a random
    /// direction otherwise, position staying at the impact point
    /// <c>p1</c> (the parent-branch offset, FedSrv.CPP:11947-11953, is
    /// skipped — no boarded ships);</item>
    /// <item>an outward velocity and a <c>pi·2</c> roll spin
    /// (<c>FedSrv.CPP:12003,12008</c>);</item>
    /// <item>a real <c>FM_S_EJECT(77)</c> broadcast to <b>everyone flying in
    /// the same sector</b> — <c>GetGroupSectorFlying(pcluster)</c>
    /// (<c>FedSrv.CPP:12011-12021</c>), a sector-scoped, <b>cross-side</b>
    /// audience (distinct from <c>_teammateExportSent</c>'s same-side gate and
    /// from P101/P102's mission-wide <c>WARP_BOMB</c>/<c>ASTEROID_DESTROYED</c>
    /// broadcasts — this one is only to the ships in this one sector).</item>
    /// </list>
    /// The ship is deliberately <b>not</b> removed from the sector and gets no
    /// <c>SHIP_DELETE</c>/<c>ENTER_LIFEPOD</c> — this is the
    /// mutually-exclusive sibling of P103/P105's true-death branch. Native also
    /// clears missiles targeting the ship (<c>FedSrv.CPP:11925-11932</c>); this
    /// port's <see cref="Missile"/> carries no target back-reference, so that
    /// step is a structural no-op (noted, not silently dropped). Returns
    /// <c>true</c> when the eject ran and the caller should stop processing
    /// this death as a kill; <c>false</c> when the required data is missing
    /// (should not happen for a live player ship) so the caller falls through
    /// to the kill path. Runs on the sim thread (called from
    /// <see cref="BroadcastKillShip"/>).
    /// </summary>
    private bool EjectSurvivor(Ship victim, IModelIgc? launcher, Vector3 p1, Vector3 p2)
    {
        var pcluster = victim.Sector;
        var mission = victim.Mission;
        if (pcluster is null || mission is null || victim.SideId is not { } sideId)
        {
            Console.WriteLine($"[eject] player ship {victim.Id} lacks sector/mission/side — cannot eject, treating as death");
            return false;
        }
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[eject] player ship {victim.Id} side {sideId} not in mission sides — cannot eject, treating as death");
            return false;
        }
        short lifepodId = side.Civilization.Lifepod;
        if (!mission.Core.HullTypes.TryGetValue(lifepodId, out var pht))
        {
            Console.WriteLine($"[eject] player ship {victim.Id} side {sideId}: lifepod hull {lifepodId} not in core — cannot eject, treating as death");
            return false;
        }

        // v = the source ship's velocity (FedSrv.CPP:11935). No boarded ships
        // (cluster E) ⇒ source ship == the victim itself.
        Vector3 v = victim.Velocity;

        // (Missiles targeting this ship would be cleared here — FedSrv.CPP:11925-11932 —
        // but this port's Missile tracks no target; structural no-op.)

        victim.ChangeHull(pht);                 // SetBaseHullType(pht), FedSrv.CPP:11942

        // P308/P653d: native SetBaseHullType restores hull fraction to 1
        // (shipIGC.cpp:1435). This port's ChangeHull leaves fraction to its
        // caller, so restore the operational pod explicitly on ejection as
        // well as on the true-death restart above (FedSrv.CPP:11942,12052).
        victim.SetFraction(1.0f);

        // ── position / orientation / velocity math (FedSrv.CPP:11944-12006) ──
        // Parent branch (FedSrv.CPP:11947-11953) omitted — no boarded ships.
        Vector3 f = RandomDirection();         // Vector::RandomDirection(), vector.cpp:37-49
        bool bLook;
        Vector3 vectorLook;
        if (launcher is not null)
        {
            // plauncher = (source ship of) the credited killer, FedSrv.CPP:11960-11963.
            // No turret chains ⇒ a Ship launcher is its own source ship. IModelIgc
            // is a marker interface, so Position comes from the concrete Model
            // (Ship or another model); a non-Model launcher can't be located.
            if (launcher is Ship attackerShip)
            {
                bool sameSector = attackerShip.Sector == pcluster;   // GetCluster() == pcluster, FedSrv.CPP:11964
                if (sameSector) { bLook = true; vectorLook = attackerShip.Position; }
                else { bLook = false; vectorLook = default; }
            }
            else if (launcher is Model attackerModel)
            {
                bLook = true; vectorLook = attackerModel.Position;
            }
            else { bLook = false; vectorLook = default; }
        }
        else { bLook = true; vectorLook = p2; }  // no launcher → look at p2 (FedSrv.CPP:11974-11978)

        Vector3 orientationForward = f;          // o.Set(f) default (FedSrv.CPP:11995,11998)
        if (bLook)
        {
            Vector3 dp = vectorLook - p1;
            float length2 = dp.LengthSquared();
            if (length2 >= 1.0f)                 // FedSrv.CPP:11985
            {
                orientationForward = dp;         // o.Set(dp), FedSrv.CPP:11987
                // Bend the random forward slightly toward the killer:
                // fNew = f + dp*(8/sqrt(length2)), FedSrv.CPP:11989-11992.
                Vector3 fNew = f + dp * (8.0f / MathF.Sqrt(length2));
                float l2 = fNew.LengthSquared();
                if (l2 > 0.01f) f = fNew / MathF.Sqrt(l2);
            }
        }

        Vector3 position = p1;                   // position = p1 (parentless branch), FedSrv.CPP:12000
        v -= f * 100.0f;                         // outward burst, FedSrv.CPP:12003
        victim.Position = position;              // SetPosition, FedSrv.CPP:12004
        victim.Velocity = v;                     // SetVelocity, FedSrv.CPP:12005
        victim.Orientation.Set(orientationForward); // SetOrientation(o), FedSrv.CPP:12006
        victim.SetTurnRate(ControlData.AxisRoll, 2f * MathF.PI); // SetCurrentTurnRate(c_axisRoll, pi*2), FedSrv.CPP:12008

        _podTrips.Begin(victim, mission.LastUpdate);

        // EJECT(77) to everyone flying in this sector — cross-side, sector-scoped.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == pcluster)
            .Select(kv => kv.Key)
            .ToArray();

        // P540b — the EJECT message's cookie is consumed ONLY by the ejecting
        // player's own client (ClientWorld.Apply(Eject): `if (e.ShipId == MyShipId)
        // ent.Cookie = e.Cookie`), which then echoes it on every native SHIP_UPDATE
        // it sends for its new lifepod. Native stamps a fresh NewCookie() here
        // (FedSrv.CPP:12019) so the client can fly the pod; shipping 0 (the old
        // direct-connect simplification) made the ejected player's pod send a
        // stale cookie that the P507 gate dropped whole — the pod froze at the
        // eject point and could never fly (nor dock) after ejecting. Rotate the
        // ejecting peer's cookie and carry it on the message.
        byte ejectCookie = 0;
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, victim)) { ejectCookie = NextCookie(EnsurePeerUpdate(kv.Key)); break; }
        victim.Mission?.Skills?.Record(victim.SideId, ShowcaseSkill.Eject);
        var eject = Eject.ForSurvivor(victim.Id, position, v, f);
        eject.Cookie = ejectCookie;
        byte[] packet = FedPacket.Pack(new[] { eject.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[eject] player ship {victim.Id} ejected -> Lifepod hull {pht.HullId} at {position}, vel ({v.X:0.#},{v.Y:0.#},{v.Z:0.#}), fwd ({f.X:0.##},{f.Y:0.##},{f.Z:0.##}); EJECT(77) -> {targets.Length} peer(s) in sector {pcluster.Id}; hull stays in sector (not removed), no SHIP_DELETE");
        return true;
    }

    /// <summary>
    /// Handle a simulated mining accrual (<see cref="ServerHost.MineAsteroidEvent"/>,
    /// raised from <see cref="Sector.ResolveMining"/> on the sim thread when an
    /// asteroid's ore moved past the native reporting threshold,
    /// <c>asteroidigc.h:246-251</c>) by logging it <em>and</em> broadcasting a real
    /// <c>FM_S_ASTEROID_MINED(198)</c> <see cref="AsteroidMined"/> message — the
    /// rock's remaining ore made wire-visible to observers in real time (P120).
    /// The <c>newOre</c> arg is the rock's ore fraction (<c>asteroidigc.h:249</c>),
    /// i.e. <c>ore/oreMax</c>, not an absolute mass.
    /// <para><b>WHOLE MISSION.</b> Native sends <c>ASTEROID_MINED</c> from
    /// <c>MineAsteroidEvent</c> behind a per-side <c>GetAsteroidCurrentEye</c>
    /// visibility gate (<c>FedSrv.CPP:12453-12468</c>), but that gate is the same
    /// class of cross-side-visibility infrastructure this port lacks for
    /// <c>PROBES_UPDATE</c>(92)/<c>ASTEROIDS_UPDATE</c>(179) and does not build
    /// here. Following the precedent this port's own sibling message set —
    /// <c>ASTEROID_DESTROYED</c>(86, P102) broadcasts whole-mission with no
    /// per-side gate (<see cref="BroadcastAsteroidDestroyed"/>, confirmed
    /// deliberate) — this broadcasts to every connected peer with a seated ship,
    /// <c>GetGroupRealSides()</c> audience (<c>FedSrv.CPP:12418</c>). The miner's
    /// own cargo ore still travels as <c>ServerShipUpdate</c> byte 45
    /// (<c>OreFraction</c>); this closes the observer gap for the <em>rock's</em>
    /// remaining ore. Runs on the sim thread (same one that owns
    /// <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void OnMine(IAsteroidIgc pasteroid, float newOre)
    {
        if (pasteroid is not Asteroid ast)
        {
            Console.WriteLine("[mine-event] MineAsteroidEvent with a non-Asteroid asteroid — ignored");
            return;
        }
        Console.WriteLine($"[mine-event] asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' ore fraction {newOre:0.###} (remaining {ast.OreRemaining:0.##}/{ast.OreMax:0.##})");

        var msg = AsteroidMined.ForAsteroid(ast, newOre);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides(), FedSrv.CPP:12418) — the ASTEROID_DESTROYED
        // precedent (P102).
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[asteroid] asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' ore fraction {newOre:0.###} " +
                          $"=> ASTEROID_MINED(198) sector {ast.Data.ClusterId} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a simulated warp transit (<see cref="ServerHost.HitWarpEvent"/>,
    /// raised from the sector tick's <c>Sector.ResolveWarpCollisions</c> on the
    /// sim thread) by refreshing the per-peer <see cref="LiveShip"/> record and
    /// sending a fresh <c>FM_S_SET_CLUSTER(7)</c>. The client's SET_CLUSTER
    /// handler (<c>appmsg.cpp:1028-1087</c>) is what calls
    /// <c>pship->SetCluster(pcluster)</c> — the client-side sector reference the
    /// whole exploration/movement loop is gated on — so a transit must re-send
    /// it, not just rebroadcast a moved position. The <see cref="ShipWarped"/>
    /// hook fires after the ship is already relocated/reassigned
    /// (<c>Ship.Sector</c> and the sector model lists), so this handler only
    /// does the wire-facing half. Scoped to a single ship by
    /// <see cref="Ship.Id"/>/reference — no multi-peer visibility concerns
    /// (P40's own-ship scoping note), matching this chain's established scope.
    /// </summary>
    /// <remarks>
    /// Two consequences ride on refreshing the record here. (1) The next
    /// <see cref="BroadcastShipStates"/> sees <c>live.Sector.Id !=
    /// _lastStatusSector</c> and re-sends <c>FM_S_SHIP_STATUS(90)</c> with the
    /// new sector — the <c>sectorChanged</c> hook P40 built that never fired for
    /// lack of a real sector change now fires. (2) All the other sector readers
    /// (chat <c>AllSector</c>/<c>FriendlySector</c> targeting,
    /// <c>BroadcastKillShip</c>) agree on the new sector. The debounce window is
    /// set here (<see cref="Ship.LastWarpTime"/>, native fsship.cpp:1188) so the
    /// fresh position near the destination warp cannot be immediately swallowed
    /// back — owned by this method because only <see cref="Server"/> knows the
    /// sim clock (<c>_simNow</c>).
    /// </remarks>
    /// <summary>P760 — handler for <see cref="ServerHost.ShipChangedCluster"/>.
    /// Sends the <c>SDR_LEFTSECTOR</c> departure notice when a ship moves from
    /// one real cluster to another (FedSrv/FedSrv.CPP:13513-13528). Dock, death,
    /// and leave transitions do not reach this path.</summary>
    private void OnShipChangedCluster(Ship ship, Sector oldSector, Sector newSector)
    {
        BroadcastShipDeparted(ship, oldSector, ShipDelete.SdrLeftSector);
    }

    /// <summary>P760 — handler for <see cref="ServerHost.GainFlagSent"/>.
    /// Routes the <c>GAIN_FLAG</c> notice built in
    /// <see cref="ServerHost.ChangeStation"/> to the docking ship's side group.</summary>
    private void OnGainFlagSent(Ship ship, GainFlag gain)
    {
        if (ship.Mission is not { } mission || ship.SideId is not { } sideId) return;
        var packet = FedPacket.Pack(new[] { gain.Encode() });
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId != sideId) continue;
            SendPeer(kv.Key, packet);
        }
    }

    /// <summary>P760 — handler for <see cref="ServerHost.TechsRecovered"/>.
    /// Award the tech-recovered score/achievement hook. This is the port of
    /// native's <c>AddTechsRecovered()</c> + Steam achievement
    /// (<c>FedSrv.CPP:13216-13218</c>). Steam achievements are out of scope, so
    /// only the score counter is advanced.</summary>
    private void OnTechsRecovered(Ship ship)
    {
        Console.WriteLine($"[tech] ship {ship.Id} recovered tech by docking");
    }

    /// <summary>
    /// P851 — handler for <see cref="ServerHost.SpotterCredit"/>, the port's home
    /// for the <c>PlayerScoreObject</c> increments native books inside
    /// <c>ThingSiteImpl::ShowObject</c>/<c>UpdateSideVisibility</c>:
    /// <c>AddTargetSpot()</c> for a first-spotted high-value enemy hull
    /// (<c>FedSrv.CPP:10961-10982</c>), <c>SpotWarp()</c> / <c>SpotSpecialAsteroid()</c>
    /// for a first-spotted aleph / special asteroid
    /// (<c>FedSrv.CPP:11103-11111</c>). Native reads the spotter's
    /// <c>CFSShip::GetPlayerScoreObject()</c>; the port resolves the same ship to
    /// its owning peer's <see cref="PlayerScore"/> — an unowned (NPC) ship has no
    /// peer and books nothing, the same outcome as native's player-only gates.
    /// </summary>
    private void OnSpotterCredit(Ship ship, SpotCredit credit)
    {
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, ship)) continue;
            var score = kv.Value.PlayerScore;
            switch (credit)
            {
                case SpotCredit.Target: score.AddTargetSpot(); break;
                case SpotCredit.Warp: score.SpotWarp(); break;
                case SpotCredit.SpecialAsteroid: score.SpotSpecialAsteroid(); break;
            }
            if (ConsoleLog.Verbose)
                ConsoleLog.Write($"[spot] ship {ship.Id} side {ship.SideId} earned {credit} (targets {score.HighValueTargetsSpotted}, warps {score.WarpsSpotted}, asteroids {score.AsteroidsSpotted})");
            return;
        }
    }

    /// <summary>P760 — handler for <see cref="ServerHost.CommandChanged"/>.
    /// Routes <c>FM_CS_ORDER_CHANGE(93)</c> to the correct audience:
    /// side group (guaranteed) for <c>c_cmdAccepted</c>, sector-flying group
    /// (not guaranteed) for <c>c_cmdCurrent</c> with an AoE weapon
    /// (<c>FedSrv/FedSrv.CPP:13699-13732</c>).</summary>
    private void OnCommandChanged(Ship ship, Sector? sector, OrderChange oc, bool guaranteed)
    {
        if (ship.Mission is not { } mission) return;
        var packet = FedPacket.Pack(new[] { oc.Encode() });
        if (guaranteed)
        {
            // c_cmdAccepted: ship's side group. P761c — never echo back to the
            // commanded ship's own connection: native's client drops ORDER_CHANGE
            // for its own ship (clintlib/appmsg.cpp:1838 `if (pship != m_ship)`),
            // and the port's own accepted text comes from MyAcceptedCommand.
            // Latch what was relayed so the per-tick sweep (SweepAcceptedCommands)
            // doesn't see the same change next tick and send it twice.
            _lastRelayedAcceptedCommand[ship] = AcceptedCommandOf(ship);
            foreach (var kv in _peerShips)
            {
                if (ReferenceEquals(kv.Value.Ship, ship)) continue;
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId != ship.SideId) continue;
                SendPeer(kv.Key, packet);
            }
        }
        else
        {
            // c_cmdCurrent: sector-flying group. P761c — same own-ship skip as
            // the accepted arm (appmsg.cpp:1838; the own current slot is local).
            if (sector is null) return;
            foreach (var kv in _peerShips)
            {
                if (ReferenceEquals(kv.Value.Ship, ship)) continue;
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (!ReferenceEquals(kv.Value.Ship.Sector, sector)) continue;
                if (kv.Value.Ship.StationId is not null) continue;
                SendPeer(kv.Key, packet);
            }
        }
    }

    private void OnShipWarped(Ship ship, Sector oldSector, Sector newSector)
    {
        // P760 — LastWarpTime is now stamped in ServerHost.HitWarpEvent so the
        // warp-state handshake and cooldown live in the site callback.

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, ship)) continue;

            // Refresh the per-peer LiveShip sector so BroadcastShipStates' own-
            // ship status / sector readers all see the new sector. Established
            // `live with { ... }` reassignment pattern (same as LastBroadcastPos,
            // Server.cs:342,500).
            _peerShips[kv.Key] = kv.Value with { Sector = newSector };

            var set = new SetCluster
            {
                SectorId = newSector.Id,
                Ship = ServerShipUpdate.FromModel(ship.Id, ship, (uint)TickIndex),
                Cookie = NextCookie(EnsurePeerUpdate(kv.Key)),   // P507 — native's NewCookie() on warp SET_CLUSTER (fsship.cpp:767)
            };
            SendPeer(kv.Key, FedPacket.Pack(new[] { set.Encode() }));
            SendTreasureSnapshot(kv.Key, newSector);
            Console.WriteLine($"[warp] peer {kv.Key} ship {ship.Id} -> SET_CLUSTER sector {newSector.Id} (LiveShip.Sector refreshed; SHIP_STATUS(90) resend via BroadcastShipStates)");
            return;
        }

        // A transit on a ship that is not a peer's own (possible only in a pure
        // sim / future-drone case) still moved the model correctly; only the
        // wire-facing routing is skipped.
        Console.WriteLine($"[warp] ship {ship.Id} transited '{oldSector.Name}' -> '{newSector.Name}' but is not a peer ship — SET_CLUSTER/LiveShip routing skipped (pure-sim transit)");
    }

    /// <summary>
    /// P370 — the explicit "this ship is no longer in your view" notice. When a
    /// ship leaves a sector's <c>_models</c> without dying (warps to another
    /// sector, or docks), peers still FLYING in that sector keep the last
    /// telemetry pose they received and would otherwise render it frozen forever
    /// (the ghost-ship gap this task closes). Emit the same one-shot
    /// <c>SHIP_DELETE(20)</c> the death path already drives with <c>SDR_KILLED</c>
    /// (BroadcastKillShip), addressed to the same sector-scoped audience. This is
    /// the port of native <c>CFSShip::AnnounceExit(pclusterOld, sdr)</c>'s
    /// <c>GetGroupSectorFlying(pclusterOld)</c> branch (fsship.cpp:206-210) for the
    /// two LEFTSECTOR/DOCKED reasons.
    /// <para><b>Audience.</b> Exactly the peers flying in the sector being left:
    /// same-side teammates AND cross-side peers who received a cross-side EXPORT
    /// for it — every peer that could have a cross-side identity for this ship
    /// has its own ship in the departed ship's sector (the P369 export gate), so
    /// the sector-flying audience subsumes the P369 bookkeeping rather than
    /// re-deriving it. A peer who never knew the ship is an inert recipient: the
    /// client guards on entity-existence before marking anything gone. The
    /// subject ship's own peer is never told to hide its own ship.</para>
    /// </summary>
    private void BroadcastShipDeparted(Ship ship, Sector? oldSector, int sdr)
    {
        if (oldSector is null) return;
        var targets = new List<ConnectionId>();
        foreach (var kv in _peerShips)
        {
            if (ReferenceEquals(kv.Value.Ship, ship)) continue;            // never tell a ship to hide its own vessel
            if (!ReferenceEquals(kv.Value.Ship.Sector, oldSector)) continue; // GetGroupSectorFlying(old) — only the sector being left
            if (kv.Value.Ship.StationId is not null) continue;             // "flying" only (native GetGroupSectorFlying)
            targets.Add(kv.Key);
        }
        if (targets.Count == 0) return;

        var msg = new ShipDelete { ShipId = ship.Id, Sdr = sdr };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets)
            SendPeer(t, packet);
        string reason = sdr == ShipDelete.SdrLeftSector ? "LEFTSECTOR"
                      : sdr == ShipDelete.SdrDocked ? "DOCKED"
                      : sdr.ToString();
        Console.WriteLine($"[shipdelete] ship {ship.Id} sdr={reason} -> {targets.Count} peer(s) flying in sector {oldSector.Id}");
    }

    /// <summary>
    /// Handle a simulated dock (<see cref="ServerHost.DockWithStationEvent"/>,
    /// raised from the sector tick's <c>Sector.ResolveDockCollisions</c> on the
    /// sim thread, after <see cref="Ship.StationId"/> has been latched) by
    /// sending the <c>FM_S_DOCKED(61)</c> notify to the docking peer. This is
    /// the wire trigger that makes the native client's <c>ship.GetStation()</c>
    /// resolve: the client's <c>FM_S_DOCKED</c> handler
    /// (<c>appmsg.cpp:1092-1115</c>) is the ONLY call site that calls
    /// <c>m_ship-&gt;SetStation(reallyNonNull)</c> (<c>:1113</c>) — the member
    /// <c>IshipIGC::GetStation()</c> reads (<c>shipIGC.h:417-420</c>). Mirrors
    /// <see cref="OnShipWarped"/>'s per-peer routing: scoped to a single ship,
    /// sent to the docked peer only (no multi-peer visibility concerns, P40's
    /// own-ship scoping). Raised once per dock transition because the dock pass
    /// skips already-docked ships — exactly once per dock event.
    /// </summary>
    private void OnShipDocked(Ship ship, short? stationSide)
    {
        _podTrips.End(ship, ship.Mission?.LastUpdate ?? SimNow, "docked");
        // P201 — flag-secure leg (native FedSrv.CPP:13111-13159): a ship
        // docking at ITS OWN side's station while carrying a flag secures it.
        // The gate is the native `pside == psideOld` — pside = the ship's side
        // (pship->GetSide()), psideOld = the station's side (pstation->GetSide())
        // — so docking at ANY other side's station does NOT secure the flag.
        // On secure: increment the owning side's Flags (or Artifacts if the
        // carried value is the team-lobby sentinel), clear the carried flag,
        // and announce whole-mission with sideidFlag = NA.
        var mission = ship.Mission;

        // P494 — the dock auto-restock (refill + desired-loadout successor
        // upgrade), for a player-piloted ship only (native gates the dock
        // restock's desired-loadout walk on IsPlayer(), FedSrv.CPP:12836).
        foreach (var kvp in _peerShips)
        {
            if (!ReferenceEquals(kvp.Value.Ship, ship)) continue;
            RestockOnDock(kvp.Value);
            break;
        }

        if (ship.Flag is { } sidFlag
            && stationSide is { } ss
            && ship.SideId is { } shipSide
            && shipSide == ss
            && mission is not null)
        {
            var side = mission.Sides.FirstOrDefault(s => s.Id == shipSide);
            if (side is not null)
            {
                // Native branch (FedSrv.CPP:13123-13137): a carried value of
                // SIDE_TEAMLOBBY is a neutral artifact; anything else is a real
                // enemy flag. The team-lobby sentinel (-2) cannot arise from a
                // real-side flag treasure's TreasureId (real sides id >= 0), but
                // P302 makes it reachable via a NEUTRAL artifact treasure whose
                // TreasureId is the sentinel — the artifact win is P302-scoped
                // and gated on mission.ArtifactEnabled below.
                if (sidFlag == Side.ArtifactSentinelFlag)
                    side.Artifacts++;
                else
                    side.Flags++;
                Console.WriteLine($"[flag] ship {ship.Id} secured side {sidFlag}'s flag at own-side station {ss} -> side {shipSide} flags={side.Flags} artifacts={side.Artifacts}");

                // P583 — the docking pilot's secured-artifact/flag score credit
                // (native AddArtifact, FedSrv.CPP:13118 / AddFlag,
                // FedSrv.CPP:13127). Live peer only (no score object on an NPC).
                if (LiveOf(ship)?.PlayerScore is { } secureScore)
                {
                    if (sidFlag == Side.ArtifactSentinelFlag)
                        secureScore.AddArtifact();
                    else
                        secureScore.AddFlag();
                    Console.WriteLine($"[scoring] ship {ship.Id} {(sidFlag == Side.ArtifactSentinelFlag ? "artifact" : "flag")} secure +1 (player peer)");
                }

                // Native FedSrv.CPP:13116-13137: artifact chat goes to the
                // scoring team; real flags also notify their owner.
                if (sidFlag == Side.ArtifactSentinelFlag)
                    SendFlagSecureChat(mission, shipSide, 1362,
                        $"{PeerName(ship) ?? $"ship{ship.Id}"} has secured an artifact."); // FedSrv.CPP:13116-13123; Igc/sounds.h:420
                else if (sidFlag >= 0)
                {
                    string owner = mission.Sides.First(s => s.Id == sidFlag).Name ?? $"side{sidFlag}";
                    SendFlagSecureChat(mission, shipSide, 1360,
                        $"{PeerName(ship) ?? $"ship{ship.Id}"} has secured {owner}'s flag.");
                    SendFlagSecureChat(mission, sidFlag, 1358,
                        $"{PeerName(ship) ?? $"ship{ship.Id}"} of {side.Name} has secured your flag.");
                }
            }
            // Clear and publish before checking victory (FedSrv.CPP:13139-13149).
            ship.Flag = null;
            BroadcastGainFlag(mission, GainFlag.NA, ship.Id, bIsTreasureDocked: false, parttypeidDocked: 0);
            if (sidFlag == Side.ArtifactSentinelFlag && mission.ArtifactEnabled)
            {
                var (winner, changed) = mission.RecomputeProsperityAndArtifacts();
                if (changed || winner is not null)
                    mission.Site.ProsperityArtifactComputed(mission, changed, null);
                if (winner is not null && mission.Stage != MissionStage.Over)
                    DeclareGameOver(mission, winner.Id,
                        $"{winner.Name} wins with the artifact recovered by {PeerName(ship) ?? $"ship{ship.Id}"}");
                // Native intent: FedSrv.CPP:13149-13158 artifact text; its
                // sidFlag != NA ternary erroneously selects flag text for -2.
            }
            else if (sidFlag >= 0 && mission.FlagsEnabled)
            {
                var (winner, changed) = mission.RecomputeFlagsAndKills();
                if (changed || winner is not null)
                    mission.Site.FlagsKillsComputed(mission, changed, null);
                if (winner is not null && mission.Stage != MissionStage.Over)
                    DeclareGameOver(mission, winner.Id,
                        $"{winner.Name} wins with the flag recovered by {PeerName(ship) ?? $"ship{ship.Id}"}"); // FedSrv.CPP:13153-13158
            }
            // Respawn BOTH real flags and artifacts; a picked-up treasure does
            // not respawn, but a secured flag does (FedSrv.CPP:13161,13842-13944).
            if (sidFlag == Side.ArtifactSentinelFlag || mission.FlagsEnabled)
                RespawnCarriedFlag(mission, sidFlag, side);
        }

        // P370 — the ship just docked (its StationId was latched by the host's
        // DockWithStationEvent), so it leaves the sector's _models and stops
        // appearing in that sector's telemetry. Tell the peers still flying in
        // this sector (the station's sector) that it is gone, so they stop
        // rendering the frozen last-known pose (native:
        // CFSShip::AnnounceExit(pship->GetCluster(), SDR_DOCKED),
        // FedSrv.CPP:13107). The docked ship's own peer is excluded by
        // BroadcastShipDeparted — it sees the station scene, not a ghost.
        BroadcastShipDeparted(ship, ship.Sector, ShipDelete.SdrDocked);

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, ship)) continue;

            var notify = new DockedNotify { StationId = ship.StationId!.Value };
            SendPeer(kv.Key, FedPacket.Pack(new[] { notify.Encode() }));
            Console.WriteLine($"[docked] peer {kv.Key} ship {ship.Id} <- FM_S_DOCKED(61) station {notify.StationId} (native ship.GetStation() resolve trigger)");

            // P519 (Bug 3) — the docked player must know its REAL mounted loadout
            // so the docked shop pre-seeds and relaunches with the full intended
            // parts (never 0, which previously stripped the ship to a bare hull:
            // ApplyLoadout clears then remounts only the requested parts). The
            // own-ship EXPORT carries the NA(-1) dummy (never the real parts),
            // and BUY_LOADOUT_ACK reaches the requester only in reply to a buy —
            // so on a FRESH dock the client has no record of its mounted parts.
            // Send the ship's real parts unsolicited as a BUY_LOADOUT_ACK
            // (FLaunch=false: no launch; the client's Apply(BuyLoadoutAck) sets
            // HullId + Loadout). Native's hangar opens with the player's current
            // loadout (RestoreLoadout, load.cpp:537) — this is the port's analog.
            var sync = new BuyLoadoutAck
            {
                HullId = ship.HullType.HullId,
                FBoughtEverything = true,
                FLaunch = false,
                Parts = ship.Parts.Select(x => x.ToExpandedPartData()).ToArray(),
            };
            SendPeer(kv.Key, FedPacket.Pack(new[] { sync.Encode() }));
            Console.WriteLine($"[docked] peer {kv.Key} ship {ship.Id} <- BUY_LOADOUT_ACK dock-sync hull {sync.HullId} + {sync.Parts.Count} part(s) (real mounted loadout)");
            return;
        }

        // A dock on a ship that is not a peer's own (possible only in a pure
        // sim / future-drone case) still latched the state correctly; only the
        // wire-facing notify is skipped.
        Console.WriteLine($"[docked] ship {ship.Id} docked but is not a peer ship — FM_S_DOCKED(61) routing skipped (pure-sim dock)");
    }

}
