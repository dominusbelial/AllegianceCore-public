using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;

// Loopback fixture only: invoke the existing native station-construction path
// on the host's sim thread after a browser asks for a mid-session station.
internal static class LateStationProof
{
    internal static void Poll(Server server, string scratch)
    {
        string request = Path.Combine(scratch, "build-station.json");
        if (!File.Exists(request)) return;
        using var data = JsonDocument.Parse(File.ReadAllText(request));
        short sectorId = data.RootElement.GetProperty("sector").GetInt16();
        short typeId = data.RootElement.GetProperty("type").GetInt16();
        var mission = server.Missions[0];
        var sector = mission.Sectors.Single(s => s.Id == sectorId);
        var rock = sector.Mineable.First(r => r.ConstructedStation is null);
        var side = mission.Sides.Single(s => s.Id == 0);
        var host = typeof(Server).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        host.GetType().GetMethod("BuildStation")!.Invoke(host, [rock, side, mission.Core.StationTypes[typeId], server.SimNow,
            Enumerable.Repeat(true, mission.Sides.Count).ToArray()]);
        var station = rock.ConstructedStation ?? throw new InvalidOperationException("Fixture station was not built.");
        File.WriteAllText(Path.Combine(scratch, "built-station.json"), JsonSerializer.Serialize(new
        { id = station.Data.StationId, type = typeId, sector = sectorId, position = station.Data.Position }));
        File.Delete(request);
    }
}
