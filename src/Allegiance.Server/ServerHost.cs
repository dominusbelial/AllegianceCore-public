using System.Collections.Concurrent;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>One discrete transport-lifecycle or datagram event, queued for the
/// tick loop to pump (mirroring FedMessaging's single-owner m_csMsgList
/// serializer: transport threads raise, the tick loop drains).</summary>
internal interface ITransportEvent { }

/// <summary>A peer completed the transport greet and was assigned an id.</summary>
internal readonly record struct PeerJoinedEvt(ConnectionId Id, string Name) : ITransportEvent;
/// <summary>A peer disconnected, cleanly or abruptly.</summary>
internal readonly record struct PeerLeftEvt(ConnectionId Id) : ITransportEvent;
/// <summary>A framed packet (messages + trailing CRC) arrived from a peer.</summary>
internal readonly record struct ReceivedEvt(ConnectionId From, byte[] Data) : ITransportEvent;
/// <summary>The host session ended for reasons other than our own shutdown.</summary>
internal readonly record struct SessionLostEvt() : ITransportEvent;

/// <summary>
/// The server's host seam — implements both the mission site
/// (<see cref="IIgcSite"/>, igc.h:4757) and the transport site
/// (<see cref="ITransportSite"/>). This is the gateway site bound at boot
/// (BOOT_PATH.md §3 "gateway mission + bound IIgcSite"); the world missions
/// bind to this same live host rather than a mute dummy, so the tick reaches
/// a site that is actually observing. Everything rides the default IIgcSite
/// behaviours (no economy, no collisions) because a headless boot has no
/// gameplay objects; the transport half is where this class earns its keep.
/// </summary>
/// <remarks>
/// Two interfaces, one serialized drain: <see cref="ITransportSite"/> callbacks
/// arrive on the transport's own accept/read threads; each is reduced to an
/// immutable <see cref="ITransportEvent"/> and pushed onto a
/// <see cref="ConcurrentQueue{T}"/>. The tick loop calls <see cref="Drain(Action{ITransportEvent})"/>
/// once per pass, so event processing happens on the simulation thread — the
/// same single-owner funnel the C++ uses (<c>m_csMsgList</c>). <see cref="TcpTransport"/>
/// is set after construction because the site and the transport reference each
/// other; <see cref="Send"/> is a convenience that hoists the <c>transport</c>.
/// </remarks>
internal sealed class ServerHost : IIgcSite, ITransportSite
{
    private readonly ConcurrentQueue<ITransportEvent> _events = new();

    /// <summary>The transport this host is bound to; assigned once the pair exists.</summary>
    public TcpTransport? Transport { get; set; }

