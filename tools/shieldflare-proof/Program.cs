using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xna = Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using BotClass = global::Allegiance.Bot.Bot;

// P463b live-proof harness. Boots a REAL in-process server hosting a REAL fight
// and connects REAL bots + a REAL MonoGame-rendering observer (ConnectionSession).
// It harvests the observer's REAL shield-absorbed PROJECTILE_HULL_HIT(213) events
// (AbsorbedByShield=true — the P463 wire signal this task's flare consumes) and:
//
//   PROOF 1 (wire): the observer drained a pile of absorbed hits, all aimed at
//     the shielded victim in its sector — the signal the flare is drawn from.
//   PROOF 2 (visual + placement math): with a REAL graphics device it renders
//     the REAL ShieldFlareParticles + ShieldFlareRenderer offscreen at the real
//     absorbed impact data, framing the struck ship in chase view, and checks
//     programmatically that each flare sits exactly on the ship's shield toward
//     its hit (centre + unitNormal · renderRadius) at the native size — saving
//     PNGs for vision-checking the blue shield-bubble flash.
//
//   MODE other — an armed shooter (side 0) pours a cheap, fully-shield-absorbed
//     round into a shielded enemy target (side 1); the same-side observer
//     receives the hits → flare on ANOTHER ship.
//   MODE my    — an armed enemy shooter (side 1) pours the round into the
//     OBSERVER'S OWN shielded ship (side 0); the observer is a cross-side viewer
//     so it receives its own hits with TargetId == MyShipId → flare on OUR OWN
//     ship in chase view.

int rc = Proof.Run(args);
return rc;

static class Proof
{
    internal const int Width = 960, Height = 640;
    internal static readonly float FovRad = MathHelper.ToRadians(52f);

    public static int Run(string[] args)
    {
        string mode = "other";
        string outDir = "";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--mode": mode = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        if (outDir.Length == 0) outDir = Path.Combine(repo, $".verify_p463b");
        Directory.CreateDirectory(outDir);

        using var game = new ProofRunner(mode, outDir);
        game.Run();
        Console.WriteLine(game.Ok
            ? $"[shieldflare] shieldflare-proof PASS ({mode}: live wire + placement + renders)"
            : "[shieldflare] shieldflare-proof FAILED");
        return game.Ok ? 0 : 1;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    // ── the fight setup (the same cheap, fully-shield-absorbed round that
    //    P463's hitsound-proof used, so dr == ShieldDamage reliably) ────────
    internal static string ArtRoot()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        if (env is { Length: > 0 }) return env;
        return "/home/domingo/Development/Artwork";
    }

