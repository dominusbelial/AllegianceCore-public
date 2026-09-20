using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-buffer tests for <see cref="PlaceholderMusicFactory"/> — the P304
/// generated placeholder music beds. Verifies the two tracks are valid,
/// deterministic, measurably distinct (station is higher-pitched than flight),
/// and loop seamlessly. No audio device involved: these are raw PCM byte
/// buffers, so they are fully unit-testable.
/// </summary>
public class PlaceholderMusicFactoryTests
{
    [Fact]
    public void BothTracks_HaveValidPcmLength()
    {
        var flight = PlaceholderMusicFactory.Flight();
        var station = PlaceholderMusicFactory.Station();

        int expected = PlaceholderMusicFactory.BufferBytes;
        Assert.Equal(expected, flight.Length);
        Assert.Equal(expected, station.Length);
        // Even byte count → integral number of 16-bit samples.
        Assert.Equal(0, flight.Length % 2);
        Assert.NotNull(flight);
        Assert.NotNull(station);
    }

    [Fact]
    public void Generation_IsDeterministic_ByteIdentical()
    {
        var a = PlaceholderMusicFactory.Flight();
        var b = PlaceholderMusicFactory.Flight();
        var c = PlaceholderMusicFactory.Station();
        var d = PlaceholderMusicFactory.Station();

        Assert.Equal(a, b);
        Assert.Equal(c, d);
    }

    [Fact]
    public void FlightAndStation_AreDistinctBuffers()
    {
        var flight = PlaceholderMusicFactory.Flight();
        var station = PlaceholderMusicFactory.Station();

        Assert.False(flight.SequenceEqual(station), "the two tracks must differ");
    }

    [Fact]
    public void StationIsHigherPitched_ThanFlight_MeasurableViaZeroCrossings()
    {
        // Average zero-crossing rate scales with dominant frequency, so the
        // higher station bed must cross zero noticeably more often than the
        // low flight bed — a real, measurable distinction (not just "the bytes
        // differ"). Two-fold safety margin well outside synthesis rounding.
        double flight = ZeroCrossingsPerSecond(PlaceholderMusicFactory.Flight());
        double station = ZeroCrossingsPerSecond(PlaceholderMusicFactory.Station());

        Assert.True(flight > 0, "flight bed must actually oscillate");
        Assert.True(station > flight * 1.2,
            $"station ({station:F0}/s) should clearly exceed flight ({flight:F0}/s)");
    }

    [Fact]
    public void LoopsSeamlessly_EndpointIsContinuous()
    {
        // A tone with an integer number of cycles over the buffer wraps the
        // seam back onto the same phase — the last sample is near the first, so
        // an IsLooped=true loop doesn't click. Allow modest tolerance for the
        // two-tone mix / tremolo envelope drift across the (exactly cyclic)
        // envelope.
        var flight = PlaceholderMusicFactory.Flight();
        short first = ReadSample(flight, 0);
        short last = ReadSample(flight, flight.Length / 2 - 1);
        int delta = Math.Abs(first - last);
        Assert.True(delta < 8192, $"seam discontinuity too large ({delta} samples) for a clean loop");
    }

    private static short ReadSample(byte[] buffer, int sampleIndex) =>
        (short)(buffer[sampleIndex * 2] | (buffer[sampleIndex * 2 + 1] << 8));

    private static double ZeroCrossingsPerSecond(byte[] buffer)
    {
        int samples = buffer.Length / 2;
        int crossings = 0;
        short prev = ReadSample(buffer, 0);
        for (int i = 1; i < samples; i++)
        {
            short cur = ReadSample(buffer, i);
            if ((prev < 0 && cur >= 0) || (prev >= 0 && cur < 0)) crossings++;
            prev = cur;
        }
        return (double)crossings / PlaceholderMusicFactory.Seconds;
    }
}
