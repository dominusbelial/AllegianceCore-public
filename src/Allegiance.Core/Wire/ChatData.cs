namespace Allegiance.Core.Wire;

/// <summary>
/// The <c>ChatData cd</c> member shared by every chat-family message
/// (<c>Messages.h:204-214</c>): <c>FM_CS_CHATMESSAGE</c>(23) and
/// <c>FM_CS_CHATBUOY</c>(108) both declare it verbatim
/// (<c>Messages.h:216-222</c> and <c>Messages.h:716-722</c>), so it is factored
/// into one type both codecs embed rather than duplicated (the codebase's
/// preference for reuse where the shapes genuinely overlap). Layout (MSVC
/// default packing, offsets relative to the start of this block — the message
/// places it at absolute offset 8):
/// <code>
///   offset 0: short  sidSender             (ShipID = ObjectID = short, igc.h:477,480)
///   offset 2: 2 pad bytes                  (align int chatTarget to 4)
///   offset 4: int    chatTarget            (ChatTarget = C++ enum int, igc.h:2729)
///   offset 8: short  oidRecipient          (ObjectID short, igc.h:477)
///   offset 10: short voiceOver             (SoundID short, igc.h:453)
///   offset 12: sbyte  commandID            (CommandID = char, igc.h:897)
///   offset 13: bool   bObjectModel         (1 byte)
///   offset 14-15: pad                     (block aligned to 4 → size 16)
/// </code>
/// Byte-for-byte decode equivalence with ChatMessage's original inline fields
/// is proven by <c>ChatMessageWireTests.ChatMessage_EmitsExactDeclaredBytes</c>,
/// which still passes unchanged (the public accessors below preserve the old
/// API). Used by the server only to read <see cref="ChatTarget"/> and
/// <see cref="OidRecipient"/> for routing; the relay forwards the raw original
/// bytes.
/// </summary>
public struct ChatData
{
    /// <summary>Wire width of the block (padded to 4-align, size 16).</summary>
    public const int Size = 16;

    public short SidSender;
    /// <summary>Raw wire value of <c>chatTarget</c> (a 4-byte C++ enum).</summary>
    public int ChatTarget;
    public short OidRecipient;
    public short VoiceOver;
    /// <summary>C++ <c>char</c> (<c>igc.h:897</c>) — widened here so -1
    /// (<c>c_cidNone</c>) survives, still one wire byte.</summary>
    public short CommandID;
    public bool BObjectModel;

    /// <summary>Serialize the 16-byte block (relative offsets above; the two
    /// pad bytes are written as zero).</summary>
    public byte[] Encode()
    {
        var b = new byte[Size];
        BitConverter.GetBytes(SidSender).CopyTo(b, 0);
        // b[2..3] pad zeros
        BitConverter.GetBytes(ChatTarget).CopyTo(b, 4);
        BitConverter.GetBytes(OidRecipient).CopyTo(b, 8);
        BitConverter.GetBytes(VoiceOver).CopyTo(b, 10);
        b[12] = unchecked((byte)(sbyte)CommandID);
        b[13] = BObjectModel ? (byte)1 : (byte)0;
        // b[14..15] pad zeros
        return b;
    }

    /// <summary>Deserialize the 16-byte block (relative offsets above).</summary>
    public static ChatData Decode(ReadOnlySpan<byte> src)
    {
        if (src.Length < Size)
            throw new ArgumentException($"ChatData needs >= {Size} bytes, got {src.Length}");
        return new ChatData
        {
            SidSender    = BitConverter.ToInt16(src.Slice(0)),
            ChatTarget   = BitConverter.ToInt32(src.Slice(4)),
            OidRecipient = BitConverter.ToInt16(src.Slice(8)),
            VoiceOver    = BitConverter.ToInt16(src.Slice(10)),
            CommandID    = unchecked((sbyte)src[12]),
            BObjectModel = src[13] != 0,
        };
    }
}
