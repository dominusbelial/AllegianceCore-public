using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// FM_C_LOGONREQ (id 1), the first message a client sends
/// (<c>Messages.h:36-51</c>). Layout (MSVC default packing; see FEDMSG.md §2):
///
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 1
///   offset 4..19: four VarItems (ib/cb pairs): CharacterName, ZoneTicket,
///                 CDKey, MissionPassword
///   offset20: ushort  fedsrvVer   (must == MSGVER 216)
///   offset22: 2 pad bytes (align Time to 4)
///   offset24: uint32   time
///   offset28: uint32   dwCookie
///   offset32: int32    crcFileList
///   offset36: byte[1024] steamAuthTicket
///   offset1060: uint32 steamAuthTicketLength
///   offset1068: uint64 steamID
///   offset1076: char[50] drmHash
///   === fixed region ends at 1128 (8-aligned) ===
/// blobs appended after offset 1128; cbmsg = final message length.
///
/// NOTE (FEDMSG.md §2): a real client sends a fifth, stale var item that
/// aliases steamAuthTicket[0..3]; this port implements the declared spec (four
/// var items + STEAM fixed fields), which is what the server validation at
/// <c>FedSrv.CPP:5824</c> consumes.
///
/// PORT EXTENSIONS (tagged trailing blobs, after the native variable items):
/// <c>"P883"</c> + u32 resume token (the P813c reconnect seat) and <c>"P894"</c> +
/// u32 length + bytes client version (the advertised <c>minClientVersion</c> gate,
/// enforced at logon). Neither changes a native offset; a message with neither is
/// byte-identical to the native shape.
/// </summary>
public sealed class LogonRequest : IFedMessage
{
    public const ushort Fmid = 1;
    public const ushort MsgVer = 216; // MessageVersion.h:21, must equal fedsrvVer

    /// <summary>Wire size of the fixed region: header + 4 var slots + fixed fields (STEAM).</summary>
    public const int FixedSize = 1128;

    // var items (declaration order on the wire)
    public string? CharacterName;
    public byte[]? ZoneTicket;      // length-supplied (encrypted; opaque bytes here)
    public string? CDKey;           // zero-terminated string
    public string? MissionPassword;

    // fixed fields
    public ushort FedsrvVer;
    public uint Time;
    public uint DwCookie = uint.MaxValue; // native NA joins the first mission (FedSrv.CPP:490).
    /// <summary>Port-only P813c/P883 reconnect token. Optional tagged trailing blob,
    /// after all native variable items. Native fixed offsets and items are unchanged.</summary>
    public uint ResumeToken;
    public int CrcFileList;

    /// <summary>Port-only P894 client build identity — the installed release string
    /// (e.g. <c>2.0.0</c>). Native's version identity at logon was the file-list
    /// CRC (<c>crcFileList</c>, <c>Inc/Messages.h:44</c>) checked by the lobby
    /// against its official list (<c>lobby/client.cpp:272-281</c>). The port keeps
    /// the same CRC gate (<see cref="CrcFileList"/>, P871) and adds this tagged
    /// trailing blob so a server can enforce the advertised
    /// <c>minClientVersion</c> at its own logon — the logon point the port's client
    /// actually uses (it connects straight to a game server). Null = an older
    /// client that does not send it; the encoded bytes are then identical to the
    /// pre-P894 message.</summary>
    public string? ClientVersion;

    // STEAM fixed fields (populated by modern clients)
    public byte[]? SteamAuthTicket;     // up to 1024 bytes
    public uint SteamAuthTicketLength;
    public ulong SteamID;
    public string? DrmHash;             // up to 50 chars, null-padded

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobChars = ZtsBlobSize(CharacterName) + ZoneTicketBlobSize(ZoneTicket)
                      + ZtsBlobSize(CDKey) + ZtsBlobSize(MissionPassword);
        // An empty/blank version is "not stated": encode nothing rather than an
        // empty blob, so the bytes match a client that never sets it.
        bool sendVersion = !string.IsNullOrWhiteSpace(ClientVersion);
        byte[] versionBytes = sendVersion ? Latin1.GetBytes(ClientVersion!) : Array.Empty<byte>();
        int versionBlob = sendVersion ? 8 + versionBytes.Length : 0;
        var buf = new byte[FixedSize + blobChars + (ResumeToken == 0 ? 0 : 8) + versionBlob];
        WireWriter w = new(buf);

        w.WriteU16(0); // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotCharNameIb = w.Position; w.Advance(2);
        int slotCharNameCb = w.Position; w.Advance(2);
        int slotZoneIb      = w.Position; w.Advance(2);
        int slotZoneCb      = w.Position; w.Advance(2);
        int slotCdKeyIb     = w.Position; w.Advance(2);
        int slotCdKeyCb     = w.Position; w.Advance(2);
        int slotPassIb      = w.Position; w.Advance(2);
        int slotPassCb      = w.Position; w.Advance(2);

