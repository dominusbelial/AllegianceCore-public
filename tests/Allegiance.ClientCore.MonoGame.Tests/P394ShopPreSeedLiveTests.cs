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
/// P394 — LIVE proof of the pre-seeded loadout fix. The shop's pending loadout
/// (<see cref="ShopModel.ChosenParts"/>), the exact list sent when the player
/// presses L, used to start EMPTY on every fresh dock, so relaunching without
/// manually re-toggling every part you already have silently dropped all of
/// them. <see cref="ShopModel"/> now seeds <c>_chosenParts</c> from the ship's
/// real current loadout (the BUY_LOADOUT_ACK tail), exactly what
/// <c>Game1.Panes</c> passes as <c>currentParts</c> when it builds the shop on a
/// dock.
///
/// These tests reproduce the player's manual sequence over a REAL in-process
/// server + real <see cref="ConnectionSession"/> wire round-trips: equip a
/// shield, reopen the shop from the ship's live loadout, and relaunch. They use
/// a docked spawner (ship placed inside a friendly station's DockRadius) so the
/// real dock-radius pass latches <c>StationId</c>, and drive
/// <c>BUY_LOADOUT(fLaunch:true)</c> — the Hangar "Launch" path — to undock,
/// mirroring <c>HangarScreen::OnButtonLaunch</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P394ShopPreSeedLiveTests
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
            new Gm { Name = "P394 Pre-Seed Loadout", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 994u, DwCookie = 0x994u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 99000000000000004ul, DrmHash = "p394",
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

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Seat the peer's ship on a friendly station's side just inside
    /// DockRadius so the real dock-radius pass latches StationId — the same
    /// dock arrangement DockLoadoutLiveTests.SpawnDocked uses.</summary>
    private static (Ship Ship, Sector Sector) SpawnDocked(
        StaticCore core, Mission mission, short shipId, out short stationId)
    {
        // H-07: the shield must fit the actual equipment slot.
        var shield = core.PartTypes[RealShieldPart().PartId];
        var hull = core.HullTypes.Values.First(h => h.GroupId >= 0 && h.CanMountPart(shield, 0));
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, StationPos(st) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId, StationId = st.StationId,
        };
        mission.PlaceShip(ship, sector);
        stationId = st.StationId;
        return (ship, sector);
    }

    /// <summary>The first real SHIELD part id in the representative core (any
    /// Shield subtype), ordered by PartId. The loadout tests use a shield — the
    /// exact non-weapon part class P394's bug dropped (weapons were spared only
    /// by MountCombatWeapon). Free-mounted because the requested hull is the
    /// current one (hullChanged == false), so it always mounts and the ACK tail
    /// carries it.</summary>
    private static ExpandedPartData RealShieldPart() =>
        new(
            RepresentativeCore.PartTypes.Values
                .Where(p => p.EquipmentType == EquipmentType.Shield)
                .OrderBy(p => p.PartId)
                .First().PartId,
            Amount: 1, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted);

    /// <summary>The shop catalogue over the same PCore001.igc the server boots
    /// (parsed once), so the ShopModel resolves real hull/part records.</summary>
    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>⭐ P394 — the headline live proof: equip a shield (real
    /// BUY_LOADOUT), reopen the shop from the ship's live loadout (exactly what
    /// Game1.Panes does on a dock), confirm the shield is pre-seeded, then press
    /// L (relaunch via BUY_LOADOUT(fLaunch:true)) with the untouched pending
    /// loadout — the shield is STILL mounted afterwards. Pre-fix, the shop's
    /// empty ChosenParts cleared the whole loadout on relaunch and the shield
    /// was silently dropped.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EquippedShield_UntouchedPreseeded_SurvivesRelaunch_Live()
    {
        Require.Artwork();
        using var server = new Srv();
        short friendlyStation = -1;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var r = SpawnDocked(core, mission, id, out short st);
            friendlyStation = st;
            return r;
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p394_preseed");
        session.SendLogonRequest(Request("p394_preseed"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;

        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, $"ship never docked (StationId={session.World.Ships[myId].StationId})");
        Assert.Equal(friendlyStation, session.World.Ships[myId].StationId);
        short hull = session.World.Ships[myId].HullId;
        Assert.NotEqual((short)0, hull);

        // Join-burst adopt-a-hull ACK -> empty loadout before any purchase.
        Assert.NotNull(session.World.Ships[myId].Loadout);
        Assert.Empty(session.World.Ships[myId].Loadout!);

        // (1) EQUIP a shield — the player buys + equips it while docked.
        var shield = RealShieldPart();
        session.SendBuyLoadout(hull, fLaunch: false, parts: new[] { shield });
        int equipped = PumpUntil(server,
            () => session.World.Ships[myId].Loadout is { Count: > 0 } lo && lo.Any(p => p.PartId == shield.PartId),
            8000);
        Assert.True(equipped < 8000, "shield never equipped (not in loadout after buy)");
        Assert.Contains(session.World.Ships[myId].Loadout!, p => p.PartId == shield.PartId);

        // (2) REOPEN the shop the way Game1.Panes does on a dock: build a fresh
        // ShopModel from the ship's live loadout (BUY_LOADOUT_ACK tail).
        short myHull = session.World.Ships[myId].HullId;
        var shop = new ShopModel(Catalog, myHull, session.World.Ships[myId].Loadout!);

        // ⭐ THE FIX: the shield is pre-seeded into the pending loadout L sends.
        Assert.Contains(shop.ChosenParts, p => p.PartId == shield.PartId);
        // And (same hull requested) it is NOT predicted dropped.
        Assert.Empty(shop.ChosenPartsThatDontFitEffectiveHull);

        // (3) PRESS L (untouched): relaunch with the pending loadout exactly as
        // Game1.Panes:162 does — this clears + re-mounts the loadout and undocks.
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);

        int undocked = PumpUntil(server, () => session.World.Ships[myId].StationId == -1, 8000);
        Assert.True(undocked < 8000, "fLaunch=true never undocked the ship");

        // ⭐ The shield is STILL mounted after relaunch (pre-fix it was dropped).
        Assert.Contains(session.World.Ships[myId].Loadout!, p => p.PartId == shield.PartId);
        // Functional shield re-arms to fully-up on mount (MountPart ->
        // CshieldIGC::Arm sets _shieldFraction = 1.0f, shieldIGC.h:99-103).
        Assert.Equal(ExpandedPartData.FullyMounted,
            session.World.Ships[myId].Loadout!.First(p => p.PartId == shield.PartId).FractionMounted);
    }

    /// <summary>⭐ P394 ∩ scope-2 — a pre-seeded part the player explicitly
    /// DESELECTS drops on relaunch (the existing remove-if-present toggle, not
    /// a stuck or double-added entry): deselect the shield in the shop, relaunch,
    /// and confirm it is gone. This guards against seeding accidentally pinning a
    /// part on.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PreSeededPart_ToggledOff_DropsOnRelaunch_Live()
    {
        Require.Artwork();
        using var server = new Srv();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var r = SpawnDocked(core, mission, id, out _);
            return r;
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p394_toggleoff");
        session.SendLogonRequest(Request("p394_toggleoff"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;

        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, "ship never docked");
        short hull = session.World.Ships[myId].HullId;

        // Equip a shield (as the player does), so it's in the ship's loadout.
        var shield = RealShieldPart();
        session.SendBuyLoadout(hull, fLaunch: false, parts: new[] { shield });
        int equipped = PumpUntil(server,
            () => session.World.Ships[myId].Loadout is { Count: > 0 } lo && lo.Any(p => p.PartId == shield.PartId),
            8000);
        Assert.True(equipped < 8000, "shield never equipped");

        // Reopen the shop from the live loadout; the shield is pre-seeded.
        short myHull = session.World.Ships[myId].HullId;
        var shop = new ShopModel(Catalog, myHull, session.World.Ships[myId].Loadout!);
        Assert.Contains(shop.ChosenParts, p => p.PartId == shield.PartId);

        // Player deselects the shield in the slot-driven PARTS tab: open the
        // shield slot and choose its "(make slot empty)" row (native "-Make Slot
        // Empty-"). If this hull has no shield socket, clear the type directly
        // (a free-mounted shield still has no slot in the compatible-only list).
        shop.SwitchTab(ShopTab.Parts);
        int shieldSlot = -1;
        for (int i = 0; i < shop.Slots.Count; i++)
            if (shop.Slots[i].Type == EquipmentType.Shield) { shieldSlot = i; break; }
        if (shieldSlot >= 0)
        {
            shop.MoveCursor(shieldSlot - shop.Cursor);
            shop.Activate();                                          // open the shield slot
            shop.MoveCursor(shop.OpenSlotParts.Count - shop.OpenSlotCursor); // to the "(empty)" row
            shop.Activate();                                          // clear it
        }
        else
        {
            shop.ClearSlot(new SlotDef(EquipmentType.Shield, 0, false));
        }
        Assert.DoesNotContain(shop.ChosenParts, p => p.PartId == shield.PartId);

        // Relaunch with the deselected loadout (empty) -> the shield drops.
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
        int undocked = PumpUntil(server, () => session.World.Ships[myId].StationId == -1, 8000);
        Assert.True(undocked < 8000, "fLaunch=true never undocked the ship");

        // ⭐ Deselected => correctly dropped on relaunch (not pinned by seeding).
        Assert.DoesNotContain(session.World.Ships[myId].Loadout!, p => p.PartId == shield.PartId);
    }
}
