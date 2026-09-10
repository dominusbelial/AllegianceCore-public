using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P121 join-sync briefing codec
/// <see cref="SetBriefingText"/> (FM_S_SET_BRIEFING_TEXT, 9). Layout pinned
/// against the native struct arithmetic (Messages.h, quoted in the codec:
/// <c>FM_VAR_ITEM(text)</c> + <c>bool fGenerateCivBriefing</c>) composed with
/// the packed-FEDMESSAGE convention this port pins from BuyLoadoutAck (P28) —
/// var items first, then fixed fields, blobs appended with no alignment pad.
/// Composed with the declarative <see cref="ByteBuf"/> builder, not hand-typed
/// hex, so a regression in the encoder surfaces as a byte mismatch against an
/// independently-derived expectation.
/// </summary>
public class SetBriefingTextWireTests
{
    [Fact]
    public void FixedSize_IsNine()
    {
        // Arithmetic: hdr(4) + one ib/cb var pair(4) + bool(1) = 9, with the
        // text blob immediately after (packed convention — no alignment pad,
        // exactly the BuyLoadoutAck P28 shape: var pair + a single byte then
        // the blob).
        Assert.Equal(9, SetBriefingText.FixedSize);
    }

    [Fact]
    public void RoundTrips_TextAndFlag()
    {
        var msg = new SetBriefingText { Text = "SolSystem", FGenerateCivBriefing = true };
        var d = SetBriefingText.Decode(msg.Encode());
        Assert.Equal(SetBriefingText.Fmid, d.GetFmid());
        Assert.Equal("SolSystem", d.Text);
        Assert.True(d.FGenerateCivBriefing);
    }

    [Fact]
    public void RoundTrips_EmptyTextFalseFlag()
    {
        var msg = new SetBriefingText { Text = "", FGenerateCivBriefing = false };
        var d = SetBriefingText.Decode(msg.Encode());
        Assert.Equal("", d.Text);
        Assert.False(d.FGenerateCivBriefing);
    }

    [Fact]
    public void EmitsExactBytes_GenerateCivBriefingTrue()
    {
        var msg = new SetBriefingText { Text = "Foo", FGenerateCivBriefing = true };
        var got = msg.Encode();

        var exp = ByteBuf.Build(13, w =>
        {
            w.U16(13);      // cbmsg = 9 fixed + 4 blob ("Foo" + NUL)
            w.U16(9);       // fmid
            w.U16(9);       // ibText = 9 (blob starts right after the bool)
            w.U16(4);       // cbText = 4 (CB_ZTS incl. trailing NUL)
            w.Byte(1);      // fGenerateCivBriefing = true
            w.Byte((byte)'F'); w.Byte((byte)'o'); w.Byte((byte)'o');
            w.Byte(0);      // NUL terminator
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void EmitsExactBytes_GenerateCivBriefingFalse()
    {
        var msg = new SetBriefingText { Text = "", FGenerateCivBriefing = false };
        var got = msg.Encode();

        var exp = ByteBuf.Build(10, w =>
        {
            w.U16(10);      // cbmsg = 9 fixed + 1 blob (just the NUL)
            w.U16(9);       // fmid
            w.U16(9);       // ibText = 9
            w.U16(1);       // cbText = 1 (CB_ZTS: single NUL for empty text)
            w.Byte(0);      // fGenerateCivBriefing = false
            w.Byte(0);      // NUL
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void TrimsTrailingSpaces_OnTheWire()
    {
        // CB_ZTS trims trailing spaces (ZtsBlob), so "Galaxy  " encodes as "Galaxy".
        var msg = new SetBriefingText { Text = "Galaxy  ", FGenerateCivBriefing = true };
        var d = SetBriefingText.Decode(msg.Encode());
        Assert.Equal("Galaxy", d.Text);
    }

    [Fact]
    public void RejectsWrongFmid()
    {
        var bytes = new SetBriefingText { Text = "X", FGenerateCivBriefing = true }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => SetBriefingText.Decode(badFmid));
    }

    [Fact]
    public void RejectsTooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => SetBriefingText.Decode(new byte[8]));
        Assert.Throws<ArgumentException>(() => SetBriefingText.Decode([]));
    }

    [Fact]
    public void Rejects_MismatchedCbmsg()
    {
        var bytes = new SetBriefingText { Text = "Y", FGenerateCivBriefing = true }.Encode();
        var badCb = (byte[])bytes.Clone();
        badCb[0] = 0x40;   // 64 != actual cbmsg → mismatch → reject
        Assert.Throws<ArgumentException>(() => SetBriefingText.Decode(badCb));
    }
}
