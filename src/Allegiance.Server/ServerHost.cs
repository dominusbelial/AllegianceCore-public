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

        Console.WriteLine($"[warp] ship {sh.Id} crossed from '{oldSector.Name}' ({oldSector.Id}) " +
                          $"to '{destSector.Name}' ({destSector.Id}) via warp {rw.Warp.WarpId}");
        ShipWarped?.Invoke(sh, oldSector, destSector);
    }

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
