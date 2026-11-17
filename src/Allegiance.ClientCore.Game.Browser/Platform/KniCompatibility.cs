using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Pinned KNI 4.3.9001 API adapter. Explicit runtime accessors avoid reflection discovery/trimming.</summary>
internal static class KniCompatibility
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_InternalGlyphs")]
    private static extern SpriteFont.Glyph[] Glyphs(SpriteFont font);
    internal readonly record struct BrowserGlyph(char Character, SpriteFont.Glyph Value)
    {
        public Rectangle BoundsInTexture => Value.BoundsInTexture;
        public Rectangle Cropping => Value.Cropping;
        public float LeftSideBearing => Value.LeftSideBearing;
        public float Width => Value.Width;
        public float RightSideBearing => Value.RightSideBearing;
        public static implicit operator SpriteFont.Glyph(BrowserGlyph glyph) => glyph.Value;
    }
    internal static Dictionary<char, BrowserGlyph> GetGlyphs(this SpriteFont font) =>
        Glyphs(font).Select((glyph, index) => new BrowserGlyph(font.Characters[index], glyph)).ToDictionary(g => g.Character);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ModelMeshPart NewPart();
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ModelMesh NewMesh(GraphicsDevice device, List<ModelMeshPart> parts);
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ModelBone NewBone(int index, string name, Matrix transform);
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern Model NewModel(List<ModelBone> bones, List<ModelMesh> meshes);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_VertexBuffer")]
    private static extern void SetVertices(ModelMeshPart part, VertexBuffer vertices);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_IndexBuffer")]
    private static extern void SetIndices(ModelMeshPart part, IndexBuffer indices);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_PrimitiveCount")]
    private static extern void SetPrimitives(ModelMeshPart part, int count);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_NumVertices")]
    private static extern void SetCount(ModelMeshPart part, int count);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_ParentBone")]
    private static extern void SetParent(ModelMesh mesh, ModelBone bone);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BoundingSphere")]
    private static extern void SetBounds(ModelMesh mesh, BoundingSphere bounds);

    internal static Model StandIn(GraphicsDevice device, VertexBuffer vertices, IndexBuffer indices, int primitives, int count)
    {
        var part = NewPart();
        SetVertices(part, vertices); SetIndices(part, indices); SetPrimitives(part, primitives); SetCount(part, count);
        var mesh = NewMesh(device, [part]);
        SetBounds(mesh, new BoundingSphere(Vector3.Zero, ShipStandInModel.HalfLength));
        var bone = NewBone(0, "root", Matrix.Identity);
        SetParent(mesh, bone);
        part.Effect = new BasicEffect(device);
        return NewModel([bone], [mesh]);
    }
}
