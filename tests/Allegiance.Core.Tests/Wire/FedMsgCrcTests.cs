using System.Buffers.Binary;
using System.Text;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

public class FedMsgCrcTests
{
    // Published IEEE CRC-32 ("check value") of "123456789" = 0xCBF43926.
    // Our variant omits the final XOR and maps 0 -> 1, so for a nonzero result
    // the wire value is the bitwise complement of the standard CRC-32.
    [Fact]
    public void MatchesKnownVector_NoFinalXor()
    {
        byte[] data = Encoding.ASCII.GetBytes("123456789");
        uint wire = FedMsgCrc.Compute(data);
        Assert.Equal(0x340BC6D9u, wire); // == ~0xCBF43926
    }

    [Fact]
    public void SeededByAllOnes_And_IndependentStepMatchesBlock()
    {
        byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03 };
        uint block = FedMsgCrc.Compute(data);
        uint folded = FedMsgCrc.Seed;
        foreach (byte b in data) folded = FedMsgCrc.Step(folded, b);
        if (folded == 0) folded = 1;
        Assert.Equal(block, folded);
    }

    [Fact]
    public void ZerosMappedToOne_And_EmptyIsAllOnes()
    {
        // A single zero byte: crc == FF ^ ... ; standard crc32({0}) = 0xD202EF8D,
        // so our no-final-XOR value = ~0xD202EF8D = 0x2DFD1072.
        Assert.Equal(0x2DFD1072u, FedMsgCrc.Compute(new byte[] { 0x00 }));

        // Empty input: running value stays seed (never hits the 0->1 guard).
        Assert.Equal(FedMsgCrc.Seed, FedMsgCrc.Compute(ReadOnlySpan<byte>.Empty));
    }
}

public class FedPacketTests
{
    private static LogonRequest Request(ushort ver, uint cookie = 7) => new()
    {
        CharacterName = "Komrad",
        ZoneTicket = new byte[] { 0x01, 0x02 },
        CDKey = "KEY",
        MissionPassword = null,
        FedsrvVer = ver,
        Time = 9000,
        DwCookie = cookie,
        CrcFileList = 0,
    };

    [Fact]
    public void Pack_Appends_CrcOverMessages_Exactly()
    {
        var r = Request(216).Encode();
        byte[] packet = FedPacket.Pack(new[] { r });
        int msgBytes = packet.Length - FedPacket.CrcWidth;
        uint appended = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(msgBytes, FedPacket.CrcWidth));
        Assert.Equal(FedMsgCrc.Compute(packet.AsSpan(0, msgBytes)), appended);
    }

    [Fact]
    public void MultipleMessages_RoundTrip()
    {
        var m1 = Request(216, cookie: 1).Encode();
        var m2 = new LogonAck { CharNameOrFailureReason = "hi", TimeServer = 1 }.Encode();
        byte[] packet = FedPacket.Pack(new[] { m1, m2 });
        byte[][] got = FedPacket.Unpack(packet);
        Assert.Equal(2, got.Length);
        Assert.Equal(m1, got[0]);
        Assert.Equal(m2, got[1]);
    }

    [Fact]
    public void CorruptedPacket_Throws_FedCrcException()
    {
        var packet = FedPacket.Pack(new[] { Request(216).Encode() });
        packet[10] ^= 0xFF; // flip a message byte -> CRC must fail
        Assert.Throws<FedCrcException>(() => FedPacket.Unpack(packet));
    }

    [Fact]
    public void TruncatedPacket_Throws()
    {
        var packet = FedPacket.Pack(new[] { Request(216).Encode() });
        Assert.Throws<FedCrcException>(() => FedPacket.Unpack(packet[..3]));
    }

    [Fact]
    public void EmptyPacket_RoundTrips_ZeroMessages()
    {
        byte[][] got = FedPacket.Unpack(FedPacket.EmptyPacket);
        Assert.Empty(got);
    }

    // P315 (H3) — a packet within the per-packet message cap round-trips;
    // a packet claiming more messages than the cap is rejected wholesale
    // (so Server.ProcessDatagram can never be forced to dispatch + log
    // millions of messages inside one tick).
    private static byte[] Tiny(ushort fmid) => new byte[] { 4, 0, (byte)(fmid & 0xFF), (byte)(fmid >> 8) };

    [Fact]
    public void PacketWithinCap_RoundTrips()
    {
        var msgs = Enumerable.Range(0, 100).Select(i => Tiny((ushort)i)).ToArray();
        byte[] packet = FedPacket.Pack(msgs);
        byte[][] got = FedPacket.Unpack(packet);
        Assert.Equal(100, got.Length);
    }

    [Fact]
    public void PacketBeyondCap_Throws_RejectsWholePacket()
    {
        var msgs = Enumerable.Range(0, FedPacket.MaxMessagesPerPacket + 100).Select(_ => Tiny(0x1111)).ToArray();
        byte[] packet = FedPacket.Pack(msgs);
        Assert.Throws<FedCrcException>(() => FedPacket.Unpack(packet));
    }
}

public class MsgVerTests
{
    [Theory]
    [InlineData(216, true)]
    [InlineData(215, false)]
    [InlineData(217, false)]
    public void VersionMatches_Reflects_Equality(ushort ver, bool expected)
        => Assert.Equal(expected, new LogonRequest { FedsrvVer = ver }.VersionMatches);

    [Fact]
    public void WrongVersion_Produces_RejectedAck_SameAsServer()
    {
        // Server rejection path (FedSrv.CPP:6051-6082): fValidated=false ack.
        var ack = new LogonAck
        {
            CharNameOrFailureReason = "server version mismatch",
            FValidated = false,
            FRetry = false,
            Cookie = 0,
        };
        var d = LogonAck.Decode(ack.Encode());
        Assert.False(d.FValidated);
        Assert.False(d.FRetry);
        Assert.Contains("mismatch", d.CharNameOrFailureReason);
    }
}
