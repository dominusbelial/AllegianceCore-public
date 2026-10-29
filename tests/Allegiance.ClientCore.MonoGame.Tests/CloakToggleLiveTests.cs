using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Flight;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P413 — LIVE proof that the new client cloak-toggle key is wired end-to-end
/// through the REAL client control path, not just the pure translator. A real
/// <see cref="ConnectionSession"/> (the pilot) sends <c>CommandControls</c> with
/// the stateM produced by <see cref="FlightInput.Translate"/> for the Cloak flag
/// — the exact path the F key's frame calls (<c>Game1._session.CommandControls(
/// cmd.Controls, cmd.StateM)</c>) — over a real in-process server to a real ship.
///
/// Geometry and cloak replicate <see cref="CloakVisibilityGateLiveTests"/>
/// (faohshcar scanner ↔ faohshcar cloaked target, 2000 u apart: uncloaked
/// detection radius ~5700, fully-cloaked collapsed ~332) so the enemy peer sees
/// the ship while uncloaked and must lose all telemetry for it while cloaked.
///
/// Sequence exercised:
///  (a) F toggled ON  → server-side <see cref="Ship.IsCloaked"/> becomes true;
///  (b) an enemy peer with no prior detection stops receiving HEAVY telemetry
///      for the cloaked ship;
///  (c) F toggled OFF → the cloak deactivates and telemetry to that enemy
///      resumes.
/// </summary>
[Collection("LiveNetwork")]
public sealed class CloakToggleLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    /// <summary>Same near-zero-draw cloak as <see cref="CloakVisibilityGateLiveTests"/>
    /// so a real hull's energy pool never throttles it (isolating the toggle wiring
    /// from the separate energy-throttle behaviour).</summary>
    private static readonly DataPartTypeIGC LowDrawCloak = new(
        Price: 9810, TimeToBuild: 440, ModelName: "bcl1gncr.igc", IconName: "",
        Name: "Cloak Toggle Test Cloak", Description: "", GroupId: 0,
        TtbmRequired: [], TtbmEffects: [],
        Mass: 966f, Signature: 0.0f, PartId: 9821, SuccessorPartId: -1, PartMask: 983,
        InventoryLineMdl: "", Type: new DataPartTypeIGC.Cloak(
            EnergyConsumption: 0.005f, MaxCloaking: 0.994f, OnRate: 4.0f, OffRate: 4.0f,
            EngageSound: -1, DisengageSound: -1));

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "Cloak Toggle Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 414u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 94130000000000001ul, DrmHash = "p413",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name));
        return s;
    }

    private static Vector3 CleanBase(Sector sector, float clearance = 8000f)
    {
        var content = new List<Vector3>();
        foreach (var a in sector.Asteroids)
            if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
        foreach (var st in sector.Stations)
            if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
        float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
        float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
        float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
        return new Vector3(maxX + clearance, cy, cz);
    }

    private static bool BotSaw(FltBot b, short id) =>
        b.HeavyShipsUpdates.SelectMany(u => u.HeavyShipEntries).Any(e => e.ShipId == id);

    /// <summary>The pilot's <c>Cloak</c> flag folded through the REAL translator —
    /// the exact control state the F-key handler ships each frame.</summary>
    private static FlightCommand PilotCloak(bool engaged) =>
        FlightInput.Translate(new FlightInputState(
            false, false, false, false, false, false, false, false, false, false,
            Cloak: engaged));

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CloakToggleKey_FoldsIntoClientControlState_ActivatesAndDeactivatesServerCloakWithTelemetryGate()
    {
        Require.Artwork();
        using var server = new Srv();
        ConnectionSession? pilot = null;
        FltBot? enemy = null;
        Mission? mission = null;
        Sector? sector = null;
        Vector3 basePos = default;
        int spawnIdx = 0;

        ShipSpawnerField.SetValue(server, (StaticCore core, Mission m, short id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var hull = core.HullTypes.Values.First(h => h.ModelName == "faohshcar");
            if (spawnIdx == 0) basePos = CleanBase(sector);
            short side = spawnIdx == 0 ? m.Sides[0].Id : m.Sides[1].Id;
            var pos = spawnIdx == 0 ? basePos : basePos + new Vector3(2000f, 0f, 0f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, sector);
            spawnIdx++;
            return (ship, sector);
        });

        server.Boot(Config());
        var port = server.BoundPort;
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        try
        {
            pilot = Connect(server, "clk_pilot");
            enemy = new FltBot(); enemy.Connect(port, "clk_enemy"); enemy.SendLogonRequest(Request("clk_enemy"));

            int seated = PumpUntil(server, () => pilot.World.MyShipId is { } && enemy.ShipAssignment is not null, 4000);
            Assert.True(seated < 4000, "both peers seated");

            Ship cloaker = mission!.Sectors.SelectMany(s => s.Models.OfType<Ship>())
                                            .First(sh => sh.Id == pilot.World.MyShipId!.Value);
            Ship enemyShip = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>())
                                            .First(sh => sh.Id == enemy.ShipAssignment!.Ship.ShipId);
            short enemySide = enemyShip.SideId!.Value;
            Assert.NotEqual(cloaker.SideId, enemyShip.SideId);
            Assert.NotNull(mission);
            Assert.NotNull(sector);

            // Mount the cloak on the PILOT's ship (identified by its own World id,
            // not by spawn order) and seed full energy. ChangeHull would not reset
            // a drained pool (Ship.cs:86-89) and an exhausted pool throttles the
            // cloak (the P269 r² gate) — so give it a real pool like the fight-hull
            // live harness does.
            cloaker.MountPart(LowDrawCloak, 1, mountId: 0, fractionMounted: 255);
            cloaker.Energy = cloaker.HullType.MaxEnergy * 100f;

            // 0) Baseline — an uncloaked in-range enemy reaches the enemy peer.
            PumpUntil(server, () => BotSaw(enemy, cloaker.Id), 4000);
            Assert.True(BotSaw(enemy, cloaker.Id), "baseline: uncloaked in-range enemy must be broadcast to the enemy peer");

            // (a)+(b) Toggle cloak ON via the REAL client control path. This is
            // exactly the stateM the F-key handler's frame sends through
            // Game1._session.CommandControls(cmd.Controls, cmd.StateM).
            var on = PilotCloak(engaged: true);
            Assert.NotEqual(0, on.StateM & ShipControlState.CloakActive); // control-bit wiring sanity
            pilot.CommandControls(on.Controls, on.StateM);

            PumpUntil(server, () => cloaker.IsCloaked && cloaker.Cloaking < 0.05f && !cloaker.SeenBySide(enemySide), 3000);
            Assert.True(cloaker.IsCloaked, "(a) the server-side ship becomes IsCloaked from the client F-key");
            Assert.True(cloaker.Cloaking < 0.05f, $"(a) cloak must reach near-full (Cloaking={cloaker.Cloaking:F3})");
            Assert.False(cloaker.SeenBySide(enemySide), "cloaked beyond collapse radius = undetectable to the enemy side");

            enemy.ResetHeavyShipsUpdates();
            PumpUntil(server, () => enemy.HeavyShipsUpdates.Count >= 10, 6000);
            Assert.False(BotSaw(enemy, cloaker.Id),
                "(b) the enemy peer without prior detection receives ZERO telemetry for the cloaked ship");

            // (c) Toggle cloak OFF via the same real path — telemetry resumes.
            var off = PilotCloak(engaged: false);
            Assert.Equal(0, off.StateM & ShipControlState.CloakActive);
            pilot.CommandControls(off.Controls, off.StateM);
            PumpUntil(server, () => !cloaker.IsCloaked, 3000);
            Assert.False(cloaker.IsCloaked, "(c) toggling the key again deactivates the cloak");

            enemy.ResetHeavyShipsUpdates();
            PumpUntil(server, () => BotSaw(enemy, cloaker.Id), 5000);
            Assert.True(BotSaw(enemy, cloaker.Id), "(c) telemetry to the enemy resumes after deactivation");

            Console.WriteLine($"P413-LIVE: F-key control path drove cloaker {cloaker.Id} side {cloaker.SideId}: " +
                $"IsCloaked true->false, enemy side {enemySide} saw it {true} -> 0 -> {true}");
        }
        finally
        {
            pilot?.Dispose();
            enemy?.Dispose();
        }
    }
}
