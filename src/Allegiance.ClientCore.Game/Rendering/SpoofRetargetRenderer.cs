using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P638 — the world-space "lock broken / missile retargeted onto chaff"
/// indicator. When a <c>FM_S_MISSILE_SPOOFED(110)</c> retargets a missile onto
/// the most recently deployed chaff decoy (native <c>pmissile->SetTarget(
/// m_pchaffLastCreated)</c>, appmsg.cpp:1667-1681), this renderer draws a
/// short amber line from the missile to the decoy position for the (brief)
/// life of the spoof — the port's equivalent of watching the missile visibly
/// curve away onto the decoy that native shows every viewer. It is a thin
/// additive <see cref="PrimitiveType.LineList"/> pass: no content-pipeline
/// asset, one reusable <see cref="VertexPositionColor"/> scratch buffer, and a
/// <see cref="BasicEffect"/> with <c>VertexColorEnabled</c>, mirroring the
/// generated-not-loaded renderer pattern of <see cref="TreasureRenderer"/> /
/// <see cref="ChaffRenderer"/>.
/// </summary>
public sealed class SpoofRetargetRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly BasicEffect _effect;
    private VertexPositionColor[] _vertices = new VertexPositionColor[512];
    private int _count;

    public SpoofRetargetRenderer(GraphicsDevice gd)
    {
        _gd = gd;
        _effect = new BasicEffect(gd) { VertexColorEnabled = true };
    }

    /// <summary>Begin a new frame's worth of segments (the draw loop calls
    /// this before gathering, then <see cref="AddLine"/> per spoofed missile,
    /// then <see cref="Draw"/> after the missile pass).</summary>
    public void Begin() => _count = 0;

    /// <summary>Add one segment in CAMERA-RELATIVE world space (the caller
    /// already subtracted <c>cameraEye</c>, matching every other world draw).</summary>
    public void AddLine(Vector3 a, Vector3 b, Color color)
    {
        if (_count + 2 > _vertices.Length)
            Array.Resize(ref _vertices, _vertices.Length * 2);
        _vertices[_count++] = new VertexPositionColor(a, color);
        _vertices[_count++] = new VertexPositionColor(b, color);
    }

    public void Draw(Matrix view, Matrix projection)
    {
        if (_count == 0) return;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.World = Matrix.Identity;
        _effect.CurrentTechnique.Passes[0].Apply();

        // Thin, additive, depth-tested-but-not-written — a transient HUD-ish
        // trace, like the other translucent overlays (chaff/mine glows).
        _gd.BlendState = BlendState.Additive;
        _gd.DepthStencilState = DepthStencilState.DepthRead;
        _gd.RasterizerState = RasterizerState.CullNone;
        _gd.DrawUserPrimitives(PrimitiveType.LineList, _vertices, 0, _count / 2);

        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose() => _effect.Dispose();
}
