using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="ChatBuoy"/> (FM_CS_CHATBUOY 108),
/// ported from <c>Messages.h:716-722</c> + <c>ChatData</c> (<c>Messages.h:204-214</c>)
/// + <c>DataBuoyIGC</c> (<c>src/Igc/igc.h:1913-1922</c>). Byte-exactness is pinned
/// against a hand-composed buffer derived from the packing rule (FEDMSG.md §2)
/// and the C++ type widths (Vector = 3 floats zlib/vector.h:20; ObjectID =
/// igc.h:477/487/481; BuoyType = char igc.h:552; bool = 1) — exactly as
/// <c>ChatMessageWireTests.ChatMessage_EmitsExactDeclaredBytes</c> does for 23.
/// The CHATBUOY fixed region shares ChatData (offset 8) with CHATMESSAGE and
/// replaces the trailing oidTarget/otTarget pair with the 20-byte DataBuoyIGC.
/// </summary>
public class ChatBuoyWireTests
{
    [Fact]
    public void ChatBuoy_FixedSize_IsFortyFour()
    {
        // header(4) + one var item(4) + ChatData(16) + DataBuoyIGC(20) = 44.
        Assert.Equal(44, ChatBuoy.FixedSize);
        Assert.Equal((ushort)108, ChatBuoy.Fmid);
        Assert.Equal(16, ChatData.Size);
        Assert.Equal(20, DataBuoyIGC.Size);
    }

    [Fact]
    public void ChatBuoy_RoundTrips_ThroughWire()
    {
        var msg = new ChatBuoy
        {
            Message = "attack the alexi",
            Cd = new ChatData
            {
                SidSender = 8123,
                ChatTarget = (int)Sim.ChatTarget.Individual,     // 6
                OidRecipient = 8100,
                VoiceOver = 990,
                CommandID = -1,                                 // c_cidNone
                BObjectModel = true,
            },
            Db = new DataBuoyIGC([1.5f, -2.0f, 300.0f], ClusterId: 13, Type: 1, BuoyId: 8150, Visible: false),
        };

        var d = ChatBuoy.Decode(msg.Encode());

        Assert.Equal("attack the alexi", d.Message);
        Assert.Equal(8123, d.Cd.SidSender);
        Assert.Equal(6, d.ChatTarget);
        Assert.Equal(8100, d.OidRecipient);
        Assert.Equal(990, d.Cd.VoiceOver);
        Assert.Equal(-1, d.Cd.CommandID);
        Assert.True(d.Cd.BObjectModel);
        Assert.Equal(new[] { 1.5f, -2.0f, 300.0f }, d.Db.Position);
        Assert.Equal(13, d.Db.ClusterId);
        Assert.Equal(1, d.Db.Type);
        Assert.Equal(8150, d.Db.BuoyId);
        Assert.False(d.Db.Visible);
    }

    [Fact]
    public void ChatBuoy_Encodes_MessageAsTrimmedZs()
    {
        // Same CB_ZTS convention as CHATMESSAGE (FEDMSG.md §2): trailing
        // spaces trimmed, single trailing NUL, cb includes the NUL.
        var d = ChatBuoy.Decode(new ChatBuoy { Message = "  go   there   " }.Encode());
        Assert.Equal("  go   there", d.Message);        // leading kept, trailing trimmed
    }

    [Fact]
    public void ChatBuoy_EmitsExactDeclaredBytes()
    {
        var msg = new ChatBuoy
        {
            Message = "hi there",                           // len 8 → ZTS cb 9
            Cd = new ChatData
            {
                SidSender = 42,
                ChatTarget = 5,                             // Team
                OidRecipient = 99,
                VoiceOver = 7,
                CommandID = 1,
                BObjectModel = false,
            },
            Db = new DataBuoyIGC([1.0f, 2.0f, 3.0f], ClusterId: 2, Type: 1, BuoyId: 8100, Visible: true),
        };
        var got = msg.Encode();

        var exp = new byte[53];                             // 44 fixed + 9 blob
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void i16(short v) { u16(unchecked((ushort)v)); }
        void i32(int v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void f32(float v) { i32(BitConverter.SingleToInt32Bits(v)); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }

        u16(53);    // cbmsg
        u16(108);   // fmid
        u16(44);    // ibMessage — the Message blob starts at sizeof(FMD_CS_CHATBUOY)
        u16(9);     // cbMessage = 8 chars + 1 NUL
        // ChatData (Messages.h:204-214)
        i16(42);    // sidSender
        pad(2);     // align int chatTarget to 4
        i32(5);     // chatTarget (Team)
        i16(99);    // oidRecipient
        i16(7);     // voiceOver
        exp[p++] = 0x01;  // commandID (char)
        exp[p++] = 0x00;  // bObjectModel (false)
        pad(2);     // align ChatData (size 16, 4-aligned) → ends at offset 24
        // DataBuoyIGC (src/Igc/igc.h:1913-1922)
        f32(1.0f); f32(2.0f); f32(3.0f);  // position (Vector = 3 floats)
        i16(2);     // clusterID (SectorID)
        exp[p++] = 0x01;  // type (BuoyType char)
        pad(1);     // align short buoyID to 2 → offset 39
        i16(8100);  // buoyID (BuoyID short)
        exp[p++] = 0x01;  // visible (bool)
        pad(1);     // struct align to 4 → offset 43
        Assert.Equal(44, p);          // fixed region ends at 44

        foreach (var b in System.Text.Encoding.Latin1.GetBytes("hi there")) exp[p++] = b;
        exp[p++] = 0;   // trailing NUL

        Assert.Equal(53, p);
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ChatBuoy_RejectsWrongFmid()
    {
        var msg = new ChatBuoy { Message = "x" }.Encode();
        msg[2] = 99;   // corrupt fmid
        Assert.Throws<ArgumentException>(() => ChatBuoy.Decode(msg));
    }
}
