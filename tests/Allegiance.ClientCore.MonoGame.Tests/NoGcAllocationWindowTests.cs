namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection(AllocationMeasurementCollection.Name)]
public sealed class NoGcAllocationWindowTests
{
    private static object? _allocationSink;
    [Fact]
    public void A_real_allocation_is_still_counted_in_the_collection_free_window()
    {
        // Do not let a measurement guard silently turn a real allocation green.
        Volatile.Write(ref _allocationSink, null); // initialize the static slot before measuring
        using var window = new NoGcAllocationWindow();
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] allocated = new byte[16];
        Volatile.Write(ref _allocationSink, allocated); // force a heap escape in optimized builds too
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(allocated);
        Assert.Equal(40, bytes); // array header + 16 bytes on the supported 64-bit test hosts
    }

    [Fact]
    public void A_collection_invalidates_the_window_instead_of_returning_a_measurement()
    {
        var window = new NoGcAllocationWindow();
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        Assert.Throws<InvalidOperationException>(window.Dispose);
    }
}
