using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P374 — the live loopback proof that a real enemy ship ENTERING a sector
/// containing a friendly station triggers a real per-side sector-threat
/// broadcast to that side's client (and NOT to the enemy's). Construction
/// mirrors <see cref="ProjectileBroadcastTests"/> (2 bots, one per side, a real
/// booted server on <c>XXV_simple.igc</c>); the test then RELOCATES the enemy
/// ship into the friendly side's station sector via <c>Mission.PlaceShip</c> and
/// pumps real ticks until the friendly client's
/// <see cref="Wire.ClusterWarningUpdate"/> accumulator shows a
/// <see cref="ClusterWarnings"/> level above no-threat for that sector — while
/// the enemy side's client receives none. This exercises the whole server-side
/// trigger (<see cref="Allegiance.Server.Server.BroadcastSectorThreats"/> →
/// <c>ClusterWarnings.GetWarning</c>) over the real wire with real clients,
/// exactly the "an enemy ship entering a sector containing a friendly station"
/// scenario the brief names.
/// </summary>
[Collection("LiveNetwork")]
public class ClusterWarningLiveTests
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
            new Gm { Name = "Cluster Warning Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 851u, DwCookie = 0x851u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 85000000000000000ul, DrmHash = "cwltest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ An enemy fighter entering the friendly side's station sector
    /// must make the FRIENDLY client receive a real ClusterWarningUpdate(214)
    /// for that sector at a level above no-threat — while the enemy side's
    /// client receives nothing for it (per-side asymmetry).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_in_friendly_station_sector_warns_friendly_side_only()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = { "p374_friendly", "p374_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "both bots must be assigned a ship");

            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship);
            short friendlyId = bots[0].ShipAssignment!.Ship.ShipId;
            short enemyId = bots[1].ShipAssignment!.Ship.ShipId;
            Assert.NotNull(ships[friendlyId].SideId);
            Assert.NotNull(ships[enemyId].SideId);
            Assert.NotEqual(ships[friendlyId].SideId, ships[enemyId].SideId);   // 2 bots / 2 teams -> opposite sides

            short friendlySide = ships[friendlyId].SideId!.Value;
            var enemy = ships[enemyId];
            var mission = enemy.Mission ?? throw new InvalidOperationException("enemy must have a mission");

            // Locate a sector containing a FRIENDLY-side station — the threat target.
            Sector? target = null;
            foreach (var sec in mission.Sectors)
                foreach (var st in sec.StationRuntimes)
                    if (st.SideId == friendlySide) { target = sec; break; }
            Assert.NotNull(target);

            bots[0].ResetClusterWarningBroadcasts();   // clear any startup "secured"/no-threat priming
            bots[1].ResetClusterWarningBroadcasts();

            // Relocate the enemy ship into the friendly station's sector. This is
            // the trigger the brief names — an enemy ship entering a sector that
            // contains a friendly station.
            if (enemy.Sector is { } oldSec) oldSec.Remove(enemy);
            mission.PlaceShip(enemy, target!);

            // The FRIENDLY side's client must now receive a real
            // ClusterWarningUpdate(214) for that sector at a level above
            // no-threat — the real consequence of the enemy's entry.
            PumpUntil(server,
                () => bots[0].ClusterWarningBroadcasts.Any(m => m.SectorId == target!.Id && m.Warning > ClusterWarnings.NoThreat),
                4000);

            var got = bots[0].ClusterWarningBroadcasts
                .FirstOrDefault(m => m.SectorId == target!.Id && m.Warning > ClusterWarnings.NoThreat);
            Assert.NotNull(got);
            Assert.Equal(target!.Id, got.SectorId);
            Assert.True(got.Warning > ClusterWarnings.NoThreat,
                $"friendly side must see a > no-threat warning for the invaded station sector, got {got.Warning}");
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}
