using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// P201 — <c>GAIN_FLAG(156)</c> codec round-trip and byte-exact layout:
/// <c>{sideidFlag: short @4, shipidRecipient: short @6, bIsTreasureDocked: bool @8,
/// pad @9, parttypeidDocked: short @10}</c> = 12 B fixed (default alignment —
/// the message sits outside Messages.h's two pack(1) regions, so the trailing
/// PartID short is 2-aligned with one pad byte after the bool).
/// </summary>
public class GainFlagWireTests
{
    [Fact]
    public void GainFlag_round_trips_a_pickup_and_a_secure()
    {
        // Pickup frame: original side 1, carrier 9042, not treasure-docked.
        var pickup = new GainFlag { SideidFlag = 1, ShipidRecipient = 9042, BIsTreasureDocked = false, ParttypeidDocked = 0 };
        var d1 = GainFlag.Decode(pickup.Encode());
        Assert.Equal(GainFlag.Fmid, d1.GetFmid());
        Assert.Equal((short)1, d1.SideidFlag);
        Assert.Equal((short)9042, d1.ShipidRecipient);
        Assert.False(d1.BIsTreasureDocked);
        Assert.Equal((short)0, d1.ParttypeidDocked);

        // Secure frame: sideidFlag = NA (-1), carrier 9066, not treasure-docked.
        var secure = new GainFlag { SideidFlag = GainFlag.NA, ShipidRecipient = 9066, BIsTreasureDocked = false, ParttypeidDocked = 0 };
        var d2 = GainFlag.Decode(secure.Encode());
        Assert.Equal((short)-1, d2.SideidFlag);
        Assert.Equal((short)9066, d2.ShipidRecipient);

        // The tech-recovery variant (decode-only in this task) round-trips too.
        var tech = new GainFlag { SideidFlag = 5, ShipidRecipient = 9077, BIsTreasureDocked = true, ParttypeidDocked = 771 };
        var d3 = GainFlag.Decode(tech.Encode());
        Assert.True(d3.BIsTreasureDocked);
        Assert.Equal((short)771, d3.ParttypeidDocked);
    }

    [Fact]
    public void GainFlag_emits_exact_declared_bytes()
    {
        // cbmsg=12, fmid=156, sideidFlag=0x0102, shipidRecipient=0x0304,
        // bIsTreasureDocked=1 (@8), pad=0 (@9), parttypeidDocked=0x0506 (@10).
        var got = new GainFlag { SideidFlag = 0x0102, ShipidRecipient = 0x0304, BIsTreasureDocked = true, ParttypeidDocked = 0x0506 }.Encode();
        Assert.Equal(12, got.Length);
        Assert.Equal(new byte[] { 12, 0, 156, 0, 0x02, 0x01, 0x04, 0x03, 1, 0, 0x06, 0x05 }, got);
    }
}