    /// <summary>The configured bound port, once the transport has been Hosted.</summary>
    public ushort BoundPort => Transport?.BoundPort ?? 0;

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <c>KillShipEvent</c> the world missions raise (a ship's hull reached zero,
    /// <see cref="Ship.ReceiveDamage"/>, <c>shipIGC.cpp:1317</c>). Null by
    /// default — matching the IIgcSite no-op — so a headless host that has not
    /// installed a broadcaster behaves exactly as before. <see cref="Server"/>
    /// sets this once at boot; the delegate runs on the simulation thread (the
    /// same thread that drives the tick and broadcasts ship states), so it can
    /// reach <see cref="Server"/>'s sim-thread-owned peer map without a lock.
    /// Args are the IIgcSite.KillShipEvent parameters verbatim: now, ship (the
    /// victim), launcher (the credited attacker, null for none), amount,
    /// and the two impact positions.
    /// </summary>
    public Action<double, IShipIgc, IModelIgc?, float, Vector3, Vector3>? ShipKilled;

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// warp transit the world missions perform (a ship crossed an aleph and was
    /// relocated to another sector). Raised by <see cref="HitWarpEvent"/> after
    /// the ship has already been repositioned and reassigned. Null by default,
    /// matching the IIgcSite no-op; a headless host that has not installed a
    /// handler behaves as before. <see cref="Server"/> sets this once at boot;
    /// the delegate runs on the simulation thread (inside the sector tick that
    /// triggered the transit), so it can reach <see cref="Server"/>'s peer map
    /// without a lock. Args: the ship, the sector it left, and the sector it
    /// entered.
    /// </summary>
    public Action<Ship, Sector, Sector>? ShipWarped;

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// dock the world missions perform (a ship entered a station's dock radius
    /// and was latched docked). Raised by <see cref="DockWithStationEvent"/>
    /// after <see cref="Ship.StationId"/> has been set. Null by default,
    /// matching the IIgcSite no-op; a headless host that has not installed a
    /// handler behaves as before. <see cref="Server"/> sets this once at boot;
    /// the delegate runs on the simulation thread (inside the sector tick's
    /// dock pass), so it can reach <see cref="Server"/>'s peer map without a
    /// lock. Args: the ship that docked.
    /// </summary>
    public Action<Ship>? ShipDocked;

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive
    /// every notable gameplay moment (<see cref="ReportNotable"/>) raised by
    /// the sim/host — dock, mine-start, mine-sell, undock, payday, warp
    /// crossing — as the same human-readable line already printed to the
    /// console at the originating site (never reformatted, so the observer
    /// sees exactly what the log shows). Null by default, matching the
    /// IIgcSite no-op; a headless host that has not installed a handler
    /// behaves as before. <see cref="Server"/> wires this once at boot; the
    /// delegate runs on the simulation thread (the same thread that drives the
    /// tick and owns <see cref="Server"/>'s peer map), so a consumer can reach
    /// the peer map without a lock.
    /// </summary>
    public Action<string>? Notable;


    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <c>PaydayEvent</c> the world missions raise on a payday tick. The args
    /// are the IIgcSite.PaydayEvent parameters verbatim: the side and the income
    /// just accrued for it (<c>fsmission.cpp:2536</c>). The hook only signals —
    /// the actual per-player payout math (player count, <c>delta</c>, the
    /// <c>FM_S_PAYDAY(114)</c> broadcast, and the <c>delta·np</c> deduction)
    /// lives in <see cref="Server"/>'s handler on the simulation thread (the
    /// same thread that drives the tick and owns <see cref="Server"/>'s peer
    /// map), mirroring the reference <c>CFSMission::DoPayday</c>
    /// (<c>fsmission.cpp:2294-2326</c>). Null by default, matching the IIgcSite
    /// no-op; a headless host that has not installed a handler behaves as before.
    /// </summary>
    public Action<ISideIgc, float>? Payday;

    /// <summary>
    /// The world-mission site implementation of <c>PaydayEvent</c>: raise the
    /// <see cref="Payday"/> hook so the owning <see cref="Server"/> can turn a
    /// simulated payday into a wire <c>FM_S_PAYDAY(114)</c> broadcast to the
    /// side's peers. This joins <see cref="KillShipEvent"/> as the second
    /// gameplay callback this host cares about; everything else keeps the
    /// interface default. Declared public (not explicit interface) so the hook
    /// and the site override live together; a class member shadows the
    /// interface default in dispatch.
    /// </summary>
    public void PaydayEvent(ISideIgc pside, float money) =>
        Payday?.Invoke(pside, money);
    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <c>BucketCompleted</c> the world missions raise the instant a
    /// construction bucket becomes fully funded AND fully time-invested — the
    /// P119 completion-<em>detection</em> seam (native gate bucketigc.h:51).
    /// The args are the IIgcSite.BucketCompleted parameters verbatim: the
    /// bucket and the sim time. Null by default, matching the IIgcSite no-op.
    /// This hook only signals — P119 deliberately builds nothing on completion
    /// (bucket-completion effects are the separate follow-up task), so the
    /// Server's handler is pure observation.
    /// </summary>
    public Action<IBucketIgc, double>? BucketCompletedHook;

    /// <summary>
    /// The world-mission site implementation of <c>BucketCompleted</c>: raise
    /// the <see cref="BucketCompletedHook"/> so the owning <see cref="Server"/>
    /// (or a test) can observe it. Declared public (not explicit interface) so
    /// the hook and the site override live together; a class member shadows the
    /// interface default in dispatch.
    /// </summary>
    public void BucketCompleted(IBucketIgc bucket, double now) =>
        BucketCompletedHook?.Invoke(bucket, now);
    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="ProjectileCreatedEvent"/> the world missions raise when
    /// <c>Weapon.FireWeapon</c> creates a real <see cref="Projectile"/> model
    /// (<c>Sim/Weapon.cs</c> after <c>sector.Add(projectile)</c>). Null by
    /// default, matching the IIgcSite no-op — a headless host that has not
    /// installed a broadcaster behaves exactly as before. <see cref="Server"/>
    /// sets this once at boot; the delegate runs on the simulation thread
    /// (inside the mission tick that created the projectile), so it can reach
    /// <see cref="Server"/>'s sim-thread-owned peer map without a lock. Arg is
    /// the created <see cref="Projectile"/> itself (its <c>ProjectileType</c>,
    /// <c>Position</c>, <c>Velocity</c>, <c>Forward</c>, <c>Lifespan</c> and
    /// <c>Launcher</c> carry everything the wire message needs). This joins
    /// <see cref="KillShipEvent"/> as another gameplay callback the host
    /// bridges out of the seam; everything else keeps the interface default.
    /// Declared public (not explicit interface) so the hook and the site
    /// override live together; a class member shadows the interface default in
    /// dispatch — exactly the established pattern above.
    /// </summary>
    public Action<Projectile>? ProjectileCreated;

    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.ProjectileCreatedEvent"/>: raise the
    /// <see cref="ProjectileCreated"/> hook so the owning <see cref="Server"/>
    /// can broadcast a real <c>FM_CS_PROJECTILE_INSTANCE(12)</c>.</summary>
    public void ProjectileCreatedEvent(Projectile p) => ProjectileCreated?.Invoke(p);

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="MissileCreatedEvent"/> the world missions raise when
    /// <c>Ship.FireMissile</c> creates a real <see cref="Missile"/> model
    /// (<c>Sim/Ship.cs</c> after <c>sector.Add(missile)</c>). Null by default,
    /// matching the IIgcSite no-op — a headless host that has not installed a
    /// broadcaster behaves exactly as before. <see cref="Server"/> sets this once
    /// at boot; the delegate runs on the simulation thread (inside the mission
    /// tick that created the missile), so it can reach <see cref="Server"/>'s
    /// sim-thread-owned peer map without a lock. The <see cref="Missile"/> arg
    /// (its <c>MissileType</c>, <c>Position</c>, <c>Velocity</c>, <c>Forward</c>,
    /// <c>Id</c> and <c>Launcher</c>) carries everything the wire
    /// <c>FM_CS_FIRE_MISSILE(70)</c> needs. The sibling of
    /// <see cref="ProjectileCreated"/>; joins the same host bridges out of the
    /// seam, everything else keeps the interface default.
    /// </summary>
    public Action<Missile>? MissileCreated;
    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.MissileCreatedEvent"/>: raise the
    /// <see cref="MissileCreated"/> hook so the owning <see cref="Server"/>
    /// can broadcast a real <c>FM_CS_FIRE_MISSILE(70)</c>.</summary>
    public void MissileCreatedEvent(Missile m) => MissileCreated?.Invoke(m);

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="IIgcSite.MissileDestroyedEvent"/> the missions raise from a
    /// missile's own expiry path (<c>Sim/Missile.cs</c> <c>OnUpdate</c>, alongside
    /// <c>sector.Remove(this)</c>). Null by default, matching the IIgcSite no-op.
    /// Delegate runs on the simulation thread (inside the sector tick that
    /// expired the missile), so it reaches the sim-thread-owned peer map without
    /// a lock. The <see cref="Missile"/> arg's <c>Id</c>, <c>Position</c> (where it
    /// expired, not where it launched) and <c>OwnerSector</c> supply the wire
    /// <c>FM_S_MISSILE_DESTROYED(83)</c>. The destruction sibling of
    /// <see cref="MissileCreated"/>/<see cref="ProjectileCreated"/>.
    /// </summary>
    public Action<Missile>? MissileDestroyed;
    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.MissileDestroyedEvent"/>: raise the
    /// <see cref="MissileDestroyed"/> hook so the owning <see cref="Server"/> can
    /// broadcast a real <c>FM_S_MISSILE_DESTROYED(83)</c>.</summary>
    public void MissileDestroyedEvent(Missile m) => MissileDestroyed?.Invoke(m);

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="IIgcSite.MineCreatedEvent"/> the world missions raise when
    /// <c>Ship.DeployMine</c> creates a real <see cref="Mine"/> model
    /// (<c>Sim/Ship.cs</c> after <c>sector.Add(mine)</c>). Null by default,
    /// matching the IIgcSite no-op — a headless host that has not installed a
    /// broadcaster behaves exactly as before. <see cref="Server"/> sets this once
    /// at boot; the delegate runs on the simulation thread (inside the mission
    /// tick that created the mine), so it can reach <see cref="Server"/>'s
    /// sim-thread-owned peer map without a lock. The <see cref="Mine"/> arg (its
    /// <c>MineType</c>, <c>Position</c>, <c>Id</c> and <c>Launcher</c>) carries
    /// everything the wire <c>FM_S_EXPORT(26)</c>/<c>OT_mine</c> needs. The
    /// sibling of <see cref="MissileCreated"/>; joins the same host bridges out
    /// of the seam, everything else keeps the interface default.
    /// </summary>
    public Action<Mine>? MineCreated;
    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.MineCreatedEvent"/>: raise the
    /// <see cref="MineCreated"/> hook so the owning <see cref="Server"/> can
    /// broadcast a real <c>FM_S_EXPORT(26)</c>/<c>OT_mine</c>.</summary>
    public void MineCreatedEvent(Mine m) => MineCreated?.Invoke(m);

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="IIgcSite.MineDestroyedEvent"/> the missions raise from a
    /// mine's own expiry path (<c>Sim/Mine.cs</c> <c>OnUpdate</c>, alongside
    /// <c>sector.Remove(this)</c>). Null by default, matching the IIgcSite no-op.
    /// Delegate runs on the simulation thread (inside the sector tick that
    /// expired the mine), so it reaches the sim-thread-owned peer map without a
    /// lock. The <see cref="Mine"/> arg's <c>Id</c>, <c>Position</c> and
    /// <c>OwnerSector</c> supply the wire <c>FM_S_MINE_DESTROYED(84)</c>. The
    /// destruction sibling of <see cref="MineCreated"/>.
    /// </summary>
    public Action<Mine>? MineDestroyed;
    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.MineDestroyedEvent"/>: raise the
    /// <see cref="MineDestroyed"/> hook so the owning <see cref="Server"/> can
    /// broadcast a real <c>FM_S_MINE_DESTROYED(84)</c>.</summary>
    public void MineDestroyedEvent(Mine m) => MineDestroyed?.Invoke(m);

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="IIgcSite.ProbeCreatedEvent"/> the world missions raise when
    /// <c>Ship.DeployProbe</c> creates a real <see cref="Probe"/> model
    /// (<c>Sim/Ship.cs</c> after <c>sector.Add(probe)</c>). Null by default,
    /// matching the IIgcSite no-op — a headless host that has not installed a
    /// broadcaster behaves exactly as before. <see cref="Server"/> sets this once
    /// at boot; the delegate runs on the simulation thread (inside the mission
    /// tick that created the probe), so it can reach <see cref="Server"/>'s
    /// sim-thread-owned peer map without a lock. The <see cref="Probe"/> arg (its
    /// <c>ProbeType</c>, <c>Position</c>, <c>Id</c>, <c>SideId</c> and
    /// <c>Launcher</c>) carries everything the wire
    /// <c>FM_S_EXPORT(26)</c>/<c>OT_probe</c> needs. The sibling of
    /// <see cref="MineCreated"/>; joins the same host bridges out of the seam,
    /// everything else keeps the interface default.
    /// </summary>
    public Action<Probe>? ProbeCreated;
    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.ProbeCreatedEvent"/>: raise the
    /// <see cref="ProbeCreated"/> hook so the owning <see cref="Server"/> can
    /// broadcast a real <c>FM_S_EXPORT(26)</c>/<c>OT_probe</c>.</summary>
    public void ProbeCreatedEvent(Probe p) => ProbeCreated?.Invoke(p);

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="IIgcSite.ProbeDestroyedEvent"/> the missions raise from a
    /// probe's own expiry path (<c>Sim/Probe.cs</c> <c>OnUpdate</c>, alongside
    /// <c>sector.Remove(this)</c>). Null by default, matching the IIgcSite no-op.
    /// Delegate runs on the simulation thread (inside the sector tick that
    /// expired the probe), so it reaches the sim-thread-owned peer map without a
    /// lock. The <see cref="Probe"/> arg's <c>Id</c>, <c>Position</c> and
    /// <c>OwnerSector</c> supply the wire <c>FM_S_PROBE_DESTROYED(85)</c>. The
    /// destruction sibling of <see cref="ProbeCreated"/> — and of
    /// <see cref="MineDestroyed"/>.
    /// </summary>
    public Action<Probe>? ProbeDestroyed;
    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.ProbeDestroyedEvent"/>: raise the
    /// <see cref="ProbeDestroyed"/> hook so the owning <see cref="Server"/> can
    /// broadcast a real <c>FM_S_PROBE_DESTROYED(85)</c>.</summary>
    public void ProbeDestroyedEvent(Probe p) => ProbeDestroyed?.Invoke(p);

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <see cref="IIgcSite.WarpBombEvent"/> the world missions raise when a live
    /// <see cref="Missile"/> hits a warp (<c>Sector.ResolveWarpBombTriggers</c>,
    /// P101). Null by default, matching the IIgcSite no-op. The delegate runs on
    /// the simulation thread (inside the sector tick that triggered the bomb), so
    /// it reaches the sim-thread-owned peer map without a lock. The concrete
    /// <see cref="ResolvedWarp"/> (its <c>Warp.WarpId</c>) and <see cref="Missile"/>
    /// (its <c>MissileType.ExpendableTypeId</c>) supply the wire
    /// <c>FM_S_WARP_BOMB(188)</c>. Unlike every prior ordnance hook
    /// (<c>MissileDestroyed</c>/<c>MineDestroyed</c>/<c>ProbeDestroyed</c> etc.,
    /// all same-side), this one must be broadcast to the WHOLE mission
    /// (<c>GetGroupRealSides()</c>, <c>FedSrv.CPP:12558</c>).
    /// </summary>
    public Action<ResolvedWarp, Missile>? WarpBomb;
    /// <summary>The world-mission site implementation of
    /// <see cref="IIgcSite.WarpBombEvent"/>: raise the <see cref="WarpBomb"/> hook
    /// so the owning <see cref="Server"/> can broadcast a real
    /// <c>FM_S_WARP_BOMB(188)</c>. Mirrors <see cref="HitWarpEvent"/>'s bridge
    /// shape (cast the marker interfaces to the concrete carriers; ignore a
    /// non-ResolvedWarp/non-Missile arg rather than fabricate).</summary>
    public void WarpBombEvent(IWarpIgc warp, IMissileIgc missile)
    {
        if (warp is not ResolvedWarp rw || missile is not Missile m) return;
        WarpBomb?.Invoke(rw, m);
    }

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <c>KillAsteroidEvent</c> the world missions raise when a real combat hit
    /// crosses an asteroid's <see cref="Asteroid.Fraction"/> to zero
    /// (<c>Model.Sector.ResolveCombatCollisions</c>, P102). Null by default,
    /// matching the IIgcSite no-op. The delegate runs on the simulation thread
    /// (inside the sector tick that killed the rock), so it reaches the
    /// sim-thread-owned peer map without a lock. The concrete <see cref="Asteroid"/>
    /// (its <c>Data.AsteroidId</c> / <c>Data.Name</c>) and the <paramref name="explodeF"/>
    /// flag supply the wire <c>FM_S_ASTEROID_DESTROYED(86)</c> (explodeF=true for
    /// combat death). This broadcast goes to the WHOLE mission
    /// (<c>GetGroupRealSides()</c>, <c>FedSrv.CPP:12418</c>) — the same audience
    /// P101 settled for <c>WARP_BOMB</c>, not a same-side gate.
    /// </summary>
    public Action<Asteroid, bool>? AsteroidKilled;

    /// <summary>The world-mission site implementation of <c>KillAsteroidEvent</c>
    /// (the <c>(IAsteroidIgc, bool)</c> overload — the only one this task
    /// implements; the side-scoped overloads are future work). Raises the
    /// <see cref="AsteroidKilled"/> hook so the owning <see cref="Server"/> can
    /// broadcast a real <c>FM_S_ASTEROID_DESTROYED(86)</c>. Mirrors
    /// <see cref="WarpBombEvent"/>'s bridge shape (cast the marker interface to
    /// the concrete carrier; ignore a non-Asteroid arg rather than fabricate).</summary>
    public void KillAsteroidEvent(IAsteroidIgc pasteroid, bool explodeF)
    {
        if (pasteroid is not Asteroid a) return;
        AsteroidKilled?.Invoke(a, explodeF);
    }

    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <c>KillStationEvent</c> the world missions raise when a real combat hit
    /// crosses a station's <see cref="Station.ArmorFraction"/> to zero
    /// (<c>Model.Sector.ResolveCombatCollisions</c>, P125 — the port of the
    /// native <c>pigc-&gt;KillStationEvent(...)</c> at <c>stationIGC.cpp:192</c>).
    /// Null by default, matching the IIgcSite no-op. The delegate runs on the
    /// simulation thread (inside the sector tick that destroyed the base), so it
    /// reaches the sim-thread-owned peer map without a lock. The concrete
    /// <see cref="Station"/> and the credited <see cref="Model"/> launcher (null
    /// when the round had no Ship launcher) supply the wire
    /// <c>FM_S_STATION_DESTROYED(59)</c> — <c>stationID</c> from the station,
    /// <c>launcher</c> from the launcher ship's id or NA (<c>FedSrv.CPP:12277-12278</c>).
    /// This broadcast goes to the WHOLE mission (<c>GetGroupRealSides()</c>,
    /// <c>FedSrv.CPP:12279</c>), and the host also answers with a VacateStation
    /// (eject every ship docked at the dead base, <c>fsmission.cpp:4783</c>).
    /// </summary>
    public Action<Station, Model?>? StationKilled;

    /// <summary>The world-mission site implementation of <c>KillStationEvent</c>
    /// (<c>stationIGC.cpp:192</c> → <c>FedSrvSiteBase::KillStationEvent</c>,
    /// <c>FedSrv.CPP:12237</c>). Raises the <see cref="StationKilled"/> hook so
    /// the owning <see cref="Server"/> can vacate docked ships and broadcast a
    /// real <c>FM_S_STATION_DESTROYED(59)</c>. Mirrors
    /// <see cref="KillAsteroidEvent"/>'s bridge shape (cast the marker interface
    /// to the concrete carrier; ignore a non-Station arg rather than fabricate;
    /// the launcher marker is cast to the concrete <see cref="Model"/> or null).</summary>
    public void KillStationEvent(IStationIgc station, IModelIgc launcher, float amount)
    {
        if (station is not Station s) return;
        StationKilled?.Invoke(s, launcher as Model);
    }


    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <c>CaptureStationEvent</c> the world missions raise when a hostile ship
    /// with a <c>c_habmBoard</c> hull docks an enemy station
    /// (<c>Model.Sector.ResolveDockCollisions</c>, P126 — the port of the native
    /// <c>FedSrvSiteBase::CaptureStationEvent</c> → <c>CFSShip::CaptureStation</c>,
    /// <c>FedSrv.CPP:13080-13084</c> / <c>fsship.cpp:577-641</c>). Null by
    /// default, matching the IIgcSite no-op. The delegate runs on the simulation
    /// thread (inside the sector tick that performed the capture) so it reaches
    /// the sim-thread-owned peer map without a lock. The concrete <see cref="Ship"/>
    /// capturer and the concrete <see cref="Station"/> are passed; the sector
    /// raises this seam <em>before</em> re-siding, so
    /// <see cref="Station.SideId"/> still holds the OLD owner when the handler
    /// reads <c>sidOld</c>. The native broadcast goes to the WHOLE mission
    /// (<c>GetGroupRealSides()</c>, <c>fsship.cpp:629</c>) — same audience as
    /// <see cref="StationKilled"/>'s STATION_DESTROYED. The sim-side capture
    /// state (fudge + re-side) is committed by the sector/station after this
    /// hook returns; only the broadcast belongs here.</summary>
    public Action<Ship, Station>? StationCaptured;

    /// <summary>The world-mission site implementation of <c>CaptureStationEvent</c>
    /// (<c>FedSrv.CPP:13080-13084</c> → <c>CFSShip::CaptureStation</c>). Raises
    /// the <see cref="StationCaptured"/> hook so the owning <see cref="Server"/>
    /// can broadcast a real <c>FM_S_STATION_CAPTURE(58)</c>. Mirrors
    /// <see cref="KillStationEvent"/>'s bridge shape (cast the marker interfaces
    /// to the concrete carriers; ignore a non-Ship / non-Station arg rather than
    /// fabricate).</summary>
    public void CaptureStationEvent(IShipIgc ship, IStationIgc station)
    {
        if (ship is not Ship sh) return;
        if (station is not Station s) return;
        StationCaptured?.Invoke(sh, s);
    }


    /// <summary>
    /// Optional hook the owning <see cref="Server"/> subscribes to receive every
    /// <c>MineAsteroidEvent</c> the world missions raise on a mining tick (the
    /// P55 analogue of <see cref="Payday"/>). The args are the
    /// IIgcSite.MineAsteroidEvent parameters verbatim: the asteroid just drained
    /// and its new ore fraction (<c>asteroidigc.h:249</c>). The hook only signals —
    /// ore itself already travels wire-visible as <c>ServerShipUpdate</c> byte 45
    /// (<c>OreFraction = Ship.Ore/Ship.MineCapacity</c>), so there is no new wire
    /// message to broadcast; this seam is the "engine observed mining happened"
    /// callback the reference host would use to relay/score. Null by default,
    /// matching the IIgcSite no-op; a headless host that has not installed a
    /// handler behaves as before.
    /// </summary>
    public Action<IAsteroidIgc, float>? Mine;

    /// <summary>The world-mission site implementation of <c>MineAsteroidEvent</c>:
    /// raise the <see cref="Mine"/> hook so the owning <see cref="Server"/> can
    /// react to a simulated mining accrual. Joins <see cref="PaydayEvent"/> /
    /// <see cref="KillShipEvent"/> / <see cref="DockWithStationEvent"/> as the
    /// gameplay callbacks this host bridges out of the seam; everything else
    /// keeps the interface default. Declared public (not explicit interface) so
    /// the hook and the site override live together; a class member shadows the
    /// interface default in dispatch.</summary>
    public void MineAsteroidEvent(IAsteroidIgc pasteroid, float newOre) =>
        Mine?.Invoke(pasteroid, newOre);
    public void ReportNotable(string text) => Notable?.Invoke(text);

    /// <summary>The world-mission site implementation of <c>KillShipEvent</c>: raise the
    /// <see cref="ShipKilled"/> hook so the owning <see cref="Server"/> can turn a
    /// simulated death into a wire <c>FM_S_KILL_SHIP(105)</c> broadcast. This is
    /// the one IIgcSite callback this host cares about — everything else keeps
    /// the interface default (no economy, no other gameplay). Declared public
    /// (not explicit interface) so the hook and the site override live together;
    /// a class member shadows the interface default in dispatch.
    /// </summary>
    public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2) =>
        ShipKilled?.Invoke(now, ship, launcher, amount, p1, p2);

