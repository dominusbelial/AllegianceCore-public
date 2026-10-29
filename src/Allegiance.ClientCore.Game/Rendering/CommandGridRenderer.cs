using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Native WinTrek/cmdview.cpp:17-115,117-257,263-304;
/// WinTrek.cpp:76,3099,7293. Circular XY grid plus height drop lines/feet.</summary>
public sealed class CommandGridRenderer : IDisposable
{
    public const float Radius = 7500;
    public const int Segments = 40;
    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    public CommandGridRenderer(GraphicsDevice device)
    {
        _device = device;
        _effect = new BasicEffect(device) { VertexColorEnabled = true };
    }

    /// <summary>Native visibility (WinTrek.cpp:7293-7294): the ShowGrid toggle shows the grid
    /// and drop lines in ANY camera, and the command camera always shows them —
    /// <c>m_bCommandGrid || CommandCamera(m_cm) || (bAnimating &amp;&amp; CommandCamera(m_cmOld))</c>.
    /// The port's camera switches modes without native's transition animation, so the
    /// animating term has no port state to read.</summary>
    public static bool IsVisible(bool showGrid, bool commandCamera) => showGrid || commandCamera;

    public static VertexPositionColor[] Geometry(IEnumerable<Vector3> objects, Vector3 eye)
    {
        var vertices = new List<VertexPositionColor>(4 * (Segments + 1));
        void Line(Vector3 from, Vector3 to, Color color)
        {
            vertices.Add(new VertexPositionColor(from - eye, color));
            vertices.Add(new VertexPositionColor(to - eye, color));
        }
        for (int segment = 0; segment <= Segments; segment++)
        {
            float x = (segment / (float)Segments - .5f) * 2 * Radius;
            float edge = MathF.Sqrt(MathF.Max(0, Radius * Radius - x * x));
            var color = segment % 5 == 0 ? new Color(0, 0, 128) : new Color(0, 0, 64);
            Line(new Vector3(x, -edge, 0), new Vector3(x, edge, 0), color);
            Line(new Vector3(-edge, x, 0), new Vector3(edge, x, 0), color);
        }
        foreach (var position in objects)
        {
            var foot = new Vector3(position.X, position.Y, 0);
            Line(position, foot, position.Z > 0 ? new Color(200, 180, 20) : new Color(150, 100, 0));
            Line(foot + new Vector3(15, 0, 0), foot - new Vector3(15, 0, 0), new Color(0, 0, 128));
            Line(foot + new Vector3(0, 15, 0), foot - new Vector3(0, 15, 0), new Color(0, 0, 128));
        }
        return vertices.ToArray();
    }

    public void Draw(IEnumerable<Vector3> objects, Matrix view, Matrix projection, Vector3 eye)
    {
        var vertices = Geometry(objects, eye);
        var blend = _device.BlendState;
        var depth = _device.DepthStencilState;
        var rasterizer = _device.RasterizerState;
        try
        {
            _device.BlendState = BlendState.AlphaBlend;
            _device.DepthStencilState = DepthStencilState.DepthRead;
            _device.RasterizerState = RasterizerState.CullNone;
            _effect.World = Matrix.Identity;
            _effect.View = view;
            _effect.Projection = projection;
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.LineList, vertices, 0, vertices.Length / 2);
            }
        }
        finally
        {
            _device.BlendState = blend;
            _device.DepthStencilState = depth;
            _device.RasterizerState = rasterizer;
        }
    }
    public void Dispose() => _effect.Dispose();
}
