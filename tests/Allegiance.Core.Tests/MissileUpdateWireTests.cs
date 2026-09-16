using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P234 — the wire codec for the this-port-only <c>FM_S_MISSILE_UPDATE(205)</c>
/// per-tick missile-position stream: a variable-length <see cref="MissileStateRecord"/>[]
/// array behind the same FM_VAR_ITEM ib/cb descriptor convention FIRE_MISSILE
/// uses. These are pure codec tests — encode/decode round-trip, fixed-width
/// invariants, and fmid hygiene (205 must sit above native's real max 200).
/// </summary>
public class MissileUpdateWireTests
{
    [Fact]
    public void RoundTrips_MultipleRecords_PreservingAllFields()
    {
        var msg = new MissileUpdate
        {
            Records =
            [
                new MissileStateRecord(100, 34, 5050, new Vector3(1f, 2f, 3f), new Vector3(0f, 0f, -1f)),
                new MissileStateRecord(101, 44, 5060, new Vector3(-5f, 6f, -7f), new Vector3(0.577f, 0.577f, -0.577f)),
                new MissileStateRecord(102, 58, 5070, new Vector3(0f, 0f, 0f), new Vector3(0f, 1f, 0f)),
            ],
        };

        byte[] bytes = msg.Encode();
        // header(4) + var descriptor(4) + 3 × 32
        Assert.Equal(8 + 3 * MissileStateRecord.Size, bytes.Length);

        var decoded = MissileUpdate.Decode(bytes);
        Assert.Equal(MissileUpdate.Fmid, decoded.GetFmid());
        Assert.Equal(3, decoded.Records.Count);

        Assert.Equal((short)100, decoded.Records[0].MissileId);
        Assert.Equal((short)34, decoded.Records[0].TypeId);
        Assert.Equal((short)5050, decoded.Records[0].ClusterId);
        Assert.Equal(1f, decoded.Records[0].Position.X);
        Assert.Equal(2f, decoded.Records[0].Position.Y);
        Assert.Equal(3f, decoded.Records[0].Position.Z);
        Assert.Equal(0f, decoded.Records[0].Forward.X);
        Assert.Equal(-1f, decoded.Records[0].Forward.Z, 4);

        Assert.Equal((short)101, decoded.Records[1].MissileId);
        Assert.Equal((short)44, decoded.Records[1].TypeId);
        Assert.Equal(-5f, decoded.Records[1].Position.X);
        Assert.Equal(6f, decoded.Records[1].Position.Y);
        Assert.Equal(-7f, decoded.Records[1].Position.Z);

        Assert.Equal((short)102, decoded.Records[2].MissileId);
        Assert.Equal((short)58, decoded.Records[2].TypeId);
        Assert.Equal(0f, decoded.Records[2].Position.X);
        Assert.Equal(1f, decoded.Records[2].Forward.Y, 4);
    }

    [Fact]
    public void EmptyRecords_EncodeToJustFixedRegion()
    {
        var msg = new MissileUpdate { Records = [] };
        byte[] bytes = msg.Encode();
        Assert.Equal(MissileUpdate.FixedSize, bytes.Length);
        var decoded = MissileUpdate.Decode(bytes);
        Assert.Empty(decoded.Records);
    }

    [Fact]
    public void Fmid_SitsAboveNativesRealMax()
    {
        // Native's real FMID range tops out at 200 (Messages.h); P234's update
        // (205, like P230's 204) must never collide with a faithful port.
        Assert.Equal((ushort)205, MissileUpdate.Fmid);
        Assert.True(MissileUpdate.Fmid > 200);
    }

    [Fact]
    public void Decode_RejectsWrongFmid()
    {
        var msg = new MissileUpdate { Records = [new MissileStateRecord(1, 34, 5020, Vector3.Zero, new Vector3(0f, 0f, -1f))] };
        byte[] bytes = msg.Encode();
        bytes[2] = (byte)(MissileUpdate.Fmid + 1);      // corrupt the fmid
        Assert.Throws<ArgumentException>(() => MissileUpdate.Decode(bytes));
    }

    [Fact]
    public void RecordSize_Is32Bytes()
    {
        // 6 B ids/pad + 24 B vectors, 4-aligned throughout.
        Assert.Equal(32, MissileStateRecord.Size);
    }
}
