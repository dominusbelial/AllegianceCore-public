using Allegiance.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
namespace Allegiance.ClientCore.Browser;
public sealed class PreviewGame : Game
{
    private readonly MdlFile _model;
    private readonly MdlTexture _pixels;
    private readonly GraphicsDeviceManager _graphics;
    private BasicEffect _effect = null!;
    private Texture2D _texture = null!;
    private SpriteBatch _sprites = null!;
    private Texture2D _white = null!;
    private (VertexPositionNormalTexture[] Vertices, short[] Indices)[] _meshes = [];
    private float _radius;
    private float _angle = .6f;
    private float _zoom = 2.9f;
    private readonly (float X, float Y, byte Light, int Size)[] _stars = CreateStars();
    private static (float, float, byte, int)[] CreateStars()
    {
        var random = new Random(1046);
        return Enumerable.Range(0, 280).Select(i => (random.NextSingle(), random.NextSingle(), (byte)random.Next(50, 170), i % 20 == 0 ? 2 : 1)).ToArray();
    }
    public void Rotate(float amount) => _angle += amount;
    public void Zoom(float amount) => _zoom = Math.Clamp(_zoom + amount, 1.8f, 5f);
    public bool Started { get; set; }
    public PreviewGame(MdlFile model, MdlTexture pixels)
    {
        _model=model; _pixels=pixels;
        _graphics=new GraphicsDeviceManager(this);
        IsFixedTimeStep=false;
        IsMouseVisible=true;
    }
    protected override void LoadContent()
    {
        _sprites=new SpriteBatch(GraphicsDevice);
        _white=new Texture2D(GraphicsDevice,1,1); _white.SetData(new[]{Color.White});
        _texture=new Texture2D(GraphicsDevice,_pixels.Width,_pixels.Height);
        _texture.SetData(_pixels.Rgba);
        _effect=new BasicEffect(GraphicsDevice) { TextureEnabled=true, Texture=_texture, LightingEnabled=true };
        _effect.EnableDefaultLighting();
        _effect.AmbientLightColor=new Vector3(.45f);
        _meshes=_model.Meshes.Select(m=>
        {
            var vertices=new VertexPositionNormalTexture[m.VertexCount];
            for(int i=0;i<vertices.Length;i++)
            {
                var p=new Vector3(m.Positions[i*3],m.Positions[i*3+1],m.Positions[i*3+2]);
                _radius=Math.Max(_radius,p.Length());
                vertices[i]=new(p,new Vector3(m.Normals[i*3],m.Normals[i*3+1],m.Normals[i*3+2]),new Vector2(m.TexCoords[i*2],m.TexCoords[i*2+1]));
            }
            return (vertices,m.Indices.Select(x=>unchecked((short)x)).ToArray());
        }).ToArray();
        Console.WriteLine($"Preview real MDL meshes={_meshes.Length} tris={_meshes.Sum(m=>m.Indices.Length/3)} radius={_radius} texture={_pixels.Width}x{_pixels.Height}");
    }
    protected override void Draw(GameTime time)
    {
        GraphicsDevice.Clear(new Color(3,7,18));
        // Procedural sky; no artwork or I/O in Draw.
        _angle += (float)Math.Min(time.ElapsedGameTime.TotalSeconds, .05) * .12f;
        _sprites.Begin(blendState:BlendState.Opaque);
        foreach (var star in _stars)
            _sprites.Draw(_white, new Rectangle((int)(star.X * GraphicsDevice.Viewport.Width), (int)(star.Y * GraphicsDevice.Viewport.Height), star.Size, star.Size), new Color(star.Light, star.Light, star.Light));
        _sprites.End();
        GraphicsDevice.DepthStencilState=DepthStencilState.Default;
        GraphicsDevice.BlendState=BlendState.Opaque;
        GraphicsDevice.RasterizerState=RasterizerState.CullNone;
        GraphicsDevice.SamplerStates[0]=SamplerState.LinearWrap;
        _effect.World=Matrix.CreateRotationY(_angle)*Matrix.CreateRotationX(-.2f);
        _effect.View=Matrix.CreateLookAt(new Vector3(_radius * _zoom * .45f, _radius * _zoom * .3f, _radius * _zoom * .84f),Vector3.Zero,Vector3.Up);
        _effect.Projection=Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4,GraphicsDevice.Viewport.AspectRatio,_radius*.01f,_radius*12);
        foreach(var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            foreach(var mesh in _meshes)
                GraphicsDevice.DrawUserIndexedPrimitives(PrimitiveType.TriangleList,mesh.Vertices,0,mesh.Vertices.Length,mesh.Indices,0,mesh.Indices.Length/3);
        }
        base.Draw(time);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _effect?.Dispose(); _texture?.Dispose(); _sprites?.Dispose(); _white?.Dispose(); }
        base.Dispose(disposing);
    }
}
