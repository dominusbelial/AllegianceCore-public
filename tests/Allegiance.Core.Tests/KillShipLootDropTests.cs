using System.Reflection;
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
/// P411 — the dead-ship loot-drop mechanic (<c>FedSrv.CPP:11686-11762</c>),
/// ported into <c>Server.BroadcastKillShip</c>'s root-ship block. Proven live
/// over the real in-process server:
///   (1) a destroyed root ship rolls one 1-in-5 chance per mounted part and one
///       1-in-5 chance for its ammo — confirmed STATISTICALLY over many kills
///       (measured ≈ 20% per category), with each surviving roll producing a REAL
///       <see cref="Treasure"/> in the correct sector at the correct part-type id /
///       amount and a position near the death spot;
///   (2) a ship with ZERO ammo never spawns an ammo treasure (native
///       <c>if (ammo &gt; 0)</c> guard);
///   (3) a CHILD/TURRET ship's death does NOT independently roll loot
///       (parent-ship-only guard);
///   (4) LIVE: a real kill's dropped part treasure is ACTUALLY PICKABLE — a
///       rescue-capable ship flown onto it receives ACQUIRE_TREASURE and the
///       treasure is destroyed (the P145 pickup flow).
/// Fuelless caveat (faithful, not a deviation): this port tracks no runtime fuel
/// pool, so native's <c>if (fuel &gt; 0)</c> guard is never satisfied — no fuel
/// treasure ever spawns. See <c>P411_KILL_SHIP_LOOT_DROPS.md</c>.
/// </summary>
[Collection("LiveNetwork")]
public class KillShipLootDropTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const short FlySector = 1052;   // XXV_simple: no-station fly sector (P111 report)

    private static readonly Lazy<StaticCore> CoreLazy = new(() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"))));

    private static StaticCore Core() => CoreLazy.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Kill Loot Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 711u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 5711000000000000000ul, DrmHash = "killloot411",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A fight-able hull with ≥1 fixed weapon hardpoint that actually
    /// hosts some weapon in the representative core (so a part can be mounted and
    /// the loot treasure's part-id resolves).</summary>
    private static readonly Lazy<DataHullTypeIGC> FightHull = new(() =>
    {
        var core = Core();
        return core.HullTypes.Values
            .OrderBy(h => h.HullId)
            .First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord)
                     && h.Hardpoints.Length >= 1
                     && h.Hardpoints[0].Fixed
                     && core.PartTypes.Values.Any(p => p.EquipmentType == EquipmentType.Weapon
                                                     && p.PartId != 0
                                                     && (p.PartMask & ~h.Hardpoints[0].PartMask) == 0));
    });

    /// <summary>A real weapon part id that fits the fight hull's fixed hardpoint 0.</summary>
    private static readonly Lazy<short> WeaponPid = new(() =>
    {
        short hp0 = FightHull.Value.Hardpoints[0].PartMask;
        var w = Core().PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && p.PartId != 0)
            .OrderBy(p => p.PartId)
            .FirstOrDefault(p => (p.PartMask & ~hp0) == 0);
        if (w.PartId == 0)
            throw new InvalidOperationException("need a weapon fitting hardpoint 0 in representative core");
        return w.PartId;
    });

    /// <summary>The mission's ammo-pack part type id (native <c>GetAmmoPack()</c>;
    /// <c>Pack.PackType == c_packAmmo = 0</c>, igc.h:549). Present in PCore001 (partId 24).</summary>
    private static short AmmoPackId() =>
        Core().PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0).PartId;

    /// <summary>P412 — the mission's fuel-pack part type id (native
    /// <c>GetFuelPack()</c>; <c>Pack.PackType == c_packFuel = 1</c>, igc.h:550).
    /// The P411 fuel branch looks this up by the same PackType filter.</summary>
    private static short FuelPackId() =>
        Core().PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 1).PartId;

    private static readonly MethodInfo? BroadcastKillShip = typeof(Srv).GetMethod(
        "BroadcastKillShip", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Drive the real server death path (BroadcastKillShip) for a placed
    /// victim directly, exactly like the P278/P215 live suites' FireKill. The loot
    /// block runs at the top of the method, before any peer/eject logic, so a
    /// victim needs no owning peer for its loot to drop.</summary>
    private static void FireKill(Srv server, Ship victim, double now = 1000.0)
    {
        BroadcastKillShip!.Invoke(server, new object?[]
        {
            now, victim, null, 1.0f, new Vector3(0, 0, 0), new Vector3(0, 0, 0),
        });
    }

    private static (Srv server, Mission mission, Sector sector) BootEmpty()
    {
        var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        var sector = mission.Sectors.First(s => s.Id == FlySector);
        return (server, mission, sector);
    }

    /// <summary>Create a fresh victim: fight hull, ONE mounted weapon part (the
    /// same part id every trial — so part-treasures are countable by id), a chosen
    /// ammo level, at the origin (so treasure positions are checkable).</summary>
    private static Ship MakeVictim(Mission mission, Sector sector, short id, short ammo, float fuel = -1f)
    {
        var v = new Ship(id, FightHull.Value, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 871 };
        mission.PlaceShip(v, sector);
        v.MountPart(Core().PartTypes[WeaponPid.Value], 1, 0, ExpandedPartData.FullyMounted);
        v.Ammo = ammo;
        if (fuel >= 0f) v.SetFuel(fuel);   // P412: default keeps the seeded MaxFuel
        return v;
    }

    // ── (1) STATISTICAL: parts and ammo each drop at ~1-in-5 ──────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kill_loot_parts_and_ammo_drop_at_one_in_five_odds_statistically()
    {
        Require.Artwork();
        var (server, mission, sector) = BootEmpty();
        using (server)
        {
            short weaponPid = WeaponPid.Value;
            short ammoPackId = AmmoPackId();
            const short Ammo = 480;
            const int N = 400;

            for (int i = 0; i < N; i++)
                FireKill(server, MakeVictim(mission, sector, (short)(9800 + i), Ammo));

            var treasures = sector.Models.OfType<Treasure>().ToList();
            var partDrops = treasures.Where(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == weaponPid).ToList();
            var ammoDrops = treasures.Where(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == ammoPackId && t.Data.Amount == Ammo).ToList();

            // Each surviving roll must be a REAL Treasure in the right sector, with
            // the correct part-type id + amount, positioned near the death spot (0,0,0).
            float maxDist = 0.5f * FightHull.Value.Length + 10.0f + 0.01f;
            foreach (var t in partDrops)
            {
                Assert.Equal(FlySector, t.Data.ClusterId);
                Assert.Equal((short)1, t.Data.Amount);                       // the mounted weapon's amount
                Assert.True((new Vector3(t.Data.P0[0], t.Data.P0[1], t.Data.P0[2])).Length() <= maxDist,
                    "part treasure must sit near the death position");
            }
            foreach (var t in ammoDrops)
            {
                Assert.Equal(FlySector, t.Data.ClusterId);
                Assert.Equal(Ammo, t.Data.Amount);                           // the ship's remaining ammo
                Assert.True((new Vector3(t.Data.P0[0], t.Data.P0[1], t.Data.P0[2])).Length() <= maxDist,
                    "ammo treasure must sit near the death position");
            }

            double partRatio = (double)partDrops.Count / N;
            double ammoRatio = (double)ammoDrops.Count / N;
            Console.WriteLine($"P411-ODDS: N={N} victims; part drops={partDrops.Count} ({partRatio:P1}), " +
                              $"ammo drops={ammoDrops.Count} ({ammoRatio:P1}); expected ~20% each");

            // Expected 20% per category; assert the measured RATIO in [0.12, 0.29]
            // (~4σ band around 0.20 for N=400) — statistically inconsequential to
            // flake while still conclusively demonstrating the ~1-in-5 roll (>10σ
            // from 0 and from any "guessed similar" probability like 50%).
            Assert.InRange(partRatio, 0.12, 0.29);
            Assert.InRange(ammoRatio, 0.12, 0.29);
        }
    }

    // ── (2) ZERO AMMO: never spawns an ammo treasure ──────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kill_loot_zero_ammo_never_spawns_ammo_treasure()
    {
        Require.Artwork();
        var (server, mission, sector) = BootEmpty();
        using (server)
        {
            short ammoPackId = AmmoPackId();
            const int N = 40;
            for (int i = 0; i < N; i++)
                FireKill(server, MakeVictim(mission, sector, (short)(9880 + i), ammo: 0));

            // Ammo is 0 on every victim, so native's `if (ammo > 0)` guard (FedSrv.CPP:11702)
            // is never entered — deterministically ZERO ammo treasures, whatever the RNG.
            int ammoDrops = sector.Models.OfType<Treasure>()
                .Count(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == ammoPackId);
            Assert.Equal(0, ammoDrops);
            Console.WriteLine($"P411-ZEROAMMO: N={N} zero-ammo kills -> {ammoDrops} ammo treasures (guard holds)");
        }
    }

    // ── (2b) ZERO AMMO guard, then the P412 FUEL drops ───────────────────────

    // ── FUEL: rolls ~1-in-5 when fuel > 0 ────────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kill_loot_fuel_drops_at_one_in_five_odds_statistically()
    {
        Require.Artwork();
        var (server, mission, sector) = BootEmpty();
        using (server)
        {
            short fuelPackId = FuelPackId();
            // Every victim carries a real, non-zero fuel pool (the fight hull's
            // FULL tank = its MaxFuel), so native's `if (fuel > 0)` guard
            // (FedSrv.CPP:11717) is entered on every kill and a fuel treasure
            // rolls at ~1-in-5 — same odds as parts/ammo.
            short fuelAmt = (short)FightHull.Value.MaxFuel;   // the real tank size
            const int N = 400;
            for (int i = 0; i < N; i++)
                FireKill(server, MakeVictim(mission, sector, (short)(9900 + i), ammo: 0, fuel: fuelAmt));

            var treasures = sector.Models.OfType<Treasure>().ToList();
            var fuelDrops = treasures
                .Where(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == fuelPackId)
                .ToList();

            // Each surviving roll is a REAL Treasure in the right sector, with the
            // fuel-pack part-type id and the ship's remaining fuel (cast to short,
            // FedSrv.CPP:11715), positioned near the death spot.
            float maxDist = 0.5f * FightHull.Value.Length + 10.0f + 0.01f;
            foreach (var t in fuelDrops)
            {
                Assert.Equal(FlySector, t.Data.ClusterId);
                Assert.Equal(fuelAmt, t.Data.Amount);                        // short(fuel)
                Assert.True((new Vector3(t.Data.P0[0], t.Data.P0[1], t.Data.P0[2])).Length() <= maxDist,
                    "fuel treasure must sit near the death position");
            }

            double fuelRatio = (double)fuelDrops.Count / N;
            Console.WriteLine($"P412-FUELODDS: N={N} victims with fuel; fuel drops={fuelDrops.Count} ({fuelRatio:P1}); expected ~20%");
            Assert.InRange(fuelRatio, 0.12, 0.29);
        }
    }

    // ── FUEL: zero fuel never spawns a fuel treasure ─────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kill_loot_zero_fuel_never_spawns_fuel_treasure()
    {
        Require.Artwork();
        var (server, mission, sector) = BootEmpty();
        using (server)
        {
            short fuelPackId = FuelPackId();
            const int N = 40;
            for (int i = 0; i < N; i++)
                FireKill(server, MakeVictim(mission, sector, (short)(9980 + i), ammo: 0, fuel: 0f));

            // Fuel is 0 on every victim, so native's `if (fuel > 0)` guard is never
            // entered — deterministically ZERO fuel treasures, whatever the RNG.
            int fuelDrops = sector.Models.OfType<Treasure>()
                .Count(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == fuelPackId);
            Assert.Equal(0, fuelDrops);
            Console.WriteLine($"P412-ZEROFUEL: N={N} zero-fuel kills -> {fuelDrops} fuel treasures (guard holds)");
        }
    }

    // ── (3) CHILD SHIP: parent-ship-only guard ────────────────────────────────
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kill_loot_child_ship_does_not_independently_drop_loot()
    {
        Require.Artwork();
        var (server, mission, sector) = BootEmpty();
        using (server)
        {
            short weaponPid = WeaponPid.Value;
            short ammoPackId = AmmoPackId();
            // A real parent ship to make the victims' ParentShip non-null.
            var parent = new Ship(9300, FightHull.Value, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 872 };
            mission.PlaceShip(parent, sector);

            const int N = 40;
            for (int i = 0; i < N; i++)
            {
                var child = MakeVictim(mission, sector, (short)(9380 + i), ammo: 640);
                child.ParentShip = parent;   // turret/child — NOT a root ship
                FireKill(server, child);
            }

            // Root guard (ParentShip is null) skips the whole loot block, so a
            // parented death rolls NOTHING — deterministic zero, even though each
            // child carried a part + ammo (>0) that WOULD have rolled had the guard
            // failed. Near-certain to fail loudly if the guard is ever removed.
            int partDrops = sector.Models.OfType<Treasure>()
                .Count(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == weaponPid);
            int ammoDrops = sector.Models.OfType<Treasure>()
                .Count(t => t.Data.TreasureCode == TreasureCode.Part && t.Data.TreasureId == ammoPackId);
            Assert.Equal(0, partDrops + ammoDrops);
            Console.WriteLine($"P411-CHILD: N={N} parented kills -> {partDrops} part + {ammoDrops} ammo treasures (parent-ship guard holds)");
        }
    }

    // ── (4) LIVE PICKUP: a real kill's loot is actually pickable ──────────────
    private sealed class PickupHolder { public Ship Pickup = null!; }

    private static PickupHolder InjectPickup(Srv server, Vector3 pos)
    {
        var holder = new PickupHolder();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            // The single bot is the rescue-capable picker (HabmRescue) at `pos`.
            var hull = FightHull.Value with { HabmCapabilities = DataHullTypeIGC.HabmRescue };
            var ship = new Ship(shipId, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = 773 };
            mission.PlaceShip(ship, fly);
            holder.Pickup = ship;
            return (ship, fly);
        };
        return holder;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_kill_loot_is_actually_pickable_by_a_rescue_ship()
    {
        Require.Artwork();
        var server = new Srv();
        var holder = InjectPickup(server, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var bots = Seat(server, "LOOTPICKER");
        using (server)
        using (bots[0])
        {
            var pickup = holder.Pickup;
            var mission = server.Missions[0];
            var sector = mission.Sectors.First(s => s.Id == FlySector);
            Assert.NotNull(pickup);

            const int Attempts = 80;
            int made = 0;
            for (int i = 0; i < Attempts; i++)
            {
                var victim = MakeVictim(mission, sector, (short)(9450 + i), ammo: 512);
                FireKill(server, victim);

                // A real part-treasure (part or ammo) dropped somewhere near the
                // death spot; fly the picker onto it so the P145 pickup pass grabs it.
                var drop = sector.Models.OfType<Treasure>()
                    .FirstOrDefault(t => t.Data.TreasureCode == TreasureCode.Part);
                if (drop is null) continue;

                pickup.Position = new Vector3(drop.Data.P0[0], drop.Data.P0[1], drop.Data.P0[2]);
                int p = PumpUntil(server, () => bots[0].AcquireTreasures.Count >= 1, 500);
                if (bots[0].AcquireTreasures.Count >= 1) { made = i + 1; break; }
            }

            Assert.True(made > 0, $"a destroyed ship's loot must be pickable within {Attempts} kill attempts");
            var acq = Assert.Single(bots[0].AcquireTreasures);
            Assert.Equal((byte)TreasureCode.Part, acq.TreasureCode);
            Console.WriteLine($"P411-LIVE: after {made} kills, a destroyed ship's part/ammo loot was picked up live " +
                              $"(ACQUIRE_TREASURE code={acq.TreasureCode} id={acq.TreasureId} amount={acq.Amount})");
        }
    }

    private static FltBot[] Seat(Srv server, params string[] names)
    {
        var bots = names.Select(name => { var b = new FltBot(); b.Connect(server.BoundPort, name); b.SendLogonRequest(Request(name)); return b; }).ToArray();
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 6000);
        return bots;
    }
}
