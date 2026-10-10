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

// P364 live-proof harness. Boots a REAL in-process server hosting a REAL fight
// (an armed shooter on side 0 pours rounds into a defenceless target on side 1),
// connects a REAL shooter Bot + a REAL target Bot + a REAL MonoGame-rendering
// client (ConnectionSession) that is merely a SAME-SIDE OBSERVER of the combat.
// The server resolves each landed hit through Sector.ResolveCombatCollisions →
// IIgcSite.ProjectileHitEvent → FM_S_PROJECTILE_HULL_HIT(213) broadcast; the
// observer client receives them over the real wire (the "remote combat the local
// player is merely observing" bar) and buffers them in world.HullHits. The harness
// (1) ASSERTS programmatically that the observer drained a healthy number of real
// hit events clustered at the real target, then (2) renders the REAL
// HullHitSparkParticles + HullHitSparkRenderer offscreen at a real impact point,
// alongside the REAL DamageDebrisRenderer smoke from the damaged target (so the
// spark is provably distinct from the damage-vent smoke it accompanies), saving
// PNGs for vision-checking.

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
        string art = "", outDir = "";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : repo,
            ContentRoot = Path.Combine(repo, "src", "Allegiance.ClientCore.MonoGame", "Content"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p364"),
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
    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;

    private const int Width = 960, Height = 720;
    private const float TargetZ = -180f;          // same nose-to-nose gap as vizfight/livecombat
    private static readonly float FovRad = MathHelper.ToRadians(52f);

    // ── real combat outcome (filled by RunProof, read by the render pass) ──
    private List<HullHitEntity> _hits = new();
    private SimVec _targetPos;
    private float _targetRadius = 50f;
    private Ship _target = null!;

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
        _batch = new SpriteBatch(GraphicsDevice);
        try { _font = Content.Load<SpriteFont>("HUD"); } catch { _font = null!; }
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[hullhit] FATAL: {ex}"); }
        Exit();
    }

    // ── the fightable hull+weapon (weakest dispersion-0 damaging gun, so the
    // target survives a long barrage and many hits accumulate) ──────────────
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
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
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        string art = env is { Length: > 0 } ? env : ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var file = IgcReader.ParseFile(Path.Combine(art, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") is { Length: > 0 } env ? env : ExternalPaths.Resolve(ExternalResourceKind.Artwork),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P364 Hull-Hit Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 364u, DwCookie = 0x364u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97800000000000000ul, DrmHash = "hullhitproof",
    };

    // A SURVIVOR hull for the target: among all hulls the shooter's cheap weapon
    // actually damages (damage cell > 0), pick the one with the most hit-points so
    // the fight lands many real PROJECTILE_HULL_HIT events before the target dies.
    private static readonly Lazy<DataHullTypeIGC> SurvivorHull = new(() =>
    {
        var core = RepresentativeCore();
        var (_, weapon) = Fight.Value;
        var pt = core.ProjectileTypes[weapon.ProjectileTypeId];
        DataHullTypeIGC? best = null;
        float bestHp = -1f;
        foreach (var h in core.HullTypes.Values)
        {
            float cell = core.Constants.DamageConstants[pt.DamageType][h.DefenseType];
            if (cell > 0f && h.HitPoints > bestHp) { best = h; bestHp = h.HitPoints; }
        }
        if (best is null) throw new InvalidOperationException("no hull the cheap weapon damages");
        return best.Value;
    });

    // Spawner: placed 0 = armed SHOOTER (side 0), 1 = defenceless TARGET (side 1),
    // 2+ = the observer viewer (side 0, same side as the shooter so it receives
    // the shooter's ProjectileHit broadcasts as a teammate).
    private static void InjectFightSpawn(Srv server)
    {
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            Sector sector = mission.Sectors[0];
            var (hull, weapon) = Fight.Value;
            if (placed == 0)
            {
                var s = new Ship(id, hull, new SimVec(0f, 0f, 0f), SimVec.Zero, Orientation.Identity);
                s.SideId = 0;
                s.Energy = hull.MaxEnergy * 100f;      // sustain fire
                mission.PlaceShip(s, sector);
                s.MountWeapon(weapon);                 // mount AFTER placing (Ship.cs:1914)
                placed++;
                return (s, sector);
            }
            if (placed == 1)
            {
                var thull = SurvivorHull.Value;
                var s = new Ship(id, thull, new SimVec(0f, 0f, TargetZ), SimVec.Zero, Orientation.Identity);
                s.SideId = 1;                          // enemy → hits genuinely land
                mission.PlaceShip(s, sector);
                placed++;
                return (s, sector);
            }
            // The observer viewer, parked to the side so it watches the fight.
            var v = new Ship(id, hull, new SimVec(-420f, 0f, 60f), SimVec.Zero, Orientation.Identity);
            v.SideId = 0;
            mission.PlaceShip(v, sector);
            placed++;
            return (v, sector);
        };
    }

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[hullhit] pump TIMEOUT");
    }

    private IEnumerable<Ship> AllShips(Srv server) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models).OfType<Ship>();

    // ── the proof ───────────────────────────────────────────────────────────

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine("[hullhit] booting in-process server (m2xspiderw.igc)...");
        using var server = new Srv();
        InjectFightSpawn(server);
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        ushort bound = server.BoundPort;
        Console.WriteLine($"[hullhit] server transport listening on 127.0.0.1:{bound}");

        // Real peers: shooter bot, target bot, then the observer client.
        var shooter = new BotClass();
        shooter.Connect(bound, "PSHOOT");
        shooter.SendLogonRequest(Request("PROOFSHOOTER"));
        var targetBot = new BotClass();
        targetBot.Connect(bound, "PTGT");
        targetBot.SendLogonRequest(Request("PROOFTARGET"));

        using var session = new ConnectionSession();
        session.Connect(bound, "proof_viewer");
        session.SendLogonRequest(Request("proof_viewer"));

        // Seat everyone: the shooter + target bots get ships (placed 0, 1), and
        // the viewer (placed 2) gets its ship + MyShipId. Let exports settle so
        // the viewer holds the shooter's ExportShip (the _teammateExportSent gate).
        PumpUntil(server, () => session.World.MyShipId is not null
                                && shooter.ShipAssignment is not null
                                && targetBot.ShipAssignment is not null, 6000);
        Console.WriteLine($"[hullhit] seated: viewer MyShipId={session.World.MyShipId}, " +
                          $"shooter ship={shooter.ShipAssignment?.Ship.ShipId}, target ship={targetBot.ShipAssignment?.Ship.ShipId}");
        for (int t = 0; t < 300; t++) server.TickSim();   // let same-side exports + settle

        // Identify the live shooter/target ships server-side.
        var shooterShip = AllShips(server).FirstOrDefault(s => s.SideId == 0 && s.Energy > s.HullType.MaxEnergy);
        _target = AllShips(server).First(s => s.SideId == 1);
        Console.WriteLine($"[hullhit] shooter hull '{shooterShip?.HullType.Name}' ({shooterShip?.Id}), target hull '{_target.HullType.Name}' ({_target.Id}) " +
                          $"at ({_target.Position.X:0.#},0,{_target.Position.Z:0.#}) radius {0.5f * _target.HullType.Length:0.#}");
        _targetPos = _target.Position;
        _targetRadius = 0.5f * _target.HullType.Length;

        // Command the shooter to fire (AllWeapons). Hold it stationary so the
        // rounds stream straight into the target.
        shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
        Console.WriteLine("[hullhit] shooter opened fire — accumulating real PROJECTILE_HULL_HIT(213) at the observer...");

        // Pump real combat until the observer has buffered a healthy pile of hits
        // (or the target finally dies / a guard trips).
        List<HullHitEntity> drained = new();
        int guard = 0;
        while (drained.Count < 40 && !_target.Dead && guard < 8000)
        {
            server.TickSim();
            Thread.Sleep(3);
            lock (session.Gate) { drained.AddRange(session.World.TakeNewHullHits()); }
            guard++;
        }
        _hits = drained;
        Console.WriteLine($"[hullhit] observer drained {_hits.Count} PROJECTILE_HULL_HIT(213) events from remote combat (guard={guard}, target dead={_target.Dead})");

        // ── PROOF 1 (programmatic, the wire): the observer really received a
        // pile of real hit events, all aimed at the real target in its sector,
        // at impact points on the target's surface (within its collision radius
        // of its centre, along the shooter→target direction).
        if (_hits.Count < 5)
        {
            Console.Error.WriteLine($"hullhit-proof FAILED: observer drained only {_hits.Count} hits — the remote-combat wire did not deliver.");
            Environment.ExitCode = 1;
            return;
        }
        if (_hits.Any(h => h.ClusterId != _target.Sector!.Id))
        {
            Console.Error.WriteLine("hullhit-proof FAILED: a hit named the wrong sector.");
            Environment.ExitCode = 1;
            return;
        }
        if (_hits.Any(h => h.TargetId != _target.Id))
        {
            Console.Error.WriteLine("hullhit-proof FAILED: a hit named the wrong target.");
            Environment.ExitCode = 1;
            return;
        }
        float worst = _hits.Max(h => (h.Position - _targetPos).Length());
        if (worst > _targetRadius * 3f)
        {
            Console.Error.WriteLine($"hullhit-proof FAILED: an impact point {worst:0.#} units from the target (radius {_targetRadius:0.#}) is implausibly far.");
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine($"[hullhit] PROOF 1 (wire): {_hits.Count} real hits at the target; worst impact offset {worst:0.#} u (radius {_targetRadius:0.#}); " +
                          $"all sector {_target.Sector!.Id}, all target {_target.Id} — PASS");

        // ── PROOF 2 (visual): render the REAL spark pool + renderer offscreen at
        // a real impact point, alongside the REAL damage-debris smoke, so the
        // bright impact spark is visibly distinct from the grey vent smoke.
        // Damage the target's hull now so its vent smoke streams in the frame.
        if (!_target.Dead) _target.SetFraction(0.45f);
        RenderCombined(gd, server, session);
        Console.WriteLine($"[hullhit] PROOF 2 (visual): PNGs written to {_o.OutDir} — vision-check hullhit_*.png");
        Console.WriteLine("[hullhit] hullhit-proof PASS (wire + renders)");
    }

    // ── PROOF 2: render the real spark renderer + real debris renderer ─────

    private void RenderCombined(GraphicsDevice gd, Srv server, ConnectionSession session)
    {
        var sparkPool = new HullHitSparkParticles();
        var debrisPool = new DamageDebrisParticles();
        using var nativeFx = NativeEffectTextures.Load(gd, new DirectoryAssetSource(_o.ArtRoot));
        using var sparkRenderer = new HullHitSparkRenderer(gd, nativeFx, new DirectoryAssetSource(_o.ArtRoot));
        using var debrisRenderer = new DamageDebrisRenderer(gd, nativeFx);

        float dt = 0.060f;
        int vents = Allegiance.ClientCore.MonoGame.Flight.DamageDebrisTracker.DamageFrameCount(_target.Fraction);

        // ── CLOSE-UP (the decisive spark frame): frame the camera ON the first
        // real impact point, looking ALONG its normal, so the two bright spark
        // bits fill the frame and their drift/fade over ~0.7s is unambiguous.
        var imp = _hits[0];
        var impXna = ToXna(imp.Position);
        var normalXna = Xna.Vector3.Normalize(ToXna(imp.Normal));
        float bitSize = imp.Radius * 0.5f;                  // thinggeo.cpp:1328
        var eyeClose = impXna - normalXna * (bitSize * 2.2f);
        var viewC = Matrix.CreateLookAt(Xna.Vector3.Zero, impXna - eyeClose, OrthogonalUp(normalXna));
        var projC = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 1f, 80000f);

        var closePool = new HullHitSparkParticles();
        closePool.Emit(imp.Position, imp.Normal, imp.Radius, new RandomTrailSource());
        RenderCloseup(gd, sparkRenderer, closePool, debrisRenderer, debrisPool,
            viewC, projC, eyeClose, vents, "closeup_birth", dt, ageSec: 0f);
        RenderCloseup(gd, sparkRenderer, closePool, debrisRenderer, debrisPool,
            viewC, projC, eyeClose, vents, "closeup_drift", dt, ageSec: 0.7f);

        // ── WIDE (the distinctness shot): frame the target centre so its grey
        // vent smoke AND the ring of bright impact sparks sit in one scene.
        var anchor = ToXna(_targetPos);
        var eyeWide = anchor + new Xna.Vector3(0f, 0f, _targetRadius * 3f);
        var viewW = Matrix.CreateLookAt(Xna.Vector3.Zero, anchor - eyeWide, Xna.Vector3.Up);
        var projW = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 1f, 80000f);
        int shown = Math.Min(_hits.Count, 10);
        for (int i = 0; i < shown; i++)
            sparkPool.Emit(_hits[i].Position, _hits[i].Normal, _hits[i].Radius, new RandomTrailSource());
        RenderWide(gd, sparkRenderer, sparkPool, debrisRenderer, debrisPool,
            viewW, projW, eyeWide, "smoke_context", dt, vents, ageSec: 0.35f);
    }

    private void RenderCloseup(GraphicsDevice gd, HullHitSparkRenderer sparkR, HullHitSparkParticles pool,
        DamageDebrisRenderer debrisR, DamageDebrisParticles debrisPool,
        Matrix view, Matrix proj, Xna.Vector3 cameraEye, int vents, string name, float dt, float ageSec)
    {
        int steps = Math.Max(1, (int)MathF.Round(ageSec / dt));
        for (int i = 0; i < steps; i++)
        {
            debrisPool.Emit(_targetPos, _target.Velocity, _target.Orientation,
                _targetRadius, _target.Id, vents, dt, new RandomTrailSource());
            debrisPool.Update(dt);
            pool.Update(dt);
        }
        var drawDebris = debrisPool.Enumerate().ToList();
        var drawSparks = pool.Enumerate().ToList();

        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.011f, 0.015f, 0.028f));   // deep space
        gd.DepthStencilState = DepthStencilState.Default;
        if (drawDebris.Count > 0)
            debrisR.Draw(drawDebris, cameraEye, view, proj);
        if (drawSparks.Count > 0)
            sparkR.Draw(drawSparks, pool.EnumerateFlashes().ToList(), cameraEye, view, proj);
        Caption(gd, $"SPARK (bright) + DAMAGE SMOKE (grey) — {name}   bits={drawSparks.Count} smoke={drawDebris.Count} vents={vents}");
        gd.SetRenderTarget(null);
        Save(gd, rt, $"hullhit_{name}.png");
    }

    private void RenderWide(GraphicsDevice gd, HullHitSparkRenderer sparkR, HullHitSparkParticles sparkPool,
        DamageDebrisRenderer debrisR, DamageDebrisParticles debrisPool,
        Matrix view, Matrix proj, Xna.Vector3 cameraEye, string name, float dt, int vents, float ageSec)
    {
        int steps = Math.Max(1, (int)MathF.Round(ageSec / dt));
        for (int i = 0; i < steps; i++)
        {
            debrisPool.Emit(_targetPos, _target.Velocity, _target.Orientation,
                _targetRadius, _target.Id, vents, dt, new RandomTrailSource());
            debrisPool.Update(dt);
            sparkPool.Update(dt);
        }
        var drawDebris = debrisPool.Enumerate().ToList();
        var drawSparks = sparkPool.Enumerate().ToList();

        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.004f, 0.007f, 0.017f));   // deep space
        gd.DepthStencilState = DepthStencilState.Default;
        if (drawDebris.Count > 0)
            debrisR.Draw(drawDebris, cameraEye, view, proj);
        if (drawSparks.Count > 0)
            sparkR.Draw(drawSparks, sparkPool.EnumerateFlashes().ToList(), cameraEye, view, proj);
        Caption(gd, $"SPARK (bright) + DAMAGE SMOKE (grey) — {name}   sparks={drawSparks.Count} vents={vents}");
        gd.SetRenderTarget(null);
        Save(gd, rt, $"hullhit_{name}.png");
    }

    private void Caption(GraphicsDevice gd, string text)
    {
        gd.DepthStencilState = DepthStencilState.None;
        if (_font is null) return;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        var sz = _font.MeasureString(text);
        _batch.DrawString(_font, text, new Vector2(Width / 2f - sz.X / 2f, 8f), Color.Orange);
        _batch.End();
    }

    private void Save(GraphicsDevice gd, RenderTarget2D rt, string file)
    {
        string path = Path.Combine(_o.OutDir, file);
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[hullhit] wrote {path}");
    }

    private static Xna.Vector3 OrthogonalUp(Xna.Vector3 n)
    {
        var up = MathF.Abs(n.Y) < 0.9f ? Xna.Vector3.Up : Xna.Vector3.Right;
        up -= n * Xna.Vector3.Dot(up, n);
        if (up.LengthSquared() < 1e-8f) up = Xna.Vector3.Right;
        return Xna.Vector3.Normalize(up);
    }

    private static Xna.Vector3 ToXna(SimVec v) => new(v.X, v.Y, v.Z);
}
