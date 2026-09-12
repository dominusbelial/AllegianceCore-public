using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (same
// pattern as ShipStatusServerTests).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P41 loopback deliverable at the wire level, the second half of the
/// warp-transit slice (the geometry half is <see cref="WarpProximityTests"/>).
/// A real <see cref="Srv"/> boots a multi-sector map, a loopback
/// <see cref="FltBot"/> joins, and its ship is spawned just inside a warp's
/// collision volume with a velocity toward it. The first sector tick trips the
/// proximity pass (<c>Sector.ResolveWarpCollisions</c>), the host performs the
/// transit (<c>ServerHost.HitWarpEvent</c>, <c>fsship.cpp:71-137</c>), and we
/// prove: (a) the ship's server-side sector genuinely changes to the
/// destination, and (b) the peer receives a fresh <c>SET_CLUSTER(7)</c> and a
/// <c>SHIP_STATUS(90)</c> both reflecting the new sector. No Windows VM / native
/// client needed — the managed <see cref="FltBot"/> harness is sufficient,
/// matching P40's precedent that server-side proof and live-client proof are
/// separable (P42's job).
/// </summary>
[Collection("LiveNetwork")]
public class WarpTransitServerTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Warp Transit Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "warptransit",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>
    /// ⭐ A ship placed inside a warp's collision volume, given a velocity
    /// toward the warp centre, crosses: the server-side sector changes to the
    /// destination and the peer receives a fresh SET_CLUSTER(7) plus a
    /// SHIP_STATUS(90) bearing the new sector.
    /// </summary>
    [Fact]
    public void ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange()
    {
        using var server = new Srv();

        // A dedicated spawner so the bot's ship starts just inside the first
        // resolvable warp's collision volume, aimed at it. This is the only
        // controlled hook into placement (tests inject a spawner exactly for
        // this — Server.cs:88 ShipSpawner).
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

            // A unit approach direction — prefer the warp's own facing (the
            // axis a real ship flies in on), fall back to a cardinal.
            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var dir = new Vector3(f[0], f[1], f[2]).Normalize();

            float shipRadius = 0.5f * hull.Length;
            float radius = warp.Warp.Radius + shipRadius;
            // Just inside the volume (the native trigger fires on ANY overlap —
            // fsship.cpp:74 ff. — it does not require approaching from outside),
            // with a velocity pointing at the warp centre so the ship is
            // genuinely crossing. Transit teleports it out on the first tick.
            var spawn = wpos + dir * (0.9f * radius);
            var ship = new Ship(shipId, hull, spawn, -dir * 20f, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "warp_transit_probe");
        bot.SendLogonRequest(Request("warp_transit_probe"));

        // Join burst completes: initial SET_CLUSTER(sector A) + SHIP_STATUS(sector A).
        // (Timing-independent: the loop always runs the done() check first, so a
        // ship can transit on the very first TickSim — no > 0 guard on these.)
        PumpUntil(server, () => bot.Assignments.Count >= 1 && bot.ShipStatuses.Count >= 1, 4000);
        short initialSector = bot.Assignments[0].SectorId;
        short shipId = bot.Assignments[0].Ship.ShipId;
        Assert.True(bot.ShipStatuses.Any(s => s.SectorId == initialSector && s.ShipId == shipId),
            $"join SHIP_STATUS should reflect the initial sector {initialSector}");

        // Pump until the transit fires and the peer sees a second SET_CLUSTER
        // with a DIFFERENT sector. The debounce (native 2s fudge) prevents any
        // rapid oscillation from masking the first crossing.
        PumpUntil(server,
            () => bot.Assignments.Count >= 2 && bot.Assignments[^1].SectorId != initialSector,
            4000);

        Assert.True(bot.Assignments.Count >= 2,
            $"ship never crossed (got {bot.Assignments.Count} SET_CLUSTER, sectors=[{string.Join(",", bot.Assignments.Select(a => a.SectorId))}])");
        var second = bot.Assignments[^1];
        Assert.Equal(shipId, second.Ship.ShipId);          // same ship, new sector
        short newSector = second.SectorId;
        Assert.NotEqual(initialSector, newSector);

        // (a) server-side proof: the LiveShip the server tracks for this peer
        // and the Ship's own sector both agree on the new sector, and the ship
        // model has genuinely moved into the destination sector's model list.
        var live = server.Ships.First(l => l.Ship.Id == shipId);
        Assert.Equal(newSector, live.Sector.Id);
        Assert.Equal(newSector, live.Ship.Sector?.Id);
        var dest = live.Ship.Mission!.Sectors.First(s => s.Id == newSector);
        Assert.Contains(live.Ship, dest.Models);
        Assert.DoesNotContain(live.Ship, live.Ship.Mission!.Sectors.First(s => s.Id == initialSector).Models);

        // (b) wire proof: a SHIP_STATUS(90) arrived bearing the new sector.
        Assert.Contains(bot.ShipStatuses, s => s.ShipId == shipId && s.SectorId == newSector);
    }
}
