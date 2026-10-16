using System.Text;

namespace Allegiance.Core.Tests;

public class P701MdlSceneTests
{
    [Fact]
    public void Nested_texture_scopes_preserve_namespace_identity_and_restore_parent()
    {
        var model = MdlFile.Parse(Scene());
        Assert.Equal(new[] { "skin", "bay", "skin" }, model.Meshes.Select(m => m.TextureStem));
        Assert.Empty(model.UnsupportedGeometryNodes);
        Assert.All(model.Meshes, m => Assert.Equal(new ushort[] { 0, 1, 2 }, m.Indices));
    }

    [Fact]
    public void Lights_come_only_from_the_lights_export_with_all_twelve_floats_intact()
    {
        var light = Assert.Single(MdlFile.Parse(Scene()).Lights);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, light.Color);
        Assert.Equal(new float[] { 2, 3, 4 }, light.Position);
        Assert.Equal(2f, light.Period);
        Assert.Equal(0.3f, light.Phase);
        Assert.Equal(0.1f, light.RampUp);
        Assert.Equal(0.2f, light.Hold);
        Assert.Equal(0.4f, light.RampDown);
    }

    // A small MDLX scene independent of the artwork corpus. The image member
    // names are deliberately identical; their namespace indices disambiguate.
    private static byte[] Scene()
    {
        using var bytes = new MemoryStream();
        using var w = new BinaryWriter(bytes, Encoding.ASCII, leaveOpen: true);
        void U(uint n) => w.Write(n);
        void S(string value)
        {
            byte[] text = Encoding.ASCII.GetBytes(value);
            w.Write(text); w.Write((byte)0);
            for (int i = text.Length + 1; i % 4 != 0; i++) w.Write((byte)0);
        }
        void Import(uint index) { U(9); U(index); }
        void Mesh()
        {
            Import(0); U(7); U(3); U(3);
            foreach (float value in new float[] {
                0,0,0, 0,0, 0,0,1, 1,0,0, 1,0, 0,0,1, 0,1,0, 0,1, 0,0,1 }) w.Write(value);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
        }
        void Lights(float red)
        {
            Import(5); U(7); U(1);
            foreach (float value in new float[] { red,0.2f,0.3f,0.4f, 2,3,4, 2,0.3f,0.1f,0.2f,0.4f }) w.Write(value);
            U(0);
        }
        U(0xdebadf00); U(0x10000); U(4); U(6); U(2); U(1);
        foreach (string ns in new[] { "model", "effect", "skin", "bay" }) S(ns);
        foreach (var (ns, member) in new (uint, string)[] {
            (0,"MeshGeo"), (0,"TextureGeo"), (0,"GroupGeo"), (2,"image"), (3,"image"), (1,"LightsGeo") })
        { U(ns); S(member); }
        S("object"); S("lights");
        U(2); Lights(0.9f); // unreachable light definition must not leak
        U(0); Import(3); U(5); U(3);
        Mesh(); U(0);
        Import(4); Mesh(); Import(1); U(6); U(0);
        Mesh(); U(0);
        Import(2); U(6); Import(1); U(6); U(0);
        U(1); Lights(0.1f);
        return bytes.ToArray();
    }
}
