using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit.Abstractions;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P616 — regression guards for the five known NATIVE gameplay/server bugs
/// triaged from the FreeAllegiance tracker
/// (<c>https://github.com/kgersen/Allegiance/issues</c>: #18, #59, #42, #146,
/// #225). Each guard reproduces the native bug's *essence* against the C#
/// port and pins the port's (correct) behaviour so a future change that
/// re-introduces the native fault fails loudly. Full analysis (native cite →
/// port cite → had-the-bug → verdict) is in <c>docs/reports/P616_TRACKER_NATIVE_BUGS.md</c>;
/// this file is the executable half.
/// <list type="bullet">
/// <item><b>#18 fuel+aleph exploit</b> — native gave free fuel when the reload
///     sequence overlapped an aleph transit (timing-dependent, client-predicted).
///     The port reloads fuel synchronously &amp; server-authoritatively and the
///     warp transit never touches the fuel pool, so the exploit cannot exist.
///     Guards: every refill costs exactly one fuel pack; the aleph transit
///     changes neither Fuel nor cargo.</item>
/// <item><b>#59 treasuureset teardown crash</b> — native crashed in
///     <c>DeactivateSide</c>/mission teardown while "Nuking N treasureset(s)"
///     (a name-formatting buffer overflow). The port has no
///     <c>CTreasureSetIGC</c> object lifecycle: the old mission is discarded
///     wholesale and GC'd at rotation. Guards: teardown with live treasures
///     present never throws and leaves no leaked treasure behind.</item>
/// <item><b>#42 Allow-Empty-Team mission-destroy crash</b> — native crashed
///     intermittently destroying AET missions. The port rotates by index swap
///     (enumerator-safe) in managed code. Guard: repeated AET rotations never
///     throw and always rebuild + re-start.</item>
/// <item><b>#146 Commander wing retained</b> — native kept the previous game's
///     wing instead of defaulting the commander to Command wing. The port
///     mints a fresh <c>Ship</c> (WingId=0 = command, <c>c_pszWingName[0]</c>)
///     on every rotation, so the commander always returns to Command wing.
///     Guard: a leader set to Attack wing comes back Command (0) after a
///     rotation.</item>
/// <item><b>#225 lobby AFK toggle</b> — a double-click + mouse-move in the
///     native lobby chat unchecked the AFK box. The port has no equivalent
///     lobby UI element (its pre-flight is a keyboard ReadyCheck screen, no
///     AFK checkbox); analysed in the report, no code guard here.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public class P616TrackerNativeBugTests
{
    private readonly ITestOutputHelper _output;
    public P616TrackerNativeBugTests(ITestOutputHelper output) => _output = output;

    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(string name = "P616", bool autoRestart = false) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
                     SideNames = ["Alpha", "Bravo"], AutoRestart = autoRestart, RestartCountdownSeconds = autoRestart ? 2 : null },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 878u, DwCookie = 0x616u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8800000000000000000ul, DrmHash = "p616",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    /// <summary>Drive a real #draw → OfferDraw → AcceptDraw → game-over chain
    /// with a 4-bot setup (two on each side), exactly as P480/P598 do.</summary>
    private static void DrawToGameOver(Srv server, FltBot b0, FltBot b1, FltBot a0, FltBot a1)
    {
        b0.SendChat("#draw");
        int offer = PumpUntil(server,
            () => b1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship), 800);
        Assert.True(offer < 800, "Bravo teammate did not receive the offer-draw ballot");
        var offerBallot = b1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship);
        b1.SendVote(offerBallot.BallotId, true);

        int acc = PumpUntil(server,
            () => a0.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side)
               && a1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side), 800);
        Assert.True(acc < 800, "Alpha bots did not receive the accept-draw ballot");
        a0.SendVote(a0.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);
        a1.SendVote(a1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);

        int ended = PumpUntil(server, () => a0.GameOvers.Count >= 1, 800);
        Assert.True(ended < 800, "game-over did not fire after the accept-draw passed");
    }

    // ── #18 — fuel auto-reload: one pack per refill, never free ─────────────

    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<DataHullTypeIGC> PinnedHull = new(() =>
    {
        var core = RepresentativeCore.Value;
        var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        return baseHull with { Speed = 100f, Thrust = 1000f, MaxFuel = 1000f };
    });

    private static Ship NewShip()
    {
        var hull = PinnedHull.Value;
        return new Ship(shipId: 818, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
    }

    private static DataPartTypeIGC AfterburnerType(float fuelConsumption, float maxThrust, float onRate, float offRate) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ab", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 8818, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Afterburner(
            FuelConsumption: fuelConsumption, MaxThrust: maxThrust,
            OnRate: onRate, OffRate: offRate, InteriorSound: 0, ExteriorSound: 0));

    private static DataPartTypeIGC FuelPackType(short amount) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "fuel", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 8918, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 1, Amount: amount));   // c_packFuel (igc.h:550)

    private static int CountCargoPacks(Ship ship) => ship.CargoHold.Count(c => c is not null);

    /// <summary>⭐ #18 guard — native gave <b>free fuel</b>: run out and start
    /// the reload just as you hit an aleph ⇒ exit with <b>full fuel while no
    /// fuel tank is expended</b> (shipIGC.h:633-651 / WinTrek/trekigc.cpp warp,
    /// turkey-verified; "Fixed with [1046]" never held). Here the reload is
    /// synchronous and server-authoritative, so EVERY refill costs exactly one
    /// <c>c_packFuel</c> pack and fuel can never be full-without-expenditure.
    /// Drive each depletion via <see cref="Ship.SetFuel"/> (the same &gt;0→0
    /// zero-transition the afterburner's burn reaches), advancing sim time
    /// past the reload debounce (<c>m_timeReloadFuel</c>) between cycles, and
    /// assert exactly one pack is consumed per refill — no free fuel.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FuelAutoReload_EachRefillCostsExactlyOnePack_NeverFreeFuel()
    {
        Require.Artwork();
        var ship = NewShip();
        ship.MountPart(AfterburnerType(fuelConsumption: 2f, maxThrust: 10f, onRate: 1e6f, offRate: 1e6f), amount: 1, mountId: 0, fractionMounted: 255);
        // H-07/H-22: spare packs occupy native cargo slots, not positive equipment mounts.
        ship.MountPart(FuelPackType(amount: 1000), amount: 1000, mountId: 255, fractionMounted: 255);
        ship.MountPart(FuelPackType(amount: 1000), amount: 1000, mountId: 254, fractionMounted: 255);

        Assert.Equal(2, CountCargoPacks(ship));
        Assert.Equal(PinnedHull.Value.MaxFuel, ship.Fuel, 3);
        float max = PinnedHull.Value.MaxFuel;

        // cycle 1 → refill from pack1 (exactly one pack, fuel back to max)
        ship.SetFuel(0f);
        Assert.Equal(1, CountCargoPacks(ship));
        Assert.Equal(max, ship.Fuel, 3);

        // advance sim time past the reload debounce (m_timeReloadFuel) so the
        // next zero-transition may reload again
        double t = 0; const double dt = 0.5;
        for (int i = 0; i < 10; i++) { t += dt; ship.Update(t); }

        // cycle 2 → refill from pack2 (the last one)
        ship.SetFuel(0f);
        Assert.Equal(0, CountCargoPacks(ship));
        Assert.Equal(max, ship.Fuel, 3);

        for (int i = 0; i < 10; i++) { t += dt; ship.Update(t); }

        // cycle 3 → no pack left: Depleted, fuel stays 0 — no phantom fuel
        ship.SetFuel(0f);
        Assert.Equal(0f, ship.Fuel);
        Assert.Equal(AutoReloadOutcome.Depleted, ship.LastFuelAutoReloadOutcome);
        Assert.Equal(0, CountCargoPacks(ship));

        _output.WriteLine($"P616-18: 2 packs → exactly 2 refills, final fuel={ship.Fuel}, packs={CountCargoPacks(ship)} — no free fuel");
    }

    /// <summary>⭐ #18 guard (live) — an aleph transit must touch NEITHER the
    /// fuel pool NOR cargo. The native exploit lived in the reload sequence
    /// completing across the warp's hidden/animated window; here
    /// <c>ServerHost.HitWarpEvent</c> (fsship.cpp:71-137) is a single
    /// synchronous sector move that never calls <c>SetFuel</c>. A ship with a
    /// partial fuel load and one fuel pack in cargo crosses an aleph; both
    /// must be bit-for-bit unchanged.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AlephTransit_ChangesNeitherFuelNorCargo()
    {
        Require.Artwork();
        using var server = new Srv();

        float partial = 773f;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var dir = new Vector3(f[0], f[1], f[2]).Normalize();
            float shipRadius = 0.5f * hull.Length;
            float radius = warp.Warp.Radius + shipRadius;
            var spawn = wpos + dir * (0.9f * radius);
            var ship = new Ship(shipId, hull, spawn, -dir * 20f, Orientation.Identity);
            ship.SetFuel(partial);
            // One fuel pack riding in cargo — the warp must not consume it.
            var fp = new DataPartTypeIGC(Price: 0, TimeToBuild: 0, ModelName: "", IconName: "",
                Name: "fuel", Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
                Mass: 0f, Signature: 0f, PartId: 7918, SuccessorPartId: -1, PartMask: 0,
                InventoryLineMdl: "", Type: new DataPartTypeIGC.Pack(PackType: 1, Amount: 500));
            ship.MountPart(fp, amount: 500, mountId: 255, fractionMounted: 255);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config("P616Warp"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p616warp");
        bot.SendLogonRequest(Request("p616warp"));

        PumpUntil(server, () => bot.Assignments.Count >= 1 && bot.ShipStatuses.Count >= 1, 4000);
        short shipId = bot.Assignments[0].Ship.ShipId;
        short initialSector = bot.Assignments[0].SectorId;

        var live = server.Ships.First(l => l.Ship.Id == shipId);
        float fuelBefore = live.Ship.Fuel;
        int packsBefore = CountCargoPacks(live.Ship);

        PumpUntil(server,
            () => bot.Assignments.Count >= 2 && bot.Assignments[^1].SectorId != initialSector, 4000);
        Assert.True(bot.Assignments.Count >= 2, "ship never crossed an aleph");

        live = server.Ships.First(l => l.Ship.Id == shipId);
        _output.WriteLine($"P616-18warp: fuel {fuelBefore} -> {live.Ship.Fuel}, cargo packs {packsBefore} -> {CountCargoPacks(live.Ship)} after aleph transit");
        Assert.Equal(fuelBefore, live.Ship.Fuel, 3);                 // fuel untouched
        Assert.Equal(packsBefore, CountCargoPacks(live.Ship));       // pack not consumed
    }

    // ── #42 + #59 — AET rotation & treasure teardown never crash ────────────

    /// <summary>⭐ #42 guard — native crashed <em>intermittently</em> when
    /// Allow-Empty-Team missions were created and destroyed
    /// (trac #43; "Need dump..."). Here the destroy path is the enumerator-safe
    /// index-swap rotation (<c>RotateMissionAfterGameOver</c>, P439) in managed
    /// code. Toggle AET on and run three full game-over rotations; assert no
    /// exception escapes (each rotation that throws fails the test) and the
    /// mission always re-starts from the migrated peers' preserved ready.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AllowEmptyTeams_ThreeRotations_NeverThrow_AndAlwaysRestart()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config("P616AET"));

        server.Missions[0].AllowEmptyTeams = true;      // the "Allow Empty Team" game flag (igc.h misparms bAllowEmptyTeams)

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot(); using var a1 = new FltBot();
        using var b0 = new FltBot(); using var b1 = new FltBot();
        Seat(server, a0, "XA0"); Seat(server, a1, "XA1");
        Seat(server, b0, "XB0"); Seat(server, b1, "XB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0.ShipAssignment!.Ship.ShipId), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, b1.ShipAssignment!.Ship.ShipId), side1));
        var core = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };

        // Refresh the AET flag on every re-armed mission as we rotate.
        for (int rotation = 1; rotation <= 3; rotation++)
        {
            server.Missions[0].AllowEmptyTeams = true;

            foreach (var (b, _) in core)
                b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
            int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
            Assert.True(started < 2000, $"AET wave {rotation}: mission did not start");

            var oldMission = server.Missions[0];
            foreach (var (b, _) in core) { b.ResetGameOvers(); b.ResetBallotSignals(); }

            // The destroy step — if the port had #42's crash, this throws.
            DrawToGameOver(server, b0, b1, a0, a1);
            Assert.NotSame(oldMission, server.Missions[0]);

            // The rebuilt AET mission re-starts (ready-preserving auto-start, P598).
            int reStarted = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 3000);
            Assert.True(reStarted < 3000, $"AET rotation {rotation}: rebuilt mission did not reach Started");

            Assert.True(server.Ships.Count() >= 4, "lost a peer across AET rotations");
            Assert.True(server.Ships.Count() <= 5, "peers accumulated across AET rotations (leak)");
        }
    }

    /// <summary>⭐ #59 guard — native crashed while "removing treasuresets"
    /// during <c>DeactivateSide</c>/mission teardown (a name-formatting buffer
    /// overflow in the "Nuking N treasureset(s)" sweep). The port has no
    /// <c>CTreasureSetIGC</c> lifecycle — the discarded mission (with its live
    /// <c>Treasure</c> objects) is dropped wholesale and GC'd at rotation. Seed
    /// live treasure objects, drive a rotation, and assert (a) no exception,
    /// (b) the fresh mission inherits none of the old treasures (clean
    /// teardown), and (c) a direct <c>DeactivateSide</c> call with treasures
    /// present also completes without throwing.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TreasureTeardown_OnRotationAndDeactivateSide_NoCrash_NoLeak()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config("P616Treasure"));

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot(); using var a1 = new FltBot();
        using var b0 = new FltBot(); using var b1 = new FltBot();
        Seat(server, a0, "TA0"); Seat(server, a1, "TA1");
        Seat(server, b0, "TB0"); Seat(server, b1, "TB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0.ShipAssignment!.Ship.ShipId), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, b1.ShipAssignment!.Ship.ShipId), side1));
        var core = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };

        foreach (var (b, _) in core)
            b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, "treasure teardown: mission did not start");

        // Seed live treasure objects into every sector (the port's analogue of
        // native per-sector treasure sets being "nuked" at teardown).
        short oid = 8200;
        foreach (var sec in server.Missions[0].Sectors)
        {
            var data = new DataTreasureIGC(
                P0: [0f, 0f, 0f], V0: [0f, 0f, 0f], Lifespan: 30f, Time0: 0u,
                ObjectId: oid, TreasureId: 829, ClusterId: sec.Id, Amount: 668,
                TreasureCode: TreasureCode.Cash, CreateNow: true);
            sec.Add(new Treasure(oid++, sec, data, server.Missions[0].LastUpdate, server.Missions[0]));
        }
        int totalSeeded = server.Missions[0].Sectors.Sum(s => s.Models.OfType<Treasure>().Count());
        Assert.True(totalSeeded >= 1, "expected at least one seeded treasure");

        // The destroy step — if the port had #42/#59's crash, this throws.
        var oldMission = server.Missions[0];
        foreach (var (b, _) in core) { b.ResetGameOvers(); b.ResetBallotSignals(); }
        DrawToGameOver(server, b0, b1, a0, a1);
        Assert.NotSame(oldMission, server.Missions[0]);

        // Clean teardown: the fresh mission inherits NONE of the old treasures
        // (native "Nuking N treasureset(s)" at mission termination — here the
        // discarded mission is dropped wholesale, so nothing leaks).
        var fresh = server.Missions[0];
        Assert.DoesNotContain(fresh.Sectors, s => s.Models.OfType<Treasure>().Any(t => t.Data.TreasureId == 829));
        int freshTreasureCount = fresh.Sectors.Sum(s => s.Models.OfType<Treasure>().Count());
        Assert.Equal(0, freshTreasureCount);

        // (c) DIRECT DeactivateSide with live treasures present — the exact
        // native #59 crash site ("removing treasuresets during DeactivateSide")
        // — must not throw. The port's DeactivateSide (Server.Lobby.cs:255-324)
        // clears loadouts/kills ships and never iterates a treasure-set
        // container, so this completes. Exercised reflectively because the
        // method is private on the Server partial.
        foreach (var sec in fresh.Sectors)
        {
            var data = new DataTreasureIGC(
                P0: [0f, 0f, 0f], V0: [0f, 0f, 0f], Lifespan: 30f, Time0: 0u,
                ObjectId: oid, TreasureId: 839, ClusterId: sec.Id, Amount: 678,
                TreasureCode: TreasureCode.Cash, CreateNow: true);
            sec.Add(new Treasure(oid++, sec, data, fresh.LastUpdate, fresh));
        }
        var side = fresh.Sides[0];
        var mi = typeof(Srv).GetMethod("DeactivateSide", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(mi);
        try
        {
            mi.Invoke(server, new object[] { fresh, side });
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;   // surface the inner exception: #59's crash would fail here
        }
        Assert.False(side.Active);
        _output.WriteLine($"P616-59: seeded {totalSeeded} treasures → rotation clean, fresh mission has {freshTreasureCount}; DeactivateSide with treasures present returned without throwing");
    }

    // ── #146 — Commander defaults to Command wing across a rotation ─────────

    /// <summary>⭐ #146 guard — when launching as Commander the wing must
    /// default to Command wing regardless of a previous game's wing choice
    /// (trac #149; native retained the prior wing because the ship object
    /// survived and was not re-defaulted to Command). The port mints a FRESH
    /// <c>Ship</c> on every rotation (<c>AssignMigratedShip</c> → default
    /// <c>WingId=0</c> = command, <c>c_pszWingName[0]</c> "command"), so the
    /// re-elected leader always comes back on Command wing. Force the Bravo
    /// leader to Attack wing (1), rotate, and assert the leader's fresh ship
    /// is Command (0).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CommanderLeader_DefaultsToCommandWing_AfterRotation()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config("P616Wing"));

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot(); using var a1 = new FltBot();
        using var b0 = new FltBot(); using var b1 = new FltBot();
        short a0Id = Seat(server, a0, "WA0");
        Seat(server, a1, "WA1");
        short b0Id = Seat(server, b0, "WB0");
        Seat(server, b1, "WB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, b1.ShipAssignment!.Ship.ShipId), side1));
        var core = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };

        // The Bravo leader (first joiner of side1) "chose" Attack wing before
        // the game — the previous-game assignment #146 complains about.
        ShipOf(server, b0Id).WingId = 1;                     // 1 = attack (c_pszWingName[1])

        foreach (var (b, _) in core)
            b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, "wing test: mission did not start");

        var oldMission = server.Missions[0];
        foreach (var (b, _) in core) { b.ResetGameOvers(); b.ResetBallotSignals(); }
        DrawToGameOver(server, b0, b1, a0, a1);
        Assert.NotSame(oldMission, server.Missions[0]);

        // Pump the rebuilt mission through migration → leader election → start,
        // so the fresh mission's leadership/wing state is settled before we read.
        PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 3000);

        // B0 is the migrated Bravo commander; its fresh ship is a NEW Ship, and
        // a fresh Ship defaults WingId=0 (command, c_pszWingName[0]).
        short b0FreshId = b0.ShipAssignment!.Ship.ShipId;
        Assert.Equal(0, ShipOf(server, b0FreshId).WingId);

        // Whatever ship is the side's commander, it must sit on Command wing —
        // the whole point of #146 ("default to Command wing regardless of
        // previous wing choice"). The previously-chosen Attack (1) is gone.
        var side = server.Missions[0].Sides.First(s => s.Id == side1);
        if (side.LeaderShipId is { } lid && lid >= 0)
            Assert.Equal(0, ShipOf(server, lid).WingId);
        _output.WriteLine($"P616-146: Bravo commander fresh wing = {ShipOf(server, b0FreshId).WingId} (command) after rotation — prior Attack(1) discarded");
    }
}