    internal static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot(), "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    internal static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon");
    });

    internal static readonly Lazy<DataPartTypeIGC> AbsorbShield = new(() =>
    {
        var core = RepresentativeCore();
        var (_, weapon) = Fight.Value;
        var pt = core.ProjectileTypes[weapon.ProjectileTypeId];
        foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
        {
            var st = (DataPartTypeIGC.Shield)sp.Type;
            if (st.MaxStrength <= 0f) continue;
            float cell = core.Constants.DamageConstants[pt.DamageType][st.DefenseType];
            if (cell <= 0f) continue;
            float absorbed = pt.Power * cell / st.MaxStrength;
            if (absorbed >= 0.2f && absorbed <= 0.5f) return sp;
        }
        throw new InvalidOperationException("no fully-absorbed shield part for the cheap round");
    });

    internal static readonly Lazy<DataHullTypeIGC> SurvivorHull = new(() =>
    {
        var core = RepresentativeCore();
        var pt = core.ProjectileTypes[Fight.Value.weapon.ProjectileTypeId];
        DataHullTypeIGC? best = null; float bestHp = -1f;
        foreach (var h in core.HullTypes.Values)
        {
            float cell = core.Constants.DamageConstants[pt.DamageType][h.DefenseType];
            if (cell > 0f && h.HitPoints > bestHp) { best = h; bestHp = h.HitPoints; }
        }
        if (best is null) throw new InvalidOperationException("no hull the cheap round damages");
        return best.Value;
    });

    internal static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P463b Shield-Flare Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    internal static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 511u, DwCookie = 0x511u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97800000000000000ul, DrmHash = "shieldflareproof",
    };

    internal static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[shieldflare] pump TIMEOUT");
    }

    internal static IEnumerable<Ship> AllShips(Srv server) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models).OfType<Ship>();

    internal static void InjectOther(Srv server)
    {
        const float TargetZ = -180f;
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var (hull, weapon) = Fight.Value;
            if (placed == 0)
            {
                var s = new Ship(id, hull, new SimVec(0f, 0f, 0f), SimVec.Zero, Orientation.Identity);
                s.SideId = 0; s.Energy = hull.MaxEnergy * 100f;
                mission.PlaceShip(s, sector); s.MountWeapon(weapon); placed++;
                return (s, sector);
            }
            if (placed == 1)   // shielded ENEMY target (victim of the shooter)
            {
                var thull = SurvivorHull.Value;
                var s = new Ship(id, thull, new SimVec(0f, 0f, TargetZ), SimVec.Zero, Orientation.Identity);
                s.SideId = 1;
                mission.PlaceShip(s, sector);
                s.MountPart(AbsorbShield.Value, amount: 1, mountId: 0, fractionMounted: 255);
                placed++;
                return (s, sector);
            }
            // the same-side observer viewer
            var v = new Ship(id, hull, new SimVec(-420f, 0f, 60f), SimVec.Zero, Orientation.Identity);
            v.SideId = 0; mission.PlaceShip(v, sector); placed++;
            return (v, sector);
        };
    }

    internal static void InjectMy(Srv server)
    {
        const float VictimZ = -180f;
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var (hull, weapon) = Fight.Value;
            if (placed == 0)   // ENEMY shooter firing at the viewer
            {
                var s = new Ship(id, hull, new SimVec(0f, 0f, 0f), SimVec.Zero, Orientation.Identity);
                s.SideId = 1; s.Energy = hull.MaxEnergy * 100f;
                mission.PlaceShip(s, sector); s.MountWeapon(weapon); placed++;
                return (s, sector);
            }
            // the observer viewer is the VICTIM (side 0, shielded) — its own shield absorbs the enemy's rounds
            var v = new Ship(id, SurvivorHull.Value, new SimVec(0f, 0f, VictimZ), SimVec.Zero, Orientation.Identity);
            v.SideId = 0;
            mission.PlaceShip(v, sector);
            v.MountPart(AbsorbShield.Value, amount: 1, mountId: 0, fractionMounted: 255);
            placed++;
            return (v, sector);
        };
    }
}

