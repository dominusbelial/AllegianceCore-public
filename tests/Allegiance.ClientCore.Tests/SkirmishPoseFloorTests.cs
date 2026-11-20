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

/// <summary>The skirmish setup screen draws the size warning with the desktop small
/// font, which has no em dash: choosing Grand crashed the client.</summary>
public sealed class SkirmishWarningGlyphTests
{
    [Fact]
    public void Size_warnings_are_plain_ascii()
    {
        foreach (var size in Enum.GetValues<SkirmishSize>())
        {
            string warning = new SkirmishOptions { Size = size }.Warning;
            Assert.True(warning.All(c => c < 128), $"{size}: {warning}");
        }
    }
}
