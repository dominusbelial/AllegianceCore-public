namespace Allegiance.Core.Sim;

/// <summary>The native Time resolution is one millisecond; duration arithmetic
/// rounds with floor(seconds * 1000 + .5), zlib/ztime.h:144-159.</summary>
public static class IgcTime
{
    public static uint ToMilliseconds(double seconds) =>
        unchecked((uint)Math.Floor(seconds * 1000 + .5));
}