    /// <summary>
    /// The world-mission site implementation of <c>DockWithStationEvent</c> —
    /// the actual dock <b>state transition</b>, ported from the skeleton of
    /// <c>FedSrvSiteBase::DockWithStationEvent</c>
    /// (<c>FedSrv.CPP:13087-13140</c>). Raised by the sector tick's dock pass
    /// (<see cref="Sector.ResolveDockCollisions"/>) on the sim thread when a
    /// ship arrives within the simplified dock radius of a friendly station.
    /// Sets the ship's <see cref="Ship.StationId"/>, which the existing
    /// per-tick <c>ShipStatus</c> assembly (<see cref="MakeOwnShipStatus"/>) now
    /// reads to populate the wire <c>StationID</c> and <c>State = c_ssDocked</c> —
    /// the whole wire-side change rides the already-implemented
    /// <c>FM_S_SHIP_STATUS(90)</c> broadcast (ShipStatus.cs:62,71,92,128). The
    /// <c>Server</c> needs no hook: it derives the status from the live
    /// <see cref="Ship"/> each broadcast, unlike <see cref="ShipKilled"/>/
    /// <see cref="ShipWarped"/>/<see cref="Payday"/> which broadcast
    /// side-channel events. Only friendly docking is handled — the native
    /// function's interleaved hostile-capture branch
    /// (<c>FedSrv.CPP:13100-13105</c>) is out of scope and absent, matching
    /// the sector pass's friendly-only gate. Returns the permission (true when
    /// docked); after the pass's gates it is effectively always true.
    /// </summary>
    public bool DockWithStationEvent(IShipIgc ship, IStationIgc station)
    {
        if (ship is not Ship sh)
        {
            Console.WriteLine("[dock] DockWithStationEvent with a non-Ship ship — ignored");
            return false;
        }
        if (station is not DockStation ds)
        {
            Console.WriteLine("[dock] DockWithStationEvent with a non-DockStation station — ignored");
            return false;
        }
        if (sh.StationId is not null)
        {
            // Already docked — the sector latch (Sector.ResolveDockCollisions
            // skips docked ships) should prevent this; honour it defensively.
            return true;
        }

        sh.StationId = ds.Station.StationId;

        // P56: sell any carried ore at the dock transition. This is exactly the
        // native sell path — a second PaydayEvent call (shipIGC.cpp:1969-1990:
        // GetIgcSite()->PaydayEvent(side, m_fOre * value * yield) then zero
        // m_fOre) — reusing the seam P51 wired (ServerHost.cs:121). Mirror the
        // established credit-then-report shape of Mission.Update's payday pass
        // (Mission.cs:298-300: `side.Money += income; Site.PaydayEvent(side,
        // income);`): credit the owning side's treasury first, then raise the
        // seam so Server.OnPayday (Server.cs:1413) broadcasts FM_S_PAYDAY(114)
        // to the side's peers. The dock gate above (StationId is not null →
        // early return) is the "newly docks" boundary, so a redundant dock call
        // never sells twice. Proceeds scale by the flat OreCreditPerUnit
        // constant, a stand-in for c_fcidValueHe3 × the per-side attribute-
        // scaled c_gaMiningYield (see Ship.cs) — a labelled simplification, not
        // a verified native value.
        if (sh.Ore > 0f)
        {
            var side = sh.Mission?.Sides.FirstOrDefault(s => s.Id == sh.SideId);
            if (side is null)
            {
                Console.WriteLine("[mine-sell] could not resolve the owning side to sell ore — skipped");
            }
            else
            {
                float proceeds = sh.Ore * Ship.OreCreditPerUnit;
                side.Money += (long)proceeds;              // credit treasury (conversion)
                PaydayEvent(side, proceeds);               // seam → host payout / FM_S_PAYDAY
                var sellNote = $"[mine-sell] ship {sh.Id} sold {sh.Ore:F1} ore at {Ship.OreCreditPerUnit:F0}/u = {proceeds:F1} credits to side {side.Id} — ore zeroed";
                Console.WriteLine(sellNote);
                ReportNotable(sellNote);                   // spectator event feed (same text)
                sh.Ore = 0f;
            }
        }

        var dockNote = $"[dock] ship {sh.Id} docked at station {ds.Station.StationId} '{ds.Station.Name}' (side {ds.Station.SideId}) in sector '{sh.Sector?.Name}' — next SHIP_STATUS(90) carries StationID + State=c_ssDocked";
        Console.WriteLine(dockNote);
        ReportNotable(dockNote);                            // spectator event feed (same text)
        // Signal the owning Server so it can send the FM_S_DOCKED(61) notify to
        // the docking peer — the wire trigger that makes the native client's
        // ship.GetStation() resolve (appmsg.cpp:1113). Mirrors how HitWarpEvent
        // raises ShipWarped for per-peer SET_CLUSTER routing (ServerHost.cs:279).
        ShipDocked?.Invoke(sh);
        return true;
    }

