using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

public sealed class BroadcastEncodeOnceFlagTests
{
    [Theory]
    [InlineData(false, null, false, false)]
    [InlineData(false, "", true, false)]
    [InlineData(true, null, true, true)]
    [InlineData(false, "1", true, true)]
    [InlineData(false, " TRUE ", true, true)]
    [InlineData(true, "false", false, false)]
    [InlineData(true, "0", true, false)]
    public void Flag_follows_config_and_environment_wins(bool configured, string? env, bool snapshot, bool expected)
        => Assert.Equal(expected, Srv.ResolveEncodeOnceFlag(configured, env, snapshot));

    [Theory]
    [InlineData("tru", true)]
    [InlineData("1", false)]
    public void Invalid_flag_or_missing_snapshot_is_rejected(string env, bool snapshot)
        => Assert.Throws<InvalidDataException>(() => Srv.ResolveEncodeOnceFlag(false, env, snapshot));

    [Fact]
    public void Json_binds_both_flags_and_defaults_on()
    {
        Assert.True(new BroadcastConfig().EncodeOnce);   // default on since the 2026-10-27 rig A/B
        string path = Path.Combine(Path.GetTempPath(), $"p669-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"broadcast":{"immutableSnapshot":false,"encodeOnce":false}}""");
            var config = ServerConfigLoader.Load(path, []);
            Assert.False(config.Broadcast!.EncodeOnce);
            Assert.False(config.Broadcast.ImmutableSnapshot);
        }
        finally { File.Delete(path); }
    }
}

[Collection("LiveNetwork")]
public sealed class BroadcastEncodeOnceTests
{
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Shared_record_is_detached_and_encodes_once_across_workers_and_viewer_references()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var ship = UpdateCadenceTests.Seed(server, 1, 1)[0].Ship;
        ship.Position = new(111.25f, -87.125f, 12);
        ship.Velocity = new(-21, 3, 8);
        ship.Orientation.Yaw(.7f);
        ship.Controls = new(.1f, -.2f, .3f, .8f);
        ship.SetTurnRate(0, .2f);
        ship.SetTurnRate(1, -.15f);
        var counters = new ShipUpdateEncodingCounters();
        const uint time = uint.MaxValue - 12;
        var common = HeavyShipUpdateEntry.Capture(time, ship.Position, ship, time, counters);
        var references = Enumerable.Range(0, 256).Select(i => new Vector3(
            i == 0 ? ship.Position.X : i * 700.125f - 90000, i * -523.75f, i % 2 == 0 ? 0 : -0f)).ToArray();
        var records = references.Select((p, i) => common.RelativeTo(unchecked(time + (uint)(i * 173)), p)).ToArray();
        static byte[] Encode(HeavyShipUpdateEntry entry)
        {
            byte[] bytes = new byte[HeavyShipUpdateEntry.Width];
            WireWriter writer = new(bytes);
            entry.EncodeBody(ref writer);
            return bytes;
        }
        var expected = references.Select((p, i) => Encode(HeavyShipUpdateEntry.From(
            unchecked(time + (uint)(i * 173)), p, ship, time))).ToArray();
        var light = LightShipUpdateEntry.From(ship.Id, ship.StateM, ship.Controls);
        var sharedLight = light.EncodeOnce();
        byte[] Light(LightShipUpdateEntry entry)
        {
            byte[] bytes = new byte[LightShipUpdateEntry.Size];
            WireWriter writer = new(bytes);
            entry.Write(ref writer);
            return bytes;
        }
        ship.Position = new(999999, 888888, 777777);
        ship.Orientation.Yaw(2);
        ship.Controls = default;
        ship.SetTurnRate(0, 9);
        BroadcastSnapshotGuard.AssertDetached(common);
        BroadcastSnapshotGuard.AssertDetached(sharedLight);
        var actual = new byte[records.Length][];
        Parallel.For(0, records.Length, i => actual[i] = Encode(records[i]));
        Assert.Equal(expected, actual);
        Assert.Equal(1, counters.HeavyRecords);
        Assert.Equal(records.Length, counters.RelativePositions);
        Assert.Equal(Light(light), Light(sharedLight));
        Assert.Equal(light, sharedLight);
        Assert.Equal(light.GetHashCode(), sharedLight.GetHashCode());
        Assert.Equal(Light(light with { ShipId = 123 }), Light(sharedLight with { ShipId = 123 }));
        // A later tick has a new owned record; it cannot rewrite an old buffer.
        var next = HeavyShipUpdateEntry.Capture(time + 1, Vector3.Zero, ship, time + 1, counters);
        Assert.NotEqual(expected[0], Encode(next.RelativeTo(time + 1, references[0])));
        Assert.Equal(expected[0], Encode(records[0]));
        Assert.Equal(2, counters.HeavyRecords);
    }
}
