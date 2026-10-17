using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_CHATBUOY</c> (id 108), the message <c>BaseClient::SendChat</c>
/// actually uses to send a chat targeting a buoy object —
/// <c>clintlib/clintlib.cpp:2432-2444</c> and <c>:2460-2469</c>: whenever
/// <c>otTarget == OT_buoy</c>, it queues CHATBUOY(108) rather than
/// CHATMESSAGE(23). The commander's dispatch to a docked miner
/// (<c>CommanderResearchAndExpand.cs:1610,1615-1618</c>, a real live decision)
/// passes <c>otTarget: buoy.GetObjectType(), oidTarget: buoy.GetObjectID()</c>,
/// so this is the fmid that carries the actual order — and one
/// <c>AllegianceCore</c> previously logged
/// <c>[wire] unsupported fmid 108 — ignored</c> for, silently dropping the
/// dispatch. Declaration (<c>src/Inc/Messages.h:716-722</c>):
/// <code>
///   DEFINE_FEDMSG(CS, CHATBUOY, 108)
///     FM_VAR_ITEM(Message);
///     ChatData      cd;
///     DataBuoyIGC   db;
///   END_FEDMSG
/// </code>
/// Compare <c>FM_CS_CHATMESSAGE</c>(23) (<c>Messages.h:216-222</c>, ported in
/// <see cref="ChatMessage"/>): both share the same <see cref="ChatData cd"/>
/// block; CHATBUOY replaces CHATMESSAGE's trailing <c>oidTarget</c>/<c>otTarget</c>
/// pair with a whole embedded <see cref="DataBuoyIGC db"/> record (20 B).
/// Layout (MSVC default packing, FEDMSG.md §2):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 108
///   offset 4: ushort ibMessage
///   offset 6: ushort cbMessage
///   offset 8: ChatData cd        (16 B — see ChatData.cs, identical to CHATMESSAGE)
///   offset 24: DataBuoyIGC db    (20 B — position 12, clusterID 2, type 1, buoyID 2, visible 1)
///   = 44 B fixed
/// blob: Message (CB_ZTS) at offset 44 → cbmsg = 44 + blobLen
/// </code>
/// The server only needs to route this message, not reinterpret it, so like
/// <c>CS_PING</c>'s echo (<c>Server.cs:1352</c>) and <c>ChatMessage</c>'s own
/// relay (<c>Server.cs:1281</c>) it forwards <c>FedPacket.Pack(new[] { m })</c>
/// verbatim — the original bytes, never re-encoded. Decode/fields serve the
/// <c>[chatbuoy]</c> log line and the router's needs.
/// </summary>
public sealed class ChatBuoy : IFedMessage, IChatRoutingTarget
{
    public const ushort Fmid = 108;

    /// <summary>Wire size of the fixed region: header + one ib/cb pair +
    /// ChatData + DataBuoyIGC. The Message blob starts at this offset.</summary>
    public const int FixedSize = 44;

    /// <summary>The chat text (CB_ZTS, as in <see cref="ChatMessage.Message"/>).</summary>
    public string Message = "";
    /// <summary>Null native text selects a sonic index, as in CHATMESSAGE.</summary>
    public bool MessageIsAbsent;

    /// <summary>The shared <see cref="ChatData cd"/> block (Messages.h:716-722).</summary>
    public ChatData Cd;

    /// <summary>The embedded <see cref="DataBuoyIGC db"/> record (Messages.h:716-722).</summary>
    public DataBuoyIGC Db;

    public ushort GetFmid() => Fmid;

    public int ChatTarget => Cd.ChatTarget;
    public short OidRecipient => Cd.OidRecipient;

    public byte[] Encode()
    {
        int blobLen = MessageIsAbsent ? 0 : ZtsBlob.BlobSize(Message);
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ibMessage @4
        int slotCb = w.Reserve(2);  // cbMessage @6

        // ChatData (16 B at offset 8)
        w.WriteBytes(Cd.Encode());
        // DataBuoyIGC (20 B at offset 24)
        w.WriteBytes(Db.Encode());
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"CHATBUOY internal layout drift: {w.Position} != {FixedSize}");

        // blob: CB_ZTS, at the fixed-region end (ib = offset from byte 0)
        var (ib, cb) = MessageIsAbsent ? ((ushort)FixedSize, (ushort)0) : ZtsBlob.Write(ref w, Message);
        w.WriteU16At(slotIb, ib);
        w.WriteU16At(slotCb, cb);
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg = total length
        return buf;
    }

    public static ChatBuoy Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CHATBUOY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CHATBUOY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair

        return new ChatBuoy
        {
            Message = it.IsPresent ? ZtsBlob.Read(buf, it) : "",
            MessageIsAbsent = !it.IsPresent,
            Cd = ChatData.Decode(buf.Slice(8)),
            Db = DataBuoyIGC.Decode(buf.Slice(24)),
        };
    }
}
