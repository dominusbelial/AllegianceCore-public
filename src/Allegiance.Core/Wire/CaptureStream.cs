using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>Whether a captured packet travelled client→server (received) or
/// server→client (sent), from the capturing host's perspective.</summary>
public enum CaptureDirection
{
    Receive,
    Send,
}

/// <summary>One framed capture record: a direction and the raw packet bytes
/// (a <see cref="FedPacket"/>: messages + trailing CRC).</summary>
/// <param name="Direction">Which way the packet went.</param>
/// <param name="Packet">The raw packet bytes, exactly as sent on the wire.</param>
public sealed record CapturedPacket(CaptureDirection Direction, byte[] Packet);

/// <summary>Result of parsing a capture stream, tolerant of truncation and
/// corruption. <see cref="Frames"/> holds every frame that parsed cleanly;
/// when the stream did not end exactly on a frame boundary,
/// <see cref="Truncated"/> is true and <see cref="Diagnostic"/> + <see cref="Offset"/>
/// explain where and why it stopped.</summary>
public sealed record CaptureParseResult(
    IReadOnlyList<CapturedPacket> Frames,
    bool Truncated,
    string? Diagnostic,
    long Offset);

/// <summary>
/// The on-disk frame format shared by the server's <c>--capture</c> option and
/// <c>tools/fedmsgdump</c>. The wire packets themselves carry no outer length
/// (FEDMSG.md §3 — the length is supplied by the transport), so a capture file
/// must supply its own delimiting so the dumper can find packet boundaries.
/// Each frame is:
/// <code>
///   offset 0: byte  direction   ('S' 0x53 = sent, 'R' 0x52 = received)
///   offset 1: uint32 len        (little-endian length of the packet, in bytes)
///   offset 5: the packet bytes  (a FedPacket: one or more messages + trailing 4-byte CRC)
/// </code>
/// This framing lives OUTSIDE the packet and OUTSIDE its CRC, so the packet
/// bytes are byte-for-byte what <see cref="FedPacket.Pack"/> produced and
/// <see cref="FedPacket.Unpack"/> would validate — capture changes nothing on
/// the wire. Parsing never throws: it stops at the first incomplete or
/// malformed frame and reports how far it got and why.
/// </summary>
public static class CaptureStream
{
    /// <summary>Wire direction marker for a packet the host sent.</summary>
    public const byte MarkerSent = (byte)'S';
    /// <summary>Wire direction marker for a packet the host received.</summary>
    public const byte MarkerReceived = (byte)'R';

    /// <summary>Width of a frame's header (direction + length).</summary>
    public const int HeaderSize = 5;

    /// <summary>Sanity ceiling for a single packet — guards a corrupt length word.</summary>
    public const int MaxPacketBytes = 64 * 1024 * 1024;

    /// <summary>Serialize one frame to bytes (direction + length + packet).</summary>
    public static byte[] EncodeFrame(bool sent, byte[] packet)
    {
        var frame = new byte[HeaderSize + packet.Length];
        frame[0] = sent ? MarkerSent : MarkerReceived;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), (uint)packet.Length);
        packet.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>Append one frame to <paramref name="stream"/> (atomic write; no flush).</summary>
    public static void WriteFrame(Stream stream, bool sent, byte[] packet) =>
        stream.Write(EncodeFrame(sent, packet));

    /// <summary>Parse a whole capture buffer, tolerantly. Never throws.</summary>
    public static CaptureParseResult ReadAll(byte[] buffer) => Read(buffer, 0, buffer.Length);

    /// <summary>Parse a window of <paramref name="buffer"/>. Never throws.</summary>
    public static CaptureParseResult Read(byte[] buffer, int start, int count)
    {
        var frames = new List<CapturedPacket>();
        int off = start;
        int end = start + count;

        while (end - off >= HeaderSize)
        {
            byte dir = buffer[off];
            if (dir != MarkerSent && dir != MarkerReceived)
                return NewPartial(frames, off, $"unknown direction marker 0x{dir:X2} at offset {off}");

            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(off + 1, 4));
            if (len < 0 || len > MaxPacketBytes)
                return NewPartial(frames, off, $"implausible packet length {len} at offset {off}");
            if (end - off - HeaderSize < len)
                return NewPartial(frames, off, $"frame at offset {off} declares {len}-byte packet, only {end - off - HeaderSize} bytes remain (truncated capture)");

            var packet = new byte[len];
            Array.Copy(buffer, off + HeaderSize, packet, 0, len);
            frames.Add(new CapturedPacket(dir == MarkerSent ? CaptureDirection.Send : CaptureDirection.Receive, packet));
            off += HeaderSize + len;
        }

        bool truncated = end - off > 0;
        string? diag = truncated ? $"leftover {end - off} trailing byte(s) after last frame" : null;
        return new CaptureParseResult(frames, truncated, diag, truncated ? off : -1);
    }

    private static CaptureParseResult NewPartial(List<CapturedPacket> frames, int off, string diag) =>
        new(frames, Truncated: true, Diagnostic: diag, Offset: off);
}