        // fixed fields
        w.WriteU16(FedsrvVer);
        w.Advance(2); // pad for 4-align Time
        w.WriteU32(Time);
        w.WriteU32(DwCookie);
        w.WriteI32(CrcFileList);

        // STEAM fixed region
        int ticketLen = Math.Min(SteamAuthTicket?.Length ?? 0, 1024);
        if (ticketLen > 0) w.WriteBytes(SteamAuthTicket.AsSpan(0, ticketLen));
        w.WriteZeros(1024 - ticketLen);
        w.WriteU32(SteamAuthTicketLength);
        w.Advance(4); // pad: steamID (uint64) is 8-aligned => offset 1068
        w.WriteU64(SteamID);
        var hash = new byte[50];
        WriteFixedAscii(hash, DrmHash);
        w.WriteBytes(hash);

        // blobs begin at sizeof(FMD_LOGONREQ) = FixedSize (C++ `pBlobDst = pbFM + cbfm`),
        // not at the packed end of the fields; the trailing delta is alignment padding.
        w.Advance(FixedSize - w.Position);

        // blobs appended after the fixed region; ib = offset from message byte 0
        (ushort ib, ushort cb) cn = WriteZtsBlob(ref w, CharacterName);
        (ushort ib, ushort cb) zt = WriteLengthBlob(ref w, ZoneTicket);
        (ushort ib, ushort cb) cd = WriteZtsBlob(ref w, CDKey);
        (ushort ib, ushort cb) ps = WriteZtsBlob(ref w, MissionPassword);

        // patch var slots
        w.WriteU16At(slotCharNameIb, cn.ib); w.WriteU16At(slotCharNameCb, cn.cb);
        w.WriteU16At(slotZoneIb, zt.ib);     w.WriteU16At(slotZoneCb, zt.cb);
        w.WriteU16At(slotCdKeyIb, cd.ib);    w.WriteU16At(slotCdKeyCb, cd.cb);
        w.WriteU16At(slotPassIb, ps.ib);     w.WriteU16At(slotPassCb, ps.cb);

        if (ResumeToken != 0)
        {
            w.WriteU32(0x33383850); // "P883" port-extension tag, not a native field
            w.WriteU32(ResumeToken);
        }
        if (sendVersion)
        {
            w.WriteU32(ClientVersionTag); // "P894" port-extension tag
            w.WriteU32((uint)versionBytes.Length);
            w.WriteBytes(versionBytes);
        }

