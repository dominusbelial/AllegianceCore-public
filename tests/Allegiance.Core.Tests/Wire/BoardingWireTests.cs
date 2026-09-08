using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P108 boarding wire codecs:
/// <see cref="BoardShip"/> (FM_C_BOARD_SHIP, 103), <see cref="LeaveShip"/>
/// (FM_S_LEAVE_SHIP, 94) and <see cref="BoardNack"/> (FM_S_BOARD_NACK, 178).
/// All three are the trivial shape <c>{ ushort cbmsg; ushort fmid; ShipID x; }</c>
/// — header + one <c>ShipID</c> short (2-aligned, no padding), so <c>cbmsg = 6</c>.
/// Matches <c>Messages.h:656-658</c> (LEAVE_SHIP), <c>686-688</c> (BOARD_SHIP)
/// and <c>913-915</c> (BOARD_NACK). Byte expectations are built with
/// <see cref="ByteBuf"/>, not hand-typed hex.
/// </summary>
public class BoardingWireTests
{
    // ---- BoardShip (103): sidParent ---------------------------------------

    [Fact]
    public void BoardShip_RoundTrips_SidParent()
    {
        var msg = new BoardShip { SidParent = 933 };
        var d = BoardShip.Decode(msg.Encode());
        Assert.Equal(BoardShip.Fmid, d.GetFmid());
        Assert.Equal((short)933, d.SidParent);
    }

    [Fact]
    public void BoardShip_EmitsExactBytes()
    {
        var msg = new BoardShip { SidParent = 934 };
        var got = msg.Encode();
        Assert.Equal(6, got.Length);
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);    // cbmsg
            w.U16(103);  // fmid
            w.I16(934);  // sidParent
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void BoardShip_NA_sidParent_LeavesMarker()
    {
        var msg = new BoardShip { SidParent = -1 };
        Assert.Equal((short)-1, BoardShip.Decode(msg.Encode()).SidParent);
    }

    [Fact]
    public void BoardShip_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new BoardShip { SidParent = 935 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 200;
        Assert.Throws<ArgumentException>(() => BoardShip.Decode(bad));
        Assert.Throws<ArgumentException>(() => BoardShip.Decode(new byte[5]));
    }

    // ---- LeaveShip (94): sidChild ------------------------------------------

    [Fact]
    public void LeaveShip_RoundTrips_SidChild()
    {
        var msg = new LeaveShip { SidChild = 936 };
        var d = LeaveShip.Decode(msg.Encode());
        Assert.Equal(LeaveShip.Fmid, d.GetFmid());
        Assert.Equal((short)936, d.SidChild);
    }

    [Fact]
    public void LeaveShip_EmitsExactBytes()
    {
        var msg = new LeaveShip { SidChild = 937 };
        var got = msg.Encode();
        Assert.Equal(6, got.Length);
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);    // cbmsg
            w.U16(94);   // fmid
            w.I16(937);  // sidChild
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void LeaveShip_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new LeaveShip { SidChild = 938 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 200;
        Assert.Throws<ArgumentException>(() => LeaveShip.Decode(bad));
        Assert.Throws<ArgumentException>(() => LeaveShip.Decode(new byte[5]));
    }

    // ---- BoardNack (178): sidRequestedParent -------------------------------

    [Fact]
    public void BoardNack_RoundTrips_SidRequestedParent()
    {
        var msg = new BoardNack { SidRequestedParent = 940 };
        var d = BoardNack.Decode(msg.Encode());
        Assert.Equal(BoardNack.Fmid, d.GetFmid());
        Assert.Equal((short)940, d.SidRequestedParent);
    }

    [Fact]
    public void BoardNack_EmitsExactBytes()
    {
        var msg = new BoardNack { SidRequestedParent = 941 };
        var got = msg.Encode();
        Assert.Equal(6, got.Length);
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6);    // cbmsg
            w.U16(178);  // fmid
            w.I16(941);  // sidRequestedParent
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void BoardNack_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new BoardNack { SidRequestedParent = 942 }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 201;
        Assert.Throws<ArgumentException>(() => BoardNack.Decode(bad));
        Assert.Throws<ArgumentException>(() => BoardNack.Decode(new byte[5]));
    }

    // ---- shared: all three fmid constants are distinct and match Messages.h --

    [Fact]
    public void Fmids_Match_Messages_h()
    {
        Assert.Equal((ushort)94, LeaveShip.Fmid);
        Assert.Equal((ushort)103, BoardShip.Fmid);
        Assert.Equal((ushort)178, BoardNack.Fmid);
    }
}
