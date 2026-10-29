// P365 live-proof harness — prove the real engine-nozzle glow decal end to end:
//
//   REAL in-process Allegiance.Server  →  REAL ConnectionSession peers (wire)
//        →  REAL ServerShipUpdate (Position/Orientation/StateM/Controls.Throttle)
//        →  the SAME NozzleGlowValue logic Game1 applies (afterburner bit +
//           throttle axis)
//        →  the REAL EngineNozzleGlow HSB colour math
//        →  the REAL EngineNozzleGlowRenderer, drawn offscreen and saved as PNGs.
//
// Phases (drive each ship's real server-side Controls.Throttle + StateM bit, the
// exact data ServerShipUpdate.FromModel quantizes onto the wire):
//   A   both idle (throttle -1, no afterburner)  -> value 0 -> NO glow; baseline.
//   B1  LOCAL low throttle (axis 0 -> value 0.5) -> a dim SMALL RED-ORANGE glow.
//   B2  LOCAL afterburner (value 1)              -> a bright LARGE YELLOW-WHITE glow
//                                                   (real colour+size shift vs B1).
//   C   REMOTE companion also afterburns         -> its own glow in the RIGHT half.
//   D   thrust stops on both                     -> glow disappears (back to A).
//
// Every frame logs: phase, active glow count, per-half lit-pixel counts (left =
// local, right = remote — a SIZE proxy) and the average R/G/B of the lit pixels
// (a HUE proxy: B1 must be strongly red-dominant, B2 yellow-dominant).
//
// Usage: p365verify <outdir>

using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;
using SimOri = Allegiance.Core.Sim.Orientation;
using XnVec = Microsoft.Xna.Framework.Vector3;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

