using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P165 — the idle <see cref="Ship.PilotTypeBuilder"/> default order (native
/// <c>PickDefaultOrder</c>'s <c>c_ptBuilder</c> case, <c>Igc/shipIGC.h:2388-2415</c>):
/// an idle Builder NPC picks the <b>least-targeted</b> reachable neutral
/// asteroid (<see cref="Ship.FindLeastTargetedAsteroidAnyCluster"/>, the exact
/// search P161's Layer case reuses) and — unlike a Layer, which merely travels —
/// actually <b>starts a build</b> on it via
/// <see cref="Sector.StartBuildingEffect"/>. This closes the loop Stage 1 (P163)
/// left open: the building mechanic previously fired only when a test called
/// <see cref="Sector.StartBuildingEffect"/> directly; now the Builder AI decides
/// on its own. These live loopback tests prove it end to end:
/// <list type="number">
/// <item><b>Autonomous full build</b> — a real Builder drone type from the core
/// (PilotType==6, PCore001's 'Constructor (Lrg)'/'Constructor (Sml)') with a
/// configured <see cref="Ship.BuilderStationType"/>, placed near a real neutral
/// asteroid, ticks the mission through the full 40 s build by itself: the
/// asteroid is gone from <see cref="Sector.Mineable"/>, a new
/// <see cref="Station"/> of the right type sits at the rock's position, and the
/// Builder ship is gone from the sector (the P163 self-sacrificing
/// transformation, now AI-triggered). Asserts <see cref="Ship.IsBuilding"/> is
/// <c>true</c> while in progress (the flag's own correctness check) and cleared
/// on completion.</item>
/// <item><b>No-configured-type stand-by</b> — a Builder with a null
/// <see cref="Ship.BuilderStationType"/> stands by: no build starts, the rock is
/// untouched — the no-target no-op every decision cycle uses.</item>
/// </list>
/// Harness mirrors P160/P161/P162 (booted in-process server + one seated bot, no
/// Windows VM); the mission advances with the server's own deterministic
/// <see cref="Srv.TickSim"/> (one 0.05 s chunk each).
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcBuilderDefaultOrderLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

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
            new Gm { Name = "P165 Builder Default Order Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 563u, DwCookie = 0x563u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 66300000000000007ul, DrmHash = "p165builder",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A synthetic rock the Builder builds at. Carries the given
    /// buildability capability mask (default <c>0x08</c>, the generic/buildable
    /// bit — what a Garrison/Outpost needs). Not He3-minable so the mining pass
    /// ignores it; the only things that care about it are the building-effect
    /// resolver and, since P280, the construction type gate. Identical record
    /// shape to P163's synthetic build rock; XXV_simple has zero map asteroids,
    /// so this is the only rock anywhere reachable.</summary>
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap = DataAsteroidIGC.AabmBuildable) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static bool PositionsMatch(float[] a, Vector3 b) =>
        a is { Length: 3 } &&
        Math.Abs(a[0] - b.X) < 0.01f && Math.Abs(a[1] - b.Y) < 0.01f && Math.Abs(a[2] - b.Z) < 0.01f;

    /// <summary>Boot the in-process server + seated bot and assemble the
    /// single-sector fixture: a real Builder drone type id from the core
    /// (PilotType==6, PCore001's 'Constructor (Lrg)'/'Constructor (Sml)' —
    /// looked up dynamically so the test never hard-codes a core-version id), a
    /// real station type to build, ONE neutral asteroid (the only rock anywhere
    /// reachable), and a parked peer player ship on the other side (bootstrap
    /// only). Disposes the server and the bot.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, short BuilderDroneId, Asteroid Rock, DataStationTypeIGC StationType, short NpcSide)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short builderDroneId = 0;
        Asteroid? rock = null;
        DataStationTypeIGC? stationType = null;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            // A REAL Builder drone type straight from the core (PilotType==6).
            var bld = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeBuilder);
            builderDroneId = bld.Key;

            // A real station type to construct (the honest Stage-3 input,
            // supplied directly to the Builder — no discovery).
            stationType = core.StationTypes.Values.First();

            // The single neutral asteroid the Builder will transform — of the
            // type its assigned station requires (AabmBuild), so P280's gate
            // accepts it.
            rock = new Asteroid(MkAsteroid(6111, new Vector3(610f, 428f, 461f), 60, sector.Id, stationType!.Value.AabmBuild));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peer, sector);
            return (peer, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        bot = new FltBot();
        var b = bot;                       // local copy — `bot` is an out param, unusable in the lambda below
        b.Connect(server.BoundPort, "p165_builder");
        b.SendLogonRequest(Request("p165_builder"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(rock); Assert.NotNull(stationType);
        Assert.True(builderDroneId != 0, "core must expose a Builder pilot-type drone");
        return (server!, mission!, sector!, builderDroneId, rock!, stationType!.Value, npcSide);
    }

    /// <summary>⭐ Primary leg: an idle real Builder drone NPC, given a
    /// configured <see cref="Ship.BuilderStationType"/>, autonomously picks the
    /// neutral asteroid, STARTS a real build, and the mission ticks the full
    /// 40 s construction through to the asteroid→station transformation with the
    /// Builder consumed — P163's mechanic now driven by the AI, not a direct
    /// test call. Asserts <see cref="Ship.IsBuilding"/> <c>true</c> in progress
    /// and <c>false</c> after completion.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Idle_builder_autonomously_builds_station_from_neutral_asteroid()
    {
        Require.Artwork();
        var (srv, mission, sector, builderDroneId, rock, stationType, npcSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            Vector3 rp = RockPos(rock);

            // Spawn far from the rock: the port commits to the build immediately
            // on target selection (no travel-before-build gate), so distance is
            // immaterial — and documents that simplification in the test.
            var spawnPos = new Vector3(238f, 549f, 447f);
            Assert.True((spawnPos - rp).Length() > 200f, "builder must spawn well beyond arrival range of the rock");
            var builder = srv.SpawnNpcShip(mission, sector, npcSide, spawnPos, builderDroneId);
            Assert.Equal(Ship.PilotTypeBuilder, builder.PilotType);   // gate premise: really a Builder
            Assert.False(builder.IsBuilding);                          // flag starts clear

            // Configure what this Builder constructs — the honest Stage-3 input,
            // set directly (no drone-type table discovery in this port).
            builder.BuilderStationType = stationType;

            // ── Phase 1: the idle Builder decides on its own and STARTS the build.
            Pump(srv, () => sector.BuildingEffects.Count > 0, 700);
            var effect = Assert.Single(sector.BuildingEffects);
            Assert.Same(rock, effect.Target);                 // picked the least-targeted neutral rock
            Assert.Equal(stationType, effect.StationType);    // builds its configured station type
            Assert.True(builder.IsBuilding);                  // ★ the flag's own check, while in progress
            Console.WriteLine($"P165-DECIDE: Builder {builder.Id} (drone {builderDroneId}) autonomously started build of '{stationType.Name}' (type {stationType.StationTypeId}) at asteroid {rock.Data.AsteroidId}");

            // ── Phase 2: partway through (well before the 40 s deadline) the
            //    build is still in progress — rock untouched, builder alive and
            //    still flagged building.
            Pump(srv, () => mission.LastUpdate > effect.CompletionTime - 25.0, 900);   // ≈15 s in (of 40)
            Assert.Contains(rock, sector.Mineable);          // rock untouched yet
            Assert.Contains(builder, sector.Models);         // builder alive & in sector
            Assert.Contains(effect, sector.BuildingEffects); // still pending
            Assert.True(builder.IsBuilding);                 // flag stays set through the build

            // ── Phase 3: run to completion (~40 s of sim time): the asteroid →
            //    station transformation + builder self-sacrifice.
            int tc = 0;
            const int MaxTicks = 4500;
            while (tc < MaxTicks && sector.BuildingEffects.Count > 0) { srv.TickSim(); tc++; Thread.Sleep(1); }
            Assert.True(tc < MaxTicks, $"build must complete within {MaxTicks} ticks (elapsed {tc})");

            Assert.DoesNotContain(rock, sector.Mineable);        // the rock became a station
            Assert.DoesNotContain(builder, sector.Models);       // the builder consumed itself
            Assert.Empty(sector.BuildingEffects);
            Assert.False(builder.IsBuilding);                    // cleared on the completion path

            var built = sector.StationRuntimes.Where(s => PositionsMatch(s.Data.Position, rp)).ToList();
            var builtStation = Assert.Single(built);
            Assert.Equal(stationType.StationTypeId, builtStation.Type.StationTypeId);
            Assert.Equal(stationType.StationTypeId, builtStation.Data.StationTypeId);
            Assert.Equal(npcSide, builtStation.SideId);          // the Builder's side
            Assert.Equal(stationType.Name, builtStation.Data.Name);
            Assert.Contains(builtStation.Data, sector.Stations); // static record registered too
            Console.WriteLine($"P165-BUILD: completed at +40s → rock {rock.Data.AsteroidId} gone, station {builtStation.Data.StationId} '{builtStation.Data.Name}' (type {stationType.StationTypeId}, side {npcSide}) at {rp}, builder {builder.Id} consumed");
        }
    }

    /// <summary>⭐ Stand-by leg (negative): a Builder with no configured
    /// <see cref="Ship.BuilderStationType"/> has nothing to build and stands by
    /// — no effect registers, the rock is untouched, the builder stays in the
    /// sector — the no-target no-op every other decision cycle uses.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Idle_builder_without_configured_station_type_stands_by()
    {
        Require.Artwork();
        var (srv, mission, sector, builderDroneId, rock, _, npcSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var spawnPos = new Vector3(261f, 555f, 439f);
            var builder = srv.SpawnNpcShip(mission, sector, npcSide, spawnPos, builderDroneId);
            Assert.Equal(Ship.PilotTypeBuilder, builder.PilotType);
            Assert.Null(builder.BuilderStationType);   // deliberately NOT configured

            Pump(srv, () => sector.BuildingEffects.Count > 0, 1000);

            Assert.False(builder.IsBuilding);          // never committed
            Assert.Empty(sector.BuildingEffects);      // no build started
            Assert.Contains(rock, sector.Mineable);    // rock untouched
            Assert.Contains(builder, sector.Models);   // builder still in the sector, unharmed
            Console.WriteLine($"P165-STANDBY: Builder {builder.Id} with no BuilderStationType stood by — no build, rock {rock.Data.AsteroidId} untouched");
        }
    }

    /// <summary>⭐ P280 — the techbase→asteroid-type construction gate, negative
    /// leg: a Builder whose assigned station type needs one asteroid type (e.g.
    /// a buildable rock) but whose only reachable rock is a <b>different</b>
    /// type refuses to build — no effect registers, the rock is untouched, the
    /// builder stands by. The AI-level expression of native rejecting the build
    /// (<c>FindTarget(m_abmOrders)</c>, shipIGC.h:2393-2397; c_cidBuild
    /// legality, shipIGC.h:1695-1707).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Idle_builder_refuses_asteroid_of_wrong_type()
    {
        Require.Artwork();
        var (srv, mission, sector, builderDroneId, rock, stationType, npcSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            // Replace the compatible rock with a WRONG-type one — a capability
            // mask sharing no bit with the station's AabmBuild, so the gate
            // must refuse it. (Outpost needs 0x08 buildable; 0x40 carbon is the
            // wrong type, and vice versa.)
            short wrongCap = stationType.AabmBuild switch
            {
                DataAsteroidIGC.AabmBuildable => (short)(DataAsteroidIGC.AabmSpecial << 2),  // carbon 0x40
                (short)(DataAsteroidIGC.AabmSpecial << 2) => DataAsteroidIGC.AabmBuildable,
                (short)(DataAsteroidIGC.AabmSpecial << 0) => (short)(DataAsteroidIGC.AabmSpecial << 1),
                (short)(DataAsteroidIGC.AabmSpecial << 1) => (short)(DataAsteroidIGC.AabmSpecial << 0),
                _ => DataAsteroidIGC.AabmBuildable,
            };
            Assert.Equal(0, stationType.AabmBuild & wrongCap);   // provably disjoint
            var wrongRock = new Asteroid(MkAsteroid(rock.Data.AsteroidId, RockPos(rock), rock.Data.Radius, sector.Id, wrongCap));
            Assert.True(sector.RemoveAsteroid(rock));
            mission.PlaceAsteroid(wrongRock, sector);
            Assert.Contains(wrongRock, sector.Mineable);

            var spawnPos = new Vector3(261f, 551f, 440f);
            var builder = srv.SpawnNpcShip(mission, sector, npcSide, spawnPos, builderDroneId);
            Assert.Equal(Ship.PilotTypeBuilder, builder.PilotType);
            builder.BuilderStationType = stationType;

            Pump(srv, () => sector.BuildingEffects.Count > 0, 1000);

            Assert.False(builder.IsBuilding);          // never committed to a wrong-type rock
            Assert.Empty(sector.BuildingEffects);      // no build started
            Assert.Contains(wrongRock, sector.Mineable); // wrong-type rock untouched
            Assert.Contains(builder, sector.Models);   // builder still in the sector, stood by
            Console.WriteLine($"P280-REJECT: Builder {builder.Id} refused to build '{stationType.Name}' (AabmBuild 0x{stationType.AabmBuild:x}) at wrong-type rock {wrongRock.Data.AsteroidId} (cap 0x{wrongRock.Data.AabmCapabilities:x}) — stood by");
        }
    }

    private static Vector3 RockPos(Asteroid rock) =>
        new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);
}
