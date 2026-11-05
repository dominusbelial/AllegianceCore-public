using System.Buffers;
using SimModel = Allegiance.Core.Sim.Model;

namespace Allegiance.Core.Model;

public sealed partial class Sector
{
    private SimModel[] _modelSnapshot = [];
    private bool _modelSnapshotInUse;

    // Each pass captures CURRENT membership in insertion order. A callback can
    // remove/add models, or nest a resolver, without overwriting an outer walk.
    private ModelSnapshot CaptureModelSnapshot()
    {
        bool ownsBuffer = !_modelSnapshotInUse;
        int count = _models.Count;
        SimModel[] buffer;
        if (ownsBuffer)
        {
            if (_modelSnapshot.Length < count)
                Array.Resize(ref _modelSnapshot, Math.Max(count, _modelSnapshot.Length * 2));
            buffer = _modelSnapshot;
            _modelSnapshotInUse = true;
        }
        else buffer = ArrayPool<SimModel>.Shared.Rent(count);
        _models.CopyTo(buffer);
        return new(this, buffer, count, ownsBuffer);
    }

    private readonly ref struct ModelSnapshot(Sector owner, SimModel[] buffer, int count, bool ownsBuffer)
    {
        public ReadOnlySpan<SimModel> Models => buffer.AsSpan(0, count);
        public void Dispose()
        {
            // Peak capacity is retained, model lifetimes are not.
            Array.Clear(buffer, 0, count);
            if (ownsBuffer) owner._modelSnapshotInUse = false;
            else ArrayPool<SimModel>.Shared.Return(buffer);
        }
    }
}