    /// <summary>The exit-speed boost added to a ship's entry speed when it
    /// leaves an aleph — <c>c_fcidExitWarpSpeed</c> (<c>fsship.cpp:126</c>).
    /// That constant is a mission float this port has not decoded (no constant
    /// namespace for warps exists yet; grep for <c>c_fcidExitWarpSpeed</c> in
    /// the C# repo finds nothing), so per the honesty-about-simplifications
    /// convention (cf. <c>ShipStatus.cs</c> P40 doc comment) a fixed value is
    /// substituted and called out here. It only adds a small nudge beyond the
    /// entry speed so the ship continues moving after transit; the exact native
    /// value is not meaningful to this server's scoped proof.</summary>
    private const float ExitWarpSpeedBonus = 5f;

    /// <summary>Uniform source for the small exit-offset jitter, matching the
    /// C++ <c>random(2.0f, 5.0f)</c> (<c>fsship.cpp:130-131</c>). Accessed only
    /// on the simulation thread, so no synchronization is needed.</summary>
    private readonly Random _rand = new();

    /// <summary>
    /// The world-mission site implementation of <c>HitWarpEvent</c>
    /// (<c>igc.h:4862</c>) — the actual warp transit, ported from
    /// <c>CFSShip::HitWarp</c> (<c>fsship.cpp:71-137</c>). Called by the sector
    /// tick's proximity pass (<c>Sector.ResolveWarpCollisions</c>) on the sim
    /// thread when a ship's position enters a warp's collision volume. Performs
    /// the full native sequence adapted to this server's data model:
    /// reposition (orientation / position / velocity per the native formula),
    /// reassign <see cref="Ship.Sector"/>, move the model between the two
    /// sectors' model lists, then raise <see cref="ShipWarped"/> so the owning
    /// <see cref="Server"/> can refresh its per-peer <c>LiveShip</c> record and
    /// send a fresh <c>SET_CLUSTER</c>.
    /// </summary>
    /// <remarks>
    /// Faithfulness notes (this repo's honesty-about-simplifications style —
    /// every deviation is explicit):
    /// <list type="bullet">
    /// <item><b>Zero wobble.</b> The C++ randomizes the exit orientation by a
    /// small angle proportional to how far off-centre the ship hit the warp
    /// (<c>fsship.cpp:96-122</c>) and spins the roll turn-rate. Simplified to
    /// zero wobble: the exit orientation is exactly the destination warp's,
    /// with no random pitch/yaw, so transit is deterministic. Allowed by the
    /// task and documented here.</item>
    /// <item><b>Orientation from Forward + world-up.</b> Native reads
    /// <c>pwarpDest->GetOrientation()</c> (<c>fsship.cpp:90</c>) — the
    /// destination warp's full rotation matrix. This port has no warp →
    /// matrix conversion, so the destination warp's <c>Forward</c> vector
    /// (decoded, <c>DataWarpIGC.Forward</c>) plus the world up (0,1,0) build
    /// the exit orientation via <c>Orientation.Set</c>. The forward — hence the
    /// exit velocity direction and the outward offset — matches native; only
    /// roll about the forward axis is unspecified.</item>
    /// <item><b>No mass-limit gate.</b> Native blocks overweight ships
    /// (<c>fsship.cpp:77</c>, <c>pwarp->MassLimit()</c>). <c>DataWarpIGC</c>
    /// does not decode a mass limit, so the gate is omitted (everything may
    /// transit).</item>
    /// <item><b>Fixed exit-speed bonus.</b> See <see cref="ExitWarpSpeedBonus"/>.</item>
    /// <item><b>Reverse is in place.</b> <c>Ship.Orientation</c> is a get-only,
    /// mutable class; <c>Orientation.Set</c> mutates it in place, so the ship
    /// keeps its identity (fine — a ship is uniquely owned here).</item>
    /// </list>
    /// Dangling edges (no destination sector / no partner warp) and dead ships
    /// are reported-not-fabricated: no transit occurs.
    /// </remarks>
    public void HitWarpEvent(IShipIgc ship, IWarpIgc warp)
    {
        if (ship is not Ship sh || warp is not ResolvedWarp rw) return;
        var destSector = rw.Destination;
        var partner = rw.Partner();
        if (destSector is null || partner is null || sh.Dead)
        {
            Console.WriteLine($"[warp] transit refused: {sh.Id} vs warp {rw.Warp.WarpId} " +
                              (destSector is null ? "(destination unresolved)" : partner is null ? "(no partner warp)" : "(ship dead)"));
            return;
        }
        var oldSector = sh.Sector;
        if (oldSector is null)
        {
            Console.WriteLine($"[warp] transit refused: ship {sh.Id} has no source sector");
            return;
        }

        // 1. Exit orientation = the DESTINATION warp's facing, from its Forward
        //    + world-up (zero wobble simplification; see remarks).
        float[] fwdSrc = partner.Warp.Forward is { Length: 3 } f ? f : new[] { 0f, 0f, -1f };
        sh.Orientation.Set(new Vector3(fwdSrc[0], fwdSrc[1], fwdSrc[2]), new Vector3(0f, 1f, 0f));
        Vector3 backward = sh.Orientation.GetBackward();

        // 2. Reverse the velocity direction at entry speed + the exit bonus
        //    (fsship.cpp:126-127).
        float entrySpeed = sh.Velocity.Length();
        sh.Velocity = backward * -(entrySpeed + ExitWarpSpeedBonus);

        // 3. Reposition at the destination warp, offset along up/right minus the
        //    ship's own radius+5 along backward so it does not immediately
        //    re-collide with the destination warp (fsship.cpp:129-132).
        float upOff = (float)_rand.Next(20, 51) / 10f;      // 2.0..5.0
        float rightOff = (float)_rand.Next(20, 51) / 10f;   // 2.0..5.0
        var destPos = partner.Warp.Position is { Length: 3 } p
            ? new Vector3(p[0], p[1], p[2])
            : Vector3.Zero;
        float shipRadius = 0.5f * sh.HullType.Length;
        sh.Position = destPos
            + sh.Orientation.GetUp() * upOff
            + sh.Orientation.GetRight() * rightOff
            - backward * (shipRadius + 5f);

        // 4. Sector reassignment — the whole point (C++ GetIGCShip()->SetCluster,
        //    fsship.cpp:134): move the model between the two sectors' lists and
        //    update Ship.Sector so every consumer (BroadcastShipStates,
        //    BroadcastKillShip, SelectChatTargets.AllSector, MakeOwnShipStatus)
        //    reads the new sector.
        oldSector.Remove(sh);
        destSector.Add(sh);
        sh.Sector = destSector;

        var warpNote = $"[warp] ship {sh.Id} crossed from '{oldSector.Name}' ({oldSector.Id}) " +
                       $"to '{destSector.Name}' ({destSector.Id}) via warp {rw.Warp.WarpId}";
        Console.WriteLine(warpNote);
        ReportNotable(warpNote);                            // spectator event feed (same text)
        ShipWarped?.Invoke(sh, oldSector, destSector);
    }

