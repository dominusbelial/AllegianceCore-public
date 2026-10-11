namespace Allegiance.Core.Transport;

/// <summary>
/// The minimal seam a message layer needs from its transport (§2 of
/// TRANSPORT_SEAM.md, adapted from <c>class ITransport</c>). Unlike the C++
/// sketch there is no <c>HRESULT</c>/<c>DPID</c> — the C# idiomatic choices are
/// **exceptions** (declared below) and a **<see cref="ConnectionId"/> value
/// type**. Every method is synchronous.
///
/// Deliberately left out of the interface (owned by the message/codec layer):
/// frame composition, the trailing CRC, packet walking, recipient bookkeeping.
/// The transport deals only in ids and bytes — a receive carries a whole
/// framed datagram (messages + CRC), which the caller validates with
/// <see cref="Allegiance.Core.Wire.FedPacket"/>.
/// </summary>
public interface ITransport
{
    /// <summary>Bind and listen. <paramref name="port"/> 0 selects an ephemeral
    /// port (see <see cref="BoundPort"/>). Throws on bind failure.</summary>
    void Host(ushort port = 0);

    /// <summary>Connect to an already-hosting peer and publish our name.
    /// Throws on connect failure.</summary>
    void Join(string address, ushort port, string playerName);

    /// <summary>Tear the session down. Idempotent.</summary>
    void Shutdown();

    /// <summary>True while the transport is live (listening, or connected).</summary>
    bool IsConnected { get; }

    /// <summary>Our own connection id (0 on both halves).</summary>
    ConnectionId LocalId { get; }

    /// <summary>Host only: the actual bound port (useful when 0/ephemeral was
    /// requested). On the client half this is 0.</summary>
    ushort BoundPort { get; }

    /// <summary>
    /// Send a framed datagram (the caller's <c>FedPacket.Pack</c> output —
    /// messages + trailing CRC) to <paramref name="target"/>. On the host half
    /// <paramref name="target"/> is a peer id returned by
    /// <see cref="ITransportSite.OnPeerJoined"/>; on the client half it is
    /// conventionally <see cref="ConnectionId.Server"/> but any value routes to
    /// the single connected socket.
    /// </summary>
    /// <remarks><paramref name="guaranteed"/> is accepted and ignored: TCP is a
    /// reliable stream, so best-effort adds nothing here (the two best-effort
    /// call sites in the C++, §3.2 of TRANSPORT_SEAM.md, are covered by a
    /// runtime bool and not on the bot path).</remarks>
    void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed);

    /// <summary>
    /// P606 — send with a per-peer <b>coalesce key</b>. When <paramref name="coalesceKey"/>
    /// is non-zero the frame is a <b>latest-wins</b> stream packet: if the
    /// peer's send queue already holds a pending frame for the same key (e.g. a
    /// previous tick's ship-state batch), the older one is superseded in place
    /// rather than queued as a stale duplicate, so a slow client's backlog stays
    /// small and current. Zero = ordinary ordered traffic (never coalesced).
    /// Mirrors native's DPNSEND_NONGUARANTEED + DPNSEND_COALESCE split — see
    /// Messages.cpp:548-563 (guaranteed: DPNSEND_GUARANTEED|COALESCE, never
    /// times out; best-effort: DPNSEND_NONSEQUENTIAL, drops at 500 ms) and
    /// FedSrv.CPP:4189-4196 (HEAVY/LIGHT_SHIPS_UPDATE sent FM_NOT_GUARANTEED).
    /// </summary>
    void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey);
}

/// <summary>
/// Events the transport raises onto a single, serialized dispatch path —
/// mirroring the DirectPlay callback funnel (§2 of TRANSPORT_SEAM.md,
/// <c>class ITransportSite</c>). An implementor that wants `FedMessaging`'
/// single-owner semantics synchronizes these (e.g. through a FIFO queue).
/// </summary>
public interface ITransportSite
{
    /// <summary>A peer joined. Raised AFTER the transport has assigned
    /// <paramref name="id"/> and BEFORE any <see cref="OnReceive"/> for that
    /// peer (so the site's connection bookkeeping sees a consistent id first).</summary>
    void OnPeerJoined(ConnectionId id, string name);

    /// <summary>A peer disconnected or was dropped.</summary>
    void OnPeerLeft(ConnectionId id);

    /// <summary>The host session died / a client lost the session.</summary>
    void OnSessionLost();

    /// <summary>A datagram arrived from <paramref name="from"/>: the bytes are a
    /// full framed packet (messages + trailing CRC); validate with
    /// <see cref="Allegiance.Core.Wire.FedPacket.Unpack"/>.</summary>
    void OnReceive(ConnectionId from, ReadOnlySpan<byte> data);
}
