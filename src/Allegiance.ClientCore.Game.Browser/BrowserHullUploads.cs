using System.Diagnostics;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P1069 — the browser's per-frame hull decode/upload budget. A hull whose
/// verified model bytes just became resident is queued once; at most
/// <see cref="PerFrameBudget"/> queued hulls are decoded and uploaded per frame,
/// off the ship-pass draw path, and the ship keeps its radius-sized stand-in
/// until its mesh is uploaded. This spreads the first draw of a newly promoted
/// hull instead of decoding/uploading it inside one frame (the P1065 ship-pass
/// hitches of 46/296/299 ms). The mesh decode and the GPU buffer/texture upload
/// are the desktop <see cref="Rendering.NativeShipRenderer"/> calls the draw
/// path already cached, so no new graphics rule is introduced.
/// </summary>
public sealed class BrowserHullUploads
{
    /// <summary>One hull per frame: the closest KNI-legal equivalent of a
    /// frame-spread upload (no async GPU upload exists under WebGL/KNI).</summary>
    public const int DefaultPerFrameBudget = 1;

    private enum Phase : byte { Queued, Uploaded }

    private struct Entry
    {
        public Phase Phase;
        public long ReadyMs;
        public bool Drawn;
    }

    private readonly Dictionary<short, Entry> _entries = new(64);
    private readonly List<short> _pending = new(64);
    private readonly Func<short, bool> _bytesReady;
    private readonly Func<short, bool> _upload;
    private int _cursor;

    /// <param name="bytesReady">True once the hull's verified original mesh bytes
    /// are resident (the P1065 <c>BattleShipScene.MeshBytesReady</c> cache).</param>
    /// <param name="upload">Decode the mesh and upload its GPU buffers/textures
    /// for one hull; returns false only when the hull model itself is unreadable
    /// (the ship then stays a stand-in).</param>
    /// <param name="perFrameBudget">Hulls uploaded per frame (≥1).</param>
    public BrowserHullUploads(Func<short, bool> bytesReady, Func<short, bool> upload,
        int perFrameBudget = DefaultPerFrameBudget)
    {
        if (perFrameBudget < 1) throw new ArgumentOutOfRangeException(nameof(perFrameBudget));
        _bytesReady = bytesReady;
        _upload = upload;
        PerFrameBudget = perFrameBudget;
    }

    public int PerFrameBudget { get; }

    /// <summary>Queued hulls not yet uploaded (diagnostic/harness metric).</summary>
    public int PendingCount => _pending.Count - _cursor;

    /// <summary>Hulls whose decode/upload has completed this view.</summary>
    public int UploadedCount
    {
        get
        {
            int count = 0;
            foreach (var entry in _entries.Values) if (entry.Phase == Phase.Uploaded) count++;
            return count;
        }
    }

    /// <summary>Milliseconds spent decoding/uploading in the last stepped frame.</summary>
    public double LastUploadMs { get; private set; }

    /// <summary>Worst single-hull decode/upload seen since the last reset.</summary>
    public double MaxUploadMs { get; private set; }

    /// <summary>Promotion→first-draw delay of the most recently drawn hull.</summary>
    public double LastFirstDrawMs { get; private set; }

    /// <summary>Worst promotion→first-draw delay since the last reset.</summary>
    public double MaxFirstDrawMs { get; private set; }

    /// <summary>True once the hull's mesh has been decoded and uploaded; until
    /// then the caller draws the stand-in.</summary>
    public bool Uploaded(short hullId) =>
        _entries.TryGetValue(hullId, out var entry) && entry.Phase == Phase.Uploaded;

    /// <summary>Record one roster ship each frame: the first frame its verified
    /// bytes are resident it is queued exactly once. A hull not yet resident is
    /// re-checked cheaply (dictionary miss) and never queued twice.</summary>
    public void Observe(short hullId, long nowMs)
    {
        if (_entries.ContainsKey(hullId)) return;
        if (!_bytesReady(hullId)) return;
        _entries[hullId] = new Entry { Phase = Phase.Queued, ReadyMs = nowMs };
        _pending.Add(hullId);
    }

    /// <summary>Spend this frame's upload budget on queued hulls, oldest first.
    /// Returns the number of hulls decoded/uploaded this frame.</summary>
    public int Step(long nowMs)
    {
        int completed = 0;
        while (completed < PerFrameBudget && _cursor < _pending.Count)
        {
            short hullId = _pending[_cursor++];
            long started = Stopwatch.GetTimestamp();
            bool readable = _upload(hullId);
            LastUploadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (LastUploadMs > MaxUploadMs) MaxUploadMs = LastUploadMs;
            var entry = _entries[hullId];
            // An unreadable model still leaves the queue: the ship draws its
            // stand-in forever instead of retrying a decode every frame.
            entry.Phase = Phase.Uploaded;
            entry.ReadyMs = readable ? entry.ReadyMs : nowMs;
            _entries[hullId] = entry;
            completed++;
        }
        if (_cursor == _pending.Count)
        {
            _pending.Clear();
            _cursor = 0;
        }
        return completed;
    }

    /// <summary>Record the first frame a hull's uploaded mesh actually drew and
    /// return the promotion→first-draw delay in milliseconds (0 when this frame
    /// is not that hull's first drawn mesh).</summary>
    public double NoteDrawn(short hullId, long nowMs)
    {
        if (!_entries.TryGetValue(hullId, out var entry) || entry.Drawn || entry.Phase != Phase.Uploaded) return 0;
        entry.Drawn = true;
        LastFirstDrawMs = nowMs - entry.ReadyMs;
        if (LastFirstDrawMs > MaxFirstDrawMs) MaxFirstDrawMs = LastFirstDrawMs;
        _entries[hullId] = entry;
        return LastFirstDrawMs;
    }

    /// <summary>Mission/session/view change: every hull is re-queued only if its
    /// bytes are still resident in the new view.</summary>
    public void Reset()
    {
        _entries.Clear();
        _pending.Clear();
        _cursor = 0;
        LastUploadMs = MaxUploadMs = LastFirstDrawMs = MaxFirstDrawMs = 0;
    }
}
