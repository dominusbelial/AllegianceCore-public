using System.Reflection;
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
/// P314 — exception isolation around <c>Server.SimPass</c> (a single bad tick
/// must degrade gracefully, not kill the server) plus the upstream part-count
/// cap that closes the one provable crash path (a ship reaching 256 parts
/// trips <see cref="ExportShip.Encode"/>'s throw in the teammate-export half
/// of BroadcastShipStates).
///
/// Each <see cref="Srv.SimPass"/> stage now runs inside an exception barrier
/// (Step): a throw is logged, that stage is skipped for the tick, and the
/// server keeps simulating. Finding H2 of the glm-5.3 review described the
/// per-tick broadcast/drain/post-update section as having zero protection —
/// before P314 an escape (a bad Notable subscriber, an over-limit export, a
/// corrupt part) propagated to the top-level catch and TERMINATED the process.
///
/// These are the DISCRIMINATING tests:
///   • revert the MountPart cap        → mounting 256 parts succeeds →
///                                       ExportShip.Encode throws → cap test red;
///   • revert the Step barrier         → the teammate-export throw escapes
///                                       TickSim → the server dies → isolation red;
///   • revert Notify to a bare Invoke  → a throwing spectator subscriber
///                                       propagates → notable test red.
/// </summary>
[Collection("LiveNetwork")]
public class SimPassExceptionIsolationTests
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
            new Gm { Name = "SimPass Isolation", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 555u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7410000000000000000ul, DrmHash = "iso",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ── 1) The part-count cap (the closed crash trigger) ─────────────────────

    /// <summary>⭐ The wire's 1-byte nParts field caps a ship's equipped-part
    /// count at <c>byte.MaxValue</c> (255). MountPart — the single place a
    /// ship's part list grows — must refuse past that, so a ship can never reach
    /// 256 parts and trip <see cref="ExportShip.Encode"/>'s throw. A 255-part
    /// loadout must still encode cleanly.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MountPart_refuses_to_exceed_255_equipped_parts()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var anyPart = core.PartTypes.Values.First();
        var ship = new Ship(1, core.HullTypes.Values.OrderBy(h => h.HullId).First(),
            new Vector3(10f, 0f, 0f), Vector3.Zero, Orientation.Identity);

        for (int i = 0; i < 300; i++)
            ship.MountPart(anyPart, 1, 0, 255);

        Assert.Equal(255, ship.Parts.Count);   // hard ceiling, never 256+

        // The 256th mount is refused (null), not silently overflowing.
        Assert.Null(ship.MountPart(anyPart, 1, 0, 255));

        // A 255-part loadout still round-trips: Encode must not throw.
        var parts = ship.Parts.Select(p => new PartData(p.PartId, p.Amount, p.MountId)).ToArray();
        var exp = new ExportShip
        {
            ObjectType = ExportShip.OtShip,
            HullId = ship.HullType.HullId,
            ShipId = 1,
            SideId = 0,
            PilotType = ExportShip.PilotTypePlayer,
            Parts = parts,
        };
        var bytes = exp.Encode();
        Assert.Equal(ExportShip.FixedSize + 255 * PartData.Size, bytes.Length);
    }

    // ── 2) The SimPass stage barrier (graceful degradation) ──────────────────

    /// <summary>⭐ Live proof: reproduce the concretely-reachable crash — a
    /// teammate ship with more than 255 parts makes the teammate-export in
    /// BroadcastShipStates throw <c>InvalidOperationException</c> (the exact
    /// H2 finding). Reaching 256 real parts is impossible now (the MountPart
    /// cap), so we inject the equivalent poisoned state directly into the
    /// private <c>_parts</c> list — the legacy exploit state — and confirm the
    /// server logs the failure and KEEPS TICKING instead of exiting.
    /// Before P314 this escaped and killed the whole process.</summary>
    /// With the broadcast snapshot pipeline (default since the 2026-10-27 rig
    /// A/B) the poisoned record is encoded on the publisher thread, so the
    /// containment is the publisher's failure counter rather than the sim pass's.
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimPass_contains_a_throwing_export_stage_and_keeps_running(bool snapshot)
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? spawnSector = null;
        server.ShipSpawner = (core, mission, id) =>
        {
            // Both peers on the SAME side so the teammate-export path runs.
            var side = mission.Sides.First();
            spawnSector ??= mission.Sectors.First();
            var hull = core.HullTypes[side.Civilization.Lifepod];
            var ship = new Ship(id, hull, new Vector3(3000f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = side.Id };
            mission.PlaceShip(ship, spawnSector);
            return (ship, spawnSector);
        };
        var config = Config();
        config.Broadcast = new() { ImmutableSnapshot = snapshot, EncodeOnce = snapshot };
        server.Boot(config);
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        long Failures() => snapshot ? server.BroadcastPublishFailures : server.SimPassStageFailures;

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "ISO_A"); b.Connect(server.BoundPort, "ISO_B");
        a.SendLogonRequest(Request("ISO_A")); b.SendLogonRequest(Request("ISO_B"));
        PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);

        var ships = server.Ships.Select(l => l.Ship).ToList();
        Assert.Equal(2, ships.Count);
        short aId = a.ShipAssignment!.Ship.ShipId;
        var teammate = ships.First(s => s.Id != aId);          // the ship bot A exports

        // Inject the legacy exploit state: force the teammate to 256 mounted
        // parts by bypassing the MountPart cap (straight into private _parts).
        var partsField = typeof(Ship).GetField("_parts", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_parts field not found");
        var list = (List<MountedPart>)partsField.GetValue(teammate)!;
        var partType = teammate.Mission!.Core.PartTypes.Values.First();
        for (int i = list.Count; i <= byte.MaxValue; i++)       // top up to 256
            list.Add(new MountedPart { PartId = partType.PartId, Amount = 1, MountId = 0, FractionMounted = 255, Type = partType });
        Assert.Equal(byte.MaxValue + 1, list.Count);            // 256 > 255 → Encode throws

        // P317: BroadcastShipStates now gates the teammate loadout re-export on
        // `Ship.LoadoutVersion` (an integer compare) instead of re-snapshotting
        // and re-comparing every pair every tick. A real loadout change bumps the
        // version; the reflection injection above deliberately bypasses MountPart
        // (and so does not bump it). To keep exercising the throwing-export
        // containment the P314 barrier guarantees, mirror a real mutation by
        // invoking the private bump — otherwise the re-export is correctly
        // suppressed for this version-unchanged ship and nothing throws.
        typeof(Ship).GetMethod("BumpLoadout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(teammate, null);

        long tick0 = server.LoopIterations;
        long fail0 = Failures();

        // Pump until the poisoned export throws and is contained. Before P314
        // this ESCAPED TickSim and killed the process.
        int pumped = PumpUntil(server, () => Failures() > fail0, 200);
        Assert.True(pumped < 200, "the over-limit teammate export should have thrown and been contained, not died");

        // The server is STILL ALIVE and ticking after the contained exception.
        long tick1 = server.LoopIterations;
        Assert.True(tick1 > tick0, "server stopped ticking after the contained exception");

        // It keeps simulating normally on subsequent cycles (the poisoned
        // teammate is still there, so failures keep being logged — but the
        // server never dies).
        long fail1 = Failures();
        server.TickSim();
        server.TickSim();
        Assert.True(server.LoopIterations > tick1, "server did not continue ticking on the next cycle");
        Assert.True(Failures() >= fail1, "failure counter regressed unexpectedly");
    }

    // ── 3) The Notable spectator-subscriber policy ───────────────────────────

    /// <summary>⭐ A throwing <see cref="Srv.Notable"/> spectator subscriber (the
    /// vizfight event-feed shape) must be logged and skipped, never allowed to
    /// stall or kill the server. <see cref="Srv"/> routes every raise through
    /// the <c>Notify</c> barrier; a bare <c>Invoke</c> would propagate.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Throwing_notable_subscriber_is_contained_not_fatal()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        server.Notable += _ => throw new InvalidOperationException("bad spectator subscriber");

        // Drive a raise through the private Notify barrier — must not escape.
        var notify = typeof(Srv).GetMethod("Notify", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Notify method not found");
        notify.Invoke(server, new object[] { "spectator line" });

        // The server is untouched and keeps simulating.
        server.TickSim();
        Assert.True(server.LoopIterations >= 1);
    }
}
