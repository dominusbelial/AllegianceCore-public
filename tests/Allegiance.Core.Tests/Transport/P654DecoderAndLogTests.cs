using System.Buffers.Binary;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Transport;

/// <summary>P654 — decoder (H-35) and log-sanitisation (H-27) regressions.</summary>
public sealed class P654DecoderAndLogTests
{
    [Fact]
    public void Reload_OutOfRangeClaimedSlice_Rejected_ValidDecodes()
    {
        // A valid one-entry reload round-trips.
        var valid = new Reload { ShipID = 7, Entries = [new ReloadData(50, 1)] };
        var decoded = Reload.Decode(valid.Encode());
        Assert.Equal(7, decoded.ShipID);
        Assert.Single(decoded.Entries);

        // Attack (H-35): tamper the var-item so the claimed slice runs past the
        // buffer — must be rejected before pre-sizing the list.
        var buf = valid.Encode();
        // var item at offset 4: ib@4, cb@6. Claim a large out-of-range slice.
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 200);  // ib
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), 400);  // cb (divisible by 4)
        Assert.Throws<ArgumentException>(() => Reload.Decode(buf));
    }

    [Fact]
    public void FireMissile_OutOfRangeClaimedSlice_Rejected_ValidDecodes()
    {
        var valid = new FireMissile
        {
            Lock = 0.5f, TimeFired = 1, MissileTypeId = 3, LauncherId = 9, ClusterId = 2,
            TargetType = 1, TargetId = 5, BDud = false,
            Entries = [default],
        };
        var decoded = FireMissile.Decode(valid.Encode());
        Assert.Single(decoded.Entries);

        var buf = valid.Encode();
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 300);  // ib
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), 400);  // cb (divisible by 40)
        Assert.Throws<ArgumentException>(() => FireMissile.Decode(buf));
    }

    [Fact]
    public void ConsoleLogSanitize_TruncatesAndEscapes()
    {
        // Control/format chars (CR/LF, ANSI ESC) are escaped so they cannot forge
        // log lines or inject terminal escapes (H-27).
        var s = ConsoleLog.Sanitize("hi\nthere\u001b[31mred");
        Assert.True(s.All(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format),
            "sanitized string must contain no control/format characters");
        // A long attacker string is truncated to the (default 128) cap.
        var longS = ConsoleLog.Sanitize(new string('a', 5000));
        Assert.True(longS.Length <= 130, $"sanitized length {longS.Length} should be capped");
    }
}
