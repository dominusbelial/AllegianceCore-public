using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// The dumper's tolerant decode path (<see cref="FedMessageDumper"/>). It
/// reuses the shipped codec, so these tests focus on the degradation contract:
/// a valid handshake renders both directions with CRC verified; unported ids
/// print as <c>UNPORTED</c> rather than throwing; truncated and corrupt packets
/// produce a clear diagnosis and a <c>CrcVerified == false</c> verdict — never
/// an exception.
/// </summary>
public class FedMessageDumperTests
{
    private static readonly LogonRequest SampleRequest = new()
    {
        CharacterName = "Komrad",
        ZoneTicket = new byte[] { 0x01, 0x02, 0x03, 0xAA, 0xBB },
        CDKey = "DOMINIUS",
        MissionPassword = "secret",
        FedsrvVer = 216,
        Time = 900u,
        DwCookie = 0xCAFEBABEu,
        CrcFileList = -1,
        SteamAuthTicketLength = 0,
        SteamID = 70000,
    };

    private static readonly LogonAck Ack = new()
    {
        CharNameOrFailureReason = "Welcome Komrad",
        TimeServer = 901,
        ShipID = 8192,
        FValidated = true,
        Cookie = 0x7E,
    };

    [Fact]
    public void LogonHandshake_decodes_both_directions_with_crc_verified()
    {
        // One packet per direction, exactly as the server/bot exchange them.
        var serverToClient = FedPacket.Pack(new[] { Ack.Encode() });
        var clientToServer = FedPacket.Pack(new[] { SampleRequest.Encode() });

        var tx = FedMessageDumper.DumpTyped(serverToClient);
        Assert.True(tx.CrcVerified);
        Assert.Contains("[msg 0 @ 0] fmid=2", string.Join("\n", tx.Lines));
        Assert.Contains("LOGONACK", string.Join("\n", tx.Lines));
        Assert.Contains("validated=True", string.Join("\n", tx.Lines));
        Assert.Contains("shipID=8192", string.Join("\n", tx.Lines));

        var rx = FedMessageDumper.DumpTyped(clientToServer);
        Assert.True(rx.CrcVerified);
        var rxText = string.Join("\n", rx.Lines);
        Assert.Contains("fmid=1", rxText);
        Assert.Contains("LOGONREQ", rxText);
        Assert.Contains("characterName: ib=", rxText);
        Assert.Contains("\"Komrad\"", rxText);
        Assert.Contains("fedsrvVer=216", rxText);
    }

    [Fact]
    public void VarItems_show_offset_length_and_value()
    {
        var packet = FedPacket.Pack(new[] { SampleRequest.Encode() });
        var lines = FedMessageDumper.DumpPacket(packet);

        // The dumper reads each ib/cb descriptor off the wire and renders offset, length, value.
        Assert.Contains(lines, l => l.StartsWith("    characterName: ib=", StringComparison.Ordinal) &&
                                    l.Contains("cb=7") && l.Contains("\"Komrad\""));
        Assert.Contains(lines, l => l.StartsWith("    zoneTicket: ib=", StringComparison.Ordinal) &&
                                    l.Contains("cb=5") && l.Contains("<5 bytes>"));
        Assert.Contains(lines, l => l.StartsWith("    cdKey: ib=", StringComparison.Ordinal) &&
                                    l.Contains("cb=9") && l.Contains("\"DOMINIUS\"")); // 8 chars + trailing NUL
    }

    [Fact]
    public void Unported_id_prints_unported_not_throw()
    {
        // Build a well-formed but unported message: CHATMESSAGE (23) is not ported.
        var msg = new byte[8];
        BitConverter.GetBytes((ushort)8).CopyTo(msg, 0);
        BitConverter.GetBytes((ushort)23).CopyTo(msg, 2);
        var packet = FedPacket.Pack(new[] { msg });

        var dump = FedMessageDumper.DumpTyped(packet);
        Assert.True(dump.CrcVerified); // the packet is structurally valid
        var text = string.Join("\n", dump.Lines);
        Assert.Contains("fmid=23", text);
        Assert.Contains("CHATMESSAGE", text);
        Assert.Contains("UNPORTED", text);
        Assert.DoesNotContain("decode failed", text);
    }

    [Fact]
    public void Corrupt_crc_is_reported_and_walk_continues()
    {
        var packet = FedPacket.Pack(new[] { SampleRequest.Encode() });
        packet[packet.Length - 1] ^= 0xFF;   // flip the last CRC byte

        var dump = FedMessageDumper.DumpTyped(packet);
        Assert.False(dump.CrcVerified);
        var text = string.Join("\n", dump.Lines);
        Assert.Contains("CRC FAILED", text);
        Assert.Contains("continuing anyway", text);
        Assert.Contains("LOGONREQ", text);  // messages still decoded best-effort
    }

    [Fact]
    public void Undersized_known_message_is_a_decode_failure_not_a_crash()
    {
        // A known (ported) message whose cbmsg is too small for its fixed layout:
        // the walk hands the exact 800 bytes to LogonRequest.Decode, which throws
        // (needs >= 1128) and the dumper must report it as a decode failure.
        var msg = new byte[800];
        BitConverter.GetBytes((ushort)800).CopyTo(msg, 0);   // cbmsg = 800 < FixedSize
        BitConverter.GetBytes((ushort)1).CopyTo(msg, 2);     // fmid = LOGONREQ
        var packet = FedPacket.Pack(new[] { msg });          // valid CRC over the 800 bytes

        var dump = FedMessageDumper.DumpTyped(packet);
        Assert.True(dump.CrcVerified);                       // structurally a fine packet
        var text = string.Join("\n", dump.Lines);
        Assert.Contains("decode failed midway", text);
        Assert.Contains("needs >= 1128", text);
        Assert.DoesNotContain("unhandled", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_cbmsg_stops_the_walk_with_an_offset()
    {
        // A packet whose second "message" claims absurd cbmsg overrunning the buffer.
        var msg = SampleRequest.Encode();
        var packet = new List<byte>(msg);
        packet.InsertRange(0, new byte[] { 0, 0, 0, 0 }); // junk 4-byte header where msg starts
        var bytes = packet.ToArray();
        var dump = FedMessageDumper.DumpTyped(bytes);

        // CRC is not valid (we mangled the structure), and the walk reports the bad offset.
        Assert.False(dump.CrcVerified);
        var text = string.Join("\n", dump.Lines);
        Assert.Contains("malformed message", text);
        Assert.Contains("stopped at offset", text);
        Assert.DoesNotContain("unhandled", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tiny_packet_shorter_than_crc_slot_is_reported()
    {
        var dump = FedMessageDumper.DumpTyped(new byte[] { 0x01, 0x02 });
        Assert.False(dump.CrcVerified);
        Assert.Contains("shorter than the trailing", string.Join("\n", dump.Lines));
    }

    [Fact]
    public void Combined_logon_capture_file_decodes_end_to_end()
    {
        // Simulate a --capture file: TX ack then RX request, framed.
        var file = new List<byte>();
        file.AddRange(CaptureStream.EncodeFrame(sent: true, FedPacket.Pack(new[] { Ack.Encode() })));
        file.AddRange(CaptureStream.EncodeFrame(sent: false, FedPacket.Pack(new[] { SampleRequest.Encode() })));

        var capture = CaptureStream.ReadAll(file.ToArray());
        Assert.False(capture.Truncated);
        Assert.Equal(2, capture.Frames.Count);
        foreach (var fr in capture.Frames)
            Assert.True(FedMessageDumper.DumpTyped(fr.Packet).CrcVerified);
    }
}
