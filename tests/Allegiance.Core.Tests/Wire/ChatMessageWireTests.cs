using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="ChatMessage"/> (FM_CS_CHATMESSAGE 23),
/// ported from <c>Messages.h:216-223</c> + <c>ChatData</c> (<c>Messages.h:204-214</c>).
/// Byte-exactness is pinned against a hand-composed buffer derived from the
/// packing rule (FEDMSG.md §2, messagecore.h:693) and the C++ type widths
/// (igc.h:477,480,453,897,115) — not from the encoder — exactly as
/// <c>GameMessageRoundTripTests.SetCluster_EmitsExactDeclaredBytes</c> does.
/// </summary>
public class ChatMessageWireTests
{
    [Fact]
    public void ChatMessage_FixedSize_IsTwentyEight()
    {
        // header(4) + one var item(4) + ChatData(16, padded) + oidTarget(2)+otTarget(2) = 28.
        Assert.Equal(28, ChatMessage.FixedSize);
        Assert.Equal((ushort)23, ChatMessage.Fmid);
    }

    [Fact]
    public void ChatMessage_RoundTrips_ThroughWire()
    {
        var msg = new ChatMessage
        {
            Message = "hello world",
            SidSender = 8123,
            ChatTarget = (int)Sim.ChatTarget.Team,     // 5
            OidRecipient = 55,
            VoiceOver = 990,
            CommandID = -1,                            // c_cidNone
            BObjectModel = true,
            OidTarget = 8100,
            OtTarget = -1,                             // OT_invalid
        };

        var d = ChatMessage.Decode(msg.Encode());

        Assert.Equal("hello world", d.Message);
        Assert.Equal(8123, d.SidSender);
        Assert.Equal(5, d.ChatTarget);
        Assert.Equal(55, d.OidRecipient);
        Assert.Equal(990, d.VoiceOver);
        Assert.Equal(-1, d.CommandID);
        Assert.True(d.BObjectModel);
        Assert.Equal(8100, d.OidTarget);
        Assert.Equal(-1, d.OtTarget);
    }

    [Fact]
    public void ChatMessage_Encodes_MessageAsTrimmedZs()
    {
        // CB_ZTS (messagecore.h:618, Messages.cpp:372-398): trailing spaces
        // trimmed, single trailing NUL, cb includes the NUL.
        var d = ChatMessage.Decode(new ChatMessage { Message = "  hi   there   " }.Encode());
        Assert.Equal("  hi   there", d.Message);        // leading kept, trailing trimmed
    }

    [Fact]
    public void ChatMessage_EmitsExactDeclaredBytes()
    {
        var msg = new ChatMessage
        {
            Message = "hello world",                    // len 11 → ZTS cb 12
            SidSender = 42,
            ChatTarget = 0,                             // Everyone
            OidRecipient = 7,
            VoiceOver = 11,
            CommandID = 1,
            BObjectModel = true,
            OidTarget = -1,
            OtTarget = -1,
        };
        var got = msg.Encode();

        var exp = new byte[40];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void i16(short v) { u16(unchecked((ushort)v)); }
        void i32(int v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void pad(int n) { for (int i = 0; i < n; i++) exp[p++] = 0; }

        u16(40);    // cbmsg
        u16(23);    // fmid
        u16(28);    // ibMessage — the Message blob starts at sizeof(FMD_CS_CHATMESSAGE) (FedSrv.CPP:4922)
        u16(12);    // cbMessage = 11 chars + 1 NUL
        i16(42);    // sidSender
        pad(2);     // align int chatTarget to 4
        i32(0);     // chatTarget (Everyone)
        i16(7);     // oidRecipient
        i16(11);    // voiceOver
        exp[p++] = 0x01;  // commandID (char)
        exp[p++] = 0x01;  // bObjectModel (bool)
        pad(2);     // align ChatData (size 16, 4-aligned)
        i16(-1);    // oidTarget (NA)
        i16(-1);    // otTarget (OT_invalid)
        Assert.Equal(28, p);          // fixed region ends at 28

        foreach (var b in System.Text.Encoding.Latin1.GetBytes("hello world")) exp[p++] = b;
        exp[p++] = 0;   // trailing NUL

        Assert.Equal(40, p);
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ChatMessage_RejectsWrongFmid()
    {
        var msg = new ChatMessage { Message = "x" }.Encode();
        msg[2] = 99;   // corrupt fmid
        Assert.Throws<ArgumentException>(() => ChatMessage.Decode(msg));
    }
}
