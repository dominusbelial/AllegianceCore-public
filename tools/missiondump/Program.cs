using System.Globalization;
using Allegiance.Core;
using Allegiance.Core.Model;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: missiondump <core.igc> [map.igc ...]");
    Console.Error.WriteLine("  Dispatches each file by its wrapper: a core prints the type");
    Console.Error.WriteLine("  universe + constant highlights; a map prints the assembled");
    Console.Error.WriteLine("  sector graph (warps with resolved destination sectors, stations,");
    Console.Error.WriteLine("  asteroids). A map is assembled against the first core in the");
    Console.Error.WriteLine("  argument list, so pass a core alongside your maps.");
    return 1;
}

// Pair every map against the first static core present in the argument list.
StaticCore? pairing = null;
foreach (var path in args)
{
    if (!File.Exists(path)) continue;
    try { if (IgcReader.ParseFile(path).Wrapper == IgcWrapper.VersionAndDataSize) { pairing = StaticCore.Load(IgcReader.ParseFile(path)); break; } }
    catch { /* a non-core earlier in the list is not a pairing candidate */ }
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
            try { DumpMap(file, path, pairing); }
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

static void DumpMap(IgcFile file, string path, StaticCore? core)
{
    var map = MissionMap.Load(file);
    Console.WriteLine($"{Path.GetFileName(path)}  MAP  clusters={map.Clusters.Count}  warps={map.Warps.Count}  " +
                      $"stations={map.Stations.Count}  asteroids={map.Asteroids.Count}" +
                      (core is null ? "  (no core in args: raw grouping only)" : ""));

    if (core is null)
    {
        // Fall back to the raw grouping when no core is present to assemble with.
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
        return;
    }

    // Assemble the live static graph. No sides are invented for a dump — the
    // caller supplies them via Mission.Assemble when real teams are known.
    var mission = Mission.Assemble(core, map, Array.Empty<SideSpec>());

    foreach (var sector in mission.Sectors)
    {
        var cl = sector.Cluster;
        Console.WriteLine($"  sector {sector.Id,4} '{sector.Name}'  seed={cl.StarSeed}  " +
                          $"home={cl.HomeSector}  active={cl.Active}  posters={cl.PosterName}  planet={cl.PlanetName}");

        foreach (var rw in sector.Warps)
        {
            var dest = rw.Destination is null
                ? $"??{rw.Warp.DestinationId}"
                : $"{rw.Warp.DestinationId}->'{destName(rw.Destination)}'";
            Console.WriteLine($"      warp    {rw.Warp.WarpId,4} '{warpName(rw.Warp.Name)}'  destSector={dest}  sig={rw.Warp.Signature:F1}");
        }
        foreach (var st in sector.Stations)
            Console.WriteLine($"      station {st.StationId,4} '{st.Name}'  type={st.StationTypeId}  side={st.SideId}");
        foreach (var a in sector.Asteroids)
            Console.WriteLine($"      asteroid {a.AsteroidId,4} '{a.Name}'  ore={a.Ore:F0}/{a.OreMax:F0}");
    }

    Console.WriteLine($"  graph: {mission.Sectors.Count} sectors, {mission.Warps.Count} warps, " +
                      $"{mission.Warps.Count - mission.DanglingWarps.Count} destinations resolved, " +
                      $"{mission.DanglingWarps.Count} dangling" +
                      $" (contents sum: {mission.Sectors.Sum(s => s.Warps.Count)}/" +
                      $"{mission.Sectors.Sum(s => s.Stations.Count)}/{mission.Sectors.Sum(s => s.Asteroids.Count)} " +
                      $"vs map {map.Warps.Count}/{map.Stations.Count}/{map.Asteroids.Count})");

    static string warpName(string raw) => raw.Length > 0 && (raw[0] == '*' || raw[0] == '+')
        ? raw[1..] : raw;
    static string destName(Sector s) => s.Name;
}
