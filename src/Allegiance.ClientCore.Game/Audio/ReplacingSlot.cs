namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// A single-slot holder for a resource that must be released when it is
/// replaced. Encapsulates the M-3 ambience-instance discipline — dispose (via
/// <paramref name="onEvict"/>) the value currently in the slot exactly once
/// whenever <see cref="Replace"/> puts a new value in, instead of abandoning
/// the old one and leaking it. Kept as a tiny pure type (no MonoGame surface)
/// so the no-leak invariant is directly unit-testable without an audio device.
/// </summary>
/// <typeparam name="T">The held resource type (e.g. <c>SoundEffectInstance</c>).</typeparam>
public sealed class ReplacingSlot<T> where T : class
{
    private readonly Action<T> _onEvict;

    /// <param name="onEvict">Callback that releases an evicted value. Must not
    /// throw (the audio <c>Safe</c> wrapper is the caller's normal supplier).</param>
    public ReplacingSlot(Action<T> onEvict)
    {
        _onEvict = onEvict ?? throw new ArgumentNullException(nameof(onEvict));
    }

    /// <summary>The value currently in the slot, or <c>null</c> when empty.</summary>
    public T? Current { get; private set; }

    /// <summary>Put <paramref name="next"/> in the slot, evicting (and thereby
    /// releasing) the previous occupant first. A null <paramref name="next"/>
    /// is treated as clearing the slot.</summary>
    public void Replace(T? next)
    {
        if (!ReferenceEquals(Current, next))
        {
            if (Current is { } prev) _onEvict(prev);
            Current = next;
        }
    }

    /// <summary>Evict and release the current occupant, emptying the slot.</summary>
    public void Clear()
    {
        if (Current is { } prev)
        {
            _onEvict(prev);
            Current = null;
        }
    }
}
