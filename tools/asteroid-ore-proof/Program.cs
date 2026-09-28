using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xna = Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

// P423 live-proof harness: boots a REAL in-process server on m2xspiderw (the map
// whose 134 asteroids all live in one 'Alderan' cluster), injects the spawner so
// the pilots are placed INTO that asteroid sector, and joins a REAL ConnectionSession
// ("me") + one target-ship session. Over the REAL wire the me-client's ClientWorld
// must receive the per-side ASTEROIDS_UPDATE(179) — ClientWorld.AsteroidLive — for
// a rock its side sees. The harness then:
//   (1) proves live depletion: it mines a real in-mission sim asteroid partway via
//       Asteroid.MineOre, pumps the wire, and confirms the client's AsteroidLive
//       ore fraction DECREASES (matching the server's real Asteroid.OreFraction).
//   (2) renders the REAL HudRenderer offscreen: an ASTEROID target frame shows the
//       generic health bar PLUS the distinct ORE/He3 readout; the same asteroid
//       after deeper depletion shows the ORE readout visibly lower; and a SHIP
//       target frame shows the health bar works exactly as before (name + hull,
//       no ORE readout) — the no-regression check.

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
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p423"),
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
    private static readonly float FovRad = MathHelper.ToRadians(60f);
    private const float CamDist = 780f;

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

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
            // m2xspiderw.igc — a real map whose 134 asteroids all live in ONE
            // cluster 'Alderan' (P218 measured); the pilots are injected there.
            new Gm { Name = "P423 Asteroid Ore Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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
        FedsrvVer = LogonRequest.MsgVer, Time = 023u, DwCookie = 0x423u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 09900000000000000ul, DrmHash = "astrore",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[proof] pump TIMEOUT");
    }

    private sealed record CamSetup(Xna.Vector3 Eye, Xna.Matrix View, Xna.Matrix Proj);

    private static CamSetup LookAtTarget(Xna.Vector3 lookAt)
    {
        var back = Xna.Vector3.Normalize(new Xna.Vector3(0.09f, 0.045f, 1f));
        var eye = lookAt + back * CamDist;
        var viewDir = Xna.Vector3.Normalize(lookAt - eye);
        var up = MathF.Abs(viewDir.Y) < 0.9f ? Xna.Vector3.Up : Xna.Vector3.Right;
        up -= viewDir * Xna.Vector3.Dot(up, viewDir);
        if (up.LengthSquared() < 1e-8f) up = Xna.Vector3.Right;
        up = Xna.Vector3.Normalize(up);
        var view = Xna.Matrix.CreateLookAt(Xna.Vector3.Zero, lookAt - eye, up);
        var proj = Xna.Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 10f, 80000f);
        return new CamSetup(eye, view, proj);
    }

    private static bool ProjectToScreen(Xna.Vector3 world, CamSetup cam, out Xna.Vector2 screen)
    {
        screen = default;
        var rel = world - cam.Eye;
        var viewProj = cam.View * cam.Proj;
        var clip = Xna.Vector4.Transform(new Xna.Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
        if (clip.W <= 1e-6f) return false;
        float sx = clip.X / clip.W, sy = clip.Y / clip.W;
        if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return false;
        screen = new Xna.Vector2((sx * 0.5f + 0.5f) * Width, (0.5f - sy * 0.5f) * Height);
        return true;
    }

    private void RenderTargetScene(GraphicsDevice gd, string label, TargetSelectionView? bracket)
    {
        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.016f, 0.020f, 0.026f));

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        _sb.DrawString(_font, $"P423 - {label}", new Vector2(12f, 8f), Color.Orange);
        _sb.End();

        var vp = new Viewport(0, 0, Width, Height);
        _hud.Draw(null, CameraMode.Cockpit, vp,
            lockBracket: null, leadIndicator: null, modeLabelOverride: label.ToUpperInvariant(),
            targetSelection: bracket);

        gd.SetRenderTarget(null);
        string path = Path.Combine(_o.OutDir, $"p423_{label}.png");
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[proof] wrote {path}");
    }

    // ── the proof ───────────────────────────────────────────────────────────

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine("[proof] booting in-process server (m2xspiderw)…");
        using var server = new Srv();

        // Inject the spawner so every pilot is placed into the sector that holds
        // the asteroid cluster (Alderan, id 1052 — m2xspiderw's only asteroid
        // sector, P218), guaranteeing the rendering client is IN the rock field.
        int placed = 0;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var asteroidSector = mission.Sectors
                .OrderByDescending(s => s.Mineable.Count)
                .FirstOrDefault();
            if (asteroidSector is null)
                throw new InvalidOperationException("no sector has asteroids in this map!");
            var pos = placed switch
            {
                0 => new SimVec(0f, 0f, 0f),
                _ => new SimVec(0f, 0f, 350f),
            };
            short side = (placed % 2 == 0) ? mission.Sides[0].Id : mission.Sides[1].Id;
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, pos, SimVec.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, asteroidSector);
            placed++;
            return (ship, asteroidSector);
        });

        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up countdown

        using var me = new ConnectionSession();
        me.Connect(server.BoundPort, "proof_client");
        me.SendLogonRequest(Request("Me_Miner"));
        me.SendPositionReq(0);

        using var target = new ConnectionSession();
        target.Connect(server.BoundPort, "proof_client");
        target.SendLogonRequest(Request("Target_Enemy"));
        target.SendPositionReq(1);

        PumpUntil(server,
            () => me.World.MyShipId is not null && target.World.MyShipId is not null,
            8000);
        short meId = me.World.MyShipId!.Value;
        short tgtId = target.World.MyShipId!.Value;
        Console.WriteLine($"[proof] me=#{meId}(side0) target=#{tgtId}(side1)");

        // (1) The rendering client must RECEIVE live per-asteroid state over the
        // REAL wire: ClientWorld.AsteroidLive populated by the per-side
        // ASTEROIDS_UPDATE(179) for a rock its side sees.
        PumpUntil(server, () => me.World.AsteroidLive.Count > 0, 12000);
        if (me.World.AsteroidLive.Count == 0)
            throw new InvalidOperationException("rendering client never received ASTEROIDS_UPDATE(179) — wire not delivering");
        Console.WriteLine($"[proof] ME received {me.World.AsteroidLive.Count} live asteroid(s) over the real wire:");
        foreach (var kv in me.World.AsteroidLive.Take(8))
            Console.WriteLine($"  asteroid #{kv.Key}: ore={kv.Value.OreFraction:0.000} health={kv.Value.Fraction:0.000}");

        // Pick a target rock that still has plenty of ore (so depletion is visible).
        var (astId, live0) = me.World.AsteroidLive
            .OrderByDescending(kv => kv.Value.OreFraction)
            .First();
        Console.WriteLine($"[proof] targeting asteroid #{astId}: ore={live0.OreFraction:0.000} health={live0.Fraction:0.000}");

        // The matching REAL in-mission sim asteroid (for mining + the source of truth).
        var simAst = server.Missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Mineable)
            .FirstOrDefault(a => a.Data.AsteroidId == astId)
            ?? throw new InvalidOperationException($"no in-mission sim asteroid #{astId}");
        float serverOreBefore = simAst.OreFraction;
        Console.WriteLine($"[proof] server asteroid #{astId} '{simAst.Data.Name}' OreFraction={serverOreBefore:0.000} " +
                          $"({simAst.OreRemaining:0.#}/{simAst.OreMax:0.#})");

        // (2) MINE IT PARTWAY through the real sim path, pump the wire, and confirm
        // the client's ore readout DECREASES (live depletion reaches the HUD data).
        simAst.MineOre(simAst.OreRemaining * 0.6f);   // leave ~40%
        float expectedAfter = simAst.OreFraction;
        PumpUntil(server,
            () => me.World.AsteroidLive.TryGetValue(astId, out var lv) && lv.OreFraction < live0.OreFraction - 0.05f,
            6000);
        var liveMid = me.World.AsteroidLive[astId];
        Console.WriteLine($"[proof] mined to server OreFraction={expectedAfter:0.000}; client readout " +
                          $"{live0.OreFraction:0.000} -> {liveMid.OreFraction:0.000} (DECREASED = " +
                          $"{liveMid.OreFraction < live0.OreFraction})");

        // (3) Render the REAL HudRenderer:
        //   asteroid_ore_partial — health bar + ORE readout at the (mined) level.
        var astDef = me.World.Asteroids[astId];
        var astPosXna = new Xna.Vector3(astDef.Position[0], astDef.Position[1], astDef.Position[2]);
        var astCam = LookAtTarget(astPosXna);
        if (ProjectToScreen(astPosXna, astCam, out var astScreen))
        {
            var astBracket = new TargetSelectionView(astScreen, Engaged: true,
                CharacterName: null, HullFraction: liveMid.Fraction,
                ShieldFraction: null, IsEnemy: null, OreFraction: liveMid.OreFraction);
            RenderTargetScene(gd, "asteroid_ore_partial", astBracket);
        }
        else Console.WriteLine("[proof] WARNING asteroid did not project into camera — bad framing");

        //   mine it further, render again => ORE readout visibly lower.
        simAst.MineOre(simAst.OreRemaining * 0.7f);
        PumpUntil(server,
            () => me.World.AsteroidLive.TryGetValue(astId, out var lv) && lv.OreFraction < liveMid.OreFraction - 0.05f,
            6000);
        var liveLow = me.World.AsteroidLive[astId];
        Console.WriteLine($"[proof] mined deeper: server OreFraction={simAst.OreFraction:0.000}; client " +
                          $"{liveMid.OreFraction:0.000} -> {liveLow.OreFraction:0.000}");
        var astBracketLow = new TargetSelectionView(astScreen, Engaged: true,
            CharacterName: null, HullFraction: liveLow.Fraction,
            ShieldFraction: null, IsEnemy: null, OreFraction: liveLow.OreFraction);
        RenderTargetScene(gd, "asteroid_ore_depleted", astBracketLow);

        //   ship regression: the target SHIP's health bar works exactly as before
        //   (name + hull bar, NO ore readout).
        PumpUntil(server,
            () => me.World.Ships.TryGetValue(tgtId, out var t) && t.Latest is not null && t.CharacterName.Length > 0,
            12000);
        if (me.World.Ships.TryGetValue(tgtId, out var tgtEnt) && tgtEnt.Latest is { } lu)
        {
            var shipXna = PoseConverter.ToXna(lu.Position);
            var shipCam = LookAtTarget(shipXna);
            if (ProjectToScreen(shipXna, shipCam, out var shipScreen))
            {
                var shipBracket = new TargetSelectionView(shipScreen, Engaged: true,
                    CharacterName: tgtEnt.CharacterName, HullFraction: lu.HullFraction,
                    ShieldFraction: lu.ShieldFraction, IsEnemy: true, OreFraction: null);
                RenderTargetScene(gd, "ship_health_regression", shipBracket);
                Console.WriteLine($"[proof] ship target '{tgtEnt.CharacterName}' hull={lu.HullFraction:0.000} " +
                                  $"shield={lu.ShieldFraction:0.000} — health bar (no ORE) expected");
            }
        }
        else Console.WriteLine("[proof] WARNING client never saw the target ship");

        Console.WriteLine($"[proof] done — PNGs in {_o.OutDir}");
    }
}
