namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SET_BRIEFING_TEXT</c> (id 9), the server's per-player "here is
/// your mission briefing" join-sync message (<c>src/Inc/Messages.h</c>,
/// quoted in full):
/// <code>
///   DEFINE_FEDMSG(S, SET_BRIEFING_TEXT, 9)
///     FM_VAR_ITEM(text); // the full briefing text if fGenerateStandardText, else just the starting location
///     bool      fGenerateCivBriefing;
///   END_FEDMSG
/// </code>
/// Wire layout (packed FEDMESSAGE convention this port pins from
/// BuyLoadoutAck, P28 — var items first, then fixed fields, blobs appended;
/// <c>ib</c> measured from byte 0, <c>messagecore.h:777-786</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 9
///   offset 4: IB   ibText  (var descriptor)
///   offset 6: CB   cbText  (= CB_ZTS length incl. trailing NUL)
///   offset 8: bool  fGenerateCivBriefing  (1 byte)
///   offset 9: text blob (CB_ZTS: trailing-space-trimmed, NUL-terminated)
///   = 9 + blobLen bytes total
/// </code>
/// <para><b>Native call site / wire semantics</b> (P121): the reference server
/// emits this inside <c>CFSMission::TellTheyCanPlay2</c>'s join-sync burst
/// (<c>FedSrv/fsmission.cpp:722-732</c>) — one message to the single joining
/// player's own connection (<c>pfsPlayer-&gt;GetConnection()</c>), carrying
/// either a mission-authored custom story text (<c>m_strStoryText</c>,
/// <c>fGenerateCivBriefing = false</c>) or, when no custom text exists, just
/// the <b>starting cluster's name</b> with <c>fGenerateCivBriefing = true</c>
/// so the client generates its own civ-flavored copy around that one location
/// string.</para>
/// <para><b>Port mapping</b> (P121, read directly): this port does
/// <b>not</b> decode a mission-level custom story-text field
/// (<c>MissionDef</c> — confirmed: no <c>StoryText</c>/<c>Story</c> hit), so
/// <b>always</b> takes the <c>fGenerateCivBriefing = true</c> branch:
/// <c>Text</c> is the name of the joining player's side's first-found
/// friendly station's cluster (reusing P105's <c>FindFriendlyStation</c>
/// stand-in, <c>Server.cs:4750-4759</c>, for the native
/// nearest-station/<c>GetBase()</c> lookup), and
/// <c>FGenerateCivBriefing</c> is always <c>true</c>. The custom-text branch
/// is a real, separate gap, out of scope for P121 — there is simply no
/// <c>m_strStoryText</c> in this port to transmit.</para>
/// </summary>
public sealed class SetBriefingText : IFedMessage
{
    public const ushort Fmid = 9;

    /// <summary>Wire width of the fixed region before the text blob:
    /// header(4) + one ib/cb var pair(4) + the bool(1) = 9. The packed
    /// convention (BuyLoadoutAck, P28) places the blob immediately after the
    /// last fixed field with no alignment pad.</summary>
    public const int FixedSize = 4 + VarItem.ItemSize + 1;

    /// <summary>The briefing text (<c>text</c>, CB_ZTS). In this port's
    /// always-generate-civ-briefing mapping it is the starting cluster's name
    /// (the joining side's first-found friendly station's sector).</summary>
    public string Text = "";

    /// <summary>
    /// <c>fGenerateCivBriefing</c> — whether the client should generate its
    /// own civ-flavored briefing copy around <see cref="Text"/> (vs. sending
    /// a complete authored story verbatim). P121 always sends <c>true</c>
    /// (there is no custom story-text field in this port); <c>false</c> is
    /// decoded/encoded faithfully so the codec is byte-correct against the
    /// reference protocol.
    /// </summary>
    public bool FGenerateCivBriefing;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobLen = ZtsBlob.BlobSize(Text);
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ibText @4
        int slotCb = w.Reserve(2);  // cbText @6
        w.WriteByte(FGenerateCivBriefing ? (byte)1 : (byte)0);   // @8
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"SET_BRIEFING_TEXT internal layout drift: {w.Position} != {FixedSize}");

        // blob: CB_ZTS, at the fixed-region end (ib = offset from byte 0)
        var (ib, cb) = ZtsBlob.Write(ref w, Text);
        w.WriteU16At(slotIb, ib);
        w.WriteU16At(slotCb, cb);
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg = total length
        return buf;
    }

    public static SetBriefingText Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_BRIEFING_TEXT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_BRIEFING_TEXT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        bool gen = buf[8] != 0;

        return new SetBriefingText
        {
            Text = it.IsPresent ? ZtsBlob.Read(buf, it) : "",
            FGenerateCivBriefing = gen,
        };
    }
}
