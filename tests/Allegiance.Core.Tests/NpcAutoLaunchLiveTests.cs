using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P381 — the auto-launch pass: a docked NPC drone undocks on its own after the
/// native ~10&nbsp;s docked minimum (<c>IshipIGC::OkToLaunch</c>,
/// <c>shipIGC.h:1914-1915</c>), with zero commands sent to it, and — because the
/// rest of the loop already exists (P156) — then proceeds to find and mine a real
/// asteroid exactly like an NPC that starts undocked. This is the missing step
/// that made previously-idle docked miners "just sit there" instead of "just
/// working". Also proves the <see cref="Ship.IsNpc"/> gate: a real (non-NPC)
/// player ship docked with a stale <see cref="Ship.LastDockTime"/> (which would
/// launch it instantly were it not for the gate) never auto-launches.
///
/// Harness mirrors P147/P155/P156's live tests (booted in-process server + one
/// seated bot, no Windows VM), advanced with the server's own deterministic
/// <see cref="Srv.TickSim"/> (one 0.05&nbsp;s chunk each).
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcAutoLaunchLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    /// <summary>Mirrors native <c>OkToLaunch</c>'s <c>10.0f</c> minimum and the
    /// server's <c>NpcAutoLaunchCooldownSeconds</c> (Server.Docking.cs). Kept in
    /// sync manually; the two are asserted against each other here only via the
    /// "stays docked below the cooldown, launches at/above it" boundary.</summary>
    private const double Cooldown = 10.0;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P381 NPC Auto-Launch Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 994u, DwCookie = 0x994u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 98800000000000001ul, DrmHash = "p381auto",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static int PumpTo(Srv server, double targetSimNow, int max)
    {
        int n = 0;
        while (server.SimNow < targetSimNow && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // He3-capable rock (bit CapMineHe3, igc.h:761) — the same synthetic builder
    // MiningTests / MinerDecisionCycleLiveTests use, so ResolveMining treats it
    // as a real pool. XXV_simple has zero map asteroids, so this is the only one.
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: oreMax, OreMax: oreMax,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>⭐ A docked NPC miner stays docked through the ~10s cooldown,
    /// then auto-undocks on its own (zero commands) and proceeds to mine a real
    /// asteroid; a real non-NPC player ship docked beside it never auto-launches.</summary>
    [Fact]
    public void Docked_npc_miner_auto_undocks_after_cooldown_then_mines()
    {
        using var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0;
        Asteroid? rock = null;
        DataStationIGC station = default;
        var stationPos = new Vector3(446f, 608f, 451f);   // the NPC's sell/dock station position
        var rockPos = new Vector3(755f, 489f, 522f);      // far enough to force travel

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            // Friendly unload-capable station (real type with the SabmUnload bit)
            // on the NPC's own side so a dock there is legal.
            var unloadType = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);
            station = new DataStationIGC(
                Position: [stationPos.X, stationPos.Y, stationPos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: npcSide, StationId: 8881,
                StationTypeId: (short)unloadType.StationTypeId, BpHull: 1, BpShield: 1, Name: "DEPOT");
            m.PlaceStation(new Station(station, unloadType), sector);

            // Minable He3 rock — the only candidate ResolveMining/the NPC can see.
            rock = new Asteroid(MkAsteroid(7791, rockPos, oreMax: 999f, radius: 60, sector.Id));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb precise assertions
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180 pattern)
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p381_auto");
        bot.SendLogonRequest(Request("p381_auto"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission);
        Assert.NotNull(rock);

        // ── Player-unaffected control ─────────────────────────────────────────
        // A REAL (non-NPC) player ship, placed docked with a stale LastDockTime
        // (NegativeInfinity ⇒ elapsed is infinite ⇒ would launch on the very next
        // pass were it not for the IsNpc gate). It must stay docked forever.
        var playerHull = mission!.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var playerShip = new Ship(7901, playerHull, stationPos, Vector3.Zero, Orientation.Identity) { SideId = npcSide };
        mission.PlaceShip(playerShip, sector!);
        playerShip.StationId = station.StationId;
        playerShip.LastDockTime = double.NegativeInfinity;

        // ── Spawn the docked NPC miner ────────────────────────────────────────
        // SpawnNpcShip gives IsNpc=true + PilotTypeMiner. Latch it docked and stamp
        // LastDockTime at the current sim clock ("just docked"), exactly as the
        // real dock sites (Sector.cs / Server.Docking.cs / Server.Combat.cs) do —
        // however it got docked (spawn, return-to-sell, rescue, teleport) the pass
        // only cares that it IS docked.
        var npc = server.SpawnNpcShip(mission, sector!, npcSide, stationPos);
        Assert.Equal(Ship.PilotTypeMiner, npc.PilotType);
        npc.StationId = station.StationId;
        npc.LastDockTime = server.SimNow;
        double dockedAt = npc.LastDockTime;

        // ── Phase A: does nothing for the first ~10s (native minimum) ─────────
        PumpTo(server, dockedAt + Cooldown - 1.0, 600);      // 1s shy of the cooldown
        Assert.NotNull(npc.StationId);                       // still docked — no premature launch
        Assert.NotNull(playerShip.StationId);

        // ── Phase B: auto-undocks on its own at/after ~10s (zero commands) ─────
        Pump(server, () => npc.StationId is null, 400);      // ~1s more crosses the 10s boundary
        Assert.Null(npc.StationId);                          // the pass undocked it
        Assert.NotNull(playerShip.StationId);                // ...but never the real player ship

        // ── Phase C: the existing decision loop takes over → finds + mines ─────
        Pump(server, () => npc.MiningAsteroidTarget is not null, 600);
        Assert.NotNull(npc.MiningAsteroidTarget);
        Assert.Null(npc.PlanTarget);                         // mining XOR selling (mutual exclusion, P156)
        Pump(server, () => npc.Ore >= 1f, 20000);
        Assert.True(npc.Ore >= 1f, $"NPC should mine after auto-undock, got ore {npc.Ore}");
        Assert.NotNull(playerShip.StationId);                // the player ship still never launched

        // ── Phase D: full return-to-sell cycle via the REAL dock path ─────────
        // Fill the bag, then the NPC autonomously retargets the sell station,
        // docks via the real ResolveDockCollisions → ServerHost.DockWithStationEvent
        // path — which is what stamps LastDockTime at the Sector.cs:3018 funnel.
        // If that stamp were missing, LastDockTime would stay NegativeInfinity and
        // the very next auto-launch pass would undock it instantly — this phase is
        // the regression guard on the real normal-dock stamp.
        Pump(server, () => npc.Ore >= Ship.MineCapacity, 40000);
        Assert.True(npc.Ore >= Ship.MineCapacity - 0.5f, $"bag should be full for the real-dock leg, got {npc.Ore}");
        Pump(server, () => npc.StationId is not null, 20000);
        Assert.NotNull(npc.StationId);                       // flew to DEPOT and docked (real path)
        Assert.NotEqual(double.NegativeInfinity, npc.LastDockTime);   // the real dock stamped the clock

        // It must NOT relaunch instantly — the real dock stamp is what prevents it.
        PumpTo(server, npc.LastDockTime + Cooldown - 1.0, 600);
        Assert.NotNull(npc.StationId);                       // still docked 1s shy of the cooldown

        // ... then auto-relaunches on its own after ~10s and the loop continues.
        Pump(server, () => npc.StationId is null, 400);
        Assert.Null(npc.StationId);                          // relaunched after the real dock + cooldown
        Assert.NotNull(playerShip.StationId);                // the player ship STILL never launched
    }
}
