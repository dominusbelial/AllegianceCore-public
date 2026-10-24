using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P803 (PAR-8) — the turret-gunner/observer HUD rows, pinned against the
/// native citations:
/// <list type="bullet">
/// <item>the per-turret occupant row <c>invturret.mdl</c> — turret icon, empty
/// key, the gunner's name at Point(35,5) — added under every occupied turret
/// mount (<c>WinTrek/inventoryimage.cpp:263-289</c>, "if (pshipGunner != NULL)");
/// the occupant/observer state is the client's own LOADOUT_CHANGE(96)
/// PassengerData re-seat (<c>appmsg.cpp:2035-2079</c>, <c>SetTurretID</c>
/// :2073);</item>
/// <item>the gunner's ASSIGNED weapon is the selected row while they man the
/// mount and the active row while they hold fire
/// (<c>WinTrek/consoledata.cpp:1080-1091</c> IsSelected gunner branch,
/// :1122 IsActive = fActive());</item>
/// <item>both renderers consume the same model row: Classic draws the turret
/// icon in <c>DrawInventoryRow</c>, Modern draws the row name in
/// <c>DrawModernInventoryRow</c> (source-content guards, the Hud-19
/// discipline, since the row paint needs a GraphicsDevice).</item>
/// </list>
/// The occupant tests fail on master (no <c>turretOccupant</c> parameter, no
/// <c>HudInventoryRowKind.Turret</c>); the gunner semantics fail on master
/// (no <c>boardedTurretId</c> parameter).
/// </summary>
public class P803TurretGunnerHudTests
{
    private const short WeaponId = 100;
    private const short ProjectileId = 7;

    private static DataPartTypeIGC Part(short id, string name, DataPartTypeIGC.Subtype type) =>
        new(Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: name, Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 1f, Signature: 0f,
            PartId: id, SuccessorPartId: -1, PartMask: 1, InventoryLineMdl: "", Type: type);

