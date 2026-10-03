using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P508 — the wire contract of the NATIVE <c>FM_C_SHIP_UPDATE(24)</c> the client
/// now sends: a real server-converted time, the current ship cookie, and the full
/// pose (position / orientation / velocity / turn rates / power) plus the
/// controls/stateM — exactly the payload a P508 client emits at the 0.15 s cadence
/// (<c>ClientShipUpdate.FromPose</c> → <c>ShipUpdate</c>, the path
/// <c>ConnectionSession.SendShipPose</c> uses).
/// </summary>
public sealed class NativeShipUpdateWireTests
{
    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    [Fact]
    public void Native_pose_update_round_trips_preserving_time_cookie_and_full_pose()
    {
        uint time = 12345u;
        byte cookie = 0xA7;
        var ori = Orientation.Identity;
        ori.Yaw(0.7f);
        ori.Renormalize();
        var controls = new ControlData(Yaw: 0.3f, Pitch: -0.2f, Roll: 0.1f, Throttle: 0.75f);
        var pos = new Vector3(1234f, -56f, 789f);
        var vel = new Vector3(80f, 0f, 0f);
        var tr = new float[] { 1.2f, -0.4f, 0.3f };

        var payload = ClientShipUpdate.FromPose(time, pos, vel, ori, 5, controls, tr, 0.9f);
        var msg = new ShipUpdate { Time = time, Cookie = cookie, Payload = payload };

        var dec = ShipUpdate.Decode(msg.Encode());

        Assert.Equal((ushort)24, dec.GetFmid());
        Assert.Equal(time, dec.Time);
        Assert.Equal(time, dec.Payload.Time);
        Assert.Equal(cookie, dec.Cookie);

        // Pose (compact-quantized, so small tolerances).
        Assert.True(Dist(dec.Payload.Position, pos) < 1f, $"position {dec.Payload.Position} vs {pos}");
        Assert.True(Dist(dec.Payload.Velocity, vel) < 5f, $"velocity {dec.Payload.Velocity} vs {vel}");
        Assert.Equal(5, dec.Payload.StateM);
        Assert.Equal(0.3f, dec.Payload.Controls.Yaw, 2);
        Assert.Equal(-0.2f, dec.Payload.Controls.Pitch, 2);
        Assert.Equal(0.75f, dec.Payload.Controls.Throttle, 2);
        Assert.Equal(0.9f, dec.Payload.Power, 2);
        Assert.Equal(3, dec.Payload.TurnRates.Length);
        Assert.Equal(1.2f, dec.Payload.TurnRates[0], 1);   // compact-quantized (rad/s)
    }
}