sealed class ProofRunner : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _mode;
    private readonly string _outDir;
    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;
    public bool Ok { get; private set; }

    public ProofRunner(string mode, string outDir)
    {
        _mode = mode; _outDir = outDir;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = Proof.Width;
        _gdm.PreferredBackBufferHeight = Proof.Height;
        Content.RootDirectory = Path.Combine(Proof.RepoRoot(), "src", "Allegiance.ClientCore.MonoGame", "Content");
        Directory.CreateDirectory(outDir);
    }

    protected override void Initialize()
    {
        base.Initialize();
        _batch = new SpriteBatch(GraphicsDevice);
        try { _font = Content.Load<SpriteFont>("HUD"); } catch { _font = null!; }
        try { Ok = RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[shieldflare] FATAL: {ex}"); }
        Exit();
    }

    private bool RunProof(GraphicsDevice gd)
    {
        Console.WriteLine($"[shieldflare] booting in-process server (m2xspiderw.igc) MODE {_mode}...");
        using var server = new Srv();
        if (_mode == "my") Proof.InjectMy(server); else Proof.InjectOther(server);
        server.Boot(Proof.Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up lobby
        ushort bound = server.BoundPort;
        Console.WriteLine($"[shieldflare] server transport listening on 127.0.0.1:{bound}");

        var shooter = new BotClass();
        shooter.Connect(bound, "PSHOOT");
        shooter.SendLogonRequest(Proof.Request("PROOFSHOOTER"));

        BotClass? targetBot = null;
        if (_mode == "other")
        {
            targetBot = new BotClass();
            targetBot.Connect(bound, "PTGT");
            targetBot.SendLogonRequest(Proof.Request("PROOFTARGET"));
        }

        using var viewer = new ConnectionSession();
        viewer.Connect(bound, "proof_viewer");
        viewer.SendLogonRequest(Proof.Request("proof_viewer"));

        Proof.PumpUntil(server,
            () => viewer.World.MyShipId is not null
                  && shooter.ShipAssignment is not null
                  && (_mode == "other" ? targetBot!.ShipAssignment is not null : true), 6000);
        short myId = viewer.World.MyShipId!.Value;
        for (int t = 0; t < 300; t++) server.TickSim();   // let same-side exports settle

        // Identify the shielded victim (another ship in MODE other, our own in MODE my).
        Ship victim;
        if (_mode == "my") victim = Proof.AllShips(server).First(s => s.Id == myId);
        else victim = Proof.AllShips(server).First(s => s.SideId == 1);
        short victimId = victim.Id;
        float victimRadius = 0.5f * victim.HullType.Length;   // P333 render scale
        short sectorId = victim.Sector!.Id;
        Console.WriteLine($"[shieldflare] MODE {_mode}: viewer MyShipId={myId}, shielded victim ship {victimId} " +
                          $"'{victim.HullType.Name}' radius {victimRadius:0.#} at ({victim.Position.X:0.#},0,{victim.Position.Z:0.#})");

        shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        Console.WriteLine("[shieldflare] shooter opened fire — harvesting ABSORBED PROJECTILE_HULL_HIT(213) at the observer...");

        // Native absorbs only ~2-4 shots per full shield (each absorbed hit costs
        // the victim 0.2-0.5 of its shield fraction), so to gather a rich pile of
        // flare events we RECHARGE the victim's shield whenever it nears the down
        // threshold and we still need more — the same reflection seam P463's
        // hitsound-proof used to knock the shield. Non-absorbed (hull) hits in the
        // stream are EXPECTED as the shield dips; they are the hull-spark path, not
        // the flare path, so they are counted separately and never fail the proof.
        var absorbed = new List<HullHitEntity>();
        int hullHits = 0;
        var bad = new List<string>();
        int guard = 0;
        const int want = 10;
        while (absorbed.Count < want && guard < 12000)
        {
            server.TickSim(); Thread.Sleep(2);
            lock (viewer.Gate)
            {
                foreach (var ev in viewer.World.TakeNewHullHits())
                {
                    if (!ev.AbsorbedByShield) { hullHits++; continue; }
                    if (ev.ClusterId != sectorId) { bad.Add("wrong sector"); continue; }
                    if (ev.TargetId != victimId) { bad.Add($"target {ev.TargetId} != victim {victimId}"); continue; }
                    absorbed.Add(ev);
                }
            }
            if (victim.ShieldFraction < 0.4f && absorbed.Count < want)
            {
                RechargeShield(victim);
                Console.WriteLine($"[shieldflare] recharged victim shield to 1.0 (had {absorbed.Count}/{want} absorbed hits)");
            }
            guard++;
        }
        Console.WriteLine($"[shieldflare] MODE {_mode}: drained {absorbed.Count} ABSORBED + {hullHits} hull hits "
                          + (bad.Count == 0 ? "(absorbed all correct) " : $"(BAD: {string.Join("; ", bad)} )")
                          + $"guard={guard}");
        if (absorbed.Count < 6 || bad.Count > 0)
        {
            Console.Error.WriteLine($"shieldflare-proof FAILED ({_mode}): expected >=6 absorbed hits on the victim, got {absorbed.Count}.");
            return false;
        }

        // ── PROOF 2 (visual + placement math): render the REAL flare pool +
        // renderer at the real absorbed hits on the victim's shield, chase view.
        bool placed = RenderFlares(gd, absorbed, victim.Position, victimRadius, myId);
        return placed;
    }

    private static void RechargeShield(Ship s)
    {
        var f = typeof(Ship).GetField("_shieldFraction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        f.SetValue(s, 1f);
    }

    private BasicEffect _wireFx = null!;

    private bool RenderFlares(GraphicsDevice gd, List<HullHitEntity> hits,
        SimVec shipCenter, float shipRadius, short victimId)
    {
        _wireFx = new BasicEffect(gd) { VertexColorEnabled = true };
        using var nativeFx = NativeEffectTextures.Load(gd, new DirectoryAssetSource(Proof.ArtRoot()));
        using var flareR = new ShieldFlareRenderer(gd, nativeFx, new DirectoryAssetSource(Proof.ArtRoot()));

        // Emit one flare per absorbed hit, exactly as the client does
        // (Game1.Rendering.cs): snapshot the ship's pose + shield ellipsoid for
        // the curved cap. The native per-ship cap of 4
        // (ShieldFlareParticles.MaxFlaresPerShip) retains only the newest four
        // on one victim — correct native behaviour.
        var pool = new ShieldFlareParticles();
        var sphere = new SimVec(shipRadius, shipRadius, shipRadius);
        foreach (var h in hits)
            pool.Emit(h.TargetId, shipCenter, Orientation.Identity, shipCenter + h.Normal.Normalize() * shipRadius,
                sphere, shipRadius, shipRadius);
        var live = pool.Enumerate().ToList();
        if (live.Count == 0)
        {
            Console.Error.WriteLine("shieldflare-proof FAILED: no flares emitted.");
            return false;
        }

        // Placement assertions (P569): each retained flare's native inputs were
        // snapshotted (sphere shield), and the curved lens30 cap's apex — via
        // the pure native cap math — sits on the shield opposite its hit normal
        // at the native mesh-radius distance (thinggeo.cpp:1289).
        int badPlace = 0;
        int from = hits.Count - live.Count;
        for (int i = 0; i < live.Count; i++)
        {
            var h = hits[from + i];
            var f = live[i];
            if (MathF.Abs(f.Radius - shipRadius) > 1e-3f) badPlace++;
            if (MathF.Abs(f.MeshRadius - shipRadius) > 1e-3f) badPlace++;
            var normal = h.Normal.Normalize();
            var apex = ShieldFlareCapMath.TransformCapPosition(
                new SimVec(0f, 0f, 1f), f.ShipOrientation, f.Center, f.HitWorldPos, f.Ellipse, f.Radius, f.MeshRadius);
            float dist = (apex - f.Center).Length();
            if (MathF.Abs(dist - shipRadius) > 1e-2f * shipRadius) badPlace++;   // apex at mesh radius
            // The cap dome points opposite the outward hit normal (native
            // Matrix(orient,0,m_radiusMesh): cap +Z → orient backward).
            if (Xna.Vector3.Dot(ToXna(apex - f.Center), ToXna(normal)) > -0.999f * shipRadius * shipRadius) badPlace++;
        }
        Console.WriteLine($"[shieldflare] PROOF 2 (placement): {live.Count} flares; " +
                          (badPlace == 0 ? "cap on the shield opposite its hit at native size — PASS" : $"{badPlace} mis-placed — FAIL"));
        if (badPlace > 0) return false;

        var centreX = ToXna(shipCenter);
        var eye = centreX + new Xna.Vector3(0f, 0.6f * shipRadius, 2.4f * shipRadius);   // chase view behind/above
        // P207 small-coordinate convention (exactly as hullhit-proof): the camera
        // sits at the ORIGIN and the geometry is stored relative to the world
        // camera eye, so the view targets centreX - eye from Vector3.Zero.
        var view = Matrix.CreateLookAt(Xna.Vector3.Zero, centreX - eye, Xna.Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(Proof.FovRad, (float)Proof.Width / Proof.Height, 1f, 80000f);

        // Real scene: the absorbed hits' flares on the wireframe shield bubble.
        RenderFrame(gd, flareR, pool, centreX, shipRadius, eye, view, proj,
            $"MODE_{_mode}_real_birth_age0", 0f, "REAL absorbed hits on the struck ship's shield");
        RenderFrame(gd, flareR, pool, centreX, shipRadius, eye, view, proj,
            $"MODE_{_mode}_real_fade_age039", ShieldFlareParticles.LifetimeSeconds * 0.525f, "REAL absorbed hits (mid-fade)");

        // Synthetic direction fan (clearly labelled): flares emitted at eight
        // DISTINCT hit directions around the same ship (separate target keys, so
        // the per-ship cap doesn't hide them), to show the flare lands on the
        // shield toward ITS OWN hit — the direction-dependence of the placement.
        var fan = new ShieldFlareParticles();
        var dirs = new[]
        {
            new SimVec(1,0,0), new SimVec(-1,0,0), new SimVec(0,1,0), new SimVec(0,-1,0),
            new SimVec(0,0,1), new SimVec(0,0,-1),
            new SimVec(0.578f,0.579f,0.576f), new SimVec(-0.581f,0.574f,-0.577f),
        };
        for (int d = 0; d < dirs.Length; d++)
            fan.Emit((short)(9000 + d), shipCenter, Orientation.Identity,
                shipCenter + dirs[d].Normalize() * shipRadius, sphere, shipRadius, shipRadius);
        RenderFrame(gd, flareR, fan, centreX, shipRadius, eye, view, proj,
            $"MODE_{_mode}_DIRECTION_FAN_synthetic", 0f, "SYNTHETIC direction fan — each flare on its own hit direction");
        return true;
    }

    private void RenderFrame(GraphicsDevice gd, ShieldFlareRenderer flareR, ShieldFlareParticles pool,
        Xna.Vector3 shipCentre, float shipRadius, Xna.Vector3 cameraEye, Matrix view, Matrix proj,
        string name, float ageSec, string label)
    {
        int steps = Math.Max(1, (int)MathF.Round(ageSec / 0.008f));
        for (int i = 0; i < steps; i++) pool.Update(0.008f);
        var draw = pool.Enumerate().ToList();
        var rings = BuildShieldWire(shipCentre, shipRadius, cameraEye);

        using var rt = new RenderTarget2D(gd, Proof.Width, Proof.Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.012f, 0.014f, 0.027f));   // deep space
        gd.DepthStencilState = DepthStencilState.Default;
        DrawShieldWire(gd, rings, view, proj);
        if (draw.Count > 0)
            flareR.Draw(draw, cameraEye, view, proj);
        Caption(gd, $"SHIELD FLARE ({_mode}) — {label}   {draw.Count} flare(s), ship radius {shipRadius:0.#}  {name}");
        gd.SetRenderTarget(null);
        Save(gd, rt, $"shieldflare_{name}.png");
    }

    // Great circles of the shield bubble (XY/XZ/YZ), as line lists relative to
    // the camera eye (the P207 small-coordinate convention) — a dim wireframe so
    // the flare's position ON the shield is visually obvious.
    private VertexPositionColor[] BuildShieldWire(Xna.Vector3 centre, float radius, Xna.Vector3 eye)
    {
        var c = centre - eye;
        const int seg = 48;
        var col = new Color(0.30f, 0.42f, 0.62f, 0.55f);
        var pts = new List<VertexPositionColor>(seg * 6);
        void Ring(Xna.Vector3 a, Xna.Vector3 b)
        {
            for (int i = 0; i < seg; i++)
            {
                float t0 = (i / (float)seg) * MathF.PI * 2f;
                float t1 = ((i + 1) / (float)seg) * MathF.PI * 2f;
                var p0 = c + a * (MathF.Cos(t0) * radius) + b * (MathF.Sin(t0) * radius);
                var p1 = c + a * (MathF.Cos(t1) * radius) + b * (MathF.Sin(t1) * radius);
                pts.Add(new VertexPositionColor(p0, col));
                pts.Add(new VertexPositionColor(p1, col));
            }
        }
        Ring(Xna.Vector3.UnitX, Xna.Vector3.UnitY);
        Ring(Xna.Vector3.UnitX, Xna.Vector3.UnitZ);
        Ring(Xna.Vector3.UnitY, Xna.Vector3.UnitZ);
        return pts.ToArray();
    }

    private void DrawShieldWire(GraphicsDevice gd, VertexPositionColor[] lines, Matrix view, Matrix proj)
    {
        if (lines.Length == 0) return;
        _wireFx.World = Matrix.Identity;
        _wireFx.View = view;
        _wireFx.Projection = proj;
        _wireFx.CurrentTechnique.Passes[0].Apply();
        gd.DrawUserPrimitives(PrimitiveType.LineList, lines, 0, lines.Length / 2);
    }

    private void Caption(GraphicsDevice gd, string text)
    {
        gd.DepthStencilState = DepthStencilState.None;
        if (_font is null) return;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        var sz = _font.MeasureString(text);
        _batch.DrawString(_font, text, new Vector2(Proof.Width / 2f - sz.X / 2f, 8f), Color.Orange);
        _batch.End();
    }

    private void Save(GraphicsDevice gd, RenderTarget2D rt, string file)
    {
        string path = Path.Combine(_outDir, file);
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[shieldflare] wrote {path}");
    }

    private static Xna.Vector3 ToXna(SimVec v) => new(v.X, v.Y, v.Z);
}
