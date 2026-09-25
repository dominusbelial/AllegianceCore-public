using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P382 — each side's initial miner drones are spawned docked at its home
/// station at mission start (the port of native's "Create miners for each side
/// in the sector with their home starbase" block, <c>fsmission.cpp:2196-2217</c>),
/// and — with P381 in place — auto-undock on their own after the ~10 s cooldown
/// and start mining, exactly the "see them working at start" behaviour Domingo
/// reported was missing.
///
/// Two live proofs against the real <c>m2xspiderw.igc</c> map (the exact map
/// this session plays), mirroring the <c>LiveP379DebugTopologyTests</c> harness
/// (in-process real Allegiance.Server, no peer needed to observe the spawn):
/// <list type="number">
/// <item><b>Normal mode</b> (<c>debugMode</c> unset/false): one docked miner
///      per side at the <b>real</b> home garrisons — side 0 → station 4328 in
///      Ganymede (1050), side 1 → station 4329 in Organia (1051) — that
///      auto-undock via P381 and begin mining unprompted.</item>
/// <item><b>Debug mode</b> (<c>liveSettings.debugMode=true</c>): the same one
///      docked miner per side, but at the <b>synthetic</b> Omega/Plato
///      garrisons P379 injects (ids 90001/90002, sectors 1053/1060) — never the
///      real Ganymede/Organia garrisons — that auto-undock and head into
///      Alderan (1052) to mine its real He3 asteroids.</item>
/// </list>
/// Both assert the P381 handoff (dock → auto-undock), the point of P382.
/// </summary>
[Collection("LiveNetwork")]
public sealed class InitialMinerSpawnLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Must match the server impl's 90000s debug-garrison ids.
    private static readonly short OmegaDebugStationId = unchecked((short)90001);
    private static readonly short PlatoDebugStationId = unchecked((short)90002);

    private static Cfg Config(bool debugMode) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = debugMode ? new Ls { DebugMode = true } : new Ls(),
        Games =
        [
            new Gm { Name = "P382 Initial Miner", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>The P382 initial miners: every IsNpc, PilotTypeMiner ship
    /// currently latched docked (<c>StationId != null</c>) in the mission.</summary>
    private static List<Ship> DockedInitialMiners(Srv server) =>
        server.Missions[0].Sectors
            .SelectMany(s => s.Models.OfType<Ship>())
            .Where(s => s.IsNpc && s.PilotType == Ship.PilotTypeMiner && s.StationId is not null)
            .ToList();

    [Fact]
    public void Normal_mode_spawns_one_docked_miner_per_side_at_real_home_and_auto_undocks()
    {
        using var server = new Srv();
        server.Boot(Config(debugMode: false));
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;   // P180: decision loops need a started mission

        // Exactly one initial miner per side, both real pilot-type Miner NPCs.
        var miners = DockedInitialMiners(server);
        Assert.Equal(2, miners.Count);
        Assert.All(miners, m => Assert.Equal(Ship.PilotTypeMiner, m.PilotType));

        var side0 = miners.Single(s => s.SideId == 0);
        var side1 = miners.Single(s => s.SideId == 1);

        // Docked at the REAL home garrisons (Ganymede 4328 / Organia 4329),
        // not any synthetic station, and resident in the real home sectors.
        Assert.Equal((short)4328, side0.StationId);
        Assert.Equal((short)1050, side0.Sector!.Id);   // Ganymede
        Assert.Equal((short)4329, side1.StationId);
        Assert.Equal((short)1051, side1.Sector!.Id);   // Organia
        Console.WriteLine($"[P382-LIVE] normal mode: side 0 miner at Ganymede(1050)#4328, side 1 at Organia(1051)#4329, both docked.");

        // P381 handoff: within the ~10 s auto-launch cooldown they undock on
        // their own (no commands, no peers).
        PumpUntil(server, () => miners.All(m => m.StationId is null), 1200);
        Assert.All(miners, m => Assert.Null(m.StationId));
        Console.WriteLine("[P382-LIVE] normal mode: both initial miners auto-undocked (P381) with no input.");
    }

    [Fact]
    public void Debug_mode_spawns_docked_miners_at_synthetic_omega_plato_not_real_homes()
    {
        using var server = new Srv();
        server.Boot(Config(debugMode: true));
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;

        var miners = DockedInitialMiners(server);
        Assert.Equal(2, miners.Count);

        var side0 = miners.Single(s => s.SideId == 0);
        var side1 = miners.Single(s => s.SideId == 1);

        // At the SYNTHETIC Omega/Plato garrisons (90001/90002), never the real
        // Ganymede/Organia garrisons (4328/4329).
        Assert.Equal(OmegaDebugStationId, side0.StationId);
        Assert.Equal((short)1053, side0.Sector!.Id);   // Omega
        Assert.Equal(PlatoDebugStationId, side1.StationId);
        Assert.Equal((short)1060, side1.Sector!.Id);   // Plato
        Assert.NotEqual((short)4328, side0.StationId);
        Assert.NotEqual((short)4329, side1.StationId);
        Console.WriteLine($"[P382-LIVE] debug mode: side 0 miner at Omega(1053)#{OmegaDebugStationId}, side 1 at Plato(1060)#{PlatoDebugStationId}, both docked.");

        // Auto-undock (P381), then head into Alderan to mine its real He3 rocks.
        PumpUntil(server, () => miners.All(m => m.StationId is null), 1200);
        Assert.All(miners, m => Assert.Null(m.StationId));

        int mt = PumpUntil(server, () => miners.All(m => m.MiningAsteroidTarget is not null), 6000);
        Assert.True(mt < 6000, $"miners should acquire a mining target after undocking (ticks {mt})");
        foreach (var m in miners)
        {
            Assert.NotNull(m.MiningAsteroidTarget);
            Assert.Equal((short)1052, m.MiningAsteroidTarget!.Data.ClusterId);   // Alderan
            Console.WriteLine($"[P382-LIVE] debug mode: side {m.SideId} miner targeted He3 asteroid {m.MiningAsteroidTarget.Data.AsteroidId} in Alderan(1052).");
        }
    }
}
