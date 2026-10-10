using System.Linq;
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
using Tu = global::Allegiance.Server.TestUnlockConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P509 — the <c>liveSettings.testUnlock</c> grant: every side gets the core's
/// required-tech union through the real tech path (Side.DevelopmentTechs, so
/// the server's buy-gate AND the client shop agree), and every seated player
/// wallet gets money. Native has no "unlock all" (its mission params only
/// restrict tech — sideigc.cpp:117, asteroidigc.cpp:183-195), so this is a PORT
/// EXTENSION for testing, explicit and off by default, in the same spirit as
/// P473's testHarness.
/// <para>Live loopback proofs over a real in-process server + a real TCP Bot:
///   (A) option ON: the side's DevelopmentTechs ⊇ the required-tech union; the
///       Bot's wallet = money at seat; the Bot buys a real tech-gated
///       capital-class hull AND a gated weapon through BUY_LOADOUT, both are
///       granted, and the wallet is charged the real price through the real
///       debit path.
///   (B) option OFF: the same side keeps its starter dev techs (union NOT
///       granted) and the same capital hull is refused — the option's only
///       effect is the explicit config.</summary>
[Collection("LiveNetwork")]
public sealed class TestUnlockGrantLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const int GrantMoney = 1_000_000;

    private static Cfg UnlockConfig() => BaseConfig(new Ls { TestUnlock = new Tu { Enabled = true, AllTech = true, Money = GrantMoney } });

    private static Cfg NoUnlockConfig() => BaseConfig(null);

    private static Cfg BaseConfig(Ls? liveSettings) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = liveSettings ?? new Ls(),
        Games =
        [
            new Gm { Name = "P509 Test Unlock", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 973u, DwCookie = 0x973u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9199000000000000000ul, DrmHash = "unlock509",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Push every mission's next-payday to the far future so the P197
    /// payday credit can't perturb exact wallet assertions (same helper as the
    /// P473 harness test).</summary>
    private static void StopPayday(Srv server)
    {
        var prop = typeof(Mission).GetProperty("NextPaydayTime", BindingFlags.Instance | BindingFlags.NonPublic)
                   ?? throw new InvalidOperationException("Mission.NextPaydayTime property not found");
        foreach (var m in server.Missions)
            prop.SetValue(m, double.MaxValue);
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static Ship LiveShip(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static int WalletOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money;

    /// <summary>The (station type, side) of the first friendly garrison — the
    /// docked tech-gate context (same helper as BuyLoadoutPricingLiveTests).</summary>
    private static (DataStationTypeIGC st, Side side) Garrison(Mission mission)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        if (!mission.Core.StationTypes.TryGetValue(garrison.StationTypeId, out var st))
            throw new InvalidOperationException("garrison station type unresolvable");
        var side = mission.Sides.First(s => s.Id == garrison.SideId);
        return (st, side);
    }

    private static bool SellsAt(DataStationTypeIGC st, TechTreeBitMask dev, DataHullTypeIGC hull) =>
        TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, st.TtbmLocal, dev);

    /// <summary>The <c>TtbmRequired</c>-gated WEAPON parts (EquipmentType.Weapon)
    /// that are buyable with the full union but NOT with the starter dev techs,
    /// and that have a compatible hardpoint on <paramref name="hull"/>. Ordered
    /// by PartId. Empty when the hull has no weapon-mountable hardpoint.</summary>
    private static IReadOnlyList<DataPartTypeIGC> GatedWeaponsFor(Mission mission, DataHullTypeIGC hull)
    {
        var (st, _) = Garrison(mission);
        var starterDev = TechTreeBitMask.FromBytes(mission.Sides.First().Civilization.TtbmBaseTechs);
        var union = mission.Core.ComputeRequiredTechUnion();
        return mission.Core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon
                        && !TechTreeBitMask.RequiredTechsBuyable(p.TtbmRequired, st.TtbmLocal, starterDev)
                        && TechTreeBitMask.RequiredTechsBuyable(p.TtbmRequired, st.TtbmLocal, union)
                        && hull.CanMountPart(p))
            .OrderBy(p => p.PartId)
            .ToList();
    }

    /// <summary>A tech-gated <b>capital-class</b> hull: a real player-flyable
    /// hull (GroupId &gt;= 0), terminal (SuccessorHullId == NA, so P488 buys it
    /// EXACTLY, no successor swap), priced &gt; 0 (observable debit), NOT sellable
    /// at the garrison with the side's starter dev techs, but sellable with the
    /// full required-tech union — AND able to mount at least one gated weapon
    /// (so one BUY_LOADOUT can grant hull + weapon together). Highest price
    /// wins (a real capital — e.g. Flagship.4 at 10 000 cr).</summary>
    private static DataHullTypeIGC TechGatedCapitalHull(Mission mission)
    {
        var (st, _) = Garrison(mission);
        var starterDev = TechTreeBitMask.FromBytes(mission.Sides.First().Civilization.TtbmBaseTechs);
        var union = mission.Core.ComputeRequiredTechUnion();
        return mission.Core.HullTypes.Values
            .Where(h => h.GroupId >= 0 && h.Price > 0 && h.SuccessorHullId == -1
                        && !SellsAt(st, starterDev, h)          // gated at starter
                        && SellsAt(st, union, h)                // unlocked by the grant
                        && GatedWeaponsFor(mission, h).Count > 0)
            .OrderByDescending(h => h.Price)
            .First();
    }

    /// <summary>The first (lowest PartId) gated weapon that fits the chosen
    /// capital hull — the gated weapon the Bot buys alongside the capital.</summary>
    private static DataPartTypeIGC TechGatedWeapon(Mission mission, DataHullTypeIGC capital) =>
        GatedWeaponsFor(mission, capital).First();

    private sealed class Harness : IDisposable
    {
        public Srv Server = null!;
        public FltBot Bot = null!;
        public short BotId;
        public void Dispose() { Server.Dispose(); Bot.Dispose(); }
    }

    /// <summary>Boot the server (with the given config), start every mission,
    /// stop payday, and seat ONE real TCP Bot on a sellable starter hull,
    /// docked at the friendly garrison (so the BUY_LOADOUT tech gate runs).</summary>
    private static Harness BootDocked(Cfg config)
    {
        var h = new Harness { Server = new Srv() };
        h.Server.Boot(config);
        foreach (var m in h.Server.Missions) m.Stage = MissionStage.Started;
        StopPayday(h.Server);

        var (gSt, gSide) = Garrison(h.Server.Missions[0]);
        short spawnHullId = h.Server.Missions[0].Core.HullTypes.Values
            .Where(hh => SellsAt(gSt, TechTreeBitMask.FromBytes(gSide.Civilization.TtbmBaseTechs), hh))
            .OrderBy(hh => hh.Price).First().HullId;
        var station = h.Server.Missions[0].Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var sector = h.Server.Missions[0].Sectors.First(s => s.Stations.Contains(station));
        h.Server.ShipSpawner = (core, mission, id) =>
        {
            var ship = new Ship(id, core.HullTypes[spawnHullId],
                StationPos(station) + new Vector3(25f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        h.Bot = new FltBot();
        h.Bot.Connect(h.Server.BoundPort, "unlock-bot");
        h.Bot.SendLogonRequest(Request("unlock-bot"));
        int seat = PumpUntil(h.Server, () => h.Bot.ShipAssignment is not null, 6000);
        if (seat >= 6000) throw new InvalidOperationException("bot did not seat");
        h.BotId = h.Bot.ShipAssignment!.Ship.ShipId;

        int dock = PumpUntil(h.Server, () => LiveShip(h.Server, h.BotId).StationId is not null, 8000);
        if (dock >= 8000) throw new InvalidOperationException("bot never latched docked");
        return h;
    }

    /// <summary>⭐ Option ON — the side has the granted union, the wallet got
    /// money at seat, and the docked Bot buys a real tech-gated capital-class
    /// hull + a gated weapon through BUY_LOADOUT: both granted, wallet charged
    /// the real price.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GrantOn_SideHasUnion_BotBuysCapitalAndGatedWeapon_ChargedRealPrice()
    {
        Require.Artwork();
        using var h = BootDocked(UnlockConfig());
        short botId = h.BotId;
        var mission = h.Server.Missions[0];
        var (st, side) = Garrison(mission);
        var union = mission.Core.ComputeRequiredTechUnion();

        // (1) The side REALLY has the granted tech through the real tech path.
        Assert.True(union.IsSubsetOf(side.DevelopmentTechs),
            $"side {side.Id} dev mask must contain the required-tech union ({union.PopCount} bits)");
        Console.WriteLine($"UNLOCK-ON-LIVE: side {side.Id} '{side.Name}' dev mask {side.DevelopmentTechs.PopCount} bits ⊇ union {union.PopCount} bits (starter was {TechTreeBitMask.FromBytes(side.Civilization.TtbmBaseTechs).PopCount})");

        // (2) The seated wallet got money at seat.
        Assert.Equal(GrantMoney, WalletOf(h.Server, botId));

        // (3) Pick the fixtures and verify the tech-gate premise is real.
        var capital = TechGatedCapitalHull(mission);
        var starterDev = TechTreeBitMask.FromBytes(side.Civilization.TtbmBaseTechs);
        Assert.False(SellsAt(st, starterDev, capital), "fixture capital must be gated at starter");
        Assert.True(SellsAt(st, union, capital), "fixture capital must be sellable with the union");
        var weapon = TechGatedWeapon(mission, capital);
        Assert.False(TechTreeBitMask.RequiredTechsBuyable(weapon.TtbmRequired, st.TtbmLocal, starterDev), "fixture weapon must be gated at starter");
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(weapon.TtbmRequired, st.TtbmLocal, union), "fixture weapon must be buyable with the union");
        short beforeHull = LiveShip(h.Server, botId).HullType.HullId;
        Assert.NotEqual(capital.HullId, beforeHull);

        // (4) The Bot buys the capital hull + the gated weapon through BUY_LOADOUT.
        h.Bot.SendBuyLoadout(capital.HullId, fLaunch: false,
            parts: [new ExpandedPartData(weapon.PartId, 1, MountId: 0, FractionMounted: 1)]);
        int applied = PumpUntil(h.Server, () => LiveShip(h.Server, botId).HullType.HullId == capital.HullId, 6000);
        Assert.True(applied < 6000, $"bot never bought capital hull {capital.HullId} (testUnlock should grant it)");
        Assert.Equal(capital.HullId, LiveShip(h.Server, botId).HullType.HullId);

        // (5) The gated weapon is mounted (granted through the part gate).
        var ship = LiveShip(h.Server, botId);
        Assert.NotEmpty(ship.Parts);
        var grantedWeapon = ship.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);
        Assert.True(TechTreeBitMask.RequiredTechsBuyable(grantedWeapon.Type.TtbmRequired, st.TtbmLocal, side.DevelopmentTechs),
            $"mounted weapon {grantedWeapon.Type.PartId} '{grantedWeapon.Type.Name}' must be buyable with the granted union");

        // (6) Charged the REAL price through the real debit path: wallet fell by
        // ShipValue(new) - ShipValue(old) = capital price + mounted part prices
        // (the sellable starter spawn hull is price 0 with no parts).
        int debit = capital.Price + ship.Parts.Sum(p => p.Type.Price);
        Assert.True(debit > 0, "expected a real, positive debit");
        Assert.Equal(GrantMoney - debit, WalletOf(h.Server, botId));

        Console.WriteLine($"UNLOCK-ON-LIVE: bot {botId} bought capital {capital.HullId}('{capital.Name}',{capital.Price}) + gated weapon {grantedWeapon.Type.PartId}('{grantedWeapon.Type.Name}',{grantedWeapon.Type.Price}) at garrison -> wallet {GrantMoney}->{WalletOf(h.Server, botId)} (debit {debit})");
    }

    /// <summary>⭐ Option OFF (absent section) — the same side keeps its starter
    /// dev techs (union NOT granted) and the same capital hull is refused: the
    /// option's only effect is the explicit config.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GrantOff_SideKeepsStarterTechs_SameCapitalRefused()
    {
        Require.Artwork();
        using var h = BootDocked(NoUnlockConfig());
        short botId = h.BotId;
        var mission = h.Server.Missions[0];
        var (_, side) = Garrison(mission);
        var union = mission.Core.ComputeRequiredTechUnion();

        // (1) The side's dev mask is the starter set — the union is NOT granted.
        Assert.False(union.IsSubsetOf(side.DevelopmentTechs),
            "with the option off the side must NOT contain the required-tech union");

        // (2) No money grant at seat.
        Assert.Equal(0, WalletOf(h.Server, botId));

        // (3) The same capital hull is refused (ship unchanged).
        var capital = TechGatedCapitalHull(mission);
        short beforeHull = LiveShip(h.Server, botId).HullType.HullId;
        h.Bot.SendBuyLoadout(capital.HullId, fLaunch: false);
        for (int i = 0; i < 1500; i++) h.Server.TickSim();
        Assert.Equal(beforeHull, LiveShip(h.Server, botId).HullType.HullId);
        Assert.Equal(0, WalletOf(h.Server, botId));
        Console.WriteLine($"UNLOCK-OFF-LIVE: bot {botId} tried capital {capital.HullId} with NO testUnlock -> refused, hull {beforeHull} unchanged, wallet 0");
    }
}