        // cbmsg = total length
        w.WriteU16At(0, (ushort)w.Position);
        return buf;
    }

    public static LogonRequest Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LOGONREQ needs >= {FixedSize} bytes, got {buf.Length}");

        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid)
            throw new ArgumentException($"expected LOGONREQ fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length)
            throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem ch = VarItem.Read(buf.Slice(4));
        VarItem zt = VarItem.Read(buf.Slice(8));
        VarItem cd = VarItem.Read(buf.Slice(12));
        VarItem ps = VarItem.Read(buf.Slice(16));

        return new LogonRequest
        {
            CharacterName  = ch.IsPresent ? ReadZts(buf, ch) : null,
            ZoneTicket     = zt.IsPresent ? ReadLengthBlob(buf, zt) : null,
            CDKey          = cd.IsPresent ? ReadZts(buf, cd) : null,
            MissionPassword= ps.IsPresent ? ReadZts(buf, ps) : null,

            FedsrvVer = BitConverter.ToUInt16(buf.Slice(20)),
            Time      = BitConverter.ToUInt32(buf.Slice(24)),
            DwCookie  = BitConverter.ToUInt32(buf.Slice(28)),
            ResumeToken = ReadResumeToken(buf, ch, zt, cd, ps),
            ClientVersion = ReadClientVersion(buf, ch, zt, cd, ps),
            CrcFileList = BitConverter.ToInt32(buf.Slice(32)),

            SteamAuthTicket      = ChompZeros(buf.Slice(36, 1024)).ToArray(),
            SteamAuthTicketLength= BitConverter.ToUInt32(buf.Slice(1060)),
            SteamID              = BitConverter.ToUInt64(buf.Slice(1068)),
            DrmHash              = ReadFixedAscii(buf.Slice(1076, 50)),
        };
    }

    private static uint ReadResumeToken(ReadOnlySpan<byte> buf, params VarItem[] items)
    {
        int end = FixedSize;
        foreach (var item in items) if (item.IsPresent) end = Math.Max(end, item.Ib + item.Cb);
        int offset = ReadPortExtension(buf, end, ResumeTokenTag, out _);
        return offset < 0 ? 0 : BitConverter.ToUInt32(buf.Slice(offset));
    }

    /// <summary>The P883 tag: "P883" as a little-endian u32.</summary>
    private const uint ResumeTokenTag = 0x33383850;

    /// <summary>The P894 tag: "P894" as a little-endian u32.</summary>
    private const uint ClientVersionTag = 0x34393850;

    private static string? ReadClientVersion(ReadOnlySpan<byte> buf, params VarItem[] items)
    {
        int end = FixedSize;
        foreach (var item in items) if (item.IsPresent) end = Math.Max(end, item.Ib + item.Cb);
        int lengthOffset = ReadPortExtension(buf, end, ClientVersionTag, out int payloadOffset);
        if (lengthOffset < 0) return null;
        uint length = BitConverter.ToUInt32(buf.Slice(lengthOffset));
        if (length == 0 || payloadOffset < 0 || payloadOffset + (long)length > buf.Length) return null;
        return Latin1.GetString(buf.Slice(payloadOffset, (int)length));
    }

    /// <summary>
    /// Scan the port's tagged trailing extensions (after the native blobs) for
    /// <paramref name="tag"/>. Two layouts exist: the P883 resume token
    /// (<c>u32 tag</c> + <c>u32 value</c>, fixed 8 bytes) and the P894 client
    /// version (<c>u32 tag</c> + <c>u32 byte length</c> + bytes). Returns the offset
    /// of the extension's first payload word, or -1 when the tag is absent.
    /// </summary>
    private static int ReadPortExtension(ReadOnlySpan<byte> buf, int start, uint tag, out int payloadOffset)
    {
        payloadOffset = -1;
        int at = start;
        while (at + 8 <= buf.Length)
        {
            uint found = BitConverter.ToUInt32(buf.Slice(at));
            uint word = BitConverter.ToUInt32(buf.Slice(at + 4));
            if (found == tag)
            {
                payloadOffset = at + 8;
                return at + 4;
            }
            if (found == ResumeTokenTag)
            {
                at += 8;                 // fixed-size payload: the token word itself
                continue;
            }
            long next = (long)at + 8 + word;   // length-prefixed payload
            if (next > buf.Length) return -1;
            at = (int)next;
        }
        return -1;
    }

    public bool VersionMatches => FedsrvVer == MsgVer;

    // --- helpers -----------------------------------------------------------

    /// <summary>C++ <c>CB_ZTS</c> creator length for a string: trimmed, +1 for NUL. Null -> 0.</summary>
    private static int ZtsBlobSize(string? s) => s is null ? 0 : 1 + TrimTrailingSpaces(s);

    private static int ZoneTicketBlobSize(byte[]? b) => b?.Length ?? 0;

    private static int TrimTrailingSpaces(string s)
    {
        int n = s.Length;
        while (n > 0 && s[n - 1] == ' ') n--;
        return n;
    }

    /// <summary>Latin-1 encoder: each char -> one byte, matching C++ <c>char</c>.</summary>
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static (ushort Ib, ushort Cb) WriteZtsBlob(ref WireWriter w, string? s)
    {
        if (s is null) return (0, 0);
        int n = TrimTrailingSpaces(s);
        int ib = w.Position;
        w.WriteBytes(Latin1.GetBytes(s.Substring(0, n)));
        w.WriteByte(0);
        return ((ushort)ib, (ushort)(n + 1));
    }

    private static (ushort Ib, ushort Cb) WriteLengthBlob(ref WireWriter w, byte[]? b)
    {
        if (b is null) return (0, 0);
        int ib = w.Position;
        w.WriteBytes(b);
        return ((ushort)ib, (ushort)b.Length);
    }

    private static string ReadZts(ReadOnlySpan<byte> msg, VarItem it)
    {
        var data = msg.Slice(it.Ib, it.Cb);
        // strip the trailing NUL (cb includes it)
        if (data.Length > 0 && data[^1] == 0) data = data[..^1];
        return Latin1.GetString(data);
    }

    private static byte[] ReadLengthBlob(ReadOnlySpan<byte> msg, VarItem it) =>
        msg.Slice(it.Ib, it.Cb).ToArray();

    /// <summary>C++ stores fixed char arrays; trailing NULs are not meaningful. Strip trailing zero bytes.</summary>
    private static ReadOnlySpan<byte> ChompZeros(ReadOnlySpan<byte> field)
    {
        int n = field.Length;
        while (n > 0 && field[n - 1] == 0) n--;
        return field[..n];
    }

    private static void WriteFixedAscii(Span<byte> field, string? s)
    {
        field.Clear();
        if (s is null) return;
        int n = Math.Min(s.Length, field.Length);
        Encoding.Latin1.GetBytes(s.Substring(0, n), field[..n]);
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> field) =>
        Latin1.GetString(ChompZeros(field));
}
