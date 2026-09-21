using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>
/// Packet-level framing. Per the C++ (<c>Messages.cpp:529-537</c>, <c>1015-1018</c>,
/// <c>PfmGetNext</c> at <c>451-471</c>): a packet is a flat concatenation of
/// messages, followed by a single 4-byte little-endian CRC that covers the
/// message bytes only. No sockets/transport here — pure byte packing.
/// </summary>
public static class FedPacket
{
    /// <summary>Wire width of the trailing CRC, in bytes.</summary>
    public const int CrcWidth = 4;

    /// <summary>
    /// P315 (stability review finding H3) — hard cap on the number of messages
    /// <see cref="Unpack"/> will decode from a single packet. Native has no such
    /// cap: <c>PfmGetNext</c> (<c>Messages.cpp:495-515</c>) walks purely on
    /// <c>cbmsg</c> until the buffer is exhausted, so a ~16 MiB frame of 4-byte
    /// messages decodes into millions of messages that <c>Server.ProcessDatagram</c>
    /// would dispatch — and log — one at a time inside a single tick (the
    /// multi-million-iteration / multi-million-<c>Console.WriteLine</c> vector).
    /// A real packet carries a handful of messages; 256 is far beyond any
    /// legitimate frame, so exceeding it is treated as hostile and the whole
    /// packet is rejected rather than decoded in full.
    /// </summary>
    public const int MaxMessagesPerPacket = 256;

    /// <summary>
    /// Pack <paramref name="messages"/> and append the CRC computed over them
    /// exactly as the C++ send path does. An empty list yields a 4-byte packet.
    /// </summary>
    public static byte[] Pack(IEnumerable<byte[]> messages)
    {
        var msg = Flatten(messages);
        var packet = new byte[msg.Count + CrcWidth];
        msg.CopyTo(packet);
        uint crc = FedMsgCrc.Compute(packet.AsSpan(0, msg.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(msg.Count, CrcWidth), crc);
        return packet;
    }

    /// <summary>
    /// Unpack and verify a received packet: recompute the CRC over the message
    /// bytes and compare to the trailing CRC. Returns the parsed messages, or
    /// throws <see cref="FedCrcException"/> on mismatch / malformed length.
    /// </summary>
    public static byte[][] Unpack(byte[] packet)
    {
        if (packet.Length < CrcWidth)
            throw new FedCrcException("packet shorter than the CRC slot");

        int msgBytes = packet.Length - CrcWidth;
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(msgBytes, CrcWidth));
        uint actual = FedMsgCrc.Compute(packet.AsSpan(0, msgBytes));
        if (actual != expected)
            throw new FedCrcException($"CRC mismatch: got 0x{actual:X8}, packet 0x{expected:X8}");

        // walk messages via cbmsg (PfmGetNext logic)
        var list = new List<byte[]>();
        int pos = 0;
        while (pos < msgBytes)
        {
            // P315 (H3): cap the message count so a packet of thousands of
            // 4-byte messages cannot force millions of dispatch iterations +
            // console writes inside one tick. Once the cap is reached there is
            // (by construction) still un-consumed bytes, so the packet is
            // implausible for a single frame — reject it outright rather than
            // decoding the rest.
            if (list.Count >= MaxMessagesPerPacket)
                throw new FedCrcException(
                    $"packet carries more than {MaxMessagesPerPacket} messages ({msgBytes} message-bytes) — implausible, rejected");

            if (msgBytes - pos < FedMessageHeader.HeaderSize)
                throw new FedCrcException("partial message header in packet");
            ushort cbmsg = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(pos));
            if (cbmsg == 0 || pos + cbmsg > msgBytes)
                throw new FedCrcException($"invalid cbmsg {cbmsg} at offset {pos}");
            var m = new byte[cbmsg];
            Array.Copy(packet, pos, m, 0, cbmsg);
            list.Add(m);
            pos += cbmsg;
        }
        return list.ToArray();
    }

    /// <summary>Total wire width of a packet with no messages.</summary>
    public static readonly byte[] EmptyPacket = Pack(Array.Empty<byte[]>());

    private static List<byte> Flatten(IEnumerable<byte[]> messages)
    {
        var list = new List<byte>();
        foreach (var m in messages)
            list.AddRange(m);
        return list;
    }
}

/// <summary>Raised when a packet's trailing CRC does not match (or the packet is malformed).</summary>
public sealed class FedCrcException : Exception
{
    public FedCrcException(string message) : base(message) { }
}
