using System.Collections.Concurrent;
using Allegiance.Core.Collision;
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
    /// The world-mission site implementation of <c>KillShipEvent</c>: raise the
    /// <see cref="ShipKilled"/> hook so the owning <see cref="Server"/> can turn a
    /// simulated death into a wire <c>FM_S_KILL_SHIP(105)</c> broadcast. This is
    /// the one IIgcSite callback this host cares about — everything else keeps
    /// the interface default (no economy, no other gameplay). Declared public
    /// (not explicit interface) so the hook and the site override live together;
    /// a class member shadows the interface default in dispatch.
    /// </summary>
    public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2) =>
        ShipKilled?.Invoke(now, ship, launcher, amount, p1, p2);

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
