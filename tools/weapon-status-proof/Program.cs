using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

// P362 live-proof harness: boots a REAL in-process server, connects a REAL
// "me" ConnectionSession that BUYS a genuine multi-weapon loadout (a projectile
// ammo-weapon + an energy weapon) over the wire, then FIRES the projectile
// weapon to zero ammo through the real client→server fire path and renders the
// REAL HudRenderer offscreen while the client's own live world observes the
// ammo depletion and P361's auto-reload behaviour.
//
// Two cases:
//   CASE A (success): the ship carries a spare ammo pack in cargo → on hitting
//     zero the server auto-reloads it synchronously and relays RELOAD(129); the
//     client's weapon-status list shows the gatling RELOADING (native "Reloading
//     ammo...") then back to READY with ammo restored — while the energy weapon
//     stays READY throughout (independent systems).
//   CASE B (depleted): no cargo spare → on hitting zero ammo stays zero → after
//     the grace the list shows DEPLETED (native "Ammo depleted.").
// The live server's real Ship.LastAutoReloadOutcome is cross-checked against the
// client-observed status at each capture.

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
return 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string ContentRoot = "";
    public string OutDir = "";

    public static ProofOptions Parse(string[] args)
    {
        string art = "", content = "", outDir = "";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--content": content = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : Path.Combine(repo, "docs", "artwork"),
            ContentRoot = content.Length > 0 ? content : Path.Combine(repo, "src", "Allegiance.ClientCore.MonoGame", "Content"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p362"),
        };
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}

