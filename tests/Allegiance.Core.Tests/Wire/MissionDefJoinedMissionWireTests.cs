using System.Linq;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="MissionDef"/>
/// (FM_S_MISSIONDEF 38) and <see cref="JoinedMission"/>
/// (FM_S_JOINED_MISSION 95), the two messages that make the client load its
/// static core. Offsets are the P29 compiler-verified layout
/// (MSVC probe, vm145): sizeof(MissionParams)=252, sizeof(FMD_S_MISSIONDEF)=864,
/// offsetof(misparms)=4, offsetof(szIGCStaticFile)=65, offsetof(verIGCcore)=120,
/// dwCookie@460, iSideMissionOwner@464, stage@468; sizeof(FMD_S_JOINED_MISSION)=20,
/// shipID@4, dtime@8, cookie@12, dwCookie@16.
/// </summary>
public class MissionDefJoinedMissionWireTests
{
    [Fact]
    public void MissionDef_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)38, MissionDef.Fmid);
        Assert.Equal(252, MissionDef.MissionParamsSize); // sizeof(MissionParams)
        Assert.Equal(864, MissionDef.FixedSize);         // sizeof(FMD_S_MISSIONDEF)
        Assert.Equal(13, MissionDef.StaticFileBufSize);  // c_cbFileName
    }

    [Fact]
    public void MissionDef_RoundTrips_ThroughWire()
    {
        var msg = new MissionDef
        {
            SzIGCStaticFile = "PCore001.igc",
            VerIgcCore = 990051329,
            DwCookie = 7,
            ISideMissionOwner = -1,
            Stage = 1,
        };
        var d = MissionDef.Decode(msg.Encode());
        Assert.Equal("PCore001.igc", d.SzIGCStaticFile);
        Assert.Equal(990051329, d.VerIgcCore);
        Assert.Equal(7u, d.DwCookie);
        Assert.Equal(-1, d.ISideMissionOwner);
        Assert.Equal(1, d.Stage);
    }

    [Fact]
    public void MissionDef_PlacesKeyFieldsAtCompilerConfirmedOffsets_AndZerosTheRest()
    {
        var msg = new MissionDef
        {
            SzIGCStaticFile = "PCore001.igc",
            VerIgcCore = 990051329,   // 0x3B02FC01 little-endian → 01 FC 02 3B
            DwCookie = 0xDEADBEEF,
        };
        var b = msg.Encode();

        Assert.Equal(864, b.Length);
        // header: cbmsg=864 (0x0360 LE), fmid=38 (0x26 00)
        Assert.Equal(new byte[] { 0x60, 0x03, 0x26, 0x00 }, b.Take(4).ToArray());

        // szIGCStaticFile at absolute offset 4+65 = 69, char[13] "PCore001.igc\0"
        var sf = "PCore001.igc\0";
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes(sf), b.Skip(69).Take(13).ToArray());
        // the 13-byte field ends exactly at offset 82; the char[13] fits exactly (12 chars + NUL).
        Assert.Equal(82, 69 + 13);

        // verIGCcore at absolute offset 4+120 = 124, int little-endian
        Assert.Equal(new byte[] { 0x01, 0xFC, 0x02, 0x3B }, b.Skip(124).Take(4).ToArray());

        // dwCookie at 460
        Assert.Equal(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE }, b.Skip(460).Take(4).ToArray());

        // Everything except the populated cells and the header must be zero
        // (proves we did not scribble a stray offset from hand-composed maths).
        for (int i = 4; i < b.Length; i++)
        {
            bool isPopulated =
                (i >= 69 && i < 82) ||     // szIGCStaticFile
                (i >= 124 && i < 128) ||   // verIGCcore
                (i >= 460 && i < 464);     // dwCookie
            if (!isPopulated)
                Assert.True(b[i] == 0, $"offset {i} non-zero ({b[i]:X2})");
        }
    }

    [Fact]
    public void MissionDef_RealUsage_BaseNameRoundTrips()
    {
        // The value the server actually sends (P29 empirical correction): the
        // extensionless base name, because the client's LoadIGCStaticCore
        // appends ".igc". Encodes and decodes; 13-byte buffer holds 12 chars + NUL.
        var msg = new MissionDef { SzIGCStaticFile = "PCore001", VerIgcCore = 990051329 };
        var b = msg.Encode();
        Assert.Equal("PCore001", System.Text.Encoding.Latin1.GetString(b.Skip(69).Take(13).ToArray()).TrimEnd('\0'));
        var d = MissionDef.Decode(b);
        Assert.Equal("PCore001", d.SzIGCStaticFile);
        Assert.Equal(990051329, d.VerIgcCore);
    }

    [Fact]
    public void MissionDef_TruncatesOverLongStaticFileName_KeepingNullTerminator()
    {
        var msg = new MissionDef { SzIGCStaticFile = "aWayTooLongStaticCoreFileNameForA13ByteBuffer.igc" };
        var b = msg.Encode();
        // First 12 chars, then NUL at index 12 (the 13th byte) — never overrun.
        var expect = System.Text.Encoding.Latin1.GetBytes("aWayTooLongS"); // 12 chars
        Assert.Equal(expect.Append((byte)0).ToArray(), b.Skip(69).Take(13).ToArray());
    }

    [Fact]
    public void MissionDef_RejectsWrongFmid_AndWrongLength()
    {
        var bytes = new MissionDef { SzIGCStaticFile = "PCore001.igc", VerIgcCore = 990051329 }.Encode();

        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 7;
        Assert.Throws<ArgumentException>(() => MissionDef.Decode(badFmid));

        Assert.Throws<ArgumentException>(() => MissionDef.Decode(bytes.AsSpan(0, 800)));       // short
        Assert.Throws<ArgumentException>(() => MissionDef.Decode(new byte[900]));             // long (content ignored; length gate first)
    }

    [Fact]
    public void JoinedMission_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)95, JoinedMission.Fmid);
        Assert.Equal(20, JoinedMission.FixedSize); // sizeof(FMD_S_JOINED_MISSION)
    }

    [Fact]
    public void JoinedMission_RoundTrips_ThroughWire()
    {
        var msg = new JoinedMission { ShipId = 8099, DTime = 1.5f, Cookie = 3, DwCookie = 9 };
        var d = JoinedMission.Decode(msg.Encode());
        Assert.Equal(8099, d.ShipId);
        Assert.Equal(1.5f, d.DTime);
        Assert.Equal(3, d.Cookie);
        Assert.Equal(9u, d.DwCookie);
    }

    [Fact]
    public void JoinedMission_EmitsExactDeclaredBytes()
    {
        //                 cbmsg=20 fmid=95   shipID=8099(LE) pad  dtime=0    pad cookie dwCookie
        var msg = new JoinedMission { ShipId = 8099, DTime = 0, Cookie = 0, DwCookie = 0 };
        var b = msg.Encode();
        Assert.Equal(20, b.Length);
        // cbmsg 0x14 00 ; fmid 0x5F 00 ; shipID 8099 = 0x1FA3 → A3 1F ; pad 00 00 ;
        // dtime 0.0f → 00 00 00 00 ; cookie 00 ; pad 00 00 00 ; dwCookie 00 00 00 00
        var exp = new byte[]
        {
            0x14, 0x00, 0x5F, 0x00,
            0xA3, 0x1F, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };
        Assert.Equal(exp, b);
    }

    [Fact]
    public void JoinedMission_NonZeroFields_LandExactly()
    {
        var msg = new JoinedMission { ShipId = 7, DTime = 1.5f, Cookie = 0xAA, DwCookie = 0xCAFEBABE };
        var b = msg.Encode();
        Assert.Equal(new byte[] { 0x07, 0x00 }, b.Skip(4).Take(2).ToArray());          // shipID
        var f = BitConverter.SingleToInt32Bits(1.5f);                                   // dtime bits
        Assert.Equal(BitConverter.GetBytes(f), b.Skip(8).Take(4).ToArray());
        Assert.Equal(0xAA, b[12]);                                                     // cookie
        Assert.Equal(BitConverter.GetBytes(0xCAFEBABEu), b.Skip(16).Take(4).ToArray()); // dwCookie
    }

    [Fact]
    public void JoinedMission_RejectsWrongFmid_AndWrongLength()
    {
        var bytes = new JoinedMission { ShipId = 1 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 38;
        Assert.Throws<ArgumentException>(() => JoinedMission.Decode(badFmid));
        Assert.Throws<ArgumentException>(() => JoinedMission.Decode(bytes.AsSpan(0, 19)));
    }
}
