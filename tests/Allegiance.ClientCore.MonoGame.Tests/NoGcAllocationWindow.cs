namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// The GC's retirement of a thread's allocation context can increase
/// GetAllocatedBytesForCurrentThread even when it allocates no objects (P1060).
/// Keep one warmed measurement free of collections, rather than retrying or
/// subtracting bytes. The reservation is process-wide, so callers belong to
/// this nonparallel collection; fixture setup and assertions stay outside it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    public const string Name = "AllocationMeasurement";
}

internal sealed class NoGcAllocationWindow : IDisposable
{
    // Space reserved for the collector, NOT an allowed allocation count. Every
    // measured byte still fails the caller's zero-byte assertion. Background
    // transport/finalizer activity has room while other test collections rest.
    private const long ReservationBytes = 16 * 1024 * 1024;

    public NoGcAllocationWindow()
    {
        if (!GC.TryStartNoGCRegion(ReservationBytes))
            throw new InvalidOperationException("Could not reserve a collection-free allocation measurement.");
    }

    public void Dispose()
    {
        // Also rejects an exhausted reservation or an explicit collection:
        // an invalid measurement is a failure, never a retry or a best window.
        GC.EndNoGCRegion();
    }
}
