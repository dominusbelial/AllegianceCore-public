using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P434 — LIVE proof that a real missile launcher is a real, buyable,
/// equippable shop row whose purchase produces the exact real loadout a
/// P433-equipped bot carries. Reproduces the HUMAN shop sequence exactly the
/// way the player performs it on a dock — cursor onto a [LAUNCHER] row, SPACE
/// (<see cref="ShopModel.Activate"/>) to toggle it into the pending loadout,
/// L (<c>SendBuyLoadout(fLaunch:true, parts: ChosenParts)</c>) to launch — over
/// a REAL in-process server + real <see cref="ConnectionSession"/> wire
/// round-trips, and asserts the authoritative BUY_LOADOUT_ACK-granted loadout
/// carries the launcher with its real ammo (the same real verification P433
/// used to prove a bot's loadout carries a launcher).
///
/// The ship is placed inside a friendly station's DockRadius (the established
/// docked-spawner arrangement, so the real dock-radius pass latches
/// <c>StationId</c> and the shop screen is live), mirroring
/// <see cref="P394ShopPreSeedLiveTests"/>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ShopLauncherBuyLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P434 Launcher Buy", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 98900000000000004ul, DrmHash = "p434",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    /// <summary>P441 — fund a peer's wallet via reflection (Server.Ships is
    /// internal and invisible to this assembly).</summary>
    private static void Fund(Srv server, short shipId, int amount)
    {
        var field = typeof(Srv).GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dict = (System.Collections.IDictionary)field.GetValue(server)!;
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            var live = e.Value!;
            var ship = (Ship)live.GetType().GetProperty("Ship")!.GetValue(live)!;
            if (ship.Id != shipId) continue;
            var wallet = live.GetType().GetProperty("Wallet")!.GetValue(live)!;
            wallet.GetType().GetProperty("Money")!.SetValue(wallet, amount);
            return;
        }
        throw new InvalidOperationException($"peer ship {shipId} not found to fund");
    }

    /// <summary>P441 — fully develop every side's tech tree (OR of all
    /// development effects) so the docked garrison sells the real launcher the
    /// test buys — a legitimate late-game purchase that passes the tech gate.
    /// Simulates tech development (P283's StationSells consults
    /// Side.DevelopmentTechs) without touching the server rule.</summary>
    private static void DevelopAllTech(Srv server)
    {
        // Fully-researched side: every tech bit set (a maximal superset that
        // provably satisfies RequiredTechsBuyable, regardless of which specific
        // development grants a given bit). Deterministic test setup.
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        foreach (var s in server.Missions[0].Sides)
            s.DevelopmentTechs = all;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Seat the peer's ship on a friendly station's side just inside
    /// DockRadius so the real dock-radius pass latches StationId (the same
    /// dock arrangement P394/DockLoadoutLiveTests use).</summary>
    private static (Ship Ship, Sector Sector) SpawnDocked(
        StaticCore core, Mission mission, short shipId, out short stationId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, StationPos(st) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        stationId = st.StationId;
        return (ship, sector);
    }

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>A real (launcher, hull) pair where the hull actually MOUNTS the
    /// launcher (via the real <see cref="DataHullTypeIGC.CanMountPart"/>) —
    /// scanned from the real core, mirroring the P433 MountableLauncherName
    /// discovery. The shop will request this hull and launcher, and the server
    /// (P433 ApplyLoadout) grants it.
    /// <para><b>P488</b> — prefers a TERMINAL launcher (no
    /// <c>SuccessorPartId</c>): under P488 a requested launcher with a buyable
    /// successor is upgraded on the server, so this test (whose intent is the
    /// real-launcher round-trip) exercises one that cannot be upgraded.</para></summary>
    private static (LauncherOffer lo, DataHullTypeIGC hull) MountablePair()
    {
        foreach (var lo in Catalog.Launchers!.Where(l => l.Part.SuccessorPartId < 0))
            foreach (var h in Catalog.Hulls)
            {
                var part = lo.Part;
                if (h.GroupId >= 0 && h.CanMountPart(part, 0))
                    return (lo, h);
            }
        throw new InvalidOperationException("PCore001 has no (terminal launcher, mounting-hull) pair");
    }

    /// <summary>⭐ P434 — the headline live proof: a real human can open the
    /// shop on a dock, SPACE onto a real [LAUNCHER] row (Activate -> real
    /// launcher id + real ammo in the pending loadout), and L (BUY_LOADOUT
    /// fLaunch) — and the authoritative server-granted loadout carries the real
    /// missile launcher with its real ammo, the identical wire shape a
    /// P433-equipped bot's launcher entry carries.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShopLauncherPurchase_GrantsTheRealLauncher_Live()
    {
        Require.Artwork();
        using var server = new Srv();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
            SpawnDocked(core, mission, id, out _));
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p434_buy");
        session.SendLogonRequest(Request("p434_buy"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;
        Fund(server, myId, 1_000_000);   // P441: fund the purchase
        DevelopAllTech(server);          // P441: develop tech so the garrison sells the real launcher

        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, $"ship never docked (StationId={session.World.Ships[myId].StationId})");

        // Choose a REAL launcher + a hull that mounts it (scanned from the core).
        var (lo, mountHull) = MountablePair();
        short spawnedHull = session.World.Ships[myId].HullId;

        // Open the shop exactly as Game1.Panes does on a dock: a fresh ShopModel
        // from the ship's live hull + loadout.
        var shop = new ShopModel(Catalog, spawnedHull, session.World.Ships[myId].Loadout!);

        // HUMAN SEQUENCE: if the spawned hull can't carry the launcher, first
        // cycle to a hull that can (native OnButtonNext — the player's way to
        // reach a compatible hull), exactly what a player does; then use the
        // slot-driven PARTS tab to buy+equip the launcher.
        bool spawnMounts = mountHull.HullId == spawnedHull;
        if (!spawnMounts)
        {
            shop.CycleHullTo(mountHull.HullId);
        }
        Assert.Equal(mountHull.HullId, shop.EffectiveHullId);

        ShopModelTests.MountViaSlots(shop, lo.Part.PartId);   // PARTS tab -> magazine slot -> launcher

        // P571 — cycling to a new hull seeds its NATIVE default loadout, so the
        // pending loadout carries that default PLUS the launcher just equipped
        // (replacing the default magazine). What matters: the real launcher id
        // and its real ammo reserve are in the pending loadout to be launched.
        var bought = shop.ChosenParts.First(p => p.PartId == lo.Part.PartId);
        Assert.Equal(lo.Part.PartId, bought.PartId);      // the real launcher id
        Assert.Equal(lo.Ammo, bought.Amount);             // the real ammo reserve

        // L — launch with the pending loadout, exactly Game1.Panes:162.
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);

        // The ship undocks and the authoritative BUY_LOADOUT_ACK loadout is
        // granted with the REAL launcher carrying its REAL ammo.
        int undocked = PumpUntil(server, () => session.World.Ships[myId].StationId == -1, 8000);
        Assert.True(undocked < 8000, "fLaunch=true never undocked the ship");

        var granted = session.World.Ships[myId].Loadout;
        Assert.NotNull(granted);
        var launcherEntry = granted!.FirstOrDefault(p => p.PartId == lo.Part.PartId);
        Assert.NotEqual(default(ExpandedPartData), launcherEntry);
        Assert.Equal(lo.Ammo, launcherEntry.Amount);
        Assert.Equal(ExpandedPartData.FullyMounted, launcherEntry.FractionMounted);
    }
}
