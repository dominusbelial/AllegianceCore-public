using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Byte-level tests: the encoder must emit the exact bytes the (derived) C++
/// layout specifies. Expected buffers here are hand-composed from the spec in
/// FEDMSG.md — they are NOT derived from the encoder's own code, so matching
/// them is a genuine cross-check of the encoder.
///
/// Caveat recorded in FEDMSG.md §2: a *live* modern client sends a stale fifth
/// var item that aliases steamAuthTicket[0..3], so a real client buffer will
/// not equal these declared-spec bytes. These tests pin the declared layout,
/// which is what the server consumes.
/// </summary>
public class ByteLevelTests
{
    // --- LOGONACK is fully deterministic (no steam fields, one var item) -----

    [Fact]
    public void LogonAck_EmitsExactDeclaredBytes()
    {
        var ack = new LogonAck
        {
            CharNameOrFailureReason = "OK",
            TimeServer = 5,
            ShipID = -1,
            FValidated = true,
            FRetry = false,
            Cookie = 0x55,
        };
        byte[] got = ack.Encode();

        // cbmsg = 20 (fixed) + ("OK"+NUL) 3 = 23; fmid = 2
        byte[] expected =
        [
            0x17, 0x00, // cbmsg = 23
            0x02, 0x00, // fmid = 2
            0x14, 0x00, // ib CharName_OR_FailureReason = 20
            0x03, 0x00, // cb = 3
            0x05, 0x00, 0x00, 0x00, // timeServer = 5
            0xFF, 0xFF, // shipID = -1
            0x01,       // fValidated = true
            0x00,       // fRetry = false
            0x55,       // cookie
            0x00, 0x00, 0x00, // 3 pad bytes (4-align struct) before the blob
            0x4F, 0x4B, 0x00, // blob: 'O','K','\0'
        ];
        Assert.Equal(expected, got);
    }

    // --- LOGONREQ declared layout -------------------------------------------

    [Fact]
    public void LogonRequest_EmitsExactDeclaredBytes()
    {
        var req = new LogonRequest
        {
            CharacterName = "A",                    // blob 'A','\0' (cb 2)
            ZoneTicket = new byte[] { 0x10 },       // blob {0x10} (cb 1)
            CDKey = "B",                            // blob 'B','\0' (cb 2)
            MissionPassword = null,                 // absent (cb 0)
            FedsrvVer = 216,
            Time = 1,
            DwCookie = 0xAABBCCDD,
            CrcFileList = -1,
            // steam fields left default: ticket zeros, length 0, id 0, drmHash ""
        };
        byte[] got = req.Encode();

        // Build the expected buffer by hand, little-endian, exactly per FEDMSG.md.
        // Fixed size = 1128; blobs: 2+1+2 = 5 => cbmsg = 1133.
        int fixedSize = 1128;
        Assert.Equal(fixedSize + 5, FedMessageHeader.Read(got).CbMsg);

        var exp = new byte[1133];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void u32(uint v)   { exp[p++] = (byte)(v); exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void i32(int v)    { u32(unchecked((uint)v)); }

        ushort cbmsg = (ushort)(fixedSize + 5);
        u16(cbmsg);           // offset 0
        u16(1);               // fmid
        // var items (offset 4..20): ib/cb pairs. Blobs start at fixedSize=1128.
        // CharacterName ib=1128 cb=2 ; ZoneTicket ib=1130 cb=1 ; CDKey ib=1131 cb=2 ; MissionPassword ib=0 cb=0
        u16(1128); u16(2);
        u16(1130); u16(1);
        u16(1131); u16(2);
        u16(0);    u16(0);
        // fixed fields
        u16(216);       // fedsrvVer
        p += 2;         // pad (2) for Time align
        u32(1);         // time
        u32(0xAABBCCDD);// dwCookie
        i32(-1);        // crcFileList
        p += 1024;      // steamAuthTicket (zeros)
        u32(0);         // steamAuthTicketLength
        p += 4;         // internal pad: steamID (uint64) is 8-aligned
        p += 8;         // steamID (zeros)
        p += 50;        // drmHash[50] (zeros)
        p += 2;         // trailing pad: struct rounds to 8-aligned size 1128
        Assert.Equal(fixedSize, p); // fixed region complete at 1128

        // blobs
        exp[p++] = (byte)'A'; exp[p++] = 0x00;      // CharacterName "A\0"
        exp[p++] = 0x10;                            // ZoneTicket
        exp[p++] = (byte)'B'; exp[p++] = 0x00;      // CDKey "B\0"

        Assert.Equal(exp, got);
    }

    // --- hand-composed buffer decodes to the same values ---------------------

    [Fact]
    public void DeclaredBytes_DecodeBack_ToSameValues()
    {
        var ack = new LogonAck { CharNameOrFailureReason = "OK", TimeServer = 5, ShipID = -1, FValidated = true, Cookie = 0x55 };
        var ackDec = LogonAck.Decode(ack.Encode());
        Assert.Equal("OK", ackDec.CharNameOrFailureReason);

        var req = new LogonRequest { CharacterName = "A", ZoneTicket = new byte[] { 0x10 }, CDKey = "B", FedsrvVer = 216, Time = 1, DwCookie = 0xAABBCCDD, CrcFileList = -1 };
        var reqDec = LogonRequest.Decode(req.Encode());
        Assert.Equal("A", reqDec.CharacterName);
        Assert.Equal(new byte[] { 0x10 }, reqDec.ZoneTicket);
        Assert.Equal("B", reqDec.CDKey);
        Assert.Null(reqDec.MissionPassword);
        Assert.Equal(216, reqDec.FedsrvVer);
        Assert.Equal(0xAABBCCDDu, reqDec.DwCookie);
        Assert.Equal(-1, reqDec.CrcFileList);
    }
}