    // MaxFixedWeapons 2 + one turret mount (MaxWeapons 3) — the native
    // vszTurretHotKeys loop (inventoryimage.cpp:248-252,330-332).
    private static DataHullTypeIGC Hull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "Fighter", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 100f, Signature: 0f, Speed: 200f,
        MaxTurnRates: [4f, 4f, 4f], TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 50000f, SideMultiplier: 1f, BackMultiplier: 1f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 20f, MaxEnergy: 200f, RechargeRate: 5f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 200, HullId: 999, SuccessorHullId: -1,
        MaxWeapons: 3, MaxFixedWeapons: 2, HitPoints: 500f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 1, CapacityDispenser: 1, CapacityChaffLauncher: 1,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "",
        PmEquipment: [1, 1, 1, 1, 1, 1, 1, 1],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0,
        Hardpoints:
        [
            new(0, 0, "", "1", 1, true),
            new(0, 0, "", "2", 1, true),
            new(0, 0, "", "T", 1, false),
        ]);

    private static ShopCatalog Catalog() => new(
        Hulls: [Hull()],
        Parts: [Part(WeaponId, "Gatling", new DataPartTypeIGC.Weapon(0.1f, 0.125f, 0f, 0f, 3, ProjectileId, 0, 0, 0))],
        ProjectileTypes: new Dictionary<short, DataProjectileTypeIGC>
        {
            [ProjectileId] = new([], 0f, 0f, "", "", 10f, 5f, 0f, 1000f, 0.8f, ProjectileId, 0, false, false, 0f, 0),
        },
        MissileTypes: new Dictionary<short, DataMissileTypeIGC>(), Launchers: []);

    private static ExpandedPartData Mounted(short partId, int mount) =>
        new ExpandedPartData(partId, 1, (byte)mount, ExpandedPartData.FullyMounted);

    private static HudInventoryPane Build(IReadOnlyList<ExpandedPartData> loadout,
        Func<int, string?>? turretOccupant = null, int? boardedTurretId = null, bool turretFiring = false,
        bool isPilot = true)
        => HudInventoryPaneBuilder.Build(Catalog(), Hull(), loadout, [], GlobalAttributeSet.Identity,
            0.9f, 0.8f, 0.5f, 0.6f, groupFire: false, selectedMount: 0, selectedCargoMount: -1, ownShipName: "Fighter",
            isPilot: isPilot,
            turretOccupant: turretOccupant, boardedTurretId: boardedTurretId, turretFiring: turretFiring);

    // ── inventoryimage.cpp:263-289 — the invturret occupant rows ────────────

    [Fact]
    public void Occupant_row_follows_its_mount_with_the_native_layout()
    {
        // invturret.mdl:1-12 — turret icon (Point(5,5)), EMPTY key, gunner name
        // (Point(35,5)). The row is added only for an OCCUPIED mount, right
        // under its weapon row (inventoryimage.cpp:275-288).
        var pane = Build([Mounted(WeaponId, 2)], turretOccupant: m => m == 2 ? "Gun Boy" : null);
        var rows = pane.Rows;
        int weaponIdx = rows.ToList().FindIndex(r => r.IsWeapon && r.Name == "Gatling");
        Assert.True(weaponIdx >= 1);                       // after the two fixed rows
        Assert.Equal("Y.", rows[weaponIdx].Key);           // native turret hotkey (pilot view)
        var occupant = rows[weaponIdx + 1];
        Assert.Equal(HudInventoryRowKind.Turret, occupant.Kind);
        Assert.Equal("", occupant.Key);                    // native passes szKey "" (inventoryimage.cpp:284)
        Assert.Equal("Gun Boy", occupant.Name);
        Assert.False(occupant.Selected);
        Assert.False(occupant.Active);
    }

    [Fact]
    public void Empty_turret_mount_gets_no_occupant_row()
    {
        // inventoryimage.cpp:275 — "if (pshipGunner != NULL)".
        var pane = Build([Mounted(WeaponId, 2)], turretOccupant: _ => null);
        Assert.DoesNotContain(pane.Rows, r => r.Kind == HudInventoryRowKind.Turret);
    }

    // ── consoledata.cpp:1080-1122 — the gunner's assigned weapon ────────────

    [Fact]
    public void Gunner_assigned_weapon_is_selected_and_active_while_firing()
    {
        // IsSelected gunner branch (consoledata.cpp:1087-1091): the gunner mans
        // the mount → fSelected. IsActive = fActive() (:1122): the local
        // gunner's fire state.
        var rows = Build([Mounted(WeaponId, 2)], boardedTurretId: 2, turretFiring: true).Rows;
        var turretWeapon = rows.Single(r => r.IsWeapon && r.Name == "Gatling");
        Assert.True(turretWeapon.Selected);
        Assert.True(turretWeapon.Active);

        // Not firing: still selected (the gunner mans it), no longer active.
        var idle = Build([Mounted(WeaponId, 2)], boardedTurretId: 2, turretFiring: false).Rows
            .Single(r => r.IsWeapon && r.Name == "Gatling");
        Assert.True(idle.Selected);
        Assert.False(idle.Active);

        // The pilot's fixed rows stay unselected for a gunner (native
        // fSelected = GetGunner() == me → false), keys blank (bIsPilot false,
        // inventoryimage.cpp:243-246).
        var fixedRow = Build([Mounted(WeaponId, 0)], boardedTurretId: 2, turretFiring: true,
            isPilot: false).Rows
            .First(r => r.IsWeapon);
        Assert.False(fixedRow.Selected);
        Assert.Equal("", fixedRow.Key);
    }

    // ── the loopback model: both clients' rosters name the right turret ─────

    [Fact]
    public void Boarded_children_resolve_to_their_turret_rows_and_the_observer_count()
    {
        // The wire path a real board produces (native appmsg.cpp:2035-2079):
        // LOADOUT_CHANGE re-seats the gunner on mount 2 and an observer with
        // turretID NA(0xFF) on the same host. Both the host's and the gunner's
        // clients hold the same entities, so both rosters resolve the occupant.
        const short host = 900, gunner = 901, observer = 902;
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = host, HullId = 15, SideId = 2, CharacterName = "host" });
        world.Apply(new ExportShip { ShipId = gunner, HullId = 445, SideId = 2, CharacterName = "gunner" });
        world.Apply(new ExportShip { ShipId = observer, HullId = 445, SideId = 2, CharacterName = "watcher" });
        world.Apply(new LoadoutChange
        {
            SidShip = host,
            HullId = 16,
            Parts = [new ExpandedPartData(WeaponId, 1, 2, ExpandedPartData.FullyMounted)],
            Passengers =
            [
                new PassengerData(gunner, TurretId: 2),
                new PassengerData(observer, TurretId: (byte)ShipEntity.BoardTurretObserver),
            ],
        });

        var (observers, occupant) = HudInventoryPaneBuilder.SourceShipOccupants(world.Ships, host);
        Assert.Equal(1, observers);
        Assert.Equal("gunner", occupant(2));
        Assert.Null(occupant(0));
        Assert.Null(occupant(1));

        // The pane built from the HOST's client state — the turret weapon row
        // (key "Y.") followed by the gunner's name row, then the observer row.
        var pane = HudInventoryPaneBuilder.Build(Catalog(), Hull(),
            [Mounted(WeaponId, 2)], [], GlobalAttributeSet.Identity,
            0.9f, 0.8f, 0.5f, 0.6f, false, 0, -1, "Fighter",
            observerCount: observers, turretOccupant: occupant);
        var rows = pane.Rows.ToList();
        int mount2 = rows.FindIndex(r => r.IsWeapon && r.Name == "Gatling");
        Assert.Equal("gunner", rows[mount2 + 1].Name);
        int obs = rows.FindIndex(r => r.Kind == HudInventoryRowKind.Observers);
        // Native order: fixed weapons → Observers → turret mounts
        // (inventoryimage.cpp:242-252), so the observer row sits between the
        // fixed block and the turret block.
        Assert.True(obs > 0 && obs < mount2);
        Assert.Equal("Observers: 1", rows[obs].Name);

        // The GUNNER's client state resolves the same occupant (source ship =
        // the parent): the identical pane.
        var (observers2, occupant2) = HudInventoryPaneBuilder.SourceShipOccupants(world.Ships, host);
        Assert.Equal(observers, observers2);
        Assert.Equal("gunner", occupant2(2));
    }

    // ── per-style consumer guards (no GPU — Hud-19 source discipline) ───────

    private static string Src(string relative)
    {
        string root = "../../../../..";
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", relative));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing {signature}");
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced method body");
    }

    [Fact]
    public void Classic_renderer_draws_the_turret_row_with_the_native_icon()
    {
        // invturret.mdl:1-12 mounts invturreticonbmp at Point(5,5) — the
        // classic DrawInventoryRow icon switch has the Turret arm.
        var drawRow = Method(Src(Path.Combine("Rendering", "HudRenderer.cs")), "private void DrawInventoryRow(");
        Assert.Contains("HudInventoryRowKind.Turret => art?.TurretIcon", drawRow);
    }

    [Fact]
    public void Modern_renderer_draws_the_turret_row_name()
    {
        // Modern drops the artwork (allowed) but must still draw the occupant
        // name: DrawModernInventoryRow is not gated on the row kind (only the
        // observer special case returns early).
        var drawRow = Method(Src(Path.Combine("Rendering", "ModernHudRenderer.cs")), "private void DrawModernInventoryRow(");
        Assert.Contains("HudInventoryRowKind.Observers", drawRow);   // the only kind gate
        Assert.Contains("row.Name", drawRow);                        // the occupant name is drawn
    }
}
