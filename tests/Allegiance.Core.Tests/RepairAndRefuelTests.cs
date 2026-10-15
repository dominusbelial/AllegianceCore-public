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
/// P261 — station repair/reload on dock (heal + rearm to 100%). Native
/// <c>CstationIGC::RepairAndRefuel</c> (<c>stationIGC.cpp:449-489</c>) fires in
/// <c>FedSrvSiteBase::ChangeStation</c>'s undock branch (<c>FedSrv.CPP:13189-13191</c>)
/// — i.e. at LAUNCH, not the moment of docking, gated per-station-type on the
/// <c>c_sabmRepair</c> (0x40) and <c>c_sabmReload</c> (0x100) ability bits.
/// These tests prove, on the real production <see cref="Ship.RepairAndRefuel"/>
/// method:
///   (unit) the capability gate — both bits restore hull+ammo; repair-only
///          restores hull but NOT ammo; reload-only restores ammo but NOT hull;
///          neither restores neither (while the unconditional shield/energy legs
///          still fire); the shield-restore leg and the full-mount/part-top-up
///          legs;
///   (live) a real damaged docked ship on a real map (XXV_simple, Garrison =
///          both bits) keeps its damage through docking and is fully restored
///          the instant it launches — not before;
///   (live) a real damaged ship launching from a real station lacking the bits
///          (m2xBowTie side-0 Refinery, caps=0x0801 = neither) is NOT healed or
///          rearmed — proving the gate end-to-end on real corpus data.
/// The one unit-level carve-out worth noting: in PCore001 every repair-capable
/// station type also carries reload (confirmed by scanning the whole core), so
/// the per-bit isolation can only be shown with a crafted capability mask — the
/// unit tests do exactly that on the real method.
/// </summary>
[Collection("LiveNetwork")]
public class RepairAndRefuelTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static readonly Vector3 Pos = new(10f, 0f, 0f);

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>A real hull with pinned max values for deterministic asserts
    /// (records are init-only, so `with` rewrites them; the fields come straight
    /// from the corpus decode).</summary>
    private static DataHullTypeIGC PinnedHull()
    {
        var core = Core();
        var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        return baseHull with { MaxAmmo = 100, MaxFuel = 200f, MaxEnergy = 50f };
    }

    /// <summary>The real Garrison (type 15) both bits carry (caps=0x0377) —
    /// the base for crafting per-bit masks via `with`.</summary>
    private static DataStationTypeIGC Garrison() => Core().StationTypes[15];

    private static Ship NewShip(short id, out DataHullTypeIGC hull)
    {
        hull = PinnedHull();
        return new Ship(id, hull, Pos, Vector3.Zero, Orientation.Identity);
    }

    // ── capability gate (unit) ──────────────────────────────────────────────

    /// <summary>⭐ Both <c>c_sabmRepair</c> and <c>c_sabmReload</c>: a damaged,
    /// dry ship is fully restored — hull → 1.0, ammo → max, energy → max.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BothBits_restore_hull_ammo_and_energy()
    {
        Require.Artwork();
        var ship = NewShip(1, out var hull);
        // Damage the hull to ~0.5 (no mission → dtm=1.0, maxHP=hull.HitPoints).
        ship.ReceiveDamage(0, 0.5f * hull.HitPoints, 0.0, Pos, Pos, null);
        ship.Ammo = 5;
        ship.Energy = 10f;
        Assert.True(ship.Fraction < 1.0f);

        ship.RepairAndRefuel(Garrison());   // real type 15: both bits

        Assert.Equal(1.0, ship.Fraction, 4);
        Assert.Equal((short)100, ship.Ammo);
        Assert.Equal(50f, ship.Energy, 3);
    }

    /// <summary>⭐ Negative gate: <c>c_sabmRepair</c> alone heals the HULL but
    /// does NOT top up the ammo magazine — a station without the reload bit
    /// doesn't rearm.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RepairOnly_heals_hull_but_not_ammo()
    {
        Require.Artwork();
        var ship = NewShip(1, out var hull);
        ship.ReceiveDamage(0, 0.5f * hull.HitPoints, 0.0, Pos, Pos, null);
        ship.Ammo = 5;
        ship.Energy = 10f;

        ship.RepairAndRefuel(Garrison() with { SabmCapabilities = DataStationTypeIGC.SabmRepair });

        Assert.Equal(1.0, ship.Fraction, 4);      // hull healed
        Assert.Equal((short)5, ship.Ammo);        // ammo NOT reloaded
        Assert.Equal(50f, ship.Energy, 3);        // energy unconditional
    }

    /// <summary>⭐ Negative gate: <c>c_sabmReload</c> alone tops up the ammo
    /// magazine but does NOT heal the hull — a station without the repair bit
    /// doesn't repair.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ReloadOnly_refills_ammo_but_not_hull()
    {
        Require.Artwork();
        var ship = NewShip(1, out var hull);
        ship.ReceiveDamage(0, 0.5f * hull.HitPoints, 0.0, Pos, Pos, null);
        ship.Ammo = 5;

        ship.RepairAndRefuel(Garrison() with { SabmCapabilities = DataStationTypeIGC.SabmReload });

        Assert.True(ship.Fraction < 1.0f);        // hull NOT healed
        Assert.Equal((short)100, ship.Ammo);      // ammo reloaded
    }

    /// <summary>⭐ Negative gate: neither bit → neither hull nor ammo restored;
    /// only the unconditional legs (energy) fire.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NeitherBit_leaves_hull_and_ammo_but_restores_energy()
    {
        Require.Artwork();
        var ship = NewShip(1, out var hull);
        ship.ReceiveDamage(0, 0.5f * hull.HitPoints, 0.0, Pos, Pos, null);
        ship.Ammo = 5;
        ship.Energy = 10f;

        ship.RepairAndRefuel(Garrison() with { SabmCapabilities = 0 });

        Assert.True(ship.Fraction < 1.0f);        // hull NOT healed
        Assert.Equal((short)5, ship.Ammo);        // ammo NOT reloaded
        Assert.Equal(50f, ship.Energy, 3);        // energy still unconditional
    }

    /// <summary>The shield-restore leg is UNCONDITIONAL (native
    /// <c>stationIGC.cpp:461-465</c>): a drained shield recharges to 1.0 even at
    /// a station with neither ability bit. Deterministic setup: mount a real
    /// shield (arms it to 1.0), hit it with 0.5·MaxStrength — the shield stops
    /// the whole hit (leakage 0, hull untouched) and drains to ~0.5.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shield_restores_to_full_even_without_capability_bits()
    {
        Require.Artwork();
        var ship = NewShip(1, out _);
        var core = Core();
        var shieldPart = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Shield);
        float maxStrength = shieldPart.Type is DataPartTypeIGC.Shield sh ? sh.MaxStrength
            : throw new InvalidOperationException("need a Shield part in representative core");

        ship.MountPart(shieldPart, 1, 0, 255);      // arms shield to 1.0 (shipIGC Arming)
        Assert.Equal(1.0, ship.ShieldFraction, 4);

        ship.ReceiveDamage(0, 0.5f * maxStrength, 0.0, Pos, Pos, null);
        Assert.True(ship.ShieldFraction < 1.0f);     // shield drained
        Assert.Equal(1.0, ship.Fraction, 4);         // hull untouched (shield stopped it)

        ship.RepairAndRefuel(Garrison() with { SabmCapabilities = 0 });

        Assert.Equal(1.0, ship.ShieldFraction, 4);   // shield recharged (unconditional)
    }

    /// <summary>The full-mount leg (native <c>stationIGC.cpp:453-456</c>) mounts
    /// every part to 1.0, and the reload leg tops every mounted pack's amount to
    /// the hull max (native <c>stationIGC.cpp:476-483</c>). A weapon mounted at a
    /// partial fraction and an ammo pack with a spent amount both get reset.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FullyMounts_parts_and_tops_up_packs()
    {
        Require.Artwork();
        var ship = NewShip(1, out var hull);
        var core = Core();

        // A real weapon mounted at 64/255 (partial) — simulates a partly-depleted
        // magazine's mounted fraction.
        var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon && p.PartId != 0);
        ship.MountPart(weapon, 1, 0, 64);
        var weaponMount = ship.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon);
        Assert.Equal((byte)64, weaponMount.FractionMounted);

        // A real pack part (ammo or fuel) mounted with a spent amount.
        var packPart = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Pack);
        ship.MountPart(packPart, 1, 0, 255);
        var pack = ship.Parts.Single(p => p.EquipmentType == EquipmentType.Pack);
        short expectPack = pack.Type.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0
            ? hull.MaxAmmo : (short)hull.MaxFuel;   // c_packAmmo=0 → maxAmmo else maxFuel

        ship.RepairAndRefuel(Garrison());

        Assert.Equal(ExpandedPartData.FullyMounted, ship.Parts.Single(p => p.EquipmentType == EquipmentType.Weapon).FractionMounted);
        Assert.Equal(expectPack, ship.Parts.Single(p => p.EquipmentType == EquipmentType.Pack).Amount);
        Assert.Equal((short)100, ship.Ammo);
    }

    /// <summary>A bare hull ship with no parts and no shield survives the restore
    /// unchanged in the parts/shield dimensions (the unconditional legs only act
    /// when the thing exists).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bare_hull_ship_without_parts_or_shield_is_unharmed()
    {
        Require.Artwork();
        var ship = NewShip(1, out var hull);
        ship.Ammo = 0;
        ship.Energy = 0f;
        ship.RepairAndRefuel(Garrison());
        Assert.Equal((short)100, ship.Ammo);
        Assert.Equal(50f, ship.Energy, 3);
        Assert.Empty(ship.Parts);
    }

    // ── live positive: restore happens at LAUNCH, not at dock ───────────────

    private static Cfg Config(string map, int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Repair Test", Map = map, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 761u, DwCookie = 0x761u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7600000000000000000ul, DrmHash = "repairtest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static (Ship Ship, Sector Sector) SpawnDockedAt(
        Mission mission, short shipId, DataStationIGC station)
    {
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var sector = mission.Sectors.First(s => s.Stations.Contains(station));
        var ship = new Ship(shipId, hull, DockTestPlacement.Inside(mission, station, hull), Vector3.Zero, Orientation.Identity)
        {
            SideId = station.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    /// <summary>⭐ The real integration on the real Garrison map (both bits):
    /// dock, then damage hull + exhaust ammo + exhaust energy server-side; while
    /// STILL DOCKED all of it persists (proof the restore is not at dock); then
    /// undock (launch) and every value is restored to full — hull → 1.0,
    /// ammo → hull max, energy → hull max — the moment it flies again.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_damaged_docked_ship_is_restored_at_launch_not_at_dock()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var friendly = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            return SpawnDockedAt(mission, shipId, friendly);
        };

        server.Boot(Config("XXV_simple.igc"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: immediate flyable
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "repair_pos_probe");
        bot.SendLogonRequest(Request("repair_pos_probe"));

        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        // (a) Confirm docked server-side first.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 200);
        var live = server.Ships.First(s => s.Ship.Id == shipId);
        Assert.NotNull(live.Ship.StationId);
        var hull = live.Ship.HullType;

        // (b) Damage/exhaust while docked. Hull down to ~0.5 (enough below 1.0 to
        // prove a real heal); ammo + energy exhausted.
        live.Ship.ReceiveDamage(0, 0.5f * hull.HitPoints, 0.0, live.Ship.Position, live.Ship.Position, null);
        live.Ship.Ammo = 0;
        live.Ship.Energy = 0f;
        Assert.True(live.Ship.Fraction < 1.0f);
        Assert.Equal((short)0, live.Ship.Ammo);

        // (c) ⭐ While still DOCKED the damage persists — the restore is NOT at
        // dock; it must wait for launch.
        PumpUntil(server, () => false, 30);
        Assert.True(live.Ship.Fraction < 1.0f);
        Assert.Equal((short)0, live.Ship.Ammo);
        Assert.Equal(0f, live.Ship.Energy, 3);

        // (d) Undock (launch) via FM_C_DOCKED(48) stationID==NA → PerformUndock
        // → UndockShipState → RepairAndRefuel.
        bot.SendDocked();
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is null, 4000);

        // (e) ⭐ Fully restored at launch: hull → 1.0, ammo → max, energy → max.
        Assert.Equal(1.0, server.Ships.First(s => s.Ship.Id == shipId).Ship.Fraction, 4);
        Assert.Equal(hull.MaxAmmo, server.Ships.First(s => s.Ship.Id == shipId).Ship.Ammo);
        Assert.Equal(hull.MaxEnergy, server.Ships.First(s => s.Ship.Id == shipId).Ship.Energy, 3);
    }

    /// <summary>⭐ Live negative on real corpus data: m2xBowTie's side-0 Refinery
    /// (type 14, caps=0x0801 — neither <c>c_sabmRepair</c> nor <c>c_sabmReload</c>).
    /// A ship damaged + exhausted there and launched is NOT healed or rearmed —
    /// the capability gate is the only thing preventing a restore (energy still
    /// tops up, proving the unconditional legs fire and the wiring runs).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_launch_from_station_without_bits_does_not_heal_or_rearm()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            // m2xBowTie side-0 Refinery: type 14, neither repair nor reload.
            var refinery = mission.Sectors.SelectMany(s => s.Stations)
                .First(st => st.SideId >= 0
                    && mission.Core.StationTypes.TryGetValue(st.StationTypeId, out var t)
                    && !t.HasCapability(DataStationTypeIGC.SabmRepair));
            var placed = SpawnDockedAt(mission, shipId, refinery);
            // P550 — the refinery type carries neither SabmLand nor SabmCapLand
            // (it is not a landing base), so the dock pass now correctly refuses
            // to dock a fighter there (native shipIGC.cpp:725-727). This test is
            // about the repair/reload gate at LAUNCH, not about docking, so spawn
            // the ship already latched docked at the refinery (bypassing the dock
            // pass) and prove the no-heal/no-rearm behaviour on launch.
            placed.Item1.StationId = refinery.StationId;
            placed.Item1.LastDockTime = 0.0;
            return placed;
        };

        server.Boot(Config("m2xBowTie.igc"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "repair_neg_probe");
        bot.SendLogonRequest(Request("repair_neg_probe"));

        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 200);
        var live = server.Ships.First(s => s.Ship.Id == shipId);
        Assert.NotNull(live.Ship.StationId);
        var hull = live.Ship.HullType;

        // Resolve the docked station type to prove the precondition (no bits).
        short stid = live.Ship.StationId!.Value;
        var dockedType = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Stations)
            .First(st => st.StationId == stid).StationTypeId;
        var dtype = server.Missions[0].Core.StationTypes[dockedType];
        Assert.False(dtype.HasCapability(DataStationTypeIGC.SabmRepair));
        Assert.False(dtype.HasCapability(DataStationTypeIGC.SabmReload));

        // Damage + exhaust while docked.
        live.Ship.ReceiveDamage(0, 0.5f * hull.HitPoints, 0.0, live.Ship.Position, live.Ship.Position, null);
        live.Ship.Ammo = 0;
        live.Ship.Energy = 0f;
        Assert.True(live.Ship.Fraction < 1.0f);

        // Launch.
        bot.SendDocked();
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is null, 4000);
        var after = server.Ships.First(s => s.Ship.Id == shipId).Ship;

        // ⭐ Negative: neither healed nor rearmed, exactly because the gate is
        // absent — while energy (unconditional) still tops up, proving the
        // RepairAndRefuel wiring ran.
        Assert.True(after.Fraction < 1.0f);
        Assert.Equal((short)0, after.Ammo);
        Assert.Equal(hull.MaxEnergy, after.Energy, 3);
    }
}
