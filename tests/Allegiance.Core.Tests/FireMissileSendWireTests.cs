using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P420 — proves the wire encoding of <c>FM_CS_FIRE_MISSILE(70)</c> as the
/// <see cref="Bot"/> / <c>ConnectionSession</c> <c>SendFireMissile</c>
/// methods actually send it: the minimal syntactically-valid player-launch
/// request (one <see cref="MissileLaunchData"/> entry, <c>TargetId = NA</c>)
/// whose payload the server's authoritative <c>HandleFireMissile</c>
/// consumes by resolving the launcher + lock itself. Checks the exact
/// byte layout from the codec's doc (<c>FireMissile.cs</c>): fixed region 32
/// bytes with the var-item descriptor preceding lock/time/ids/target, then
/// the 40-byte launch-data tail.
/// </summary>
public sealed class FireMissileSendWireTests
{
    /// <summary>The exact message <c>Bot.SendFireMissile</c> and
    /// <c>ConnectionSession.SendFireMissile</c> construct and encode.</summary>
    private static FireMissile MinimalSendShape() => new()
    {
        Lock = 0f,
        TimeFired = 0u,
        MissileTypeId = 0,
        LauncherId = 0,
        ClusterId = 0,
        TargetType = 0,
        TargetId = -1,
        BDud = false,
        Entries = [new MissileLaunchData(Vector3.Zero, Vector3.Zero, Vector3.Zero, 0)],
    };

    [Fact]
    public void MinimalSendShape_EncodesToExpectedLayout()
    {
        var msg = MinimalSendShape();
        byte[] bytes = msg.Encode();

        // header(4) + fixed(28..32) + one MissileLaunchData(40)
        Assert.Equal(FireMissile.FixedSize + MissileLaunchData.Size, bytes.Length);   // 72
        Assert.Equal(FireMissile.FixedSize, FireMissile.FixedSize);
        Assert.Equal(72, bytes.Length);

        // fmid at offset 2.
        Assert.Equal(FireMissile.Fmid, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));
        Assert.Equal((ushort)70, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));

        // var-item descriptor: ib = 32 (start of the tail), cb = 40 (one entry).
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(MissileLaunchData.Size, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));

        // Fixed fields land at their documented offsets.
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8))));   // lock
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));                                    // timeFired
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(16)));                              // missiletypeID
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(18)));                              // launcherID
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20)));                              // clusterID
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));                                     // targetType
        Assert.Equal((short)(-1), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(28)));                           // targetID = NA
        Assert.Equal(0, bytes[30]);                                                                                    // bDud = false
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsTheMinimalShape()
    {
        var msg = MinimalSendShape();
        var decoded = FireMissile.Decode(msg.Encode());

        Assert.Equal(FireMissile.Fmid, decoded.GetFmid());
        Assert.Equal(msg.Lock, decoded.Lock);
        Assert.Equal(msg.TimeFired, decoded.TimeFired);
        Assert.Equal(msg.MissileTypeId, decoded.MissileTypeId);
        Assert.Equal(msg.LauncherId, decoded.LauncherId);
        Assert.Equal(msg.ClusterId, decoded.ClusterId);
        Assert.Equal(msg.TargetType, decoded.TargetType);
        Assert.Equal(msg.TargetId, decoded.TargetId);
        Assert.Equal(msg.BDud, decoded.BDud);
        var e = Assert.Single(decoded.Entries);
        Assert.Equal(Vector3.Zero, e.VecPosition);
        Assert.Equal(Vector3.Zero, e.VecForward);
    }

    [Fact]
    public void SalvoOfTwoEntries_EncodeRoundTripsCorrectly()
    {
        var msg = new FireMissile
        {
            Lock = 0.5f,
            TimeFired = 777u,
            MissileTypeId = 36,
            LauncherId = 8021,
            ClusterId = 1050,
            TargetType = 1,
            TargetId = 8134,
            BDud = false,
            Entries =
            [
                new MissileLaunchData(new Vector3(1, 2, 3), new Vector3(4, 5, 6), new Vector3(0, 0, 0), 0),
                new MissileLaunchData(new Vector3(9, 8, 7), new Vector3(6, 5, 4), new Vector3(1, 1, 1), 1),
            ],
        };
        byte[] bytes = msg.Encode();
        Assert.Equal(FireMissile.FixedSize + 2 * MissileLaunchData.Size, bytes.Length);
        Assert.Equal(2 * MissileLaunchData.Size, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));   // cb = 80

        var decoded = FireMissile.Decode(bytes);
        Assert.Equal(msg.Lock, decoded.Lock);
        Assert.Equal(msg.MissileTypeId, decoded.MissileTypeId);
        Assert.Equal(msg.LauncherId, decoded.LauncherId);
        Assert.Equal(msg.ClusterId, decoded.ClusterId);
        Assert.Equal(msg.TargetId, decoded.TargetId);
        Assert.Equal(2, decoded.Entries.Count);
        Assert.Equal(new Vector3(9, 8, 7), decoded.Entries[1].VecPosition);
        Assert.Equal(1, decoded.Entries[1].MissileId);
    }
}