sealed class ProofRunner : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private HudRenderer _hud = null!;
    private SpriteBatch _sb = null!;
    private Texture2D _white = null!;
    private SpriteFont _font = null!;

    private const int Width = 880, Height = 620;
    private static readonly Color SceneBg = new(0.018f, 0.022f, 0.028f);

    private static readonly PropertyInfo ShipsProp =
        typeof(Srv).GetProperty("Ships", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Server.Ships property not found");

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = Width;
        _gdm.PreferredBackBufferHeight = Height;
        Content.RootDirectory = o.ContentRoot;
        Directory.CreateDirectory(o.OutDir);
    }

    protected override void Initialize()
    {
        base.Initialize();
        _sb = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });
        _font = Content.Load<SpriteFont>("HUD");
        _hud = new HudRenderer(_sb, _white, _font, _font, gauges: null);
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[proof] FATAL: {ex}"); }
        Exit();
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P362 Weapon Status Proof", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return env is { Length: > 0 } e2 ? e2 : "/home/domingo/Development/Artwork";
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 961u, DwCookie = 0x362u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 94100000000000000ul, DrmHash = "wstproof",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[proof] pump TIMEOUT");
    }

    // Reach the server's internal Ships table and return the ship object as Ship.
    private static Ship FindServerShip(Srv server, short id)
    {
        var coll = (System.Collections.IEnumerable)ShipsProp.GetValue(server)!;
        foreach (var ls in coll)
        {
            var ship = ls.GetType().GetProperty("Ship")!.GetValue(ls)!;
            var s = ship as Ship;
            if (s is not null && s.Id == id) return s;
        }
        throw new InvalidOperationException($"no server ship for #{id}");
    }

    /// <summary>A constructed ammo pack part (<c>c_packAmmo</c> = 0), same as the
    /// P361 test helper — the cargo spare a successful auto-reload pulls.</summary>
    private static DataPartTypeIGC AmmoPackType() => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ammo", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 910, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 0, Amount: 0));   // c_packAmmo

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine("[proof] booting in-process server (XXV_simple)…");
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up countdown

        using var me = new ConnectionSession();
        me.Connect(server.BoundPort, "proof_client");
        me.SendLogonRequest(Request("Me_Weapon"));
        me.SendPositionReq(0);
        PumpUntil(server, () => me.World.MyShipId is not null, 6000);
        short meId = me.World.MyShipId!.Value;

        // Wait for launch (undocked) so the ship can fly & fire.
        PumpUntil(server, () =>
            me.World.Ships.TryGetValue(meId, out var e) && e.StationId == -1 && e.Latest is not null, 9000);
        Console.WriteLine($"[proof] me=#{meId} launched; picking weapons from PCore001…");

        // Build the shop catalogue to resolve names/types and find real weapon ids.
        var cat = ShopCatalog.TryLoad(_o.ArtRoot);
        if (cat is null) throw new InvalidOperationException("PCore001 catalogue unavailable");

        // Pick a projectile weapon (consumes ammo) and an energy weapon (never does).
        var projectile = cat.Parts
            .Where(p => p.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 })
            .OrderBy(p => p.PartId).First();
        var energy = cat.Parts
            .Where(p => p.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: 0 })
            .OrderBy(p => p.PartId).First();
        Console.WriteLine($"[proof] projectile '{ShopLayout.AsciiSafe(projectile.Name)}' (id {projectile.PartId}), " +
                          $"energy '{ShopLayout.AsciiSafe(energy.Name)}' (id {energy.PartId})");

        short hullId = me.World.Ships[meId].HullId;
        short maxAmmo = FindServerShip(server, meId).HullType.MaxAmmo;
        // Seed the pool to an exact multiple of the projectile weapon's CAmmoPerShot
        // (so firing drains to EXACTLY 0 and trips P361's auto-reload), sized so the
        // fraction is clearly above the client's empty-epsilon at the "ready" capture.
        short ca = ((DataPartTypeIGC.Weapon)projectile.Type).CAmmoPerShot;
        short seedAmmo = (short)(ca * Math.Max(1, (int)MathF.Round(maxAmmo * 0.06f / ca)));
        Console.WriteLine($"[proof] me hull #{hullId}, MaxAmmo={maxAmmo}, CAmmoPerShot={ca}; " +
                          $"seeding pool to {seedAmmo} (frac {seedAmmo/(float)maxAmmo:0.000})");

        // Buy the multi-weapon loadout over the REAL wire → BUY_LOADOUT_ACK populates
        // the client's ShipEntity.Loadout (what the HUD list renders). Mount 0 =
        // projectile (ammo), mount 1 = energy (no ammo). Projectile's Loadout amount
        // = MaxAmmo so the capacity column reads sensibly.
        me.SendBuyLoadout(hullId, fLaunch: true, parts:
        [
            new ExpandedPartData(projectile.PartId, maxAmmo, 0, ExpandedPartData.FullyMounted),
            new ExpandedPartData(energy.PartId, 0, 1, ExpandedPartData.FullyMounted),
        ]);
        PumpUntil(server,
            () => me.World.Ships.TryGetValue(meId, out var e) && e.Loadout is { Count: >= 2 } && e.StationId == -1,
            9000);
        var loadout = me.World.Ships[meId].Loadout!;
        Console.WriteLine($"[proof] client Loadout after BUY_LOADOUT_ACK: {loadout.Count} entries — " +
                          string.Join(", ", loadout.Select(p => $"mount{p.MountId}=part{p.PartId}(amt {p.Amount})")));

        // Slots mirror Game1.BuildWeaponStatusRows (same classification/naming).
        var slots = BuildSlots(cat, loadout);
        Console.WriteLine("[proof] HUD slots: " + string.Join(" | ", slots.Select(s =>
            $"m{s.MountId}={s.Name}{(s.ConsumesAmmo ? "[AMMO]" : "[NRG]")}(cap {s.CapacityAmmo})")));

        RunCaseA(gd, server, me, meId, cat, slots, maxAmmo, seedAmmo);
        RunCaseB(gd, server, me, meId, cat, slots, maxAmmo, seedAmmo);

        Console.WriteLine($"[proof] done — PNGs in {_o.OutDir}");
    }

    // ── the two live cases ──────────────────────────────────────────────────

    // Cease fire and settle the wire so a reseed takes clean effect between cases.
    private static void CeaseFire(Srv server, ConnectionSession me)
    {
        me.CommandControls(new ControlData(0f, 0f, 0f, 0f), 0);   // clear the fire latch
        for (int i = 0; i < 10; i++) { server.TickSim(); Thread.Sleep(1); }
    }

    private void RunCaseA(GraphicsDevice gd, Srv server, ConnectionSession me, short meId,
        ShopCatalog cat, IReadOnlyList<WeaponSlotDescriptor> slots, short maxAmmo, short seedAmmo)
    {
        Console.WriteLine("\n===== CASE A: auto-reload SUCCESS (spare pack in cargo) =====");
        CeaseFire(server, me);

        var ship = FindServerShip(server, meId);
        ship.Energy = ship.HullType.MaxEnergy;
        ship.Ammo = seedAmmo;                                   // a multiple of CAmmoPerShot → drains to exact 0
        ship.MountPart(AmmoPackType(), amount: (short)Math.Max(1, (int)MathF.Round(maxAmmo * 0.5f)), 1, 255);
        ship.TrySwapPartWithCargo(EquipmentType.Pack, 1, -1);   // spare lands in cargo slot 0
        Console.WriteLine($"[proof] A: seeded Ammo={ship.Ammo} (frac {ship.Ammo/(float)maxAmmo:0.000}), " +
                          $"cargo ammo pack present: {ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null}");
        for (int i = 0; i < 5; i++) server.TickSim();           // settle so the wire shows the seed

        var tracker = new WeaponReloadTracker();
        double now = 0.0;
        long lastReload = me.World.Ships[meId].ReloadCount;
        bool capturedReloading = false, capturedRecovered = false;

        // Ready-before: rendered once the settled broadcast reflects the seed.
        if (me.World.Ships.TryGetValue(meId, out var preEnt) && preEnt.Latest is { } preUp)
        {
            var preRows = tracker.Update((float)now, preUp.AmmoFraction, false, slots);
            RenderHud(gd, "a_ready", BuildFrame(preUp, preRows), preUp);
            Console.WriteLine($"[proof] A: captured a_ready — gatling READY, ammoFrac={preUp.AmmoFraction:0.000} " +
                              $"(server LastAutoReload={ship.LastAutoReloadOutcome})");
        }

        for (int i = 0; i < 4000 && !capturedRecovered; i++)
        {
            me.CommandControls(new ControlData(0f, 0f, 0f, 0f), (int)ShipControlState.AllWeapons);
            server.TickSim();
            now += 0.05;

            if (!me.World.Ships.TryGetValue(meId, out var ent) || ent.Latest is null) continue;
            var up = ent.Latest;
            bool reloadArrived = ent.ReloadCount != lastReload;
            lastReload = ent.ReloadCount;
            var rows = tracker.Update((float)now, up.AmmoFraction, reloadArrived, slots);
            var g0 = rows.First(r => r.MountId == 0);

            if (reloadArrived && !capturedReloading)
            {
                RenderHud(gd, "a_reloading", BuildFrame(up, rows), up);
                Console.WriteLine($"[proof] A: RELOAD(129) relay for me — captured a_reloading — gatling {g0.Status}, " +
                                  $"energy={rows.First(r => r.MountId == 1).Status}, ammoFrac={up.AmmoFraction:0.000} " +
                                  $"(server LastAutoReload={ship.LastAutoReloadOutcome})");
                capturedReloading = true;
            }
            if (capturedReloading && g0.Status == WeaponStatus.Ready && up.AmmoFraction > WeaponReloadTracker.EmptyEpsilon && !capturedRecovered)
            {
                RenderHud(gd, "a_recovered", BuildFrame(up, rows), up);
                Console.WriteLine($"[proof] A: gatling back to READY with ammo — captured a_recovered — " +
                                  $"ammoFrac={up.AmmoFraction:0.000} (server Ammo={ship.Ammo}, LastAutoReload={ship.LastAutoReloadOutcome})");
                capturedRecovered = true;
            }
            if (i == 3999) Console.WriteLine("[proof] A: loop ended without full sequence");
        }
        Console.WriteLine($"[proof] A: relay observed={capturedReloading}, recovered={capturedRecovered}");
    }

    private void RunCaseB(GraphicsDevice gd, Srv server, ConnectionSession me, short meId,
        ShopCatalog cat, IReadOnlyList<WeaponSlotDescriptor> slots, short maxAmmo, short seedAmmo)
    {
        Console.WriteLine("\n===== CASE B: auto-reload FAILURE (no spare in cargo) → DEPLETED =====");
        CeaseFire(server, me);                                  // stop the case-A fire latch

        var ship = FindServerShip(server, meId);
        ship.Energy = ship.HullType.MaxEnergy;
        ship.Ammo = seedAmmo;                                   // drains to exact 0; cargo is empty ⇒ reload MUST fail
        Console.WriteLine($"[proof] B: seeded Ammo={ship.Ammo} (frac {ship.Ammo/(float)maxAmmo:0.000}), " +
                          $"cargo ammo pack present: {ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null} (expect False)");
        for (int i = 0; i < 5; i++) server.TickSim();           // settle

        var tracker = new WeaponReloadTracker();
        double now = 0.0;
        long lastReload = me.World.Ships[meId].ReloadCount;
        bool capturedDepleted = false;

        if (me.World.Ships.TryGetValue(meId, out var preEnt) && preEnt.Latest is { } preUp)
        {
            var preRows = tracker.Update((float)now, preUp.AmmoFraction, false, slots);
            RenderHud(gd, "b_ready", BuildFrame(preUp, preRows), preUp);
            Console.WriteLine($"[proof] B: captured b_ready — gatling READY, ammoFrac={preUp.AmmoFraction:0.000}");
        }

        for (int i = 0; i < 4000 && !capturedDepleted; i++)
        {
            me.CommandControls(new ControlData(0f, 0f, 0f, 0f), (int)ShipControlState.AllWeapons);
            server.TickSim();
            now += 0.05;

            if (!me.World.Ships.TryGetValue(meId, out var ent) || ent.Latest is null) continue;
            var up = ent.Latest;
            bool reloadArrived = ent.ReloadCount != lastReload;
            lastReload = ent.ReloadCount;
            var rows = tracker.Update((float)now, up.AmmoFraction, reloadArrived, slots);
            var g0 = rows.First(r => r.MountId == 0);

            if (g0.Status == WeaponStatus.Depleted && !capturedDepleted)
            {
                RenderHud(gd, "b_depleted", BuildFrame(up, rows), up);
                Console.WriteLine($"[proof] B: gatling DEPLETED — captured b_depleted — ammoFrac={up.AmmoFraction:0.000}, " +
                                  $"energy={rows.First(r => r.MountId == 1).Status} (server Ammo={ship.Ammo}, LastAutoReload={ship.LastAutoReloadOutcome})");
                capturedDepleted = true;
            }
            if (i == 3999) Console.WriteLine("[proof] B: loop ended without DEPLETED");
        }
        Console.WriteLine($"[proof] B: depleted observed={capturedDepleted}");
    }

    // ── helpers (mirror Game1) ──────────────────────────────────────────────

    private static IReadOnlyList<WeaponSlotDescriptor> BuildSlots(ShopCatalog cat, IReadOnlyList<ExpandedPartData> loadout)
    {
        var slots = new List<WeaponSlotDescriptor>();
        foreach (var p in loadout)
        {
            DataPartTypeIGC part = default;
            bool found = false;
            foreach (var pt in cat.Parts) if (pt.PartId == p.PartId) { part = pt; found = true; break; }
            if (!found) continue;
            bool consumes;
            if (part.Type is DataPartTypeIGC.Weapon w) consumes = w.CAmmoPerShot != 0;
            else if (part.Type is DataPartTypeIGC.MissileLauncher) consumes = false;
            else continue;
            slots.Add(new WeaponSlotDescriptor(p.MountId, ShopLayout.AsciiSafe(part.Name), consumes, p.Amount));
        }
        return slots;
    }

    private static HudFrame BuildFrame(ServerShipUpdate up, IReadOnlyList<WeaponStatusRow> rows) => new(
        HullFraction: up.HullFraction,
        ShieldFraction: up.ShieldFraction,
        AmmoFraction: up.AmmoFraction,
        FuelFraction: up.FuelFraction,
        EnergyFraction: up.EnergyFraction,
        Power: up.Power,
        Speed: up.Velocity.Length(),
        Throttle: up.Controls.Throttle,
        AfterburnerActive: (up.StateM & ShipControlState.AfterburnerButton) != 0,
        Firing: (up.StateM & (ShipControlState.AllWeapons | ShipControlState.OneWeapon)) != 0,
        GroupFire: false, SelectedMount: 0,
        Flashing: false, WarpFlash: 0f, Mining: false, Cloaked: false,
        KillBonusPercent: 0f, KillBonusStreak: 0,
        RollDegrees: up.Orientation.GetRollDegrees(),
        WeaponSystems: rows);

    private void RenderHud(GraphicsDevice gd, string label, HudFrame frame, ServerShipUpdate up)
    {
        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(SceneBg);

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        _sb.DrawString(_font, $"P362 - {label}   ammo {frame.AmmoFraction:0.00}", new Vector2(12f, 8f), Color.Orange);
        _sb.End();

        var vp = new Viewport(0, 0, Width, Height);
        _hud.Draw(frame, CameraMode.Cockpit, vp, lockBracket: null, leadIndicator: null,
            modeLabelOverride: label.ToUpperInvariant());

        gd.SetRenderTarget(null);
        string path = Path.Combine(_o.OutDir, $"p362_{label}.png");
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[proof]   wrote {path}");
    }
}
