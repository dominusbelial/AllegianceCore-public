using System.Collections;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Content;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using CoreVec = Allegiance.Core.Collision.Vector3;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

// P335 live proof harness: a REAL MonoGame-rendering client (ConnectionSession +
// the same HullModelResolver/ShipRenderer/content-models the in-game client
// uses) docks, opens the real hangar shop (ShopModel), picks a genuinely
// different hull, launches, and — once the server's authoritative BUY_LOADOUT_ACK
// lands (ShipEntity.HullId = the real new hull) — renders the new hull's model
// to PNG screenshots and logs the server-reported physical stats. Repeated for
// a second, different hull to prove it isn't a one-shot fluke.

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
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p335"),
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
    private readonly ShopCatalog _catalog;
    private readonly HullModelResolver _resolver = new();
    private readonly ShipRenderer _shipRenderer;
    private readonly Dictionary<string, XnaModel> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;
    private Texture2D _white = null!;

    private const float TileR = 62f;

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = 64;
        _gdm.PreferredBackBufferHeight = 64;
        Content.RootDirectory = o.ContentRoot;
        Directory.CreateDirectory(o.OutDir);
        _catalog = ShopCatalog.TryLoad(o.ArtRoot)
            ?? throw new InvalidOperationException($"cannot load PCore001 from '{o.ArtRoot}'");
        _shipRenderer = new ShipRenderer(GraphicsDevice);
    }

    protected override void Initialize()
    {
        base.Initialize();
        _batch = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });
        _font = Content.Load<SpriteFont>("HUD");
        try
        {
            RunProof(GraphicsDevice);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[proof] FATAL: {ex}");
        }
        Exit();
    }

    // ── server / client wiring (mirrors DockLoadoutLiveTests / HullSwapLiveTests) ──

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server.ShipSpawner field not found");

    private static PropertyInfo? _shipsProp =
        typeof(Srv).GetProperty("Ships", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static CoreVec StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static (Ship Ship, Sector Sector) SpawnDocked(StaticCore core, Mission mission, short shipId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, StationPos(st) + new CoreVec(20f, 0f, 0f), CoreVec.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P335 Hull Swap Proof", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return env is { Length: > 0 } ? env : "/home/domingo/Development/Artwork";
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 955u, DwCookie = 0x335u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 93600000000000000ul, DrmHash = "hullproof",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[proof] pump TIMEOUT");
    }

    // ── the proof ───────────────────────────────────────────────────────────

    private void RunProof(GraphicsDevice gd)
    {
        string civ = "";
        // We run two independent hull swaps (fresh missions) to prove the real
        // swap isn't a one-shot fluke.
        RunOne(gd, ref civ, label: "A", pickHighest: true, suffix: "swap1");
        RunOne(gd, ref civ, label: "B", pickHighest: false, suffix: "swap2");
        Console.WriteLine($"[proof] done — screenshots in {_o.OutDir}");
    }

    private void RunOne(GraphicsDevice gd, ref string civ, string label, bool pickHighest, string suffix)
    {
        Console.WriteLine($"[proof] === swap '{label}' (pick { (pickHighest ? "highest HullId" : "next-lowest distinct") }) ===");
        using var server = new Srv();
        ShipSpawnerField.SetValue(server, (Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)>)SpawnDocked);
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up countdown (public setter)

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "proof_client");
        session.SendLogonRequest(Request("proof_client"));

        PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        short myId = session.World.MyShipId!.Value;
        PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        short hullA = session.World.Ships[myId].HullId;
        if (civ.Length == 0)
        {
            civ = CivKey(session);
            Console.WriteLine($"[proof] civ key = '{civ}'");
        }
        _lastCiv = civ;   // resolve assets for the distinct-model preference below
        Console.WriteLine($"[proof] docked; current hull #{hullA} '{HullName(hullA)}'");

        var shop = new ShopModel(_catalog, hullA, Array.Empty<ExpandedPartData>());
        short target = PickDifferentHull(hullA, pickHighest);
        PickHull(shop, target);
        Console.WriteLine($"[proof] shop pick -> ChosenHullId={shop.ChosenHullId} EffectiveHullId={shop.EffectiveHullId} WillSwapHull={shop.WillSwapHull}");

        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true);
        PumpUntil(server,
            () => session.World.Ships[myId].HullId == target && session.World.Ships[myId].StationId == -1,
            8000);

        short landed = session.World.Ships[myId].HullId;
        Console.WriteLine($"[proof] launched; client ShipEntity.HullId={landed} (authoritative ACK) StationId={session.World.Ships[myId].StationId}");
        LogServerPhysics(server, myId, hullA, landed);

        RenderHull(gd, civ, hullA, $"before_{suffix}");
        RenderHull(gd, civ, landed, $"after_{suffix}");
        CompositePair(gd, civ, hullA, landed, suffix);
    }

    private static string CivKey(ConnectionSession session)
    {
        if (session.World.SideRoster is not { } roster) return "";
        var side = roster.Sides.FirstOrDefault(s => s.SideId == session.World.MySideId);
        return side is { } ss ? HullModelResolver.CivKeyByIdShort(ss.CivId) ?? "" : "";
    }

    private string HullName(short hullId) =>
        _catalog.Hulls.FirstOrDefault(h => h.HullId == hullId) is { } h ? h.Name : $"#{hullId}";

    private short PickDifferentHull(short current, bool highest)
    {
        var ordered = _catalog.Hulls
            .Where(h => h.HullId != current)
            .OrderBy(h => highest ? -h.HullId : h.HullId)
            .ToList();
        // Prefer one that resolves to a DIFFERENT model asset than the current
        // hull's, so the screenshot provably shows a different ship; fall back
        // to the first candidate if every candidate shares the current asset.
        string curAsset = AssetFor(current) ?? "";
        foreach (var h in ordered)
            if ((AssetFor(h.HullId) ?? "") != curAsset)
                return h.HullId;
        return ordered[0].HullId;
    }

    private void PickHull(ShopModel shop, short target)
    {
        int idx = CatalogIndexOfHull(target);
        shop.MoveCursor(idx - shop.Cursor);
        shop.Activate();
    }

    private int CatalogIndexOfHull(short hullId)
    {
        var list = _catalog.Hulls.ToList();
        int i = list.FindIndex(h => h.HullId == hullId);
        return i >= 0 ? i : 0;
    }

    private string? AssetFor(short hullId)
    {
        var hull = _catalog.Hulls.FirstOrDefault(h => h.HullId == hullId);
        if (hull.HullId != hullId) return null;   // not found (DataHullTypeIGC is a struct; HullIds are > 0)
        return _resolver.ResolveAsset(_lastCiv, hull);
    }

    private string _lastCiv = "";

    // ── server-side physics log (internal Ships via reflection, like P334) ──

    private static void LogServerPhysics(Srv server, short shipId, short hullBefore, short hullAfter)
    {
        try
        {
            if (_shipsProp is null) { Console.WriteLine("[proof] (Server.Ships not reflectable — skipping physics log)"); return; }
            var coll = (IEnumerable)_shipsProp.GetValue(server)!;
            object? ship = null;
            foreach (var ls in coll)
            {
                var lsShip = ls.GetType().GetProperty("Ship")!.GetValue(ls);
                if (lsShip is null) continue;
                object? sidVal = lsShip.GetType().GetProperty("ShipId")?.GetValue(lsShip)
                                 ?? lsShip.GetType().GetProperty("Id")?.GetValue(lsShip);
                if (Convert.ToInt32(sidVal) == shipId) { ship = lsShip; break; }
            }
            if (ship is null) { Console.WriteLine("[proof] server ship not found via reflection"); return; }
            var ht = ReadProp(ship, "HullType");
            Console.WriteLine($"[proof] SERVER Ship #{shipId} physics (before hull #{hullBefore} -> after hull #{hullAfter}):");
            Console.WriteLine($"   hull            {ReadProp(ht, "HullId")}");
            Console.WriteLine($"   Mass            {Fmt(ReadFloat(ship, "Mass"))}");
            Console.WriteLine($"   HitPoints       {Fmt(ReadFloat(ship, "HitPoints"))}");
            Console.WriteLine($"   Hull.Speed      {Fmt(ReadFloat(ht, "Speed"))}");
            Console.WriteLine($"   Hull.Thrust     {Fmt(ReadFloat(ht, "Thrust"))}");
            Console.WriteLine($"   Energy(reseed)  {Fmt(ReadFloat(ship, "Energy"))}   (new hull MaxEnergy {Fmt(ReadFloat(ht, "MaxEnergy"))})");
            Console.WriteLine($"   Ammo(reseed)    {Fmt(ReadFloat(ship, "Ammo"))}   (new hull MaxAmmo {Fmt(ReadFloat(ht, "MaxAmmo"))})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[proof] physics-log reflection error: {ex.Message}");
        }
    }

    private static object ReadProp(object o, string name)
    {
        var p = o.GetType().GetProperty(name);
        return p is null ? throw new InvalidOperationException($"no prop {name} on {o.GetType().Name}") : p.GetValue(o)!;
    }

    private static float ReadFloat(object o, string name)
    {
        var v = ReadProp(o, name);
        return v is float f ? f : Convert.ToSingle(v);
    }

    private static string Fmt(float v) => v.ToString("0.##");

    // ── rendering (real HullModelResolver + ShipRenderer + content models) ──

    private XnaModel LoadModel(string asset)
    {
        if (!_models.TryGetValue(asset, out var m))
        {
            m = Content.Load<XnaModel>("Models/" + asset);
            _models[asset] = m;
        }
        return m;
    }

    private Texture2D LoadTexture(string asset)
    {
        if (!_textures.TryGetValue(asset, out var t))
        {
            t = Content.Load<Texture2D>("Textures/ships/" + asset);
            _textures[asset] = t;
        }
        return t;
    }

    private void RenderHull(GraphicsDevice gd, string civ, short hullId, string name)
    {
        _lastCiv = civ;
        var asset = AssetFor(hullId) ?? HullModelResolver.FallbackAsset;
        var model = LoadModel(asset);
        float renderR = RenderRadius(model);
        float scale = (TileR * 0.62f) / renderR;
        const int S = 720;
        using var rt = new RenderTarget2D(gd, S, S, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.012f, 0.022f, 0.045f));
        gd.DepthStencilState = DepthStencilState.Default;
        var view = Matrix.CreateLookAt(new Vector3(0f, TileR * 0.9f, TileR * 1.7f), Vector3.Zero, Vector3.Up);
        var proj = Matrix.CreateOrthographic(2.6f * TileR, 2.6f * TileR, 1f, 60000f);
        _shipRenderer.Draw(model,
            Matrix.CreateScale(scale) * Matrix.CreateRotationY(-0.7f),
            view, proj, texture: LoadTexture(asset));
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        string caption = $"{name}: hull #{hullId}  {Ascii(HullName(hullId))}  [{asset}]";
        var sz = _font.MeasureString(caption);
        _batch.DrawString(_font, caption, new Vector2(S / 2f - sz.X / 2f, 12f), Color.Orange);
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg($"hull_{name}.jpg", rt);
        Console.WriteLine($"[proof] wrote hull_{name}.jpg  (asset {asset}, hull #{hullId})");
    }

    private void CompositePair(GraphicsDevice gd, string civ, short a, short b, string suffix)
    {
        // Re-render both hulls into one shared RT with labels, then blit the
        // composite from the individual RTs so the visual difference is provable
        // in a single screenshot.
        const int S = 940, half = S / 2;
        using var ca = RenderToRt(gd, civ, a);
        using var cb = RenderToRt(gd, civ, b);
        using var comp = new RenderTarget2D(gd, S, 470, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(comp);
        gd.Clear(new Color(0.005f, 0.011f, 0.024f));
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        _batch.Draw(ca, new Rectangle(0, 0, half, 470), Color.White);
        _batch.Draw(cb, new Rectangle(half, 0, half, 470), Color.White);
        string la = $"BEFORE hull #{a} {Ascii(HullName(a))}";
        string lb = $"AFTER  hull #{b} {Ascii(HullName(b))}";
        var sa = _font.MeasureString(la); var sb = _font.MeasureString(lb);
        _batch.DrawString(_font, la, new Vector2(half / 2f - sa.X / 2f, 4f), Color.Cyan);
        _batch.DrawString(_font, lb, new Vector2(half + half / 2f - sb.X / 2f, 4f), Color.Orange);
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg($"composite_{suffix}.jpg", comp);
        Console.WriteLine($"[proof] wrote composite_{suffix}.jpg  (before #{a} vs after #{b})");
    }

    private RenderTarget2D RenderToRt(GraphicsDevice gd, string civ, short hullId)
    {
        const int S = 920;
        var rt = new RenderTarget2D(gd, S, S, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.006f, 0.013f, 0.028f));
        gd.DepthStencilState = DepthStencilState.Default;
        _lastCiv = civ;
        var asset = AssetFor(hullId) ?? HullModelResolver.FallbackAsset;
        var model = LoadModel(asset);
        float scale = (TileR * 0.62f) / RenderRadius(model);
        var view = Matrix.CreateLookAt(new Vector3(0f, TileR * 1.1f, TileR * 1.9f), Vector3.Zero, Vector3.Up);
        var proj = Matrix.CreateOrthographic(2.4f * TileR, 2.4f * TileR, 1f, 60000f);
        _shipRenderer.Draw(model, Matrix.CreateScale(scale) * Matrix.CreateRotationY(-0.7f), view, proj,
            texture: LoadTexture(asset));
        gd.DepthStencilState = DepthStencilState.None;
        gd.SetRenderTarget(null);
        return rt;
    }

    private static float RenderRadius(XnaModel m)
    {
        BoundingSphere? acc = null;
        foreach (var mesh in m.Meshes)
        {
            var T = mesh.ParentBone.Transform;
            float s = new Vector3(T.M11, T.M12, T.M13).Length();
            var c = Vector3.Transform(mesh.BoundingSphere.Center, T);
            var sph = new BoundingSphere(c, mesh.BoundingSphere.Radius * s);
            acc = acc is null ? sph : BoundingSphere.CreateMerged(acc.Value, sph);
        }
        return acc is null ? 1f : acc.Value.Radius;
    }

    private void SaveJpeg(string name, RenderTarget2D rt)
    {
        using var fs = File.Create(Path.Combine(_o.OutDir, name));
        rt.SaveAsJpeg(fs, rt.Width, rt.Height);
    }

    private static string Ascii(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c >= 32 && c <= 126 ? c : ' ');
        return sb.ToString();
    }
}
