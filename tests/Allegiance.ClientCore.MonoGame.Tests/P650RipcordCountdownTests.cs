using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P650RipcordCountdownTests
{
    [Fact]
    public void Voices_each_integer_boundary_once_and_resets_on_abort_or_completion()
    {
        var cue = new RipcordCountdownSound();
        Assert.Null(cue.Update(false, 10));
        Assert.Null(cue.Update(true, 10)); // native 1 + int(10) = 11
        for (int second = 10; second >= 1; second--)
        {
            Assert.Equal(second, cue.Update(true, second - 0.01f));
            Assert.Null(cue.Update(true, second - 0.02f));
            Assert.Equal($"countdown{second}", CountdownSound.TickAssetFor(second));
        }
        Assert.Null(cue.Update(true, 0));
        Assert.Equal(10, cue.Update(true, 9.9f));
        Assert.Null(cue.Update(false, 9.9f));
        Assert.Equal(10, cue.Update(true, 9.9f));
        Assert.InRange(RipcordCountdownSound.Gain, 0.44668f, 0.44669f);
    }

    [Fact]
    public void Skipped_frames_do_not_queue_stale_seconds_and_retarget_can_restart()
    {
        var cue = new RipcordCountdownSound();
        Assert.Equal(10, cue.Update(true, 9.8f));
        Assert.Equal(6, cue.Update(true, 5.8f));
        Assert.Equal(9, cue.Update(true, 8.8f));
        Assert.Null(cue.Update(true, 20));
    }
    [Fact]
    public void Countdown_and_HUD_expiry_follow_changing_simulation_scale_and_retarget()
    {
        var cue = new RipcordCountdownSound();
        Assert.Equal(10f, cue.TimeLeft(true, 1000, 1000, 10, 0.5));
        Assert.Equal(5f, cue.TimeLeft(true, 1000, 11000, 10, 0.5));
        Assert.Equal(4f, cue.TimeLeft(true, 1000, 12000, 10, 1));
        Assert.Equal(4f, cue.TimeLeft(true, 1000, 12000, 2, 1)); // research does not reseed an active timer
        Assert.Equal(0f, cue.TimeLeft(true, 1000, 16000, 10, 1));
        Assert.Equal(10f, cue.TimeLeft(true, 17000, 17000, 10, 1));
        Assert.Equal(0f, cue.TimeLeft(false, null, 18000, 10, 1));
        Assert.Equal(10f, cue.TimeLeft(true, 19000, 19000, 10, 1));
    }

}
