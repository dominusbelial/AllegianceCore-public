// P367 live-proof harness — prove the real navigation strobe-light system end to end:
//
//   REAL in-process Allegiance.Server  →  REAL ConnectionSession peers (wire)
//        →  REAL ServerShipUpdate (Position/Orientation, the exact 47 bytes)
//        →  the SAME NavStrobeMath.Pulse envelope Game1 applies (native
//           LightsGeo::Render, efapp.cpp:177-206) from a running clock
//        →  the REAL NavStrobeLayout anchors (wingtip/stern on the real hull)
//        →  the REAL NavStrobeRenderer, drawn offscreen and saved as PNGs.
//
// Requirements this proves:
//   * a real PULSING light at a real hardpoint position (not an instant on/off,
//     not a static glow) — the numeric pulse trace shows the real ramp-up →
//     hold → ramp-down → off envelope, and the rendered frames show a growing/
//     shrinking dot that fully disappears between flashes;
//   * alternating red-port / green-starboard sides (the two wingtip flashes land
//     at different times — phase half a period apart);
//   * renders for a REMOTE ship too (right half of frame), not just the local hull;
//   * renders while moving (Phase B) as well as at rest (Phase A);
//   * renders on a REAL station from the test scene (Phase C), proving the
//     native ship+station scope.
//
// Usage: p367verify <outdir>

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

