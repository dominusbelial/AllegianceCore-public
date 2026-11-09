using System.Globalization;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

/// <summary>Real NativeShipRenderer frames for the P985 replay. The proof CSV
/// determines the pose; no replacement interpolation or gameplay model lives
/// here. Python only adds captions/reference marks and encodes the frames.</summary>
public static class P985ClipRenderer
{
    public static void Run(string[] args)
    {
        string csv = Arg(args, "--csv") ?? throw new ArgumentException("--csv is required");
        string method = Arg(args, "--method") ?? "p985-timeline";
        string output = Arg(args, "--out") ?? throw new ArgumentException("--out is required");
        string art = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") is { Length: > 0 } value
            ? value : ExternalPaths.Require(ExternalResourceKind.Artwork);
        Directory.CreateDirectory(output);
        var rows = File.ReadLines(csv).Skip(1).Select(line => line.Split(','))
            .Where(c => c[1] == method && double.Parse(c[0], CultureInfo.InvariantCulture) >= 2.0
                        && double.Parse(c[0], CultureInfo.InvariantCulture) < 10.0)
            .Select(c => new ClipRow(
                double.Parse(c[0], CultureInfo.InvariantCulture),
                V(c, 2), V(c, 5), float.Parse(c[8], CultureInfo.InvariantCulture), c[9] == "1"))
            .Where((_, index) => index % 4 == 0) // 60 Hz replay -> 15 fps clips
            .ToArray();
        if (rows.Length < 2) throw new InvalidOperationException("no replay rows selected");
        using var game = new ClipGame(rows, method, art, output);
        game.Run();
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    private static Vector3 V(string[] c, int offset) => new(
        float.Parse(c[offset], CultureInfo.InvariantCulture),
        float.Parse(c[offset + 1], CultureInfo.InvariantCulture),
        float.Parse(c[offset + 2], CultureInfo.InvariantCulture));

    internal sealed record ClipRow(double Time, Vector3 Pose, Vector3 Truth, float Delay, bool Interpolated);

    private sealed class ClipGame : Game
    {
        private const int W = 640, H = 360;
        private readonly GraphicsDeviceManager _gdm;
        private readonly ClipRow[] _rows;
        private readonly string _method, _art, _output;

        public ClipGame(ClipRow[] rows, string method, string art, string output)
        {
            _rows = rows; _method = method; _art = art; _output = output;
            _gdm = new GraphicsDeviceManager(this)
            { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64 };
        }

        protected override void Initialize()
        {
            base.Initialize();
            try { Render(); }
            catch (Exception ex) { Console.Error.WriteLine($"[p985-clip] {ex}"); Environment.ExitCode = 2; }
            Exit();
        }

        private void Render()
        {
            var gd = GraphicsDevice;
            gd.Viewport = new Viewport(0, 0, W, H);
            var assets = new DirectoryAssetSource(_art);
            using var native = new NativeShipRenderer(gd, assets);
            var geo = native.MeshFor("fig30") ?? throw new InvalidOperationException("fig30 native mesh missing");
            string? textureStem = native.TextureStemFor("fig30", null);
            var texture = textureStem is null ? null : native.TextureFor(textureStem);
            // Inspection zoom (presentation only): the 30 m hull would be a
            // handful of pixels in this whole-trajectory orthographic view.
            float scale = 3f * NativeShipResolver.NativeScaleForLength(30, geo.MeshRadius);
            var light = SectorLighting.FromCluster(new[] { 0.6f, 0.2f, 0.7f }, 0x00FFAAAA);

            Vector3 min = _rows[0].Truth, max = min;
            foreach (var row in _rows)
            {
                min = Vector3.Min(min, row.Truth);
                max = Vector3.Max(max, row.Truth);
            }
            Vector3 centre = (min + max) * 0.5f;
            Vector3 eye = centre + new Vector3(0, 600, 500);
            var view = Matrix.CreateLookAt(eye, centre, Vector3.UnitY);
            var projection = Matrix.CreateOrthographic(800, 450, 1, 5000);
            var metadata = new List<string> { "frame\ttime\tdelay\tinterpolated\tposeX\tposeY\ttruthX\ttruthY" };
            for (int i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                using var target = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
                gd.SetRenderTarget(target);
                gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(2, 4, 10), 1, 0);
                var world = Matrix.CreateRotationY(0.45f) * Matrix.CreateTranslation(row.Pose);
                native.Draw(geo, texture, world, view, projection, 1, scale, light, "fig30", textureStem);
                gd.SetRenderTarget(null);
                using (var image = new Texture2D(gd, W, H))
                {
                    var pixels = new Color[W * H];
                    target.GetData(pixels);
                    image.SetData(pixels);
                    using var stream = File.Create(Path.Combine(_output, $"frame_{i:D4}.png"));
                    image.SaveAsPng(stream, W, H);
                }
                // RenderTarget resets gd.Viewport to the 64px backbuffer;
                // project with the actual 640x360 evidence viewport explicitly.
                var evidenceViewport = new Viewport(0, 0, W, H);
                Vector3 ps = evidenceViewport.Project(row.Pose, projection, view, Matrix.Identity);
                Vector3 ts = evidenceViewport.Project(row.Truth, projection, view, Matrix.Identity);
                metadata.Add(string.Join('\t', i, row.Time.ToString("R", CultureInfo.InvariantCulture),
                    row.Delay.ToString("R", CultureInfo.InvariantCulture), row.Interpolated ? 1 : 0,
                    ps.X.ToString("R", CultureInfo.InvariantCulture), ps.Y.ToString("R", CultureInfo.InvariantCulture),
                    ts.X.ToString("R", CultureInfo.InvariantCulture), ts.Y.ToString("R", CultureInfo.InvariantCulture)));
            }
            File.WriteAllLines(Path.Combine(_output, "frames.tsv"), metadata);
            Console.WriteLine($"[p985-clip] method={_method} frames={_rows.Length} native=fig30 out={_output}");
        }
    }
}
