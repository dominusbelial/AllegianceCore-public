using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

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
            new Gm { Name = "P382 Initial Miner", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], ShowMap = true },
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

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Normal_mode_spawns_one_docked_miner_per_side_at_real_home_and_auto_undocks()
    {
        Require.Artwork();
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

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_mode_spawns_docked_miners_at_synthetic_omega_plato_not_real_homes()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: true));
        var mission = server.Missions[0];
        // P864c tops up empty sectors. Keep this cross-sector mining fixture's
        // authored Alderan-only rock distribution before the first simulation tick.
        var authoredRockIds = mission.Map.Asteroids.Select(a => a.AsteroidId).ToHashSet();
        foreach (var arena in mission.Sectors)
            foreach (var generated in arena.Mineable.Where(a => !authoredRockIds.Contains(a.Data.AsteroidId)).ToArray())
                arena.RemoveAsteroid(generated);
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

    // ══ P387 ════════════════════════════════════════════════════════════════
    // The P387 regression: a freshly-undocked NPC miner's FIRST decision was
    // delayed by tens of minutes (observed ~26 min of silence this session)
    // whenever no player was connected. Root cause: a fresh mission is born
    // NotStarted (Mission.Update early-returns and freezes the whole sim on
    // NotStarted, Mission.cs:562-566), and the mission only leaves NotStarted
    // via TryAutoStart, which is called solely from RecomputeSideReady — i.e.
    // only when a player joins and readies (Server.Lobby.cs:324). P381's
    // RunNpcAutoLaunch is NOT stage-gated, so the docked miners still
    // auto-undock at ~10 s, but with the sim frozen they never tick and never
    // call DecideMiningOrSelling — no first decision until a peer connects and
    // starts the mission (which unblocks BOTH sides' ships ~1 min later, the
    // exact asymmetric-looking-but-actually-symmetric signature observed).
    //
    // Fix (P387): in debug mode the mission auto-starts at boot, so the
    // autonomous NPC-miner demo runs with no player at all. These two tests
    // pin the behavior:
    //   1. Debug_mode_auto_starts_at_boot_and_first_miner_decides_without_any_player —
    //      the MUST-FAIL discriminator: with debugMode ON and NO peer ever
    //      connecting, the mission auto-starts at boot and the undocked miner
    //      makes its first decision (acquires an Alderan mining target) and
    //      actually WARPS to Alderan, all unprompted. Red when the P387 fix is
    //      removed (mission stays NotStarted -> sim frozen -> no target -> no
    //      warp; the warp pump times out).
    //   2. Normal_mode_with_no_player_stays_notstarted_and_miner_never_decides —
    //      the CONTROL that production (debugMode off) is UNCHANGED: without a
    //      peer the mission legitimately stays NotStarted awaiting ready-up,
    //      and the miner therefore never decides. Guards against accidentally
    //      widening the auto-start beyond debug mode.
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_mode_auto_starts_at_boot_and_first_miner_decides_without_any_player()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: true));
        var mission = server.Missions[0];
        // P864c tops up empty sectors. Keep this cross-sector mining fixture's
        // authored Alderan-only rock distribution before the first simulation tick.
        var authoredRockIds = mission.Map.Asteroids.Select(a => a.AsteroidId).ToHashSet();
        foreach (var arena in mission.Sectors)
            foreach (var generated in arena.Mineable.Where(a => !authoredRockIds.Contains(a.Data.AsteroidId)).ToArray())
                arena.RemoveAsteroid(generated);
        // Native GotoPlan travels only along routes/targets the side has seen
        // (common.cpp:899-927 + igc.h:4909-4913). The P387 metric — undock → first
        // [warp] into Alderan with no player — presumes the route and field are
        // known the way a scouted game's is: bShowMap (igc.h:1123) reveals every
        // aleph to every side (missionigc.cpp:4056-4071, mapmakerigc.cpp:1002-1017)
        // and the mining field is marked seen as in P528b's scouted-field fixture.
        mission.SeedStartingVisibility();
        foreach (var sector in mission.Sectors)
            foreach (var rock in sector.Mineable)
                foreach (var side in mission.Sides) rock.SideVisibility.SetSideVisibility(side.Id, true);
        var miners = DockedInitialMiners(server);
        Assert.Equal(2, miners.Count);

        // THE regression: the mission auto-started at boot (P387), even though
        // no player ever connected. Before the fix this stayed NotStarted and
        // the whole sim was frozen.
        Assert.NotEqual(MissionStage.NotStarted, mission.Stage);

        // P381 auto-undock still fires (~10 s cooldown) regardless of stage.
        PumpUntil(server, () => miners.All(m => m.StationId is null), 1200);
        Assert.All(miners, m => Assert.Null(m.StationId));

        // First decision, unprompted: each undocked miner acquires an Alderan
        // mining target (the exact DecideMiningOrSelling gate that was frozen).
        int mt = PumpUntil(server, () => miners.All(m => m.MiningAsteroidTarget is not null), 6000);
        Assert.True(mt < 6000, $"debug-mode miners never acquired a target with no player (ticks {mt}) — the sim is frozen pre-start");
        foreach (var m in miners)
        {
            Assert.NotNull(m.MiningAsteroidTarget);
            Assert.Equal((short)1052, m.MiningAsteroidTarget!.Data.ClusterId);   // Alderan
            Console.WriteLine($"[P387-LIVE] debug mode, no player: side {m.SideId} miner targeted He3 asteroid {m.MiningAsteroidTarget.Data.AsteroidId} in Alderan(1052).");
        }

        // The task's literal metric — undock -> first [warp] — driven to ground:
        // both miners actually transit into Alderan (sector 1052) on their own.
        int wt = PumpUntil(server, () => miners.All(m => m.Sector!.Id == 1052), 24000);
        Assert.True(wt < 24000, $"debug-mode miners never warped to Alderan with no player (ticks {wt})");
        Console.WriteLine($"[P387-LIVE] debug mode, no player: both miners warped into Alderan(1052) on their own (first decision ~{wt * 50}ms of sim, no peer).");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Normal_mode_with_no_player_stays_notstarted_and_miner_never_decides()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: false));
        var mission = server.Missions[0];

        // Production is UNCHANGED: with no player, a non-debug mission stays
        // NotStarted awaiting ready-up — never auto-starts at boot.
        Assert.Equal(MissionStage.NotStarted, mission.Stage);

        // P381 still auto-undocks the docked miner after its ~10 s cooldown
        // (RunNpcAutoLaunch is NOT stage-gated), but with the sim frozen
        // pre-start the undocked miner never ticks, so it never decides — the
        // exact pre-P387 symptom, here in non-debug mode where the hold is
        // legitimate. This locks in that production still waits for players.
        var miners = DockedInitialMiners(server);
        Assert.Equal(2, miners.Count);
        PumpUntil(server, () => miners.All(m => m.StationId is null), 1200);
        Assert.All(miners, m => Assert.Null(m.StationId));        // undocked (P381)
        PumpUntil(server, () => false, 2000);                     // ample sim time (100 s)
        Assert.Equal(MissionStage.NotStarted, mission.Stage);     // still held
        Assert.All(miners, m =>
        {
            Assert.Null(m.MiningAsteroidTarget);                  // never decided
            Console.WriteLine($"[P387-CONTROL] non-debug, no player: side {m.SideId} miner undocked but never decided (mission NotStarted) — production ready-up hold intact.");
        });
    }
}
