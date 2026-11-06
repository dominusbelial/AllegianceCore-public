using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Sector
{
    // Igc/clusterIGC.cpp:500-518,539-551: a separate, mixed list appends
    // pickables on cluster entry (m_models itself inserts at the FRONT).
    // Keep identities, not snapshots: moving treasure/missile geometry is live.
    private readonly List<IModelIgc> _pickableModels = [];
    public IReadOnlyList<IModelIgc> PickableModels => _pickableModels;

    internal void AddPickable(IModelIgc model) => _pickableModels.Add(model);
    internal void RemovePickable(IModelIgc model) => _pickableModels.Remove(model);
    internal void ClearPickables() => _pickableModels.Clear();

    internal void RestoreImportedPickableOrder(
        Dictionary<(IgcObjectType Type, short Cluster, short Id), Queue<int>> order)
    {
        // Import creates objects in record order (Igc/missionigc.cpp:3269-3294).
        // Assembly resolves static types separately; restore their mixed order
        // before any live insertion, without changing the other sector lists.
        // Anything else (none today) keeps its relative order after the map models.
        var sorted = _pickableModels.OrderBy(model =>
        {
            (IgcObjectType, short, short)? key = model switch
            {
                Station s => (IgcObjectType.Station, Id, s.StationId),
                Asteroid a => (IgcObjectType.Asteroid, Id, a.Data.AsteroidId),
                ResolvedWarp w => (IgcObjectType.Warp, Id, w.Warp.WarpId),
                _ => null
            };
            return key is { } k && order.TryGetValue(k, out var q) && q.Count > 0 ? q.Dequeue() : int.MaxValue;
        }).ToArray();
        _pickableModels.Clear();
        _pickableModels.AddRange(sorted);
    }
}
