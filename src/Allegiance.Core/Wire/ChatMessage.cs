using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_CHATMESSAGE</c> (id 23), bidirectional (<c>Messages.h:216-223</c>).
/// A client sends it; the server relays it. Variable-length <c>Message</c>
/// string plus a fixed <c>ChatData</c> struct plus an <c>oidTarget</c> /
/// <c>otTarget</c> pair. Layout (MSVC default packing; see FEDMSG.md §2 for the
/// rule — each member aligned to its own size, struct aligned to largest):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 23
///   offset 4: ushort ibMessage   (var item — FEDMSG.md §2, messagecore.h:693)
///   offset 6: ushort cbMessage   (length includes the trailing NUL)
///   offset 8: ChatData cd:                      (Messages.h:204-214; see ChatData.cs)
///       offset  8: short  sidSender             (ShipID = ObjectID = short, igc.h:477,480)
///       offset 10: 2 pad bytes                  (align int chatTarget to 4)
///       offset 12: int    chatTarget            (ChatTarget = C++ enum int, igc.h:2729)
///       offset 16: short  oidRecipient          (ObjectID short, igc.h:477)
///       offset 18: short  voiceOver             (SoundID short, igc.h:453)
///       offset 20: sbyte  commandID             (CommandID = char, igc.h:897)
///       offset 21: bool   bObjectModel          (1 byte)
///       = 14 → padded to 16 (4-align)           (next member at 24)
///   offset 24: short  oidTarget                 (ObjectID short, igc.h:477)
///   offset 26: short  otTarget                  (ObjectType short, igc.h:115)
///   = 28 B fixed, 4-aligned
/// blob: Message (CB_ZTS string) at offset 28 → cbmsg = 28 + blobLen
/// </code>
/// The C++ server asserts <c>ibMessage == sizeof(FMD_CS_CHATMESSAGE)</c>
/// (<c>FedSrv.CPP:4922</c>) and reads the blob via
/// <c>FM_VAR_REF(pfmChat, Message)</c> (<c>FedSrv.CPP</c>); the client builds
/// the blob with <c>FM_VAR_PARM(pszText, CB_ZTS)</c> (<c>clintlib.cpp:2444-2445</c>),
/// i.e. trailing-space-trimmed, NUL-terminated, <c>cb</c> includes the NUL.
/// The client drops an echoed chat whose <c>sidSender</c> is its own ship
/// (<c>appmsg.cpp:2644</c>).
/// </summary>
public sealed class ChatMessage : IFedMessage, IChatRoutingTarget
{
    public const ushort Fmid = 23;

    /// <summary>Wire size of the fixed region: header + one ib/cb pair +
    /// ChatData + oidTarget/otTarget. The Message blob starts at this offset.</summary>
    public const int FixedSize = 28;

    /// <summary>The chat text. Encoded as a CB_ZTS string (trailing spaces
    /// trimmed, one trailing NUL, <c>cb</c> includes the NUL) exactly as the
    /// client's <c>FM_VAR_PARM(pszText, CB_ZTS)</c> does.</summary>
    public string Message = "";
    /// <summary>Native null pszText: cbMessage=0 selects a sonic-chat index
    /// instead of a direct SoundID (trekigc.cpp:4507-4536).</summary>
    public bool MessageIsAbsent;

    // The ChatData block (Messages.h:204-214), factored into ChatData.cs so it
    // is shared with ChatBuoy(108) (Messages.h:716-722) rather than duplicated
    // per codec — the two shapes genuinely overlap, matching this codebase's
    // reuse-over-duplication preference. The public fields below were the
    // original inline members of this class; they are now forwarding accessors
    // onto Cd, preserving the exact API Bot.SendChat, tools/chatdemo and the
    // wire tests use (they set/read them via object initializer or property
    // access, so the property-vs-field change is invisible to them).
    public ChatData Cd = new() { SidSender = -1, VoiceOver = -1 };

    public short SidSender { get => Cd.SidSender; set => Cd.SidSender = value; }
    /// <summary>Raw wire value of <c>chatTarget</c> (a 4-byte C++ enum; see
    /// <c>AGCIDL.idl:450-471</c>). Kept as <see cref="int"/> because the wire
    /// field is a C++ int enum, wider than the sim-layer
    /// <see cref="Sim.ChatTarget"/> (short).</summary>
    public int ChatTarget { get => Cd.ChatTarget; set => Cd.ChatTarget = value; }
    public short OidRecipient { get => Cd.OidRecipient; set => Cd.OidRecipient = value; }
    public short VoiceOver { get => Cd.VoiceOver; set => Cd.VoiceOver = value; }
    public short CommandID { get => Cd.CommandID; set => Cd.CommandID = value; }   // C++ char (igc.h:897) — widened here so -1 (c_cidNone) survives, still one wire byte
    public bool BObjectModel { get => Cd.BObjectModel; set => Cd.BObjectModel = value; }
    public short OidTarget;
    public short OtTarget;

    int IChatRoutingTarget.ChatTarget => ChatTarget;
    short IChatRoutingTarget.OidRecipient => OidRecipient;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobLen = MessageIsAbsent ? 0 : ZtsBlob.BlobSize(Message);
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ibMessage @4
        int slotCb = w.Reserve(2);  // cbMessage @6

        // ChatData (16 B at offset 8) — shared block, factored into ChatData.cs
        w.WriteBytes(Cd.Encode());
        w.WriteI16(OidTarget);
        w.WriteI16(OtTarget);
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"CHATMESSAGE internal layout drift: {w.Position} != {FixedSize}");

        // blob: CB_ZTS, at the fixed-region end (ib = offset from byte 0)
        // PFedMsgCreate always writes the blob boundary, including NULL blobs
        // (_Utility/Messages.cpp:403-413; FedSrv.CPP:4922 validates this offset).
        var (ib, cb) = MessageIsAbsent ? ((ushort)FixedSize, (ushort)0) : ZtsBlob.Write(ref w, Message);
        w.WriteU16At(slotIb, ib);
        w.WriteU16At(slotCb, cb);
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg = total length
        return buf;
    }

    public static ChatMessage Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CHATMESSAGE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CHATMESSAGE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair

        return new ChatMessage
        {
            Message = it.IsPresent ? ZtsBlob.Read(buf, it) : "",
            MessageIsAbsent = !it.IsPresent,
            Cd = ChatData.Decode(buf.Slice(8)),
            OidTarget = BitConverter.ToInt16(buf.Slice(24)),
            OtTarget  = BitConverter.ToInt16(buf.Slice(26)),
        };
    }
}
