using Allegiance.ClientCore.Flight;

namespace Allegiance.ClientCore.Tests;

public sealed class P695ServerClockTests
{
    [Fact]
    public void Latest_ping_reanchors_after_a_dilation_rate_change()
    {
        var clock = new OwnShipServerClock();
        const long epoch = 1_000_000_000;
        uint server = 2_600_000;
        for (int i = 0; i < 32; i++)
        {
            clock.AddSample(server, epoch + i * 6000);
            server += 6000;
        }
        // A busy battle reduces the rate to .8. An actual new server timestamp
        // must win over the three minutes of history at the old rate.
        for (int i = 32; i < 48; i++)
        {
            clock.AddSample(server, epoch + i * 6000);
            Assert.InRange(Math.Abs((long)clock.ServerTimeMs(epoch + i * 6000) - server), 0, 1499);
            server += 4800;
        }
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    public void Sparse_keepalives_extrapolate_at_the_advertised_rate(double rate)
    {
        var clock = new OwnShipServerClock();
        const long epoch = 9_000_000_000;
        clock.SetTimeScale(rate, epoch);
        clock.AddSample(2_636_000, epoch);
        Assert.Equal((uint)(2_636_000 + 5999 * rate), clock.ServerTimeMs(epoch + 5999));
        clock.SetTimeScale(0.5, epoch + 5999);
        Assert.Equal((uint)(2_636_000 + 5999 * rate + 500), clock.ServerTimeMs(epoch + 6999));
    }

    [Fact]
    public void Reconnect_discards_the_old_epoch_but_duplicate_echoes_do_not_rewind_it()
    {
        var clock = new OwnShipServerClock();
        clock.AddSample(3_000_000, 1_000_000);
        clock.AddSample(2_999_000, 1_001_000);
        Assert.Equal(3_001_000u, clock.ServerTimeMs(1_001_000));
        clock.Reset();
        Assert.False(clock.Ready);
        Assert.Equal(0u, clock.ServerTimeMs(1_002_000));
        clock.AddSample(50, 1_002_000);
        Assert.Equal(150u, clock.ServerTimeMs(1_002_100));
    }
}
