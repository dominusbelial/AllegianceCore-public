using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class ZZ_Probe550
{
    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        return StaticCore.Load(IgcReader.ParseFile(path));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hulls()
    {
        Require.Artwork();
        var core = Core();
        foreach (var hid in new short[] { 792, 504, 1 })
        {
            if (!core.HullTypes.TryGetValue(hid, out var h)) { Console.WriteLine($"hull {hid} not in core"); continue; }
            Console.WriteLine($"hull {hid} '{h.Name}' Model='{h.ModelName}' HabmFighter={h.HasCapability(DataHullTypeIGC.HabmFighter)} HabmCarrier={h.HasCapability(DataHullTypeIGC.HabmCarrier)} HabmLifepod={h.HasCapability(DataHullTypeIGC.HabmLifepod)} HabmBoard={h.HasCapability(DataHullTypeIGC.HabmBoard)} habm=0x{h.HabmCapabilities:X}");
        }
        // list a few CapLand station types
        Console.WriteLine("CapLand station types:");
        foreach (var t in core.StationTypes.Values)
            if (t.HasCapability(DataStationTypeIGC.SabmCapLand))
                Console.WriteLine($"  type {t.StationTypeId} '{t.ModelName}' Land={t.HasCapability(DataStationTypeIGC.SabmLand)}");
    }
}
