using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A static field of point "stars" scattered in a spherical shell around a
/// center in world space — the minimal visual reference needed to actually
/// see motion. Before this existed, flying in a straight line was
/// indistinguishable from sitting still: the chase camera tracks the ship at
/// a constant relative offset, and with nothing else rendered there was
/// nothing on screen to judge movement against (confirmed live — real,
/// correct thrust was reaching the server and moving the ship, verified via
/// a temporary diagnostic, but was completely imperceptible on screen).
/// </summary>
/// <remarks>
/// One <see cref="Starfield"/> is a single distance band — a single-layer
/// field gives weak parallax (everything moves at the same apparent rate
/// regardless of true distance). <see cref="Game1"/> layers several
/// instances at different (innerRadius, outerRadius) bands with different
/// brightness so nearer stars sweep past faster than farther ones, the way
/// real motion parallax works — this is what actually makes flying feel 3D
/// rather than like a painted backdrop.
/// </remarks>
public sealed class Starfield
{
    private readonly Vector3[] _worldPositions;
    private readonly Color[] _colors;
    private readonly VertexPositionColor[] _scratch;
    private readonly BasicEffect _effect;

    /// <param name="gd">The graphics device (for the effect only — no content
    /// pipeline asset needed, stars are generated, not loaded).</param>
    /// <param name="center">World-space center of the star shell — pick
    /// something near where the ship actually spawns so stars are nearby
    /// enough to provide parallax instead of sitting uselessly far away.</param>
    /// <param name="innerRadius">Inner radius of the spherical shell.</param>
    /// <param name="outerRadius">Outer radius of the spherical shell —
    /// distinct (innerRadius, outerRadius) bands across several instances is
    /// what gives multi-layer parallax its depth.</param>
    /// <param name="count">Star count — kept low enough to regenerate the
    /// scratch buffer every frame without a measurable cost (see Draw).</param>
    /// <param name="seed">Fixed seed so the field is stable across runs
    /// instead of randomizing itself on every launch.</param>
    /// <param name="minBrightness">Lower bound of the grayscale brightness range.</param>
    /// <param name="maxBrightness">Upper bound of the grayscale brightness
    /// range — a dimmer range for farther layers is a real depth cue on its
    /// own, on top of the parallax rate difference.</param>
    public Starfield(GraphicsDevice gd, Vector3 center, float innerRadius, float outerRadius, int count,
        int seed = 12345, byte minBrightness = 160, byte maxBrightness = 255)
    {
        var rng = new Random(seed);
        _worldPositions = new Vector3[count];
        _colors = new Color[count];
        _scratch = new VertexPositionColor[count];
        for (int i = 0; i < count; i++)
        {
            // Random direction (rejection-sampled off the unit sphere) times
            // a radius uniformly picked within the shell band.
            Vector3 dir;
            do
            {
                dir = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1));
            } while (dir.LengthSquared() is < 1e-6f or > 1f);
            dir.Normalize();

            float r = MathHelper.Lerp(innerRadius, outerRadius, (float)rng.NextDouble());
            _worldPositions[i] = center + dir * r;

            int span = maxBrightness - minBrightness;
            byte brightness = (byte)(minBrightness + (span > 0 ? rng.Next(span) : 0));
            _colors[i] = new Color(brightness, brightness, brightness);
        }
        _effect = new BasicEffect(gd) { VertexColorEnabled = true, World = Matrix.Identity };
    }

    /// <summary>
    /// Draw every star camera-relative — subtract <paramref name="cameraEye"/>
    /// from each star's true world position on the CPU before it ever reaches
    /// the GPU, same convention <see cref="PoseConverter.ToXnaWorldRelative"/>
    /// uses for ships. Rebuilding vertex positions on the CPU every frame
    /// (rather than a persistent VertexBuffer) is deliberate: cameraEye moves
    /// every frame, and re-deriving large absolute star coordinates on the
    /// GPU side is exactly the category of bug P207 already hit once
    /// (uniformly blank frames from large-world-coordinate precision loss) —
    /// so, as with ships, only ever-small camera-relative values are uploaded.
    /// </summary>
    public void Draw(Matrix view, Matrix projection, Vector3 cameraEye)
    {
        for (int i = 0; i < _worldPositions.Length; i++)
            _scratch[i] = new VertexPositionColor(_worldPositions[i] - cameraEye, _colors[i]);

        _effect.View = view;
        _effect.Projection = projection;
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _effect.GraphicsDevice.DrawUserPrimitives(PrimitiveType.PointList, _scratch, 0, _scratch.Length);
        }
    }
}
