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
/// P201 — the flag-capture mechanic (GAIN_FLAG, fmid 156): the port of native
/// <c>FedSrvSiteBase::HitTreasureEvent</c>'s <c>c_tcFlag</c> case
/// (FedSrv.CPP:13562-13575) and <c>DockWithStationEvent</c>'s flag-securing
/// block (FedSrv.CPP:13111-13159), plus the join-sync leg (fsmission.cpp:740-762).
/// Live loopback proofs over a real in-process server + real TCP bots:
///   (1) a rescue-capable ship overlapping an ENEMY side's flag treasure picks
///       it up: <c>Ship.Flag</c> is set to the original side, the treasure is
///       removed, GAIN_FLAG(sideidFlag=&lt;enemy side&gt;, shipidRecipient=&lt;carrier&gt;)
///       broadcasts whole-mission, and DESTROY_TREASURE also fires (native
///       HitTreasureEvent returns true → KillTreasureEvent);
///   (2) a ship can NOT pick up its own side's flag treasure, nor a second
///       flag while already carrying one (both refused per FedSrv.CPP:13562-13568);
///   (3) a flag-carrying ship docking at its OWN side's station secures it:
///       <c>Ship.Flag</c> clears, <c>Side.Flags</c> increments, GAIN_FLAG(NA, ...)
///       broadcasts whole-mission — and <b>GameState.ForMission's nFlags reflects
///       the real incremented count, not zero</b> (the correction this task
///       exists for; P191 had always-zeroed it on an incomplete "never called"
///       finding);
///   (4) docking at a station of a DIFFERENT side does not secure the flag (the
///       native <c>pside == psideOld</c> gate), proven both live (a flag-carrying
///       ship at a hostile station never docks/secures) and surgically (the
///       private OnShipDocked gate rejects a mismatched station side);
///   (5) join-sync: a player joining a mission with an existing flag-carrying
///       ship receives a GAIN_FLAG for it;
///   (6) multi-mission scoping: a pickup in mission A reaches only mission A's
///       peers, not mission B's.
/// The codec round-trips byte-exact in Wire tests (see GainFlag.cs).
/// </summary>
[Collection("LiveNetwork")]
public class LiveGainFlagTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple (measured, P111 report): 1052 = no-station fly sector.
    private const short FlySector = 1052;

    private static readonly Vector3 FarAway = new(1000f, 990f, 985f);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Flag Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Flag A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Flag B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 731u, DwCookie = 0x731u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7300000000000000000ul, DrmHash = "flag",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Place a real flag treasure of the ORIGINAL side
    /// <paramref name="flagSideId"/> in the fly sector at <paramref name="pos"/>.
    /// For a flag treasure the <c>TreasureId</c> slot carries the original side
    /// whose flag this is (native <c>oid = treasure-&gt;GetTreasureID()</c> IS
    /// the side, shipIGC.h:1349). Long lifespan (this slice never expires).</summary>
    private static Treasure PlaceFlagTreasure(Srv server, Vector3 pos, short flagSideId, short oid)
    {
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var data = new DataTreasureIGC(
            P0: [pos.X, pos.Y, pos.Z],
            V0: [0f, 0f, 0f],
            Lifespan: 9999f,
            Time0: 0u,
            ObjectId: oid,
            TreasureId: flagSideId,   // the ORIGINAL side whose flag this is
            ClusterId: FlySector,
            Amount: 0,
            TreasureCode: TreasureCode.Flag,
            CreateNow: true);
        var t = new Treasure(oid, fly, data, 0.0);
        fly.Add(t);
        return t;
    }

    private static bool SectorHasTreasure(Mission mission, short oid) =>
        mission.Sectors.First(s => s.Id == FlySector).Models.OfType<Treasure>().Any(t => t.Id == oid);

    private static bool SectorHasTreasure(Srv server, short oid) =>
        SectorHasTreasure(server.Missions[0], oid);

    /// <summary>Holder for the three pickup-role ships (returned so the roles are
    /// captured on the object AFTER the spawner runs, not in a prematurely-bound
    /// tuple).</summary>
    private sealed class PickupRoles
    {
        public Ship Pickup = null!, Bystander = null!, Outsider = null!;
    }

    /// <summary>Three-ship pickup spawn: pickup ship (rescue-capable) side 0 at
    /// <paramref name="pickupPos"/>, same-side bystander (no caps, far away) and
    /// cross-side outsider (side 1, no caps, far away). Roles captured on the
    /// returned holder as built — same shape as the P123 cash-pickup harness.</summary>
    private static PickupRoles InjectPickupSpawn(Srv server, Vector3 pickupPos)
    {
        var sp = new PickupRoles();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            Ship ship;
            if (n == 0)
                ship = new Ship(shipId, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmRescue }, pickupPos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            else if (n == 1)
                ship = new Ship(shipId, baseHull with { HabmCapabilities = 0 }, FarAway, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            else
                ship = new Ship(shipId, baseHull with { HabmCapabilities = 0 }, FarAway, Vector3.Zero, Orientation.Identity) { SideId = 1 };
            n++;
            mission.PlaceShip(ship, fly);
            if (n == 1) sp.Pickup = ship;
            else if (n == 2) sp.Bystander = ship;
            else sp.Outsider = ship;
            return (ship, fly);
        };
        return sp;
    }

    private static FltBot[] Seat(Srv server, params string[] names)
    {
        var bots = names.Select(name => { var b = new FltBot(); b.Connect(server.BoundPort, name); b.SendLogonRequest(Request(name)); return b; }).ToArray();
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 6000);
        return bots;
    }

    // ── 1. pickup: enemy flag → Ship.Flag + whole-mission GAIN_FLAG + destroy ─

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rescue_ship_picks_up_enemy_flag_sets_ship_flag_and_broadcasts_gainflag_whole_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectPickupSpawn(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        var bots = Seat(server, "FA", "FB", "FO");     // pickup (side 0), bystander (side 0), outsider (side 1)

        var treasure = PlaceFlagTreasure(server, sp.Pickup.Position, flagSideId: 1, oid: 8101);
        Assert.True(SectorHasTreasure(server, treasure.Id), "treasure should be present before pickup");

        foreach (var b in bots) { b.ResetGainFlags(); b.ResetDestroyTreasures(); }

        int n = PumpUntil(server,
            () => bots.All(b => b.GainFlags.Count >= 1 && b.DestroyTreasures.Count >= 1), 3000);
        Assert.True(n < 3000, "GAIN_FLAG + DESTROY_TREASURE did not reach all three peers");

        // The carrier now holds the ORIGINAL enemy side's flag.
        Assert.Equal((short)1, sp.Pickup.Flag);

        // Treasure removed.
        Assert.False(SectorHasTreasure(server, treasure.Id), "treasure should be gone after pickup");

        // GAIN_FLAG(156) whole-mission: all three peers got the same frame — the
        // ORIGINAL side, the carrier, not treasure-docked, no part id.
        foreach (var b in bots)
        {
            var gf = Assert.Single(b.GainFlags);
            Assert.Equal((short)1, gf.SideidFlag);
            Assert.Equal(sp.Pickup.Id, gf.ShipidRecipient);
            Assert.False(gf.BIsTreasureDocked);
            Assert.Equal((short)0, gf.ParttypeidDocked);
        }
        // DESTROY_TREASURE(30) also fired (native HitTreasureEvent → KillTreasureEvent).
        foreach (var b in bots)
            Assert.Contains(b.DestroyTreasures, dt => dt.TreasureId == treasure.Id);

        Console.WriteLine($"FLAG-PICKUP: ship {sp.Pickup.Id} (side 0) picked side 1's flag -> GAIN_FLAG(156) to all 3 peers (sideidFlag=1, recipient={sp.Pickup.Id}); treasure {treasure.Id} removed");
    }

    // ── 2. refusals: own-side flag, or a second flag while carrying ─────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cannot_pick_up_own_sides_flag_treasure()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectPickupSpawn(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        Seat(server, "OA", "OB");

        // The pickup ship is side 0; a flag treasure OF side 0 is its own flag.
        var treasure = PlaceFlagTreasure(server, sp.Pickup.Position, flagSideId: 0, oid: 8201);
        Assert.True(SectorHasTreasure(server, treasure.Id));

        for (int i = 0; i < 8; i++) server.TickSim();   // plenty of proximity passes

        // Refused: not carried, no GAIN_FLAG, no destroy, treasure still present.
        Assert.Null(sp.Pickup.Flag);
        Assert.True(SectorHasTreasure(server, treasure.Id), "own-side flag must still be present");
        Console.WriteLine("FLAG-NEG-OWNSIDE: own-side flag treasure refused — treasure remains, ship carries nothing");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cannot_pick_up_a_second_flag_while_already_carrying_one()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectPickupSpawn(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        var bots = Seat(server, "SA", "SB");
        foreach (var b in bots) b.ResetGainFlags();

        // Simulate already carrying side 1's flag (native GetFlag() != NA).
        sp.Pickup.Flag = 1;

        var treasure = PlaceFlagTreasure(server, sp.Pickup.Position, flagSideId: 1, oid: 8211);
        for (int i = 0; i < 8; i++) server.TickSim();

        // Refused: still carrying only the one flag, no new GAIN_FLAG, treasure kept.
        Assert.Equal((short)1, sp.Pickup.Flag);
        Assert.True(SectorHasTreasure(server, treasure.Id), "second flag must not be consumed while carrying one");
        Assert.All(bots, b => Assert.Empty(b.GainFlags));
        Console.WriteLine("FLAG-NEG-SECOND: second flag refused while carrying one — carrier keeps its single flag, treasure remains, no GAIN_FLAG");
    }

    // ── 3. secure: flag-carrying ship docks at OWN side station ────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flag_carrier_docking_at_own_side_station_secures_and_game_state_reflects_real_count()
    {
        Require.Artwork();
        using var server = new Srv();
        short ownSide = 0, enemySide = 0;
        Vector3 dockPos = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var friendly = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            ownSide = friendly.SideId;
            enemySide = (short)(ownSide == 0 ? 1 : 0);
            dockPos = StationPos(friendly);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var sector = mission.Sectors.First(s => s.Stations.Contains(friendly));
            // Spawn OUTSIDE the dock radius but in the same (stationed) sector,
            // on the ship's own side, carrying the enemy's flag — so the test can
            // first confirm the carrier is flying with the flag held, then move
            // it into the radius to trigger the dock + secure.
            var ship = new Ship(shipId, hull, dockPos + new Vector3(300f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = ownSide;
            ship.Flag = enemySide;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        var bot = Seat(server, "SECURE").Single();
        bot.ResetGainFlags();

        var sh = server.Ships.First().Ship;   // the single peer's ship
        Assert.Equal(enemySide, sh.Flag!.Value);   // starts carrying the enemy flag, still flying
        Assert.Null(sh.StationId);

        // Drive it into the friendly station's dock radius → the dock pass trips,
        // the ship docks, and the secure leg fires.
        sh.Position = dockPos + new Vector3(20f, 0f, 0f);

        int n = PumpUntil(server, () => bot.GainFlags.Count >= 1, 4000);
        Assert.True(n < 4000, "GAIN_FLAG(NA) secure announce never reached the docking peer");
        Assert.True(sh.StationId is not null, "ship must have docked for the secure to fire");

        Assert.Null(sh.Flag);                                   // cleared (SetFlag(NA))
        Assert.Equal((short)1, server.Missions[0].Sides.First(s => s.Id == ownSide).Flags);
        Assert.Equal((short)0, server.Missions[0].Sides.First(s => s.Id == enemySide).Flags);

        var gf = Assert.Single(bot.GainFlags);
        Assert.Equal((short)-1, gf.SideidFlag);                 // NA on secure
        Assert.Equal(sh.Id, gf.ShipidRecipient);
        Assert.False(gf.BIsTreasureDocked);

        // ⭐ The key proof: GameState now broadcasts the REAL incremented counter,
        // not the P191-era hardcoded zero.
        var gs = GameState.ForMission(server.Missions[0]);
        Assert.Equal((byte)1, gs.NFlags[ownSide]);
        Assert.Equal((byte)0, gs.NFlags[enemySide]);

        Console.WriteLine($"FLAG-SECURE: ship {sh.Id} (side {ownSide}) docked at own station carrying side {enemySide}'s flag -> cleared, side {ownSide}.Flags=1, GAIN_FLAG(NA,{sh.Id}) whole-mission, GameState.nFlags[{ownSide}]=1");
    }

    // ── 4. wrong-side dock does not secure ────────────────────────────────

    /// <summary>Live: a flag-carrying ship parked at a HOSTILE station never
    /// docks (the port's friendly-only dock gate, DockDockingTests) and so never
    /// secures — flag held, no counter, no GAIN_FLAG(NA). Mirrors native where
    /// you cannot secure a flag at an enemy station (the pside == psideOld gate
    /// plus hostile-capture being separate).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flag_carrier_at_hostile_station_does_not_secure()
    {
        Require.Artwork();
        using var server = new Srv();
        short ownSide = 0, enemySide = 0;
        DataStationIGC hostile = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            hostile = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            ownSide = hostile.SideId;
            enemySide = (short)(ownSide == 0 ? 1 : 0);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var sector = mission.Sectors.First(s => s.Stations.Contains(hostile));
            // Park INSIDE the hostile radius but on the OTHER side (so a dock
            // would be a gate failure, not geometry), carrying a flag.
            var ship = new Ship(shipId, hull, StationPos(hostile) + new Vector3(5f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = enemySide;       // NOT the station's side
            ship.Flag = ownSide;           // carrying a flag
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby
        var bot = Seat(server, "HOST").Single();
        bot.ResetGainFlags();

        var sh = server.Ships.First().Ship;
        // Ensure genuinely inside the hostile radius so geometry is not the blocker.
        Assert.True((sh.Position - StationPos(hostile)).Length() <= Sector.DockRadius, "test must be inside the hostile radius");

        PumpUntil(server, () => sh.StationId is not null, 150);
        Assert.Null(sh.StationId);                    // never docked
        Assert.Equal((short)ownSide, sh.Flag!.Value); // flag held, NOT secured
        Assert.Equal((short)0, server.Missions[0].Sides.First(s => s.Id == enemySide).Flags);
        Assert.Empty(bot.GainFlags);                  // no GAIN_FLAG(NA) secure announce

        Console.WriteLine($"FLAG-HOSTILE: flag-carrying ship at hostile station never docked/secured — flag held (side {ownSide}), side {enemySide}.Flags=0, no GAIN_FLAG");
    }

    /// <summary>Surgical: the private <c>OnShipDocked</c> gate itself — the native
    /// <c>pside == psideOld</c> check (FedSrv.CPP:13111). Calling it with a
    /// station side that differs from the ship's side must NOT secure (even
    /// though the dock pass normally prevents such a dock); calling it with the
    /// matching side secures.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void OnShipDocked_gate_rejects_different_station_side_and_secures_on_match()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectPickupSpawn(server, Vector3.Zero);   // pickup = side 0
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        var bot = Seat(server, "GATE").Single();
        var sh = server.Ships.First().Ship;             // side 0
        Assert.Equal((short)0, sh.SideId!.Value);
        sh.Flag = 1;                                     // carrying side 1's flag
        sh.StationId = 8077;                            // normally latched by the dock pass before OnShipDocked fires (needed by the FM_S_DOCKED notify leg)
        bot.ResetGainFlags();

        // Different-station-side dock: refused (flag held, no counter, no broadcast).
        InvokeOnShipDocked(server, sh, (short?)1);
        Assert.Equal((short)1, sh.Flag!.Value);
        Assert.Equal((short)0, server.Missions[0].Sides.First(s => s.Id == 0).Flags);
        Assert.Empty(bot.GainFlags);

        // Matching-station-side dock: secured.
        InvokeOnShipDocked(server, sh, (short?)0);
        int pump = PumpUntil(server, () => bot.GainFlags.Count >= 1, 200);   // direct-call SendPeer rides the wire asynchronously (P183)
        Assert.True(pump < 200, "secure GAIN_FLAG never reached the bot after OnShipDocked");
        Assert.Null(sh.Flag);
        Assert.Equal((short)1, server.Missions[0].Sides.First(s => s.Id == 0).Flags);
        var gf = Assert.Single(bot.GainFlags);
        Assert.Equal((short)-1, gf.SideidFlag);
        Assert.Equal(sh.Id, gf.ShipidRecipient);

        Console.WriteLine($"FLAG-GATE: OnShipDocked(side-mismatch) refused (flag held), OnShipDocked(side-match) secured (side 0.Flags=1, GAIN_FLAG NA/{sh.Id})");
    }

    private static void InvokeOnShipDocked(Srv server, Ship ship, short? stationSide)
    {
        var mi = typeof(Srv).GetMethod("OnShipDocked", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Server.OnShipDocked");
        mi.Invoke(server, new object?[] { ship, stationSide });
    }

    // ── 5. join-sync: a joiner learns about an existing carrier ──────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Joiner_receives_gainflag_for_existing_flag_carrier()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectPickupSpawn(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        // Carrier joins first, then picks up side 1's flag (so it is carrying it
        // BEFORE the second player joins).
        var carrier = Seat(server, "CARR").Single();
        var carryShip = sp.Pickup;
        var treasure = PlaceFlagTreasure(server, carryShip.Position, flagSideId: 1, oid: 8991);
        PumpUntil(server, () => carryShip.Flag is not null, 3000);
        Assert.Equal((short)1, carryShip.Flag!.Value);

        // Second player joins a mission that already has a carrier.
        var joiner = Seat(server, "JN").Single();
        int seen = PumpUntil(server, () => joiner.GainFlags.Count >= 1, 3000);
        Assert.True(seen < 3000, "joiner never received the flag join-sync");

        var gf = Assert.Single(joiner.GainFlags);
        Assert.Equal((short)1, gf.SideidFlag);                  // the carried side
        Assert.Equal(carryShip.Id, gf.ShipidRecipient);          // the carrier
        Assert.False(gf.BIsTreasureDocked);

        Console.WriteLine($"FLAG-JOINSYNC: joiner received GAIN_FLAG({gf.SideidFlag},{gf.ShipidRecipient}) for pre-existing carrier {carryShip.Id}");
    }

    // ── 6. multi-mission scoping: a pickup announces only to its mission ───

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flag_pickup_broadcast_scopes_to_acting_mission_only()
    {
        Require.Artwork();
        using var server = new Srv();
        short shipA0 = 0;
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            // NOTE: the server passes _missions[0] to the spawner for every peer
            // (Server.AssignShip), so routing must be by seq here — the same
            // explicit-control pattern LiveWinConditionTests.InjectMultiMissionSpawn
            // uses. seq 0 → mission A (pickup ship), seq 1 → mission B (far away).
            var m = seq == 0 ? server.Missions[0] : server.Missions[1];
            var fly = m.Sectors.First(s => s.Id == FlySector);
            if (seq == 0)
            {
                var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
                var ship = new Ship(shipId, baseHull with { HabmCapabilities = DataHullTypeIGC.HabmRescue }, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = m.Sides[0].Id };
                m.PlaceShip(ship, fly);
                shipA0 = ship.Id;
                seq++;
                return (ship, fly);
            }
            var hullB = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var sb = new Ship(shipId, hullB, FarAway, Vector3.Zero, Orientation.Identity) { SideId = m.Sides[0].Id };
            m.PlaceShip(sb, fly);
            seq++;
            return (sb, fly);
        };

        server.Boot(ConfigTwoGames());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby
        Assert.Equal(2, server.Missions.Count);

        var a0 = Seat(server, "MA").Single();    // mission A peer
        var b0 = Seat(server, "MB").Single();    // mission B peer
        a0.ResetGainFlags(); b0.ResetGainFlags();

        // The pickup ship is the mission-A peer; place side 1's flag treasure at it.
        var a0ship = server.Ships.First(s => s.Ship.Id == shipA0).Ship;
        var t = PlaceFlagTreasure(server, a0ship.Position, flagSideId: 1, oid: 8771);
        int n = PumpUntil(server, () => a0.GainFlags.Count >= 1, 3000);
        Assert.True(n < 3000, "mission A peer never received the pickup GAIN_FLAG");

        Assert.Single(a0.GainFlags);
        Assert.Equal(a0ship.Id, a0.GainFlags[0].ShipidRecipient);
        // Mission B's peer hears NOTHING (whole-mission scoped to the acting mission).
        Assert.Empty(b0.GainFlags);
        Assert.False(SectorHasTreasure(server.Missions[0], t.Id), "treasure consumed by the pickup in mission A");

        Console.WriteLine($"FLAG-MULTI: mission A pickup GAIN_FLAG reached A peer only ({a0.GainFlags.Count}); mission B peer saw {b0.GainFlags.Count}");
    }
}