internal sealed class P365Harness : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _outDir;

    private static readonly string ContentRoot =
        System.IO.Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private const ushort Port = 8791;

    private const int Width = 960;
    private const int Height = 640;

    private Srv _server = null!;
    private readonly List<ConnectionSession> _peers = new();
    private Ship _shipLocal = null!, _shipRemote = null!;
    private float _hullRadius;
    private SimVec _fwd;

    private EngineNozzleGlowRenderer _renderer = null!;
    private readonly List<EngineGlowView> _glows = new();
    private readonly List<string> _logLines = new();

    public P365Harness(string[] args)
    {
        _outDir = args[0];
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Width,
            PreferredBackBufferHeight = Height,
        };
        Content.RootDirectory = ContentRoot;
    }

    protected override void LoadContent()
    {
        base.LoadContent();
        _renderer = new EngineNozzleGlowRenderer(GraphicsDevice, NativeEffectTextures.Load(GraphicsDevice, new DirectoryAssetSource(ArtRoot)));
        RunAll();
    }

    private void Log(string line)
    {
        Console.WriteLine(line);
        _logLines.Add(line);
    }

    private const int Ab = Allegiance.Core.Sim.ShipControlState.AfterburnerButton;

    // Desired drive for each ship: throttle axis in [-1,1] and afterburner-held flag.
    private bool _ab0, _ab1;
    private float _throttle0 = -1f, _throttle1 = -1f;

    private void Pump(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            _server.TickSim();
            // Re-assert the real drive every tick (the real client holds keys), so
            // the server's Ship.StateM + Controls keep the afterburner bit / throttle
            // that ServerShipUpdate.FromModel quantizes onto the wire.
            ApplyDrive(0, _ab0, _throttle0);
            ApplyDrive(1, _ab1, _throttle1);
            Thread.Sleep(1);
        }
    }

    private void ApplyDrive(int peerIdx, bool ab, float throttle)
    {
        var ship = peerIdx == 0 ? _shipLocal : _shipRemote;
        if (ship == null) return;
        var ctrl = new ControlData(0f, 0f, 0f, throttle);
        if (ab) ship.StateM |= Ab; else ship.StateM &= ~Ab;
        ship.Controls = ctrl;
        if (ab) ship.Velocity = _fwd * 120f;
        else ship.Velocity = _fwd * 40f;
    }

    private void RunAll()
    {
        Directory.CreateDirectory(_outDir);
        Log("[p365] P365 engine-nozzle glow live proof");
        Log("[p365] real server -> real wire -> real HSB colour math -> real renderer offscreen");

        BootServer();
        Pump(150);
        Log($"[p365] peers: {string.Join(", ", _peers.Select(p => $"ship={p.World.MyShipId}"))}");
        var myShipId = _peers[0].World.MyShipId!.Value;

        // Phase A — idle: value 0 -> scaleGlow 0 -> no glow, byte-identical frames.
        Log("[p365] Phase A: both idle (throttle -1) — expect 0 glows, black baseline.");
        Pump(30);
        string a0 = "", a1 = "", a2 = "";
        RenderFrame(myShipId, out _, out _, out _, out _, out _, out _, out a0, "A0");
        RenderFrame(myShipId, out _, out _, out _, out _, out _, out _, out a1, "A1");
        RenderFrame(myShipId, out _, out _, out _, out _, out _, out _, out a2, "A2");
        Log($"[p365] A0 md5={a0}  A1 md5={a1} matchA0={a1==a0}  A2 md5={a2} matchA0={a2==a0}");

        // Phase B1 — LOCAL low throttle (axis 0 -> value 0.5): dim small RED-ORANGE.
        Log("[p365] Phase B1: LOCAL low throttle (value 0.5) — dim red-orange glow on LEFT.");
        _throttle0 = 0f;
        Pump(20);
        RenderFrame(myShipId, out int b1n, out int b1l, out int b1r, out _, out float b1R, out float b1G, out _, "B1");
        Log($"[p365] B1 glows={b1n} leftLit={b1l} rightLit={b1r} avgRGB=({b1R:F0},{b1G:F0},_) redDominant={(b1G / (b1R + 1e-3f)):F2}<0.25");

        // Phase B2 — LOCAL afterburner (value 1): bright LARGE YELLOW-WHITE.
        Log("[p365] Phase B2: LOCAL afterburner (value 1) — bright yellow glow, LARGER than B1.");
        _throttle0 = 1f; _ab0 = true;
        Pump(20);
        RenderFrame(myShipId, out int b2n, out int b2l, out int b2r, out _, out float b2R, out float b2G, out _, "B2");
        Log($"[p365] B2 glows={b2n} leftLit={b2l} rightLit={b2r} avgRGB=({b2R:F0},{b2G:F0},_) biggerThanB1={b2l>b1l} yellowShift={(b2G/b2R):F2}vs{(b1G/(b1R+1e-3f)):F2}");

        // Phase C — REMOTE companion also afterburns -> its glow in the RIGHT half.
        Log("[p365] Phase C: REMOTE companion also afterburns — its glow in the RIGHT half.");
        _throttle1 = 1f; _ab1 = true;
        Pump(20);
        RenderFrame(myShipId, out int cn, out int cl, out int cr, out _, out _, out _, out _, "C");
        Log($"[p365] C glows={cn} leftLit={cl} rightLit={cr} (right>0 => remote glow renders too)");

        // Phase D — thrust stops on both -> glow gone (back to A baseline).
        Log("[p365] Phase D: thrust stops on both — glow disappears (back to A).");
        _throttle0 = -1f; _ab0 = false;
        _throttle1 = -1f; _ab1 = false;
        Pump(15);
        RenderFrame(myShipId, out int dn, out int dl, out int dr, out _, out _, out _, out string dmd5, "D");
        Log($"[p365] D glows={dn} leftLit={dl} rightLit={dr} md5={dmd5} backToBaseline={dmd5==a0}");

        string logPath = Path.Combine(_outDir, "p365_run.log");
        File.WriteAllLines(logPath, _logLines);
        foreach (var p in _peers) p.Dispose();
        _server.Dispose();
        Log($"[p365] wrote {logPath}");
        Exit();
    }

    private void BootServer()
    {
        var config = new Cfg
        {
            ArtPath = ArtRoot,
            StaticCore = "PCore001.igc",
            Port = Port,
            UpdatesPerSecond = 20,
            Games =
            [
                new Gm { Name = "P365 Verify", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
            ],
        };

        _server = new Srv();
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner not found");
        int spawned = 0;
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            int idx = spawned++;
            var candidates = core.HullTypes.Values.Where(h => h.Length >= 38f && h.Length <= 70f).ToList();
            var hull = candidates.Count > 0 ? candidates[0] : core.HullTypes.Values.First(h => h.HullId == 414);
            _hullRadius = 0.5f * hull.Length;
            var pos = new SimVec(-45f + idx * 90f, 0f, 0f);
            var o = new SimOri();
            o.SetForward(new SimVec(1f, 0f, 0f));
            _fwd = o.GetForward();
            var ship = new Ship(id, hull, pos, SimVec.Zero, o) { SideId = mission.Sides[0].Id };
            mission.PlaceShip(ship, mission.Sectors[0]);
            if (idx == 0) _shipLocal = ship; else _shipRemote = ship;
            return (ship, mission.Sectors[0]);
        };
        field.SetValue(_server, spawner);

        _server.Boot(config);
        Log($"[p365] server on port {_server.BoundPort} hull radius={_hullRadius:F1}");

        LogonRequest Req(string name) => new()
        {
            CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 901u, DwCookie = uint.MaxValue, CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
            SteamID = 99100000000000000ul, DrmHash = "p365verify",
        };

        for (int i = 0; i < 2; i++)
        {
            var c = new ConnectionSession();
            _peers.Add(c);
            c.Connect(Port, $"p365p{i}");
            c.SendLogonRequest(Req($"p365p{i}"));
            c.SendPositionReq(0);
        }
        for (int i = 0; i < 2000 && _peers.Any(p => p.World.MyShipId is null); i++)
        {
            _server.TickSim();
            Thread.Sleep(2);
        }
        Log($"[p365] radius={_hullRadius:F1} ships local={_shipLocal != null} remote={_shipRemote != null}");
    }

    /// <summary>Mirror Game1's per-frame nozzle-glow gather: build each ship's real
    /// ServerShipUpdate (the exact 47 bytes the wire carries), compute its glow
    /// value (afterburner bit + throttle, Game1's NozzleGlowValue), the HSB colour +
    /// scale, and draw the REAL renderer offscreen. Reports glow count, per-half
    /// lit pixels (size) and the average R/G/B of lit pixels (hue).</summary>
    private void RenderFrame(short myShipId, out int glowCount, out int leftLit, out int rightLit,
        out string glowIds, out float avgR, out float avgG, out string md5, string tag)
    {
        uint time = (uint)Environment.TickCount;

        _glows.Clear();
        var ids = new List<short>();
        double sr = 0, sg = 0, sb = 0;
        foreach (var ship in new[] { _shipLocal, _shipRemote })
        {
            if (ship == null) continue;
            // The real wire payload — ServerShipUpdate.FromModel quantizes the
            // live ship's StateM + Controls.Throttle exactly as it does on the wire.
            var up = ServerShipUpdate.FromModel(ship.Id, ship, time);
            float value = ((up.StateM & Ab) != 0) ? 1f : Math.Clamp(0.5f * (up.Controls.Throttle + 1f), 0f, 1f);
            if (value <= 0f) continue;
            var (r, g, b, scale) = EngineNozzleGlow.Compute(value);
            var backward = up.Orientation.GetBackward();
            var pos = up.Position + (backward * (_hullRadius * EngineNozzleGlowRenderer.AftCenterFraction));
            float half = scale * _hullRadius * EngineNozzleGlowRenderer.GlowSizeFactor;
            _glows.Add(new EngineGlowView(pos, half, new XnVec(r, g, b)));
            ids.Add(ship.Id);
            sr += r * 255f; sg += g * 255f; sb += b * 255f;
        }
        ids.Sort();
        glowIds = string.Join("+", ids);
        glowCount = _glows.Count;
        if (glowCount > 0) { avgR = (float)(sr / glowCount); avgG = (float)(sg / glowCount); }
        else { avgR = avgG = 0f; }

        var gd = GraphicsDevice;
        var rt = new RenderTarget2D(gd, Width, Height);
        gd.SetRenderTarget(rt);
        gd.Clear(Color.Black);

        var eye = new XnVec(0f, -5f, 95f);
        var target = new XnVec(0f, 0f, 0f);
        var view = Matrix.CreateLookAt(XnVec.Zero, target - eye, XnVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60), Width / (float)Height, 1f, 2000f);

        _renderer.Draw(_glows, eye, view, proj);
        gd.SetRenderTarget(null);

        var pixels = new Color[Width * Height];
        rt.GetData(pixels);
        using (var fs = File.Create(Path.Combine(_outDir, $"{tag}.png")))
            rt.SaveAsPng(fs, Width, Height);
        rt.Dispose();

        int left = 0, right = 0;
        double rsum = 0, gsum = 0, bsum = 0; int lit = 0;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var bytes = new byte[Width * Height * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                int r = pixels[i].R, g = pixels[i].G, b = pixels[i].B;
                bytes[i * 4] = (byte)r; bytes[i * 4 + 1] = (byte)g;
                bytes[i * 4 + 2] = (byte)b; bytes[i * 4 + 3] = (byte)pixels[i].A;
                if (r + g + b > 45)
                {
                    lit++;
                    rsum += r; gsum += g; bsum += b;
                    if (i % Width < Width / 2) left++; else right++;
                }
            }
            md5 = Convert.ToHexString(sha.ComputeHash(bytes))[..12];
        }
        leftLit = left; rightLit = right;
        if (lit > 0) { avgR = (float)(rsum / lit); avgG = (float)(gsum / lit); }
        else { avgR = 0f; avgG = 0f; }
    }
}

internal static class Program
{
    private static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0]
            : System.IO.Path.GetFullPath(".verify_p365/frames");
        using var g = new P365Harness([outDir]);
        g.Run();
    }
}
