using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P285 — pure codec round-trip for the new port-extension
/// <c>SET_BUILDER_TARGET</c>(201) C-&gt;S message: the client's chosen Builder
/// target station type for a purchased Builder hull. 8-byte fixed shape
/// (header 4 + hullId short + stationTypeId short), mirroring <see cref="JoinSide"/>.
/// </summary>
public class SetBuilderTargetCodecTests
{
    [Fact]
    public void Roundtrips_fields_and_fmid()
    {
        var msg = new SetBuilderTarget { HullId = 4242, StationTypeId = 5313 };
        var bytes = msg.Encode();

        Assert.Equal(SetBuilderTarget.FixedSize, bytes.Length);
        Assert.Equal((ushort)201, SetBuilderTarget.Fmid);

        var decoded = SetBuilderTarget.Decode(bytes);
        Assert.Equal(msg.GetFmid(), decoded.GetFmid());
        Assert.Equal(4242, decoded.HullId);
        Assert.Equal(5313, decoded.StationTypeId);
    }

    [Fact]
    public void Null_target_ids_encode_as_minus_one()
    {
        // A player may launch a Builder without ever cycling a target; the
        // message is simply not sent then, but the codec must still handle the
        // explicit "none" spelling (-1 / NA) losslessly.
        var decoded = SetBuilderTarget.Decode(new SetBuilderTarget { HullId = -1, StationTypeId = -1 }.Encode());
        Assert.Equal(-1, decoded.HullId);
        Assert.Equal(-1, decoded.StationTypeId);
    }

    [Fact]
    public void Decode_rejects_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => SetBuilderTarget.Decode(new byte[7]));
    }
}
