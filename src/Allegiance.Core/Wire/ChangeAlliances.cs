namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_CHANGE_ALLIANCES</c> (id 197, <c>Messages.h:1022-1024</c>, quoted in
/// full):
/// <code>
///   DEFINE_FEDMSG(S, CHANGE_ALLIANCES, 197) // sent by server when alliances change
///     char      Allies[c_cSidesMax];
///   END_FEDMSG
/// </code>
/// Server-only: the server announces the whole mission's alliance groups after
/// any <c>CHANGE_ALLIANCE(196)</c> (or a normalization-only <c>sideID == NA</c>
/// call) — the port of native <c>UpdateAlliances</c>'s final broadcast
/// (<c>fsmission.cpp:5797-5807</c>). <c>Allies[i]</c> holds side <c>i</c>'s
/// alliance-group number (a small <c>0..2</c> index into the group table), or
/// <c>NA</c> (-1, wire byte <c>0xFF</c>) for "no alliance". Two sides are
/// allied iff they carry the <b>same</b> non-<c>NA</c> group value (a group
/// id, not a bitmask — see <c>fsmission.cpp:5750-5785</c> and the P190 report).
/// <c>c_cSidesMax = 6</c> (<c>igc.h:507</c>); a fixed 6-byte region:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 197
///   offset 4: char[6] Allies   (signed char; -1 = NA)
///   = 10 B fixed
/// </code>
/// </summary>
public sealed class ChangeAlliances : IFedMessage
{
    public const ushort Fmid = 197;

    /// <summary>Width of the <c>Allies</c> array — <c>c_cSidesMax = 6</c>.</summary>
    public const int SideAlliesMax = 6;

    /// <summary>Whole-message wire width: header(4) + 6 = 10.</summary>
    public const int FixedSize = 4 + SideAlliesMax;

    /// <summary>Per-side alliance-group value (<c>char</c> on the wire, signed
    /// byte here): side <c>i</c>'s group number (0..2) or <c>-1</c> (NA) for no
    /// alliance. Slot <c>i</c> corresponds to side id <c>i</c>.</summary>
    public sbyte[] Allies { get; set; } = new sbyte[SideAlliesMax];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        if (Allies.Length != SideAlliesMax)
            throw new ArgumentException($"CHANGE_ALLIANCES Allies must be {SideAlliesMax} entries");
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        for (int i = 0; i < SideAlliesMax; i++)
            w.WriteByte(unchecked((byte)Allies[i]));   // -1 (NA) -> 0xFF
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"CHANGE_ALLIANCES internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ChangeAlliances Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CHANGE_ALLIANCES needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CHANGE_ALLIANCES fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        var allies = new sbyte[SideAlliesMax];
        for (int i = 0; i < SideAlliesMax; i++)
            allies[i] = unchecked((sbyte)r.ReadByte());
        return new ChangeAlliances { Allies = allies };
    }
}
