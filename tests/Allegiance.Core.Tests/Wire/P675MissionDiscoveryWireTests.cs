using System.Buffers.Binary;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

public class P675MissionDiscoveryWireTests
{
    [Fact]
    public void Discovery_reads_native_short_fields_without_enum_padding()
    {
        byte[] bytes = [12, 0, 6, 0, 7, 0, 0x34, 0x12, 0, 0, 0xfe, 0xff];
        var message = ObjectSpotted.Decode(bytes);
        Assert.Equal(7, message.OtObject);
        Assert.Equal(0x1234, message.OidObject);
        Assert.Equal(0, message.OtSpotter);
        Assert.Equal(-2, message.OidSpotter);
        Assert.Equal(bytes, message.Encode());
    }

    [Theory]
    [InlineData(0)] [InlineData(11)] [InlineData(13)] [InlineData(20)]
    public void Discovery_rejects_non_native_fixed_width_even_with_matching_header(int size)
    {
        var bytes = new byte[size];
        if (size >= 4) { BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)size); bytes[2] = 6; }
        Assert.Throws<ArgumentException>(() => ObjectSpotted.Decode(bytes));
    }

    [Fact]
    public void Joined_identity_survives_nonzero_native_alignment_bytes()
    {
        byte[] bytes = [20, 0, 95, 0, 0x34, 0x12, 0xcc, 0xcc, 0, 0, 0x20, 0x40, 0xab, 0xcc, 0xcc, 0xcc, 0x78, 0x56, 0x34, 0x12];
        var message = JoinedMission.Decode(bytes);
        Assert.Equal(0x1234, message.ShipId);
        Assert.Equal(2.5f, message.DTime);
        Assert.Equal(0xab, message.Cookie);
        Assert.Equal(0x12345678u, message.DwCookie);
        Assert.Equal(message.DwCookie, JoinedMission.Decode(message.Encode()).DwCookie);
    }
}
