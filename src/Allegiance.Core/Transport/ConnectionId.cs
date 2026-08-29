namespace Allegiance.Core.Transport;

/// <summary>
/// A stable, opaque identifier for one remote participant in a session — the
/// C# equivalent of DirectPlay's <c>DPID</c> (<c>typedef DWORD</c>,
/// <c>messagecore.h:23</c>). Used by the transport to address sends and to
/// identify the <c>from</c> peer on receive / lifecycle events.
///
/// It is a <see langword="readonly record struct"/> wrapping a <c>uint</c>
/// (value equality + immutable). The concrete integer sequence is a transport
/// detail (host mints them; the client's transport fabricates its own), exactly
/// as DirectPlay assigned them before. We kept the underlying type an unsigned
/// 32-bit to mirror <c>DWORD</c>, but consumers treat it as opaque.
/// </summary>
public readonly record struct ConnectionId(uint Value)
{
    /// <summary>Conventional self id: the host and every client are <c>0</c>.</summary>
    public static readonly ConnectionId Me = new(0);

    /// <summary>Conventional server id used by the client half to address its one peer.</summary>
    public static readonly ConnectionId Server = new(1);

    public static explicit operator ConnectionId(uint value) => new(value);
    public static explicit operator uint(ConnectionId id) => id.Value;

    public override string ToString() => Value.ToString();
}
