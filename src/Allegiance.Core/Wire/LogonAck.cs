using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// FM_S_LOGONACK (id 2), the server's reply to <c>LOGONREQ</c>
/// (<c>Messages.h:53-60</c>). Layout (fully deterministic — no steam fields):
///
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 2
///   offset 4: VarItem CharName_OR_FailureReason (ib/cb)
///   offset 8: uint32  timeServer
///   offset12: int16   shipID        (ShipID = ObjectID = short, igc.h:477,480)
///   offset14: bool    fValidated
///   offset15: bool    fRetry
///   offset16: byte    cookie        (Cookie = BYTE, igc.h:516)
///   === fixed region ends at 20 (4-aligned); one blob follows at 20 ===
///
/// On a version mismatch the server fills <paramref name="CharNameOrFailureReason"/>
/// with the rejection text and sends <c>fValidated = false</c>
/// (<c>FedSrv.CPP:6076-6082</c>).
/// </summary>
public sealed class LogonAck : IFedMessage
{
    public const ushort Fmid = 2;
    public const int FixedSize = 20;

    public string? CharNameOrFailureReason;  // var item
    public uint TimeServer;
    public short ShipID;
    public bool FValidated;
    public bool FRetry;
    public byte Cookie;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobLen = CharNameOrFailureReason is null
            ? 0
            : 1 + TrimTrailingSpaces(CharNameOrFailureReason);
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0); // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Position; w.Advance(2);
        int slotCb = w.Position; w.Advance(2);

        w.WriteU32(TimeServer);
        w.WriteI16(ShipID);
        w.WriteByte(FValidated ? (byte)1 : (byte)0);
        w.WriteByte(FRetry ? (byte)1 : (byte)0);
        w.WriteByte(Cookie);

        // blob begins at sizeof(FMD_LOGONACK) = FixedSize (C++ `pBlobDst = pbFM + cbfm`).
        w.Advance(FixedSize - w.Position);

        (ushort ib, ushort cb) = WriteZtsBlob(ref w, CharNameOrFailureReason);
        w.WriteU16At(slotIb, ib);
        w.WriteU16At(slotCb, cb);
        w.WriteU16At(0, (ushort)w.Position);
        return buf;
    }

    public static LogonAck Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LOGONACK needs >= {FixedSize} bytes, got {buf.Length}");

        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid)
            throw new ArgumentException($"expected LOGONACK fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length)
            throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem it = VarItem.Read(buf.Slice(4));

        var ack = new LogonAck
        {
            TimeServer   = BitConverter.ToUInt32(buf.Slice(8)),
            ShipID       = BitConverter.ToInt16(buf.Slice(12)),
            FValidated   = buf[14] != 0,
            FRetry       = buf[15] != 0,
            Cookie       = buf[16],
        };
        if (it.IsPresent)
        {
            var data = buf.Slice(it.Ib, it.Cb);
            if (data.Length > 0 && data[^1] == 0) data = data[..^1];
            ack.CharNameOrFailureReason = Encoding.Latin1.GetString(data);
        }
        return ack;
    }

    private static int TrimTrailingSpaces(string s)
    {
        int n = s.Length;
        while (n > 0 && s[n - 1] == ' ') n--;
        return n;
    }

    private static (ushort Ib, ushort Cb) WriteZtsBlob(ref WireWriter w, string? s)
    {
        if (s is null) return (0, 0);
        int n = TrimTrailingSpaces(s);
        int ib = w.Position;
        w.WriteBytes(Encoding.Latin1.GetBytes(s.Substring(0, n)));
        w.WriteByte(0);
        return ((ushort)ib, (ushort)(n + 1));
    }
}