    /// <summary>P172 — optional hook the owning <see cref="Server"/> subscribes to
    /// receive an <b>NPC</b>-initiated ripcord routed through the IIgcSite seam
    /// (native <c>pigc->RequestRipcord(this, pclusterTarget)</c>,
    /// <c>shipIGC.cpp:4161</c>, <c>igc.h:4865</c>). The ripper is the concrete
    /// <see cref="Ship"/> and the target the concrete <see cref="Sector"/>
    /// (concrete-model pattern, like <see cref="ProjectileCreated"/> — the empty
    /// markers carry no members). No-op default via the absent hook, so a headless
    /// host that has not subscribed behaves exactly as before. The real-player
    /// ripcord path (FM_C_RIPCORD_REQUEST) does NOT route here — it calls
    /// <c>Server.RequestRipcord</c> directly with a real connection; only the IGC-
    /// level (NPC) caller needs this seam.</summary>
    public Action<Ship, Sector?>? RequestRipcordReceived;
    void IIgcSite.RequestRipcord(IShipIgc ship, IClusterIgc pcluster)
        => RequestRipcordReceived?.Invoke((Ship)ship, (Sector)pcluster);

    // ── ITransportSite ──────────────────────────────────────────────────────
    // These run on transport threads; they only reduce to an immutable event.
    void ITransportSite.OnPeerJoined(ConnectionId id, string name) => _events.Enqueue(new PeerJoinedEvt(id, name));
    void ITransportSite.OnPeerLeft(ConnectionId id) => _events.Enqueue(new PeerLeftEvt(id));
    void ITransportSite.OnSessionLost() => _events.Enqueue(new SessionLostEvt());
    void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data) =>
        _events.Enqueue(new ReceivedEvt(from, data.ToArray()));

    /// <summary>
    /// Atomically drain everything currently queued, invoking <paramref name="handle"/>
    /// for each event on the calling (simulation) thread. Safe to call every tick.
    /// </summary>
    public void Drain(Action<ITransportEvent> handle)
    {
        while (_events.TryDequeue(out var evt)) handle(evt);
    }

    /// <summary>Send bytes to <paramref name="target"/> through the bound transport.</summary>
    public void Send(ConnectionId target, byte[] data) => Transport!.Send(target, data, guaranteed: true);
}
