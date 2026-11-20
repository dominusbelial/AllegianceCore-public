using Allegiance.Server;
using Allegiance.SinglePlayer;

namespace Allegiance.ClientCore.Tests;

/// <summary>Loopback single-player skirmish raises the remote-ship pose floor to
/// the full tick rate; LAN hosting, Grand and dedicated servers keep the default.</summary>
public sealed class SkirmishPoseFloorTests
{
    [Theory]
    [InlineData(SkirmishSize.Small)]
    [InlineData(SkirmishSize.Medium)]
    [InlineData(SkirmishSize.Large)]
    [InlineData(SkirmishSize.Epic)]
    public void Loopback_skirmish_uses_full_rate_pose_floor(SkirmishSize size)
    {
        var config = new SkirmishOptions { Size = size }.CreateConfig();
        Assert.Equal(SkirmishOptions.LoopbackMinUpdateHz, config.Broadcast!.MinUpdateHz);
        Assert.Equal(20, SkirmishOptions.LoopbackMinUpdateHz);
        // Build sizing stays with the resolver.
        Assert.Null(config.Broadcast.ParallelBuild);
        Assert.Null(config.Broadcast.MaxDegreeOfParallelism);
    }

    [Fact]
    public void Grand_and_lan_host_keep_default_floor()
    {
        double fallback = new BroadcastConfig().MinUpdateHz;
        Assert.Equal(fallback, new SkirmishOptions { Size = SkirmishSize.Grand }.CreateConfig().Broadcast!.MinUpdateHz);
        Assert.Equal(fallback, new SkirmishOptions { HostLan = true, GameName = "Friends", Password = "test-pass" }.CreateConfig().Broadcast!.MinUpdateHz);
        Assert.Equal(2, new ServerConfig().Broadcast?.MinUpdateHz ?? fallback);
    }
}
