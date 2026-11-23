using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private string? _proofViewKind;
    private short _proofViewId;
    public void ProofView(string? kind, short id) { _proofViewKind = kind; _proofViewId = id; }

    /// <summary>Dev-only graphics regression, invoked between frames by the
    /// loopback proof. Uses already verified resident bytes; no IO or world edits.</summary>
    public object ProbeLateArt()
    {
        var results = new List<object>();
        var previous = GraphicsDevice.GetRenderTargets();
        try
        {
            foreach (var (name, model) in new[] { ("station", "dgarr"), ("asteroid", "bgrnd05"),
                ("ship", "quizscoutv2"), ("missile", "mis05"), ("probe", "utl02"),
                ("treasure", "acs14"), ("projectile", "bgrnd55") })
            {
                // KNI caches GetData's CPU copy: use a fresh target per case so
                // a previous model's readback cannot satisfy this model's proof.
                using var target = new RenderTarget2D(GraphicsDevice, 96, 96, false, SurfaceFormat.Color, DepthFormat.Depth24);
                var parsed = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, model + ".mdl"));
                string texture = NativeShipResolver.ResolveTextureStem(parsed, null, model,
                    stem => _assets.Exists(stem + ".mdl")) ?? throw new InvalidDataException("Proof texture missing: " + model);
                var gate = new HeldArt(_assets, model, texture);
                using var stations = name == "station" ? new NativeStationRenderer(GraphicsDevice, gate) : null;
                using var rocks = name == "asteroid" ? new NativeAsteroidRenderer(GraphicsDevice, gate) : null;
                using var ships = name == "ship" ? new NativeShipRenderer(GraphicsDevice, gate) { HdTexturesEnabled = false } : null;
                using var missiles = name == "missile" ? new NativeMissileRenderer(GraphicsDevice, gate) : null;
                using var treasures = name == "treasure" ? new NativeTreasureRenderer(GraphicsDevice, gate) : null;
                using var projectiles = name == "projectile" ? new ProjectileModelRenderer(GraphicsDevice, gate) : null;
                object? Mesh()
                {
                    try
                    {
                        return name switch
                        {
                            "station" => stations!.MeshFor(model), "asteroid" => rocks!.GeometryFor(model),
                            "ship" => ships!.MeshFor(model), "missile" => missiles!.MeshFor(model),
                            "treasure" => treasures!.MeshFor(model), "projectile" => projectiles!.MeshFor(model),
                            _ => MdlFile.Parse(AssetSources.ReadAllBytes(gate, model + ".mdl")),
                        };
                    }
                    catch (FileNotFoundException) { return null; }
                }
                bool initialMissing = Mesh() is null;
                gate.PromoteModel();
                var mesh = Mesh();
                using var probe = name == "probe" ? new ProbeRenderer(GraphicsDevice, parsed.Meshes,
                    assets: gate, modelStem: model, textureStem: null) : null;
                float radius = Math.Max(1, NativeShipResolver.MaxVertexRadius(parsed.Meshes));
                var view = Matrix.CreateLookAt(new(0, radius * .3f, radius * 3), Vector3.Zero, Vector3.Up);
                var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), 1, .1f, radius * 20);
                Texture2D? Surface() => name switch
                {
                    "station" => stations!.TextureFor(texture), "asteroid" => rocks!.TextureFor(texture),
                    "ship" => ships!.TextureFor(texture), "missile" => missiles!.TextureFor(model, null),
                    "treasure" => treasures!.TextureFor(texture), "projectile" => projectiles!.TextureFor(model), _ => null,
                };
                void Draw()
                {
                    GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
                    GraphicsDevice.BlendState = BlendState.Opaque; GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                    var surface = Surface();
                    switch (mesh)
                    {
                        case NativeStationRenderer.NativeStationGeometry station:
                            stations!.Draw(station, surface, Matrix.Identity, view, projection, 1, null, model, texture); break;
                        case NativeShipRenderer.NativeShipGeometry geo when name == "asteroid":
                            rocks!.Draw(geo, surface, Matrix.Identity, view, projection, 1, null, model, texture); break;
                        case NativeShipRenderer.NativeShipGeometry geo when name == "ship":
                            ships!.Draw(geo, surface, Matrix.Identity, view, projection, 1, 1, null, model, texture, false); break;
                        case NativeShipRenderer.NativeShipGeometry:
                            treasures!.DrawModel(new(TreasureVisualKind.Cash, model, texture, "", "proof", false), 1,
                                Matrix.Identity, view, projection, 0, null, spin: false); break;
                        case ProjectileModelRenderer.Geometry when name == "missile":
                            missiles!.Draw(Matrix.Identity, view, projection, model, surface); break;
                        case ProjectileModelRenderer.Geometry geo:
                            projectiles!.Draw(geo, surface, Vector3.Zero, Vector3.UnitZ, 0, view, projection, modelName: model); break;
                        case MdlFile: probe!.Draw(Matrix.Identity, view, projection, Color.White); break;
                    }
                }
                Draw();
                bool textureMissing = name == "probe" ? !probe!.TextureResident : Surface() is null;
                gate.PromoteTexture();
                Draw();
                var pixels = new Color[96 * 96]; target.GetData(pixels);
                results.Add(new { name, model, initialMissing, modelAfterPromotion = mesh is not null, textureMissing,
                    textureAfterPromotion = name == "probe" ? probe!.TextureResident : Surface() is not null,
                    textureStem = texture, nonBlackPixels = pixels.Count(c => c.R + c.G + c.B > 12) });
                GraphicsDevice.SetRenderTargets(previous);
            }
        }
        finally { GraphicsDevice.SetRenderTargets(previous); }
        return new { rows = results };
    }

    private sealed class HeldArt(IAssetSource source, string model, string texture) : IAssetSource, IAssetContentRevision
    {
        private bool _model, _texture;
        private readonly HashSet<string> _texturePaths = MdlFile.Parse(AssetSources.ReadAllBytes(source, model + ".mdl"))
            .Namespaces.Where(n => !n.Equals(model, StringComparison.OrdinalIgnoreCase))
            .Append(texture).Select(n => n + ".mdl").ToHashSet(StringComparer.OrdinalIgnoreCase);
        public long ContentRevision { get; private set; }
        public void PromoteModel() { _model = true; ContentRevision++; }
        public void PromoteTexture() { _texture = true; ContentRevision++; }
        private bool Held(string path) => (!_model && path.Equals(model + ".mdl", StringComparison.OrdinalIgnoreCase))
            || (!_texture && _texturePaths.Contains(path));
        public bool Exists(string path) => !Held(path) && source.Exists(path);
        public Stream OpenRead(string path) => Held(path) ? throw new FileNotFoundException(path) : source.OpenRead(path);
        public IEnumerable<string> Enumerate(string path) => source.Enumerate(path).Where(p => !Held(p));
    }
}
