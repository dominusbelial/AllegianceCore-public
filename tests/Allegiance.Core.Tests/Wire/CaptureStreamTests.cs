using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// The capture frame format shared by the server's <c>--capture</c> and
/// <c>tools/fedmsgdump</c>: frame = <c>dir(1) + len(u32 LE) + packet</c>. These
/// tests pin the byte layout and the tolerant (never-throw) parser behaviour
/// on truncation and corruption.
/// </summary>
public class CaptureStreamTests
{
    [Fact]
    public void Frame_layout_is_dir_plus_le_length_plus_packet()
    {
        var packet = new byte[] { 0x01, 0x02, 0x03 };
        var frame = CaptureStream.EncodeFrame(sent: true, packet);

        Assert.Equal(CaptureStream.HeaderSize, 5);
        Assert.Equal((byte)'S', frame[0]);
        // length little-endian = 3
        Assert.Equal(3, frame[1]);
        Assert.Equal(0, frame[2]);
        Assert.Equal(0, frame[3]);
        Assert.Equal(0, frame[4]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, frame[5..]);
    }

    [Fact]
    public void Markers_match_direction()
    {
        Assert.Equal((byte)'R', CaptureStream.MarkerReceived);
        Assert.Equal((byte)'S', CaptureStream.MarkerSent);
    }

    [Fact]
    public void Round_trip_separates_recv_and_send_in_order()
    {
        var tx = new byte[] { 0xA1, 0xB2 };
        var rx = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var buffer = new List<byte>();
        buffer.AddRange(CaptureStream.EncodeFrame(sent: true, tx));
        buffer.AddRange(CaptureStream.EncodeFrame(sent: false, rx));

        var res = CaptureStream.ReadAll(buffer.ToArray());
        Assert.False(res.Truncated);
        Assert.Null(res.Diagnostic);
        Assert.Equal(2, res.Frames.Count);
        Assert.Equal(CaptureDirection.Send, res.Frames[0].Direction);
        Assert.Equal(tx, res.Frames[0].Packet);
        Assert.Equal(CaptureDirection.Receive, res.Frames[1].Direction);
        Assert.Equal(rx, res.Frames[1].Packet);
    }

    [Fact]
    public void Partial_last_frame_is_reported_not_thrown()
    {
        var buffer = new List<byte>();
        buffer.AddRange(CaptureStream.EncodeFrame(sent: true, new byte[] { 1, 2, 3, 4, 5, 6 }));
        // declare a 100-byte packet but provide only 3 — the parser must stop, not throw.
        buffer.Add((byte)'R');
        buffer.Add(100); buffer.Add(0); buffer.Add(0); buffer.Add(0);
        buffer.Add(0xDE); buffer.Add(0xAD); buffer.Add(0xBE);

        var res = CaptureStream.ReadAll(buffer.ToArray());
        Assert.True(res.Truncated);
        Assert.Single(res.Frames);
        Assert.Contains("truncated capture", res.Diagnostic);
        Assert.Equal((long)CaptureStream.HeaderSize + 6, res.Offset);
    }

    [Fact]
    public void Bad_direction_marker_is_reported_not_thrown()
    {
        var buffer = new List<byte>();
        buffer.AddRange(CaptureStream.EncodeFrame(sent: false, new byte[] { 9, 9 }));
        buffer.Add(0x41); // 'A' — neither 'R' nor 'S'
        buffer.Add(4); buffer.Add(0); buffer.Add(0); buffer.Add(0);
        buffer.AddRange(new byte[] { 1, 2, 3, 4 });

        var res = CaptureStream.ReadAll(buffer.ToArray());
        Assert.True(res.Truncated);
        Assert.Single(res.Frames);
        Assert.Contains("unknown direction marker 0x41", res.Diagnostic);
    }

    [Fact]
    public void Implausible_length_word_is_rejected()
    {
        var buffer = new List<byte>();
        buffer.Add((byte)'R');
        buffer.Add(0xFF); buffer.Add(0xFF); buffer.Add(0xFF); buffer.Add(0x7F); // huge
        var res = CaptureStream.ReadAll(buffer.ToArray());
        Assert.True(res.Truncated);
        Assert.Empty(res.Frames);
        Assert.Contains("implausible packet length", res.Diagnostic);
    }

    [Fact]
    public void Trailing_bytes_after_last_frame_are_a_diagnosis()
    {
        var buffer = new List<byte>();
        buffer.AddRange(CaptureStream.EncodeFrame(sent: true, new byte[] { 5, 5 }));
        buffer.Add(0xAA); buffer.Add(0xBB); // orphan tail
        var res = CaptureStream.ReadAll(buffer.ToArray());
        Assert.True(res.Truncated);
        Assert.Single(res.Frames);
        Assert.Contains("leftover 2 trailing byte(s)", res.Diagnostic);
    }
}
