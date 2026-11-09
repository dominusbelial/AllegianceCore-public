using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P982 one point batch and at most one line batch, depth-free.
/// Buffers/effects are reused. A live toggle clears history before the first tail.</summary>
public sealed class SpaceStarRenderer(GraphicsDevice device) : IDisposable
{
    private readonly BasicEffect _effect = new(device) { VertexColorEnabled = true, World = Matrix.Identity };
    private VertexPositionColor[] _points = [];
    private VertexPositionColor[] _lines = [];
    private Matrix _previous;
    private bool _history;
    private float _fade;
    private long _sequence;
    public int LastTailCount { get; private set; }

    public void Draw(VertexStar[] stars, Matrix view, Matrix projection, SpaceVisualFrame frame, bool drawPoints = true)
    {
        LastTailCount = 0;
        if (_points.Length != stars.Length)
        {
            _points = new VertexPositionColor[stars.Length];
            _lines = new VertexPositionColor[stars.Length * 2];
        }
        bool valid = frame.AdvanceHistory && frame.Streaks && _history && !frame.Cut && (frame.Sequence == 0 || frame.Sequence == _sequence + 1) && frame.DeltaSeconds > 0;
        var viewport = new Vector2(device.Viewport.Width, device.Viewport.Height);
        var rotation = SimStarMath.RotationOnly(view);
        var exposure = valid ? SimStarMath.ExposureView(_previous, rotation, frame.DeltaSeconds) : rotation;
        // Fade follows angular speed, in seconds, rather than a per-frame multiplier.
        float motion = Vector3.Distance(rotation.Forward, exposure.Forward) + Vector3.Distance(rotation.Up, exposure.Up);
        float target = valid ? Math.Clamp(motion * 160f, 0, 1) : 0;
        if (frame.AdvanceHistory) _fade = valid ? MathHelper.Lerp(_fade, target, 1f - MathF.Exp(-frame.DeltaSeconds / 0.08f)) : 0;
        for (int i = 0; i < stars.Length; i++)
        {
            var star = stars[i];
            _points[i] = new VertexPositionColor(star.Direction * 7000f, star.Color);
            if (!valid || _fade <= 0.001f) continue;
            if (!SimStarMath.Project(star.Direction, rotation, projection, viewport, out var head)
                || head.X < 0 || head.X > viewport.X || head.Y < 0 || head.Y > viewport.Y) continue;
            var tail = SimStarMath.Tail(star.Direction, rotation, exposure, projection, viewport);
            if (tail == Vector2.Zero) continue;
            _lines[LastTailCount * 2] = new VertexPositionColor(new Vector3(head, 0), new Color(new Vector4(star.Color.ToVector3(), _fade * 0.6f)));
            _lines[LastTailCount * 2 + 1] = new VertexPositionColor(new Vector3(head + tail, 0), Color.Transparent);
            LastTailCount++;
        }
        // Turning tails off must not change the legacy star batch's inherited
        // blend state. Empty history-only calls submit no graphics work.
        if ((drawPoints && stars.Length > 0) || LastTailCount > 0)
            device.BlendState = BlendState.Additive;
        _effect.View = rotation;
        _effect.Projection = projection;
        if (drawPoints && stars.Length > 0)
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                device.DrawUserPrimitives(PrimitiveType.PointList, _points, 0, _points.Length);
            }
        if (LastTailCount > 0)
        {
            _effect.View = Matrix.Identity;
            _effect.Projection = Matrix.CreateOrthographicOffCenter(0, viewport.X, viewport.Y, 0, 0, 1);
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                device.DrawUserPrimitives(PrimitiveType.LineList, _lines, 0, LastTailCount);
            }
        }
        // Glimpses explicitly opt out of advancing the flight history.
        if (frame.AdvanceHistory)
        {
            _previous = rotation;
            _sequence = frame.Sequence;
            _history = frame.Streaks;
        }
    }

    public void Dispose() => _effect.Dispose();
}

/// <summary>Per-tier seeded layer, preserving authored seed/count and a 500-point
/// ceiling. Allocated lazily on opt-in; setting a glimpse cluster does not advance
/// the flight renderer's history.</summary>
public sealed class SimStarLayer(GraphicsDevice device) : IDisposable
{
    private uint _seed;
    private int _count = -1;
    private VertexStar[] _stars = [];
    private readonly SpaceStarRenderer _renderer = new(device);
    public void SetCluster(Allegiance.Core.DataClusterIGC cluster)
    {
        int count = SimStarMath.SectorCount(cluster.Stars);
        if (_seed == cluster.StarSeed && _count == count) return;
        _seed = cluster.StarSeed; _count = count;
        _stars = SimStarMath.Generate(_seed, count);
    }
    public void Draw(Matrix view, Matrix projection, SpaceVisualFrame frame) => _renderer.Draw(_stars, view, projection, frame);
    public void Dispose() => _renderer.Dispose();
}
