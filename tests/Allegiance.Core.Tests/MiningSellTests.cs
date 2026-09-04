using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (same
// pattern as MiningTests / DockDockingTests / PaydayTests).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Host = global::Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P56 deliverable — the sell half of the mining loop, closing P55's
/// deliberately-open "ore → money" step. A ship carrying ore
/// (<see cref="Ship.Ore"/> &gt; 0) that <b>newly docks</b> at a friendly station
/// converts its cargo to credits (<see cref="Ship.OreCreditPerUnit"/> per unit),
/// credits the owning side's treasury, raises the P51 <c>PaydayEvent</c> seam so
/// <c>Server.OnPayday</c> (<c>Server.cs:1413</c>) broadcasts a real
/// <c>FM_S_PAYDAY(114)</c> to the side's peers, and zeroes the cargo. Proven in
/// two layers mirroring the P51/P53/P55 suites: direct <see cref="Host"/>
/// construction (precise PaydayEvent call-count + Money math, no network) and a
/// real loopback TCP <see cref="FltBot"/> boot that combines the two injection
/// patterns (a synthetic asteroid like <c>MiningTests</c> + a friendly station
/// like <c>DockDockingTests</c>) in one mission. No VM, no native repo, no
/// refinery/unload variant, no per-side yield scaling.
/// </summary>
[Collection("LiveNetwork")]
public class MiningSellTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    // ── synthetic builders ────────────────────────────────────────────────
    // Mirrors MiningTests.MkAsteroid (He3-capable rock, bit 0x01 igc.h:761).
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId)
    {
        float ore = oreMax;
        return new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: ore, OreMax: oreMax,
            AabmCapabilities: Asteroid.CapMineHe3,   // He3-capable => mineable + regenerative
            AsteroidId: id, HitPoints: 0f, Radius: radius,
            ModelName: "roid.mdl", TextureName: "", IconName: "",
            ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);
    }

    private static DataStationIGC MkStation(short sectorId, short sideId, short stationId, Vector3 pos) =>
        new(
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sectorId, SideId: sideId, StationId: stationId, StationTypeId: 0,
            BpHull: 0, BpShield: 0, Name: $"SELL_{stationId}");

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    // ── representative static core + a real map, for the no-network mission ─
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

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    /// <summary>A started mission whose Site is a recording harness (no host
    /// installed → <c>PaydayEvent</c> is a recording stub, so money accrues in
    /// the treasury rather than being distributed — ideal for isolating the
    /// sell's treasury credit from the host's payout half).</summary>
    private static Mission NewStartedMission(IIgcSite site, int sides = 2)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(sides).ToList();
        IReadOnlyList<SideSpec> specs = [];
        for (int i = 0; i < civs.Count; i++)
            specs = specs.Append(new SideSpec((short)i, $"Team {i + 1}", civs[i].CivilizationId)).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, site);
        m.Stage = MissionStage.Started;
        return m;
    }

    private static int PaydayEvents(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("PaydayEvent("));

    // ── unit: direct ServerHost, precise call-count + Money math ─────────
    // Constructing a ServerHost directly (InternalsVisibleTo
    // "Allegiance.Core.Tests" → AssemblyInfo.cs) exercises the exact
    // DockWithStationEvent branch without the uncertainty of a live game clock
    // or the periodic 60s payday muddying the Money/PaydayEvent picture.

    /// <summary>⭐ A ship carrying ore that docks sees its cargo sold: the
    /// owning side's treasury increases by exactly <c>ore × OreCreditPerUnit</c>,
    /// the seam fires a <c>PaydayEvent</c> with that amount and side, and the
    /// cargo is zeroed. A <b>redundant</b> dock call (ship already docked) sells
    /// nothing more — the existing "already docked" guard is the boundary.</summary>
    [Fact]
    public void Dock_transition_sells_ore_into_treasury_and_zeroes_cargo()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var side = mission.Sides[0];
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        var ship = new Ship(9111, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side.Id };
        mission.PlaceShip(ship, sector);
        ship.Ore = 40f;                                        // loaded with cargo
        var station = MkStation(sector.Id, side.Id, stationId: 5611, new Vector3(50f, 0f, 0f));

        var host = new Host();
        ISideIgc? capturedSide = null; float capturedMoney = 0f;
        host.Payday = (s, m) => { capturedSide = s; capturedMoney = m; };

        // Docked state starts null → this is a genuine "newly docks" transition.
        Assert.Null(ship.StationId);
        bool perm = host.DockWithStationEvent(ship, new DockStation(station));
        Assert.True(perm);
        Assert.Equal(station.StationId, ship.StationId!.Value);   // latched docked
        Assert.Equal(0f, ship.Ore);                               // cargo sold
        Assert.Equal((long)(40f * Ship.OreCreditPerUnit), side.Money);   // treasury credited
        Assert.Same(side, capturedSide);                          // sold to the ship's own side
        Assert.Equal(40f * Ship.OreCreditPerUnit, capturedMoney); // the exact converted amount

        // Redundant dock (already docked) — no second sale even with cargo set.
        ship.Ore = 10f;
        host.DockWithStationEvent(ship, new DockStation(station));
        Assert.Equal(10f, ship.Ore);                              // untouched
        Assert.Equal((long)(40f * Ship.OreCreditPerUnit), side.Money); // not double-sold
    }

    /// <summary>⭐ Docking with zero ore causes no payday side-effect at all:
    /// no <c>PaydayEvent</c> through the seam, no treasury change, cargo stays 0.</summary>
    [Fact]
    public void Dock_with_zero_ore_has_no_payday_side_effect()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var side = mission.Sides[0];
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        var ship = new Ship(9112, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side.Id };
        mission.PlaceShip(ship, sector);
        ship.Ore = 0f;                                            // no cargo
        var station = MkStation(sector.Id, side.Id, stationId: 5622, new Vector3(50f, 0f, 0f));

        var host = new Host();
        int paydays = 0; host.Payday = (_, _) => paydays++;

        host.DockWithStationEvent(ship, new DockStation(station));
        Assert.Equal(0, paydays);                                 // no PaydayEvent
        Assert.Equal(0L, side.Money);                             // treasury untouched
        Assert.Equal(0f, ship.Ore);                               // still empty
        Assert.NotNull(ship.StationId);                           // but it DID dock
    }

    /// <summary>⭐ The "newly docks" gate does not stick shut after the first
    /// sale: a ship that docks (sells), undocks (<c>StationId</c> cleared the
    /// way the real undock does), loads more ore, and docks again sells a second
    /// time — each transition sells exactly once.</summary>
    [Fact]
    public void Redock_after_undock_sells_again()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var side = mission.Sides[0];
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        var ship = new Ship(9113, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side.Id };
        mission.PlaceShip(ship, sector);
        var station = MkStation(sector.Id, side.Id, stationId: 5633, new Vector3(50f, 0f, 0f));
        var host = new Host();
        float lastPaid = 0f; host.Payday = (_, m) => lastPaid = m;

        ship.Ore = 30f;
        host.DockWithStationEvent(ship, new DockStation(station));          // sale #1
        Assert.Equal(0f, ship.Ore);
        Assert.Equal((long)(30f * Ship.OreCreditPerUnit), side.Money);
        Assert.Equal(30f * Ship.OreCreditPerUnit, lastPaid);

        // Undock (StationId cleared, exactly what FM_C_DOCKED(48) does) and load more.
        ship.StationId = null;
        ship.Ore = 25f;
        host.DockWithStationEvent(ship, new DockStation(station));          // sale #2
        Assert.Equal(0f, ship.Ore);
        Assert.Equal((long)(55f * Ship.OreCreditPerUnit), side.Money);       // cumulative
        Assert.Equal(25f * Ship.OreCreditPerUnit, lastPaid);                 // this transition only
    }

    // ── loopback wire (real TCP, demo map XXV_simple — zero asteroids) ───
    // Combines the two injection patterns in ONE test mission: a synthetic
    // asteroid (MiningTests) for mining + a real friendly station
    // (DockDockingTests) for the sell. The bot's ship mines, then is moved to
    // the station's radius, docks, and the sell emits a real FM_S_PAYDAY(114).

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Mining Sell Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 5656u, DwCookie = 0x56u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 56000000000000000ul, DrmHash = "miningsell",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Thread.Sleep(60);
        return n;
    }

    /// <summary>Choose a map station whose side is a real mission side, and
    /// place the synthetic asteroid <c>gap</c> away from it so mining and
    /// docking never coincide geometrically.</summary>
    private static DataStationIGC ChooseFriendly(Mission mission)
    {
        var friendly = mission.Sectors
            .SelectMany(s => s.Stations)
            .First(st => mission.Sides.Any(sd => sd.Id == st.SideId));
        return friendly;
    }

    /// <summary>⭐ End-to-end over real TCP: a bot's ship mines a synthetic
    /// asteroid, then is moved into a friendly station's dock radius, docks, and
    /// the <b>sell</b> zeroes its cargo and broadcasts a real <c>FM_S_PAYDAY(114)</c>
    /// whose <c>dMoney</c> equals the converted <c>ore × OreCreditPerUnit</c>
    /// (one player on the side ⇒ the whole proceeds are paid out and observed).</summary>
    [Fact]
    public void Loopback_mine_then_dock_sells_ore_and_pays_FM_S_PAYDAY()
    {
        using var server = new Srv();
        Asteroid? placedRock = null;
        DataStationIGC friendly = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            friendly = ChooseFriendly(mission);
            var sector = mission.Sectors.First(s => s.Stations.Contains(friendly));
            var apos = StationPos(friendly) + new Vector3(600f, 0f, 0f);
            placedRock = new Asteroid(MkAsteroid(7201, apos, oreMax: 500f, radius: 60, sectorId: sector.Id));
            mission.PlaceAsteroid(placedRock, sector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, apos, Vector3.Zero, Orientation.Identity) { SideId = friendly.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "msell_probe");
        bot.SendLogonRequest(Request("msell_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var live = () => server.Ships.First(s => s.Ship.Id == shipId).Ship;

        // (1) Mine (dt = 0.05 s => 5 ore/tick, 20 UPS) until loaded.
        PumpUntil(server, () => live().Ore >= 20f, 400);
        Assert.True(live().Ore >= 20f, $"should have mined >=20 ore, got {live().Ore}");
        float oreBeforeDock = live().Ore;
        bot.ResetPaydays();

        // (2) Move to the friendly station's radius → the dock pass fires and
        // the sell runs on that transition.
        live().Position = StationPos(friendly) + new Vector3(30f, 0f, 0f);
        PumpUntil(server, () => live().StationId is not null, 400);

        // Cargo zeroed; the value went to money.
        Assert.Equal(0f, live().Ore);

        // A real FM_S_PAYDAY(114) reached the bot (one player on the side ⇒ the
        // converted whole is paid out), matching ore × the flat constant.
        PumpUntil(server, () => bot.Paydays.Count >= 1, 400);
        long expected = (long)(Ship.OreCreditPerUnit * oreBeforeDock);
        Assert.Single(bot.Paydays);
        Assert.Equal(expected, bot.Paydays[0].DMoney);
    }

    /// <summary>⭐ The sell gate stays open across transitions over real TCP:
    /// mine → dock (sale #1) → undock → mine → dock again (sale #2) produces two
    /// distinct <c>FM_S_PAYDAY</c> payouts, each equal to that trip's ore × the
    /// flat constant — proving the "newly docks" boundary, not a one-shot.</summary>
    [Fact]
    public void Loopback_redock_sells_again_each_transition()
    {
        using var server = new Srv();
        Asteroid? placedRock = null;
        DataStationIGC friendly = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            friendly = ChooseFriendly(mission);
            var sector = mission.Sectors.First(s => s.Stations.Contains(friendly));
            var apos = StationPos(friendly) + new Vector3(600f, 0f, 0f);
            placedRock = new Asteroid(MkAsteroid(7202, apos, oreMax: 500f, radius: 60, sectorId: sector.Id));
            mission.PlaceAsteroid(placedRock, sector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, apos, Vector3.Zero, Orientation.Identity) { SideId = friendly.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "msell_redo");
        bot.SendLogonRequest(Request("msell_redo"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var live = () => server.Ships.First(s => s.Ship.Id == shipId).Ship;

        // Trip 1: mine, dock, sell.
        PumpUntil(server, () => live().Ore >= 20f, 400);
        float oreTrip1 = live().Ore;
        bot.ResetPaydays();
        live().Position = StationPos(friendly) + new Vector3(30f, 0f, 0f);
        PumpUntil(server, () => live().StationId is not null, 400);
        Assert.Equal(0f, live().Ore);
        PumpUntil(server, () => bot.Paydays.Count >= 1, 400);
        Assert.Single(bot.Paydays);
        Assert.Equal((long)(Ship.OreCreditPerUnit * oreTrip1), bot.Paydays[0].DMoney);

        // Undock (clears StationId, exactly the FM_C_DOCKED(48) announce), then
        // move back onto the rock. Undock itself now pushes the ship just
        // outside DockRadius with an outward velocity (Server.cs's FM_C_DOCKED
        // handler, mirroring the native reference's own CstationIGC::Launch),
        // applied server-side whenever the FM_C_DOCKED message is actually
        // drained/processed — which happens inside a later Tick(), not
        // synchronously with SendDocked() (this is a real TCP bot, the message
        // is still in flight when SendDocked() returns). So the reposition must
        // wait for that to land first, or it would overwrite a same-tick manual
        // write with the station-adjacent launch point instead of the rock.
        bot.SendDocked();                                  // FM_C_DOCKED, stationID == NA
        PumpUntil(server, () => live().StationId is null, 400);
        live().Position = placedRock is null ? StationPos(friendly) + new Vector3(600f, 0f, 0f)
            : new Vector3(placedRock.Data.Position[0], placedRock.Data.Position[1], placedRock.Data.Position[2]);   // back on the rock
        live().Velocity = Vector3.Zero;
        PumpUntil(server, () => live().Ore >= 20f, 400);
        float oreTrip2 = live().Ore;

        live().Position = StationPos(friendly) + new Vector3(30f, 0f, 0f);
        PumpUntil(server, () => live().StationId is not null, 400);
        Assert.Equal(0f, live().Ore);
        PumpUntil(server, () => bot.Paydays.Count >= 2, 400);
        Assert.Equal(2, bot.Paydays.Count);
        Assert.Equal((long)(Ship.OreCreditPerUnit * oreTrip2), bot.Paydays[1].DMoney);
    }

    /// <summary>⭐ Docking with zero cargo over real TCP triggers no payday:
    /// the ship docks normally but no <c>FM_S_PAYDAY</c> is emitted for the
    /// transition (no spurious sale / no spurious payout).</summary>
    [Fact]
    public void Loopback_dock_with_no_ore_emits_no_FM_S_PAYDAY()
    {
        using var server = new Srv();
        DataStationIGC friendly = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            friendly = ChooseFriendly(mission);
            var sector = mission.Sectors.First(s => s.Stations.Contains(friendly));
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, StationPos(friendly) + new Vector3(30f, 0f, 0f),
                                Vector3.Zero, Orientation.Identity) { SideId = friendly.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "msell_zero");
        bot.SendLogonRequest(Request("msell_zero"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var live = () => server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(0f, live().Ore);                       // no cargo to sell

        bot.ResetPaydays();
        PumpUntil(server, () => live().StationId is not null, 400);   // docks
        Assert.NotNull(live().StationId);
        Assert.Equal(0f, live().Ore);                       // still empty

        // Short window well before the periodic 60s payday: no FM_S_PAYDAY may
        // arrive from the dock transition itself.
        PumpUntil(server, () => bot.Paydays.Count >= 1, 600);
        Assert.Empty(bot.Paydays);                          // no spurious sale
    }
}
