using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    // Native clintlib/appmsg.cpp:3353-3373 replaces the catalog, including empty.
    public IReadOnlyList<StaticMapRecord> StaticMaps { get; private set; } = Array.Empty<StaticMapRecord>();
    private void Apply(StaticMapInfo info) => StaticMaps = Array.AsReadOnly(info.Maps.ToArray());

    /// <summary>Use a server-advertised file/team selection for game provisioning.</summary>
    public bool SelectStaticMap(MissionParamsMsg parameters, string fileName)
    {
        if (!StaticMaps.Any(m => m.FileName == fileName && m.Teams == parameters.NTems)) return false;
        parameters.SzCustomMapFile = fileName;
        return true;
    }
}
