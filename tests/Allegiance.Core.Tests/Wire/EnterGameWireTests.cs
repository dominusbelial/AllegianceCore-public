using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P141 codec <see cref="EnterGame"/>
/// (FM_S_ENTER_GAME, 128). The message is deliberately EMPTY: the struct is
/// header-only (<c>FMD_S_ENTER_GAME</c> = <c>{ ushort cbmsg; ushort fmid;
/// }</c>, Messages.h:831-835), so it is exactly 4 bytes — the same zero-field
/// shape as <c>SUICIDE</c>(80) and <c>LOGOFF</c>(3). Byte expectations are
/// built with <see cref="ByteBuf"/>, not hand-typed hex.
/// </summary>
public class EnterGameWireTests
{
    [Fact]
    public void EnterGame_RoundTrips_Empty()
    {
        var d = EnterGame.Decode(new EnterGame().Encode());
        Assert.Equal(EnterGame.Fmid, d.GetFmid());
        Assert.Equal(128, d.GetFmid());
    }

    [Fact]
    public void EnterGame_EmitsExactBytes_HeaderOnly()
    {
        var got = new EnterGame().Encode();
        Assert.Equal(4, got.Length);
        var exp = ByteBuf.Build(4, w =>
        {
            w.U16(4);    // cbmsg
            w.U16(128);  // fmid
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void EnterGame_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new EnterGame().Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 222;
        Assert.Throws<ArgumentException>(() => EnterGame.Decode(bad));
        Assert.Throws<ArgumentException>(() => EnterGame.Decode(new byte[3]));
    }
}
