using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// M-3 regression tests for the ambience-instance disposal discipline. The
/// previous sector-ambience <c>SoundEffectInstance</c> must be released each
/// time a new one is started — the port used to overwrite the reference,
/// abandoning ~1 undisposed (stopped) voice handle every ~10s (roughly 700
/// leaked handles over a 2-hour session, i.e. the "audio cuts out late in a
/// long session" failure). <see cref="ReplacingSlot{T}"/> is the pure holder
/// that enforces this, so the no-accumulation invariant is testable without an
/// audio device.
/// </summary>
public class ReplacingSlotTests
{
    [Fact]
    public void Replace_EvictsPreviousExactlyOnce_InOrder()
    {
        int evictions = 0;
        var evicted = new List<string>();
        var slot = new ReplacingSlot<string>(s => { evictions++; evicted.Add(s); });

        slot.Replace("a");
        Assert.Equal(0, evictions); // first occupant has no predecessor

        slot.Replace("b");
        Assert.Equal(1, evictions);
        Assert.Equal(new[] { "a" }, evicted);

        slot.Replace("c");
        Assert.Equal(2, evictions);
        Assert.Equal(new[] { "a", "b" }, evicted);

        // The newest occupant is retained (and only it).
        Assert.Equal("c", slot.Current);
    }

    [Fact]
    public void ManyReplaces_NoAccumulation()
    {
        // Directly encodes the M-3 leak: N ambience triggers must dispose N-1
        // predecessors — never accumulate N undisposed voices.
        int evictions = 0;
        var slot = new ReplacingSlot<object>(_ => evictions++);
        const int N = 731; // > the ~700 voices a 2-hour session used to leak
        for (int i = 0; i < N; i++)
            slot.Replace(new object());

        Assert.Equal(N - 1, evictions);
        Assert.NotNull(slot.Current);
    }

    [Fact]
    public void ReplaceSameReference_DoesNotEvict()
    {
        int evictions = 0;
        var slot = new ReplacingSlot<object>(_ => evictions++);
        var o = new object();
        slot.Replace(o);
        slot.Replace(o); // same instance — no premature release
        Assert.Equal(0, evictions);
        Assert.Same(o, slot.Current);
    }

    [Fact]
    public void Clear_EvictsCurrentOnce_AndIsIdempotent()
    {
        int evictions = 0;
        var slot = new ReplacingSlot<string>(_ => evictions++);
        slot.Replace("x");
        slot.Clear();
        Assert.Equal(1, evictions);
        Assert.Null(slot.Current);

        slot.Clear(); // empty slot — nothing more to release
        Assert.Equal(1, evictions);
    }
}
