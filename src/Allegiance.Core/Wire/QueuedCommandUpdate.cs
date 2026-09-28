namespace Allegiance.Core.Wire;

/// <summary>
/// <c>QUEUED_COMMAND_UPDATE</c> (id 215) — the port's <b>owner-only</b>
/// extension message that tells a player's own client whether it currently has
/// a <b>pending queued order</b> (a command another player — or, in this port's
/// test-only path, a test/demo helper — has sent to this ship and which the
/// player has not yet accepted or rejected).
///
/// <para>Why a new message. Native exposes a received-but-unaccepted order as a
/// slot on the <em>client's</em> own copy of the ship (<c>c_cmdQueued</c>,
/// <c>m_commandTargets[c_cmdQueued]</c>/<c>m_commandIDs[c_cmdQueued]</c>,
/// <c>shipIGC.h:2631</c>), populated by <c>CTrekClientWindow::SetQueuedCommand</c>
/// (<c>WinTrek.cpp:10713-10727</c>) and displayed/consumed client-side. This
/// port's MonoGame client is a <b>pure renderer with no static sim</b> (the
/// same fact P230/P234/P423 established for missile-lock/mining/ore), and its
/// real-player queued/accepted command slots live <b>server-side</b> on
/// <c>Ship.QueuedCommandId</c>/<c>Ship.AcceptedShipCommandId</c>. So the port
/// must tell the receiving player's client that an order is pending, exactly
/// as native's client learns it locally — this message is that delivery, and
/// it rides the same owner-only, above-native-FMID-max (200) convention P230
/// (<c>MissileLockUpdate</c> 204) established.</para>
///
/// <para>Semantics: when <see cref="CommandId"/> is <see cref="OrderChange.CidNone"/>
/// (-1) the player has no pending queued order (fresh / accepted / rejected /
/// auto-rejected). Otherwise the player has a pending queued order whose action
/// is <see cref="CommandId"/> aimed at <see cref="TargetId"/> (of type
/// <see cref="TargetType"/>; NA when the order carries no target). The
/// MonoGame client echoes <see cref="CommandId"/> back on its ACCEPT key so the
/// server can recognise "the player is honouring <em>this</em> pending order"
/// (native's <c>AcceptQueuedCommand</c>, <c>WinTrek.cpp:10626-10712</c>).</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 215
///   offset 4: short  commandID    (CommandID — the pending order's action; NA -1 = none)
///   offset 6: short  objectType   (ObjectType — of the pending order's target; OT_invalid when none)
///   offset 8: short  objectID     (ObjectID — of the pending order's target; NA when none)
///   = 10 B fixed, no var items → cbmsg = 10
/// </code>
/// Same short/short/short fixed shape as the <c>ORDER_CHANGE</c> payload fields,
/// distinct in fmid and direction (server → the single owning peer).
/// </summary>
public sealed class QueuedCommandUpdate : IFedMessage
{
    public const ushort Fmid = 215;

    /// <summary>Wire width of the whole message: header(4) + commandID(2) +
    /// objectType(2) + objectID(2). No alignment gaps.</summary>
    public const int FixedSize = 10;

    /// <summary>The pending queued order's action (<c>CommandID</c>), or
    /// <see cref="OrderChange.CidNone"/> (-1) when there is no pending order.</summary>
    public short CommandId;

    /// <summary>The <see cref="IgcObjectType"/> of the pending order's target;
    /// <c>OT_invalid</c> (-1) when the order carries no target.</summary>
    public IgcObjectType TargetType;

    /// <summary>The <see cref="ObjectID"/> of the pending order's target;
    /// NA (-1) when the order carries no target.</summary>
    public short TargetId;

    /// <summary>True iff a queued order is currently pending (id != CidNone).</summary>
    public bool HasPending => CommandId != OrderChange.CidNone;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                                  // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(CommandId);                          // offset 4: commandID
        w.WriteI16((short)TargetType);                  // offset 6: objectType
        w.WriteI16(TargetId);                           // offset 8: objectID
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"QUEUED_COMMAND_UPDATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static QueuedCommandUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"QUEUED_COMMAND_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected QUEUED_COMMAND_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new QueuedCommandUpdate
        {
            CommandId = r.ReadI16(),
            TargetType = (IgcObjectType)r.ReadI16(),
            TargetId = r.ReadI16(),
        };
    }
}
