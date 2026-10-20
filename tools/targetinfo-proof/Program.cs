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

// P359 live-proof harness: boots a REAL in-process server with two REAL client
// sessions (one is "me"/rendering client, the other is the TARGET ship), and once
// the rendering client can actually SEE the target ship in its live world tables
// (its real CharacterName + live hull/shield fractions + position, exported over
// the real wire), it projects the target like Game1.ComputeTargetSelectionBracket
// does and renders the REAL HudRenderer offscreen — the pilot-name + health-bar
// readout beneath the bracket, and the bearing arrow. It then DAMAGES the target
// ship through the server's real Ship.ReceiveDamage path, pumps the wire so the
// rendering client observes the lowered hull fraction, and renders AGAIN to prove
// the bar visibly drops live. Finally it renders a station target (no readout,
// but bearing present) and an off-screen target (bearing arrow pointing the right
// way when the target is out of view).

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
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p359"),
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

    // Access to the server's internal ship table (Server.Ships is internal) and
    // the target Ship's damage method — the same reflection seam hullswap-proof
    // and the live tests already use to reach internal server state.
    private static readonly PropertyInfo ShipsProp =
        typeof(Srv).GetProperty("Ships", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Server.Ships property not found");

    // P368 placed each side in its OWN home sector (side 0 and side 1 spawn in
    // different sectors of XXV_simple), so a cross-side client would never see an
    // opposing ship unless they share a sector. P376 needs opposing AND same-side
    // ships mutually visible to prove the bracket colour, so force every ship into
    // mission.Sectors[0] via the same ShipSpawner injection seam the P375
    // CrossSideCombat live test uses (reflection — Server.ShipSpawner is internal).
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

    // ── server / client wiring (mirrors warp-visual-proof / hullswap-proof) ──

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: the proof picks sides explicitly; keep the pre-flag fallback
        Games =
        [
            new Gm { Name = "P359 Target Info Proof", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return env is { Length: > 0 } e2 ? e2 : ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 961u, DwCookie = 0x359u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 94100000000000000ul, DrmHash = "tgtproof",
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
        Console.WriteLine("[proof] booting in-process server (XXV_simple)…");
        using var server = new Srv();
        // P376 — force every ship (both sides + both teammates) into Sectors[0] so
        // the four pilots are all mutually visible, replicating the pre-P368
        // same-sector world the P359 proof was built against. Distinct spawn offsets
        // keep the ships apart; sides alternate 0/1 in seating order.
        int placed = 0;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var s0 = mission.Sectors[0];
            var pos = placed switch
            {
                0 => new SimVec(0f, 0f, 0f),
                1 => new SimVec(0f, 0f, 550f),
                2 => new SimVec(550f, 0f, 0f),
                _ => new SimVec(550f, 0f, 550f),
            };
            short side = (placed % 2 == 0) ? mission.Sides[0].Id : mission.Sides[1].Id;
            var ship = new Ship(shipId, hull, pos, SimVec.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, s0);
            placed++;
            return (ship, s0);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up countdown

        using var me = new ConnectionSession();
        me.Connect(server.BoundPort, "proof_client");
        me.SendLogonRequest(Request("Me_Fighter"));

        using var target = new ConnectionSession();
        target.Connect(server.BoundPort, "proof_client");
        target.SendLogonRequest(Request("Target_Enemy"));

        // P376 — one more pilot PER SIDE so each viewer has a real same-side ship to
        // target (friendly/blue) alongside the cross-side ship (enemy/red). This is
        // what lets us prove the bracket colour is RELATIVE to the viewer: side 0's
        // pilot sees side 1's ship as red and its own teammate as blue, and side 1's
        // pilot sees the mirror image.
        using var friendA = new ConnectionSession();
        friendA.Connect(server.BoundPort, "proof_client");
        friendA.SendLogonRequest(Request("Friend_Alpha"));

        using var friendB = new ConnectionSession();
        friendB.Connect(server.BoundPort, "proof_client");
        friendB.SendLogonRequest(Request("Teammate_Bravo"));

        // Pick sides explicitly (0/1) so the spawner seats us immediately instead of
        // waiting out the opt-in 30 s auto-assign on the side-select screen (P763
        // enables that flag above; it is off by default).
        me.SendPositionReq(0);
        target.SendPositionReq(1);
        friendA.SendPositionReq(0);
        friendB.SendPositionReq(1);

        PumpUntil(server,
            () => me.World.MyShipId is not null && target.World.MyShipId is not null
                  && friendA.World.MyShipId is not null && friendB.World.MyShipId is not null,
            6000);
        short meId = me.World.MyShipId!.Value;
        short tgtId = target.World.MyShipId!.Value;
        short friendAId = friendA.World.MyShipId!.Value;
        short friendBId = friendB.World.MyShipId!.Value;
        Console.WriteLine($"[proof] me=#{meId}(side0) target=#{tgtId}(side1) friendA=#{friendAId}(side0) friendB=#{friendBId}(side1)");

        // The rendering client ("me") must actually SEE the target ship: live hull
        // fraction + a name + a position, exported over the real wire.
        PumpUntil(server,
            () => me.World.Ships.TryGetValue(tgtId, out var t)
                  && t.Latest is not null && t.CharacterName.Length > 0,
            10000);
        if (!me.World.Ships.TryGetValue(tgtId, out var tgtEnt))
            throw new InvalidOperationException("rendering client never saw the target ship");
        Console.WriteLine($"[proof] ME sees target: name='{tgtEnt.CharacterName}' " +
                          $"hull={tgtEnt.Latest!.HullFraction:0.000} shield={tgtEnt.Latest.ShieldFraction:0.000} " +
                          $"pos=({tgtEnt.Latest.Position.X:0.#},{tgtEnt.Latest.Position.Y:0.#},{tgtEnt.Latest.Position.Z:0.#})");

        // Healthy render (camera framed so the target is centered in front of it).
        var targetXna = PoseConverter.ToXna(tgtEnt.Latest.Position);
        var centerCam = LookAtTarget(targetXna);

        RenderTargetScene(gd, "ship_healthy",
            BuildBracket(tgtEnt, centerCam),
            BuildBearing(tgtEnt.Latest.Position, centerCam));

        // P376 — friend/enemy bracket colour, proven from BOTH sides' perspective.
        // First make sure each viewer genuinely SEES the two ships it must target
        // (its cross-side enemy + its own same-side teammate), then render the real
        // HudRenderer bracket for each, deriving isEnemy exactly as Game1 does:
        // viewer.MySideId vs the target ship's SideId.
        PumpUntil(server, () =>
            me.World.Ships.TryGetValue(friendAId, out var ca) && ca.Latest is not null
            && target.World.Ships.TryGetValue(meId, out var ma) && ma.Latest is not null
            && target.World.Ships.TryGetValue(friendBId, out var db) && db.Latest is not null,
            10000);
        var enemyFromAlpha = me.World.Ships[tgtId];           // side0 viewer -> side1 ship: RED
        var friendlyFromAlpha = me.World.Ships[friendAId];    // side0 viewer -> side0 ship: BLUE
        var enemyFromBravo = target.World.Ships[meId];        // side1 viewer -> side0 ship: RED
        var friendlyFromBravo = target.World.Ships[friendBId];// side1 viewer -> side1 ship: BLUE
        Console.WriteLine($"[proof] sides: alpha viewer={me.World.MySideId} target(side1)SideId={enemyFromAlpha.SideId} " +
                          $"friendA.SideId={friendlyFromAlpha.SideId}  |  bravo viewer={target.World.MySideId} " +
                          $"me(side0)SideId={enemyFromBravo.SideId} friendB.SideId={friendlyFromBravo.SideId}");
        RenderColorCase(gd, "alpha_sees_enemy_red", me.World, enemyFromAlpha);
        RenderColorCase(gd, "alpha_sees_friendly_blue", me.World, friendlyFromAlpha);
        RenderColorCase(gd, "bravo_sees_enemy_red", target.World, enemyFromBravo);
        RenderColorCase(gd, "bravo_sees_friendly_blue", target.World, friendlyFromBravo);

        // Damage the target through the server's real ReceiveDamage path, pump the
        // wire, then re-render to prove the health bar drops live. Gate on the
        // target having fully LAUNCHED first — a peer seated but still transitioning
        // gets RepairAndRefuel at launch which resets the fraction to 1.0, undoing
        // any damage (observed when the target was damaged before it undocked).
        short stationOfTarget = -1;
        if (target.World.Ships.TryGetValue(tgtId, out var ent0)) stationOfTarget = ent0.StationId;
        PumpUntil(server, () =>
        {
            if (!target.World.Ships.TryGetValue(tgtId, out var e)) return false;
            return e.StationId == -1 && e.Latest is not null;
        }, 8000);
        Console.WriteLine($"[proof] target launched (was StationId={stationOfTarget}); preparing damage");

        double now = 0.0;
        float before = tgtEnt.Latest!.HullFraction;
        var tgtPosSim = tgtEnt.Latest.Position;
        var fractionProp = typeof(global::Allegiance.Core.Sim.Ship).GetProperty("Fraction")
            ?? throw new InvalidOperationException("Ship.Fraction property not found");
        float serverBefore = (float)fractionProp.GetValue(FindServerShip(server, tgtId))!;
        int shots = 0;
        float serverAfter = serverBefore;
        var damageMethod = typeof(global::Allegiance.Core.Sim.Ship).GetMethod("ReceiveDamage")
            ?? throw new InvalidOperationException("Ship.ReceiveDamage not found");
        while (serverAfter > serverBefore * 0.5f && shots < 60)
        {
            var tgtShip = FindServerShip(server, tgtId);
            damageMethod.Invoke(tgtShip, new object?[] { (byte)0, 120f, now, tgtPosSim, tgtPosSim, null });
            now += 1.0;
            shots++;
            server.TickSim();
            serverAfter = (float)fractionProp.GetValue(tgtShip)!;
        }
        Console.WriteLine($"[proof] server hull {serverBefore:0.000} -> {serverAfter:0.000} after {shots} damage shots");
        if (serverAfter >= serverBefore)
            Console.WriteLine("[proof] WARNING: target hull did not drop!");

        PumpUntil(server,
            () => me.World.Ships.TryGetValue(tgtId, out var td) && td.Latest is { } lu && lu.HullFraction < before,
            4000);
        me.World.Ships.TryGetValue(tgtId, out var tgtDamaged);
        Console.WriteLine($"[proof] rendering client now observes hull={tgtDamaged!.Latest!.HullFraction:0.000}");
        RenderTargetScene(gd, "ship_damaged",
            BuildBracket(tgtDamaged, centerCam),
            BuildBearing(tgtDamaged.Latest.Position, centerCam));

        // Station target: bracket + bearing, but NO name/readout (no crash, no bogus
        // empty bar) — proving the non-ship degrade.
        var st = me.World.Stations.Values.FirstOrDefault();
        if (st is { } station)
        {
            RenderTargetScene(gd, "station_no_readout",
                BuildNonShipBracket(station, centerCam),
                BuildBearing(new SimVec(station.Position[0], station.Position[1], station.Position[2]), centerCam));
            Console.WriteLine($"[proof] station target '{station.Name}' — no readout expected");
        }
        else
        {
            Console.WriteLine("[proof] (no station seen by client — skipping station render)");
        }

        // Off-screen bearing: same real target, but a camera that is turned AWAY from
        // it so the target sits 45° OFF to the LEFT — well outside the 30° half-FOV,
        // so the target is genuinely out of the view frustum. The bracket must vanish
        // (out of view -> null) but the bearing arrow must point LEFT (the real
        // bearing). The target position is PINNED at this instant so the geometry is
        // exact and self-verifying (the ship otherwise drifts between reads).
        var pinXna = PoseConverter.ToXna(tgtEnt.Latest.Position);
        var offCam = OffAxisCam(pinXna, angleDeg: 45f);
        bool offInFrustum = ProjectToScreen(pinXna, offCam, out _);
        var offBearing = BuildBearing(tgtEnt.Latest.Position, offCam);
        Console.WriteLine($"[proof] offscreen check: target-in-frustum={offInFrustum} (expect False), " +
                          $"bearing=({offBearing.X:0.00},{offBearing.Y:0.00}) (expect pointing LEFT => x<0)");
        if (offInFrustum)
            Console.WriteLine("[proof] WARNING: target still in frustum for the offscreen case!");
        if (offBearing.X >= 0f)
            Console.WriteLine("[proof] WARNING: offscreen bearing does not point LEFT as the geometry requires!");
        RenderTargetScene(gd, "bearing_offscreen",
            null,   // bracket is null — target not in view
            offBearing);

        Console.WriteLine($"[proof] done — PNGs in {_o.OutDir}");
    }

    // Reflect the server's internal Ships table for the target ship object.
    private static object FindServerShip(Srv server, short tgtId)
    {
        var coll = (System.Collections.IEnumerable)ShipsProp.GetValue(server)!;
        foreach (var ls in coll)
        {
            var ship = ls.GetType().GetProperty("Ship")!.GetValue(ls)!;
            var sid = (short?)(ship.GetType().GetProperty("ShipId")?.GetValue(ship)
                                ?? ship.GetType().GetProperty("Id")?.GetValue(ship));
            if (sid == tgtId) return ship;
        }
        throw new InvalidOperationException($"no server ship for target #{tgtId}");
    }

    // ── bracket / readout / bearing construction (mirrors Game1) ────────────

    private sealed record CamSetup(Xna.Vector3 Eye, Xna.Matrix View, Xna.Matrix Proj);

    // Camera behind the target looking at it (target frames centered): eye sits at
    // (lookAt + back*dist), view gazes toward lookAt — the warp-visual-proof framing.
    private static CamSetup LookAtTarget(Xna.Vector3 lookAt)
    {
        var back = Xna.Vector3.Normalize(new Xna.Vector3(0.07f, 0.06f, 1f));
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

    // A camera that is turned AWAY from the target so it falls `angleDeg` to the LEFT
    // of the view centre (outside the frustum once angleDeg > half-FOV). The camera
    // sits at (target + (0,0,CamDist)) and gazes along a forward rotated `angleDeg`
    // toward +X (right) from -Z — so the target, due -Z of the eye, lands 45° off to
    // the LEFT of the view axis.
    private static CamSetup OffAxisCam(Xna.Vector3 targetXna, float angleDeg)
    {
        float a = MathHelper.ToRadians(angleDeg);
        var fwd = new Xna.Vector3(MathF.Sin(a), 0f, -MathF.Cos(a));
        var eye = targetXna + new Xna.Vector3(0f, 0f, CamDist);
        var view = Xna.Matrix.CreateLookAt(Xna.Vector3.Zero, fwd, Xna.Vector3.Up);
        var proj = Xna.Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 10f, 80000f);
        return new CamSetup(eye, view, proj);
    }

    // World -> screen centre (exactly Game1.ProjectToScreen).
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

    private static TargetSelectionView BuildBracket(ShipEntity ent, CamSetup cam,
        bool engaged = true, bool? isEnemy = null)
    {
        var worldPos = PoseConverter.ToXna(ent.Latest!.Position);
        if (!ProjectToScreen(worldPos, cam, out var screen))
            throw new InvalidOperationException("target did not project into this camera — bad framing");
        var name = string.IsNullOrEmpty(ent.CharacterName) ? "ship" : ent.CharacterName;
        return new TargetSelectionView(screen, engaged, name, ent.Latest.HullFraction, ent.Latest.ShieldFraction, isEnemy);
    }

    /// <summary>P376 — exactly Game1.ComputeTargetSelectionBracket's side test: friendly
    /// collapses to "same side as me" (no alliance concept), so a ship on any other
    /// side is an enemy.</summary>
    private static bool IsEnemyOf(ClientWorld viewer, ShipEntity targetEnt)
        => !(viewer.MySideId is { } mySide && targetEnt.SideId == mySide);

    /// <summary>P376 — render the REAL HudRenderer target bracket for one viewer
    /// locking one target ship, framing the camera on that target so it projects
    /// into the view, and emitting BOTH the engaged and the selected variants (to
    /// prove the engaged-vs-selected brightness/thickness distinction survives within
    /// the red and blue colour families). Derives isEnemy from the viewer's own side,
    /// mirroring production.</summary>
    private void RenderColorCase(GraphicsDevice gd, string label, ClientWorld viewer, ShipEntity targetEnt)
    {
        if (targetEnt.Latest is not { } lu)
            throw new InvalidOperationException($"'{label}' target has no Latest (not visible to viewer)");
        bool isEnemy = IsEnemyOf(viewer, targetEnt);
        var cam = LookAtTarget(PoseConverter.ToXna(lu.Position));
        RenderTargetScene(gd, label + "_engaged",
            BuildBracket(targetEnt, cam, engaged: true, isEnemy),
            BuildBearing(lu.Position, cam));
        RenderTargetScene(gd, label + "_selected",
            BuildBracket(targetEnt, cam, engaged: false, isEnemy),
            BuildBearing(lu.Position, cam));
        Console.WriteLine($"[proof] color-case '{label}': viewerSide={viewer.MySideId} targetSide={targetEnt.SideId} => isEnemy={isEnemy} (expect {(isEnemy ? "RED" : "BLUE")})");
    }

    private static TargetSelectionView BuildNonShipBracket(DataStationIGC st, CamSetup cam)
    {
        var pos = new SimVec(st.Position[0], st.Position[1], st.Position[2]);
        var worldPos = PoseConverter.ToXna(pos);
        if (!ProjectToScreen(worldPos, cam, out var screen))
            screen = new Xna.Vector2(Width / 2f, Height / 2f);   // keep it framed; no readout anyway
        return new TargetSelectionView(screen, Engaged: false, null, null, null);
    }

    // Screen-space unit bearing toward the target (exactly Game1.ComputeTargetBearing).
    private static Vector2 BuildBearing(SimVec targetPos, CamSetup cam)
    {
        var rel = PoseConverter.ToXna(targetPos) - cam.Eye;
        var viewProj = cam.View * cam.Proj;
        var clip = Xna.Vector4.Transform(new Xna.Vector4(rel.X, rel.Y, rel.Z, 0f), viewProj);
        Vector2 dir;
        if (MathF.Abs(clip.W) > 1e-6f)
            dir = new Vector2(clip.X / clip.W, -clip.Y / clip.W);
        else
            dir = new Vector2(0f, 1f);
        if (dir.LengthSquared() < 1e-6f) dir = new Vector2(0f, 1f);
        dir.Normalize();
        return dir;
    }

    // ── rendering through the REAL HudRenderer ──────────────────────────────

    private void RenderTargetScene(GraphicsDevice gd, string label,
        TargetSelectionView? bracket, Vector2? bearing)
    {
        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.018f, 0.022f, 0.028f));

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        _sb.DrawString(_font, $"P359 - {label}", new Vector2(12f, 8f), Color.Orange);
        _sb.End();

        // Draw the REAL HUD with the target readout + bearing. frame=null is fine:
        // the bracket/readout/bearing draw before the null-frame return in
        // HudRenderer.Draw. modeLabelOverride pins the bottom-left mode tag.
        var vp = new Viewport(0, 0, Width, Height);
        _hud.Draw(null, CameraMode.Cockpit, vp,
            lockBracket: null, leadIndicator: null, modeLabelOverride: label.ToUpperInvariant(),
            targetSelection: bracket, targetBearing: bearing);

        gd.SetRenderTarget(null);
        string path = Path.Combine(_o.OutDir, $"p359_{label}.png");
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[proof] wrote {path}  " +
                          $"(bracket={(bracket is null ? "null" : "present")}, " +
                          $"bearing={(bearing is null ? "null" : $"({bearing.Value.X:0.00},{bearing.Value.Y:0.00})")})");
    }
}