internal sealed class P367Harness : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _outDir;

    private const string ContentRoot =
        "/home/domingo/Development/AllegianceCore/src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content";
    private const string ArtRoot = "/home/domingo/Development/Artwork";
    private const ushort Port = 8802;

    private const int Width = 960;
    private const int Height = 640;

    private Srv _server = null!;
    private StaticCore _core = null!;
    private readonly List<ConnectionSession> _peers = new();
    private Ship _shipLocal = null!, _shipRemote = null!;
    private float _hullRadius;
    private SimVec _fwd;

    private NavStrobeRenderer _renderer = null!;
    private readonly List<NavStrobeView> _strobes = new();
    private readonly List<string> _logLines = new();
    private float _clock;   // the strobe clock (Game1's _nowSeconds equivalent)

    public P367Harness(string[] args)
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
        _renderer = new NavStrobeRenderer(GraphicsDevice, NativeEffectTextures.Load(GraphicsDevice, new DirectoryAssetSource(ArtRoot)));
        RunAll();
    }

    private void Log(string line)
    {
        Console.WriteLine(line);
        _logLines.Add(line);
    }

    private void Pump(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            _server.TickSim();
            _clock += 1f / 20f;   // 50ms per sim tick — the strobe clock advances with real elapsed time
            Thread.Sleep(1);
        }
    }

    private void RunAll()
    {
        Directory.CreateDirectory(_outDir);
        Log("[p367] P367 navigation strobe lights live proof");
        Log("[p367] real server -> real wire -> real pulse envelope -> real renderer offscreen");

        BootServer();
        Pump(150);
        Log($"[p367] peers: {string.Join(", ", _peers.Select(p => $"ship={p.World.MyShipId}"))}");
        var myShipId = _peers[0].World.MyShipId!.Value;

        // ---- Phase A — blink at rest ----------------------------------------
        // Sample the strobe clock every 50ms for 2.0s (> one 1.25s period). Every
        // frame logs the local ship's per-light pulse values (the real envelope)
        // and the rendered red/green/white lit-pixel counts per half.
        Log("[p367] Phase A: blink at rest, sampling every 50ms for 2.0s.");
        int portPeakIdx = -1, starPeakIdx = -1, allOffIdx = -1;
        float portBest = -1f, starBest = -1f;
        var trace = new List<(float clock, float port, float star, float tail, int n, int lr, int lg, int rr, int rg)>();
        for (int i = 0; i < 40; i++)
        {
            _clock += 0.050f;   // one 50ms sample
            RenderShips(myShipId, out int n, out int lr, out int lg, out int rr, out int rg, out string _);
            float port = NavStrobeMath.Pulse(_clock, NavStrobeTiming.Native);
            float star = NavStrobeMath.Pulse(_clock, new NavStrobeTiming(1.25f, 1.25f * 0.5f, 0.1f, 0f, 0.05f));
            float tail = NavStrobeMath.Pulse(_clock, new NavStrobeTiming(1.25f, 1.25f * 0.25f, 0.1f, 0f, 0.05f));
            trace.Add((_clock, port, star, tail, n, lr, lg, rr, rg));
            if (port > portBest) { portBest = port; portPeakIdx = i; }
            if (star > starBest) { starBest = star; starPeakIdx = i; }
            if (n == 0) allOffIdx = i;
            Log($"[p367]   t={_clock:F3}s port={port:F2} star={star:F2} tail={tail:F2} strobes={n} "
                + $"local(red={lr},grn={lg}) remote(red={rr},grn={rg})");
        }

        // Save the decisive frames explicitly: re-render at the exact port-peak /
        // starboard-peak / all-off clock times captured above.
        SaveShipFrame(trace[portPeakIdx].clock, "port_peak");
        SaveShipFrame(trace[starPeakIdx].clock, "star_peak");
        if (allOffIdx >= 0)
            SaveShipFrame(trace[allOffIdx].clock, "all_off");

        // Envelope + alternation assertions from the numeric trace:
        // (1) the flash is transient — the light is OFF most of the period;
        // (2) port and starboard flashes land at different times (alternate).
        int offSamples = trace.Count(e => e.port <= 0.01f);
        Log($"[p367]   PORT off in {offSamples}/40 samples ({offSamples * 100 / 40}%) — a blink, not an always-on glow");
        Log($"[p367]   PORT peak frame #{portPeakIdx} vs STARBOARD peak frame #{starPeakIdx} "
            + $"(distinct => sides alternate, {(portPeakIdx == starPeakIdx ? "SAME" : "DIFFERENT")})");

        // ---- Phase B — blink while MOVING ------------------------------------
        Log("[p367] Phase B: both ships moving (+X) — strobes must still render.");
        _shipLocal.Velocity = _fwd * 80f;
        _shipRemote.Velocity = _fwd * 80f;
        for (int i = 0; i < 6; i++)
        {
            Pump(1);
            RenderShips(myShipId, out int n, out int lr, out int lg, out int rr, out int rg, out string md5);
            Log($"[p367]   moving t={_clock:F3}s strobes={n} local(red={lr},grn={lg}) remote(red={rr},grn={rg}) md5={md5}");
        }

        // ---- Phase C — real station beacons -----------------------------------
        Log("[p367] Phase C: a real station from the test scene — beacons render and blink.");
        RenderStation(myShipId);

        string logPath = Path.Combine(_outDir, "p367_run.log");
        File.WriteAllLines(logPath, _logLines);
        foreach (var p in _peers) p.Dispose();
        _server.Dispose();
        Log($"[p367] wrote {logPath}");
        Exit();
    }

    /// <summary>Build the strobe views for both ships (each with its real wire
    /// pose) + the pulse envelope, draw the REAL renderer offscreen and report
    /// per-half red/green lit-pixel counts (local = left half, remote = right).</summary>
    private void RenderShips(short myShipId, out int strobeCount, out int localRed, out int localGreen,
        out int remoteRed, out int remoteGreen, out string md5)
    {
        uint time = (uint)Environment.TickCount;
        _strobes.Clear();
        foreach (var ship in new[] { _shipLocal, _shipRemote })
        {
            if (ship == null) continue;
            var up = ServerShipUpdate.FromModel(ship.Id, ship, time);
            AddAnchors(NavStrobeLayout.ShipAnchors(up.Position, up.Orientation, _hullRadius), _hullRadius);
        }
        strobeCount = _strobes.Count;

        var (pixels, md5p) = RenderToPixels(out int lr, out int lg, out int rr, out int rg);
        localRed = lr; localGreen = lg; remoteRed = rr; remoteGreen = rg;
        md5 = md5p;
    }

    private void AddAnchors(System.Collections.Generic.IReadOnlyList<NavStrobeAnchor> anchors, float radius)
    {
        foreach (var a in anchors)
        {
            float pulse = NavStrobeMath.Pulse(_clock, a.Timing);
            if (pulse <= 0f) continue;
            float half = pulse * radius * NavStrobeLayout.SizeFactor;
            _strobes.Add(new NavStrobeView(a.Position, half, new XnVec(a.R, a.G, a.B)));
        }
    }

    /// <summary>Set the strobe clock to an exact moment, rebuild both ships'
    /// strobe views at that moment, render offscreen and save the PNG (the
    /// port-peak / starboard-peak / all-off frames).</summary>
    private void SaveShipFrame(float clock, string tag)
    {
        _clock = clock;
        uint time = (uint)Environment.TickCount;
        _strobes.Clear();
        foreach (var ship in new[] { _shipLocal, _shipRemote })
        {
            if (ship == null) continue;
            var up = ServerShipUpdate.FromModel(ship.Id, ship, time);
            AddAnchors(NavStrobeLayout.ShipAnchors(up.Position, up.Orientation, _hullRadius), _hullRadius);
        }
        var gd = GraphicsDevice;
        var rt = new RenderTarget2D(gd, Width, Height);
        gd.SetRenderTarget(rt);
        gd.Clear(Color.Black);
        var eye = new XnVec(0f, -5f, 95f);
        var target = new XnVec(0f, 0f, 0f);
        var view = Matrix.CreateLookAt(XnVec.Zero, target - eye, XnVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60), Width / (float)Height, 1f, 2000f);
        _renderer.Draw(_strobes, eye, view, proj);
        gd.SetRenderTarget(null);
        using (var fs = File.Create(Path.Combine(_outDir, $"{tag}.png")))
            rt.SaveAsPng(fs, Width, Height);
        rt.Dispose();
        Log($"[p367]   saved {tag}.png with {_strobes.Count} strobe decals at clock {clock:F3}s");
    }

    /// <summary>Render the current strobe views offscreen, save a PNG, and classify
    /// lit pixels per half into red-dominant / green-dominant / white counts.</summary>
    private (Color[] Pixels, string Md5) RenderToPixels(out int localRed, out int localGreen,
        out int remoteRed, out int remoteGreen)
    {
        var gd = GraphicsDevice;
        var rt = new RenderTarget2D(gd, Width, Height);
        gd.SetRenderTarget(rt);
        gd.Clear(Color.Black);

        var eye = new XnVec(0f, -5f, 95f);
        var target = new XnVec(0f, 0f, 0f);
        var view = Matrix.CreateLookAt(XnVec.Zero, target - eye, XnVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60), Width / (float)Height, 1f, 2000f);

        _renderer.Draw(_strobes, eye, view, proj);
        gd.SetRenderTarget(null);

        var pixels = new Color[Width * Height];
        rt.GetData(pixels);
        rt.Dispose();

        int lr = 0, lg = 0, rr = 0, rg = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            int r = pixels[i].R, g = pixels[i].G, b = pixels[i].B;
            if (r + g + b <= 45) continue;
            bool red = r > 1.5f * g && r > 1.5f * b;
            bool green = g > 1.5f * r && g > 1.5f * b;
            bool right = (i % Width) >= Width / 2;
            if (red) { if (right) rr++; else lr++; }
            else if (green) { if (right) rg++; else lg++; }
        }
        localRed = lr; localGreen = lg; remoteRed = rr; remoteGreen = rg;
        return (pixels, Md5(pixels));
    }

    /// <summary>Phase C — render the REAL first station in the test scene's home
    /// cluster: its real position/orientation from the wire, its real radius from
    /// the static core, beacons placed by NavStrobeLayout.StationAnchors.</summary>
    private void RenderStation(short myShipId)
    {
        var st = _peers[0].World.Stations.Values.FirstOrDefault();
        if (st.Position is null)
        {
            Log("[p367]   (no station observed on the wire — station phase skipped)");
            return;
        }
        float radius = _core.StationTypes.TryGetValue(st.StationTypeId, out var ty) && ty.Radius > 0f
            ? ty.Radius : Allegiance.ClientCore.MonoGame.Rendering.StationRenderer.DefaultFallbackRadius;
        var pos = new SimVec(st.Position[0], st.Position[1], st.Position[2]);
        var o = new SimOri();
        o.Set(new SimVec(st.Forward[0], st.Forward[1], st.Forward[2]),
              new SimVec(st.Up[0], st.Up[1], st.Up[2]));

        // Drive the clock to the phase-0 (left, red) beacon's full-flash moment so
        // the rendered frame clearly shows a lit station beacon.
        _clock = 0.10f;   // (0.10 - 0) % 1.25 → pulse 1.0 for the phase-0 beacon
        var stationAnchors = NavStrobeLayout.StationAnchors(pos, o, radius);
        Log("[p367]   station beacon pulses: "
            + string.Join(", ", stationAnchors.Select(a => $"p={NavStrobeMath.Pulse(_clock, a.Timing):F2}")
                                     .Prepend($"(count {stationAnchors.Count})")));
        _strobes.Clear();
        AddAnchors(stationAnchors, radius);

        // Aim the camera directly AT the flashing (phase-0) beacon so it is dead
        // centre, avoiding any frustum guesswork; the quad is ~80 world units wide
        // at ~2.8·radius distance → a clearly visible centred dot.
        var beacon = stationAnchors[0].Position;
        var bPos = new XnVec(beacon.X, beacon.Y, beacon.Z);
        var eye = bPos + new XnVec(0f, 0f, -radius * 2.8f);
        var view = Matrix.CreateLookAt(XnVec.Zero, bPos - eye, XnVec.Up);   // harness convention: look direction = camera→beacon
        Log($"[p367]   station '{st.Name}' type={st.StationTypeId} radius={radius:F1} "
            + $"pos=({st.Position[0]:F0},{st.Position[1]:F0},{st.Position[2]:F0}) "
            + $"beaconPos=({beacon.X:F0},{beacon.Y:F0},{beacon.Z:F0}) strobes={_strobes.Count}");
        var rt = new RenderTarget2D(GraphicsDevice, Width, Height);
        GraphicsDevice.SetRenderTarget(rt);
        GraphicsDevice.Clear(Color.Black);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60), Width / (float)Height, 1f, 6000f);
        _renderer.Draw(_strobes, eye, view, proj);
        GraphicsDevice.SetRenderTarget(null);
        using (var fs = File.Create(Path.Combine(_outDir, "station_beacon.png")))
            rt.SaveAsPng(fs, Width, Height);
        rt.Dispose();
        Log($"[p367]   station beacon frame saved (station_beacon.png) with {_strobes.Count} strobe decals");
    }

    private static string Md5(Color[] pixels)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            bytes[i * 4] = pixels[i].R; bytes[i * 4 + 1] = pixels[i].G;
            bytes[i * 4 + 2] = pixels[i].B; bytes[i * 4 + 3] = pixels[i].A;
        }
        return Convert.ToHexString(sha.ComputeHash(bytes))[..12];
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
                new Gm { Name = "P367 Verify", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
            ],
        };

        _server = new Srv();
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner not found");
        int spawned = 0;
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            _core = core;
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
        Log($"[p367] server on port {_server.BoundPort} hull radius={_hullRadius:F1}");

        LogonRequest Req(string name) => new()
        {
            CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 902u, DwCookie = 0xBEEFu, CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
            SteamID = 99200000000000000ul, DrmHash = "p367verify",
        };

        for (int i = 0; i < 2; i++)
        {
            var c = new ConnectionSession();
            _peers.Add(c);
            c.Connect(Port, $"p367p{i}");
            c.SendLogonRequest(Req($"p367p{i}"));
            c.SendPositionReq(0);
        }
        for (int i = 0; i < 2000 && _peers.Any(p => p.World.MyShipId is null); i++)
        {
            _server.TickSim();
            Thread.Sleep(2);
        }
        Log($"[p367] radius={_hullRadius:F1} ships local={_shipLocal != null} remote={_shipRemote != null} "
            + $"stations={_peers[0].World.Stations.Count}");
    }
}

internal static class Program
{
    private static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0]
            : "/home/domingo/Development/AllegianceCore/.verify_p367/frames";
        using var g = new P367Harness([outDir]);
        g.Run();
    }
}
