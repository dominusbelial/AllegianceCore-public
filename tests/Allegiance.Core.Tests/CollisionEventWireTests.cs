using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using CollisionEvent = Allegiance.Core.Wire.CollisionEvent;

namespace Allegiance.Core.Tests;

/// <summary>
/// P463c — wire codec test for the <see cref="CollisionEvent"/> message
/// (<c>COLLISION_EVENT(216)</c>): the 32-byte fixed layout round-trips
/// party1/party2 ids, cluster, the two per-party jiggles and the impact point.
/// </summary>
public sealed class CollisionEventWireTests
{
    [Fact]
    public void CollisionEvent_encodes_and_decodes_the_full_layout()
    {
        var msg = new CollisionEvent
        {
            Party1Id = 901,
            Party2Id = -1,               // a rock/station party → NA
            ClusterId = 512,
            Jiggle1 = 191.25f,
            Jiggle2 = 0f,
            Position = new Vector3(10f, -20f, 30.5f),
        };

        byte[] bytes = msg.Encode();
        Assert.Equal(CollisionEvent.FixedSize, bytes.Length);
        Assert.Equal(32, bytes.Length);

        var decoded = CollisionEvent.Decode(bytes);
        Assert.Equal(CollisionEvent.Fmid, decoded.GetFmid());
        Assert.Equal((short)901, decoded.Party1Id);
        Assert.Equal((short)-1, decoded.Party2Id);
        Assert.Equal((short)512, decoded.ClusterId);
        Assert.Equal(191.25f, decoded.Jiggle1);
        Assert.Equal(0f, decoded.Jiggle2);
        Assert.Equal(10f, decoded.Position.X);
        Assert.Equal(-20f, decoded.Position.Y);
        Assert.Equal(30.5f, decoded.Position.Z);
    }

    [Fact]
    public void CollisionEvent_round_trips_a_ship_ship_pair()
    {
        var msg = new CollisionEvent
        {
            Party1Id = 902,
            Party2Id = 903,
            ClusterId = 513,
            Jiggle1 = 41.06875f,
            Jiggle2 = 55.9375f,
            Position = new Vector3(1f, 2f, 3f),
        };
        var decoded = CollisionEvent.Decode(msg.Encode());
        Assert.Equal((short)902, decoded.Party1Id);
        Assert.Equal((short)903, decoded.Party2Id);
        Assert.Equal(41.06875f, decoded.Jiggle1);
        Assert.Equal(55.9375f, decoded.Jiggle2);
    }
}
