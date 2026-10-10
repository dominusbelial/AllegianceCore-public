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
/// P334 — real server-side hull swap (Stage 1 of the hangar/loadout plan).
/// BUY_LOADOUT is dispatched to native <c>CfsShip::PurchaseShipLoadout</c>
/// (FedSrv.CPP:6189), which mutates the EXISTING ship object IN PLACE via
/// <c>SetBaseHullType(pht)</c> (shipIGC.h:1019-1020) — it does NOT
/// retire-and-recreate. The port mirrors that with <c>Ship.ChangeHull</c>
/// (port of <c>CshipIGC::SetBaseHullType</c>, shipIGC.cpp:1352), so the
/// server's own simulated physics (mass/turn-rates/torques/thrust/speed/
/// hitpoints — all live getters over <c>_hull</c>) reflect the new hull
/// immediately. These tests prove it on the SERVER side (reading
/// <c>Server.Ships</c> directly), not just from the client's cosmetic echo.
///
/// Hardpoint policy: native PurchaseShipLoadout only mounts a part the (new)
/// hull can host — <c>m_myHullType.CanMount(ppt, mountID)</c> (shipIGC.h:1030);
/// a part with nowhere to mount is simply not mounted. The port gates the
/// mount loop with <c>DataHullTypeIGC.CanMountPart</c> (the P276 port of that
/// same test). Test 2 exercises that policy firing on a real swap.
/// </summary>
[Collection("LiveNetwork")]
public class HullSwapLiveTests
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
            new Gm { Name = "Hull Swap Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 490u, DwCookie = 0x334u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 83400000000000000ul, DrmHash = "hullswap",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static (Ship Ship, Sector Sector) SpawnUndocked(Mission mission, short shipId, out DataStationIGC station)
    {
        // P441: the hull-swap tests exercise swap MECHANICS (in-place hull
        // change, physics, part-drop), not shop/tech legality — so the ship is
        // spawned UNDOCKED in a station-less sector (no station ⇒ no tech gate),
        // and the wallet is funded so the unconditional P441 pricing never
        // refuses the swap. Seat it on the garrison's side so it's a real member.
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => !s.Stations.Any(ss => ss.SideId >= 0));
        var ship = new Ship(shipId, hull, new Vector3(0f, 942f, 968f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        station = st;
        return (ship, sector);
    }

    /// <summary>⭐ Live proof of the real swap: a docked bot sends BUY_LOADOUT
    /// naming a genuinely DIFFERENT hull, and the SERVER's own Ship object
    /// immediately simulates the new hull — HullId, Mass, HitPoints, Speed,
    /// Thrust and turn-rate/torque vectors all change to the new hull's
    /// values, and the construction-seeded pools are re-seeded to the new
    /// hull's max (Energy/Ammo). Before P334 the ship kept simulating the OLD
    /// hull while only the ACK's echoed hullId changed — this assert would be
    /// red on that code.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuyLoadout_with_a_different_hull_swaps_the_server_simulated_physics_live()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) => SpawnUndocked(mission, shipId, out _);

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "swap_probe");
        bot.SendLogonRequest(Request("swap_probe"));

        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        short shipId = bot.ShipAssignment!.Ship.ShipId;
        var ship = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money = 1_000_000;   // P441: fund the swap

        // Pick a genuinely different hull (highest HullId vs the spawn's lowest).
        var mission = ship.Mission!;
        var targetHull = mission.Core.HullTypes.Values.OrderByDescending(h => h.HullId).First();
        Assert.NotEqual(ship.HullType.HullId, targetHull.HullId);
        Assert.NotEqual(ship.Mass, targetHull.Mass);   // sanity: the two hulls really differ physically

        float massBefore = ship.Mass;
        float hpBefore = ship.HitPoints;
        float speedBefore = ship.HullType.Speed;
        float thrustBefore = ship.HullType.Thrust;
        var turnRatesBefore = (float[])ship.HullType.MaxTurnRates.Clone();
        var torquesBefore = (float[])ship.HullType.TurnTorques.Clone();

        // The real client's Launch-screen purchase shape for a hull change.
        bot.SendBuyLoadout(targetHull.HullId, fLaunch: false);

        int swapped = PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.HullType.HullId == targetHull.HullId, 4000);
        Assert.True(swapped < 4000, $"server ship never adopted the new hull (still {ship.HullType.HullId})");

        var s = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        // THE server-side proof: simulated physics now reflect the NEW hull.
        Assert.Equal(targetHull.HullId, s.HullType.HullId);
        Assert.Equal(targetHull.Mass, s.Mass);
        // P467b: Ship.HitPoints is the GA-scaled absolute = Fraction × effective
        // max hit points (native IshipIGC::GetHitPoints), so the adopted hull's
        // absolute HP equals the raw record × the side's c_gaMaxArmorShip base.
        Assert.Equal(targetHull.HitPoints * s.GlobalAttributeFactor(GlobalAttributeSet.MaxArmorShip), s.HitPoints, 3);
        Assert.Equal(targetHull.Speed, s.HullType.Speed);
        Assert.Equal(targetHull.Thrust, s.HullType.Thrust);
        Assert.Equal(targetHull.MaxTurnRates, s.HullType.MaxTurnRates);
        Assert.Equal(targetHull.TurnTorques, s.HullType.TurnTorques);
        // And they really CHANGED (not vacuously equal).
        Assert.NotEqual(massBefore, s.Mass);
        Assert.NotEqual(hpBefore, s.HitPoints);
        Assert.NotEqual(speedBefore, s.HullType.Speed);
        Assert.NotEqual(thrustBefore, s.HullType.Thrust);
        Assert.NotEqual(turnRatesBefore, s.HullType.MaxTurnRates);
        Assert.NotEqual(torquesBefore, s.HullType.TurnTorques);
        // The construction-seeded pools are re-seeded to the NEW hull's max
        // (the port has no passive energy regen, so full-start is the
        // functional equivalent of native's zero-then-recharge; Ship.cs:152-153).
        // P467b: the reseed uses the GA-scaled effective ceiling.
        Assert.Equal(s.EffectiveMaxEnergy(), s.Energy);
        Assert.Equal(targetHull.MaxAmmo, s.Ammo);
        // The same Ship object was mutated in place — it did NOT retire-and-recreate.
        Assert.Same(ship, s);
    }

    /// <summary>⭐ The mounted-part policy: swapping into a hull whose hardpoint
    /// layout cannot host an already-mounted part must DROP that part (native
    /// only mounts what the new hull CanMount, shipIGC.h:1030) rather than
    /// silently corrupting state. Here the same part mounts fine on the spawn
    /// hull, then a hull change to an incompatible hull drops it from the ACK
    /// tail — the server's real mounted parts reflect the policy.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_change_into_an_incompatible_hull_drops_nonmountable_parts_live()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) => SpawnUndocked(mission, shipId, out _);

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "drop_probe");
        bot.SendLogonRequest(Request("drop_probe"));

        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        short shipId = bot.ShipAssignment!.Ship.ShipId;
        var ship = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money = 1_000_000;   // P441: fund the swaps
        var mission = ship.Mission!;

        // Precondition: pick a (part, fitHull, badHull) triple from the REAL
        // core where the part mounts on fitHull but NOT on badHull — so the
        // swap onto badHull genuinely drops it. Independent of the spawn hull:
        // step 1 first swaps the docked ship onto fitHull (where the part
        // mounts), step 2 then swaps onto badHull (where it is dropped).
        DataPartTypeIGC droppingPart = default;
        DataHullTypeIGC fitHull = default, incompatibleHull = default;
        bool found = false;
        foreach (var p in mission.Core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon))
        {
            DataHullTypeIGC? a = null, b = null;
            foreach (var h in mission.Core.HullTypes.Values)
            {
                if (h.CanMountPart(p)) a ??= h;
                else b ??= h;
            }
            if (a.HasValue && b.HasValue)
            {
                droppingPart = p;
                fitHull = a.Value;
                incompatibleHull = b.Value;
                found = true;
                break;
            }
        }
        Assert.True(found, "no (part, fits-hull, nonfits-hull) triple exists in PCore001");

        // (1) Swap onto fitHull with the part in the request — it mounts
        //     (that hull CanMount it): the ACK tail has it.
        bot.SendBuyLoadout(fitHull.HullId, fLaunch: false, parts: new[] { new ExpandedPartData(droppingPart.PartId, 20, 0, ExpandedPartData.FullyMounted) });
        int mounted = PumpUntil(server,
            () => server.Ships.First(s => s.Ship.Id == shipId).Ship.HullType.HullId == fitHull.HullId
                  && server.Ships.First(s => s.Ship.Id == shipId).Ship.Parts.Count == 1, 4000);
        Assert.True(mounted < 4000, "baseline part did not mount on fitHull");
        Assert.Equal(droppingPart.PartId, server.Ships.First(s => s.Ship.Id == shipId).Ship.Parts[0].PartId);

        // (2) Swap onto badHull with the SAME part in the request. The hull
        //     swaps (in place), the part is DROPPED by the CanMount gate.
        bot.SendBuyLoadout(incompatibleHull.HullId, fLaunch: false, parts: new[] { new ExpandedPartData(droppingPart.PartId, 20, 0, ExpandedPartData.FullyMounted) });
        int dropped = PumpUntil(server,
            () => server.Ships.First(s => s.Ship.Id == shipId).Ship.HullType.HullId == incompatibleHull.HullId
                  && server.Ships.First(s => s.Ship.Id == shipId).Ship.Parts.Count == 0, 4000);
        Assert.True(dropped < 4000, "hull did not swap to the incompatible hull and/or the nonmountable part was not dropped");

        var s = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(incompatibleHull.HullId, s.HullType.HullId);
        Assert.Empty(s.Parts);                 // the non-mountable part is gone — the policy fired
        Assert.Equal(incompatibleHull.MaxEnergy, s.Energy);   // pool re-seeded to the new hull (not corrupted)
    }
}
