using Allegiance.Core.Audio;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Native soundengine/soundtemplates.cpp:1832-2015. One active
/// template per mutex; expire stale requests, select minimum expiration minus
/// priority, suppress active duplicates and refresh queued duplicates without
/// changing their original urgency. Different native mutexes may overlap.</summary>
public sealed class SerializedVoiceQueue
{
    private sealed record Pending(NativeVoiceCue Cue, double Expires, double Urgency);
    private readonly Dictionary<string, NativeVoiceCue> _active = new();
    private readonly Dictionary<string, List<Pending>> _pending = new();

    public NativeVoiceCue? Enqueue(NativeVoiceCue cue, double now)
    {
        if (!_active.TryGetValue(cue.Mutex, out var active))
        {
            _active[cue.Mutex] = cue;
            return cue;
        }
        if (cue.SingleInstance && active.Name == cue.Name) return null;
        if (!_pending.TryGetValue(cue.Mutex, out var pending))
            _pending[cue.Mutex] = pending = new();
        int existing = cue.SingleInstance ? pending.FindIndex(p => p.Cue.Name == cue.Name) : -1;
        if (existing >= 0)
            pending[existing] = pending[existing] with { Expires = now + cue.Timeout };
        else
            pending.Add(new Pending(cue, now + cue.Timeout, now + cue.Timeout - cue.Priority));
        return null;
    }

    public NativeVoiceCue? Complete(string mutex, double now)
    {
        _active.Remove(mutex);
        if (!_pending.TryGetValue(mutex, out var pending)) return null;
        pending.RemoveAll(p => p.Expires < now);
        if (pending.Count == 0) return null;
        var next = pending.MinBy(p => p.Urgency)!;
        pending.Remove(next);
        _active[mutex] = next.Cue;
        return next.Cue;
    }

    public void Clear()
    {
        _active.Clear();
        _pending.Clear();
    }
}
