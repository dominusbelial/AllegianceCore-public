using System.Globalization;
using Allegiance.Core;
using Allegiance.Core.Model;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: missiondump <core.igc> [map.igc ...]");
    Console.Error.WriteLine("  Dispatches each file by its wrapper: a core prints the type");
    Console.Error.WriteLine("  universe + constant highlights; a map prints clusters with the");
    Console.Error.WriteLine("  warps/stations/asteroids in each.");
    return 1;
}

var bad = 0;
foreach (var path in args)
{
    if (!File.Exists(path)) { Console.Error.WriteLine($"missing: {path}"); bad++; continue; }

    IgcFile file;
    try { file = IgcReader.ParseFile(path); }
    catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); bad++; continue; }

    switch (file.Wrapper)
    {
        case IgcWrapper.VersionAndDataSize:
            try { DumpCore(file, path); }
            catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); bad++; }
            break;
        case IgcWrapper.DataSize:
            try { DumpMap(file, path); }
            catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); bad++; }
            break;
    }
}
return bad == 0 ? 0 : 1;

static void DumpCore(IgcFile file, string path)
{
    var core = StaticCore.Load(file);
    var stamp = DateTimeOffset.FromUnixTimeSeconds(core.Version).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    Console.WriteLine($"{Path.GetFileName(path)}  STATIC CORE  version={core.Version} ({stamp})");
    void Row(string label, int n) => Console.WriteLine($"    {label,-14} {n,6}");
    Row("projectiles", core.ProjectileTypes.Count);
    Row("missiles", core.MissileTypes.Count);
    Row("mines", core.MineTypes.Count);
    Row("probes", core.ProbeTypes.Count);
    Row("chaff", core.ChaffTypes.Count);
    Row("civilizations", core.Civilizations.Count);
    Row("hull types", core.HullTypes.Count);
    Row("parts", core.PartTypes.Count);
    Row("launchers", core.Launchers.Count);
    Row("station types", core.StationTypes.Count);
    Row("developments", core.Developments.Count);
    Row("drone types", core.DroneTypes.Count);
    Row("treasure sets", core.TreasureSets.Count);

    var fc = core.Constants.FloatConstants;
    const string money = "#,##0.##";
    // Named float constants (igc.h:59-99): index -> display name.
    (int, string)[] notable =
    [
        (11, "StartingMoney"), (12, "WinTheGameMoney"), (6, "SecondsBetweenPaydays"),
        (7, "CapacityHe3"), (8, "ValueHe3"), (9, "AmountHe3"), (13, "RipcordTime"),
        (30, "Income"), (31, "LifepodEndurance"),
        (33, "LifepodCost"), (34, "TurretCost"), (35, "DroneCost"), (36, "PlayerCost"),
    ];
    Console.WriteLine("  Constants highlights:");
    foreach (var (idx, name) in notable)
        Console.WriteLine($"    constants[{name}] = {fc[idx].ToString(money, CultureInfo.InvariantCulture)}");
}

static void DumpMap(IgcFile file, string path)
{
    var map = MissionMap.Load(file);
    Console.WriteLine($"{Path.GetFileName(path)}  MAP  clusters={map.Clusters.Count}  warps={map.Warps.Count}  " +
                      $"stations={map.Stations.Count}  asteroids={map.Asteroids.Count}");

    foreach (var cl in map.Clusters)
    {
        Console.WriteLine($"  cluster {cl.ClusterId,4} '{cl.Name}'  seed={cl.StarSeed}  " +
                          $"home={cl.HomeSector}  active={cl.Active}  posters={cl.PosterName}  planet={cl.PlanetName}");
        foreach (var st in map.Stations.Where(s => s.ClusterId == cl.ClusterId))
            Console.WriteLine($"      station {st.StationId,4} '{st.Name}'  type={st.StationTypeId}  side={st.SideId}");
        foreach (var w in map.Warps.Where(w => w.ClusterId == cl.ClusterId))
            Console.WriteLine($"      warp    {w.WarpId,4} '{w.Name}'  dest={w.DestinationId}  sig={w.Signature:F1}");
        foreach (var a in map.Asteroids.Where(x => x.ClusterId == cl.ClusterId))
            Console.WriteLine($"      asteroid {a.AsteroidId,4} '{a.Name}'  ore={a.Ore:F0}/{a.OreMax:F0}");
    }
}
