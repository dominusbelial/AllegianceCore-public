using Allegiance.ClientCore.Training;

namespace Allegiance.ClientCore.Tests;

[Collection("Training")]
public sealed class P931TrainingClockTests : IDisposable
{
    public P931TrainingClockTests()
    {
        TrainingClock.ResetForTests();
        TrainingClock.DebugTrainingKeys = false;
    }

    public void Dispose()
    {
        TrainingClock.ResetForTests();
        TrainingClock.DebugTrainingKeys = false;
    }

    [Fact]
    public void Shipping_lesson_clock_keeps_the_hosts_elapsed_time_across_slow_frames()
    {
        // zlib/ztime.h:50-77: the 4 FPS clamp is inside _DEBUG_TRAINING;
        // shipping Now reads the complete monotonic timeGetTime interval.
        double hostElapsed = 0;
        foreach (double frame in new[] { .1, .7, .4, .95, .05 })
        {
            hostElapsed += frame;
            Assert.Equal(hostElapsed, TrainingClock.Advance(frame), 8);
        }
    }

    [Fact]
    public void Debug_clock_alone_retains_the_native_quarter_second_cap_and_shift()
    {
        TrainingClock.DebugTrainingKeys = true;
        TrainingClock.SetShift(1);
        Assert.Equal(.5, TrainingClock.Advance(.7), 8);
    }
}
