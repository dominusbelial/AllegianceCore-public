using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

internal static class MapGenTestCore
{
    internal static StaticCore LoadRepresentative()
    {
        // Structural topology tests need one usable core. Surveying all 98
        // cores here made the randomly first test pay a full-corpus scan.
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        int usable = core.Civilizations.Values.Count(c => c.InitialStationTypeId != -1
            && core.StationTypes.ContainsKey(c.InitialStationTypeId));
        if (usable < 6) throw new InvalidDataException("map topology fixture requires six usable civilizations");
        return core;
    }
}
