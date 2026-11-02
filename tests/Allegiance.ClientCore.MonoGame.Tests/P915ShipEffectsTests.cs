using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Collision;
using Allegiance.Core;
using Allegiance.TestInfra;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P915ShipEffectsTests
{
    [Theory]
    [InlineData(4, 13.4f, 1f / 3f)] // PCore001 utl100 lifepod
    [InlineData(13, 24.7f, 1)]      // PCore001 wc_bifig fighter
    [InlineData(200, 380, 1)]      // capital hull
    [InlineData(4, 6, .25f)]       // close camera limits size independently
    [InlineData(4, 100, 1f / 3f)] // distant camera still respects small hull
    public void Own_external_smoke_uses_real_length_and_camera_distance(float length, float distance, float expected)
        => Assert.Equal(expected, ShipEffectRules.DamageSmokeScale(true, true, length, distance), 5);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Other_ships_and_cockpit_keep_native_size(bool own, bool external)
        => Assert.Equal(1, ShipEffectRules.DamageSmokeScale(own, external, 4, 13));

    [Theory]
    [InlineData(0, 13)]
    [InlineData(4, 0)]
    [InlineData(float.NaN, 13)]
    [InlineData(4, float.PositiveInfinity)]
    public void Unknown_dimensions_keep_native_size(float length, float distance)
        => Assert.Equal(1, ShipEffectRules.DamageSmokeScale(true, true, length, distance));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(.91f, 0)]
    [InlineData(.9f, 1)]
    [InlineData(.8f, 1)] // native float truncation: (1-.8f)*10 is below 2
    [InlineData(.45f, 5)]
    [InlineData(.1f, 9)]
    [InlineData(0, 0)]
    public void Scaling_preserves_thresholds_vent_counts_and_lifetime(float fraction, int expected)
    {
        int vents = DamageDebrisTracker.DamageFrameCount(fraction);
        Assert.Equal(expected, vents);
        var native = new DamageDebrisParticles();
        var scaled = new DamageDebrisParticles();
        native.Emit(Vector3.Zero, Vector3.Zero, Orientation.Identity, 2, 7, vents, .02f, new MidpointRandom());
        scaled.Emit(Vector3.Zero, Vector3.Zero, Orientation.Identity, 2, 7, vents, .02f, new MidpointRandom(), 1f / 3f);
        Assert.Equal(vents, native.ActiveCount);
        Assert.Equal(native.ActiveCount, scaled.ActiveCount);
        var original = native.Enumerate().ToArray();
        var smaller = scaled.Enumerate().ToArray();
        for (int i = 0; i < vents; i++)
        {
            Assert.Equal(original[i].Size / 3, smaller[i].Size, 5);
            Assert.Equal(original[i].RemainingFrac, smaller[i].RemainingFrac);
            Assert.Equal(original[i].Velocity.X / 3, smaller[i].Velocity.X, 5);
        }
        native.Update(DamageDebrisParticles.DurationSeconds);
        scaled.Update(DamageDebrisParticles.DurationSeconds);
        Assert.Equal(0, native.ActiveCount);
        Assert.Equal(0, scaled.ActiveCount);
    }

    [Theory]
    [InlineData(true, false, false, false)] // owner has no part
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]   // stale loadout after ejection
    [InlineData(false, false, true, false)] // remote pod
    [InlineData(false, false, false, true)] // remote loadout unavailable
    public void Afterburner_gate_respects_available_client_knowledge(bool own, bool mounted, bool pod, bool expected)
        => Assert.Equal(expected, ShipEffectRules.HasAfterburner(own, mounted, pod));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absent_afterburner_clears_ramp_even_with_button_and_previous_power(bool button)
    {
        Assert.Equal(0, ShipEffectRules.ThrustPower(false, button, 1, .02f, 6, 3));
        Assert.Equal(0, ShipEffectRules.NozzleGlow(false, button, -1));
        Assert.Equal(.5f, ShipEffectRules.NozzleGlow(false, button, 0));
        Assert.Equal(1, ShipEffectRules.NozzleGlow(false, button, 1));
    }

    [Fact]
    public void Mounted_afterburner_keeps_existing_ramp_glow_and_jiggle_gate()
    {
        Assert.Equal(.12f, ShipEffectRules.ThrustPower(true, true, 0, .02f, 6, 3), 5);
        Assert.Equal(.94f, ShipEffectRules.ThrustPower(true, false, 1, .02f, 6, 3), 5);
        Assert.Equal(1, ShipEffectRules.NozzleGlow(true, true, -1));
        var jiggle = new FlightJiggle(new MidpointRandom());
        Assert.Equal((0f, 0f), jiggle.Step(.02f, 1, afterburnerMounted: false));
    }

    [Theory]
    [InlineData(true, true, 4, true)]
    [InlineData(false, true, 4, false)]
    [InlineData(true, false, 4, false)]
    [InlineData(true, true, 13, false)]
    [InlineData(true, true, 200, false)]
    [InlineData(true, true, 0, false)]
    public void Texture_fix_is_scoped_to_own_small_external_hulls(bool own, bool external, float length, bool expected)
        => Assert.Equal(expected, ShipEffectRules.UprightHullTexture(own, external, length));

    [Fact]
    public void Texture_correction_preserves_geometry_materials_and_source_uvs()
    {
        var source = new MdlMesh
        {
            Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
            Indices = [0, 1, 2], TexCoords = [.1f, .2f, .5f, .7f, .9f, .3f], TextureStem = "hull",
        };
        var fixedMesh = NativeShipResolver.WithReversedTextureV(source);
        Assert.Same(source.Positions, fixedMesh.Positions);
        Assert.Same(source.Normals, fixedMesh.Normals);
        Assert.Same(source.Indices, fixedMesh.Indices);
        Assert.Equal(source.TextureStem, fixedMesh.TextureStem);
        Assert.Equal(.2f, source.TexCoords[1]);
        Assert.Equal(.8f, fixedMesh.TexCoords[1], 5);
        Assert.Equal(source.TexCoords[0], fixedMesh.TexCoords[0]);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Real_pod_atlas_recovers_detail_from_black_with_existing_unit_normals()
    {
        Require.Artwork();
        string art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var mesh = MdlFile.Parse(File.ReadAllBytes(Path.Combine(art, "utl100.mdl"))).Meshes.Single();
        var flipped = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(art, "utl100bmp.mdl")));
        var fileOrder = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(art, "utl100bmp.mdl")), flipVertical: false);
        var corrected = NativeShipResolver.WithReversedTextureV(mesh);
        int recovered = 0;
        foreach (var tri in mesh.Indices.Chunk(3))
        {
            float u = tri.Average(i => mesh.TexCoords[2 * i]);
            float v = tri.Average(i => mesh.TexCoords[2 * i + 1]);
            float correctedV = tri.Average(i => corrected.TexCoords[2 * i + 1]);
            int before = Sample(flipped, u, v), after = Sample(flipped, u, correctedV);
            Assert.InRange(Math.Abs(after - Sample(fileOrder, u, v)), 0, 10); // edge rounding
            if (before < 12 && after > 60) recovered++;
        }
        Assert.True(recovered >= 4, $"Expected recovered pod panels, got {recovered}");
        for (int i = 0; i < mesh.VertexCount; i++)
            Assert.InRange(new Vector3(mesh.Normals[3*i], mesh.Normals[3*i+1], mesh.Normals[3*i+2]).Length(), .999f, 1.001f);

        var core = Allegiance.Core.Model.StaticCore.Load(IgcReader.Parse(File.ReadAllBytes(Path.Combine(art, "PCore001.igc"))));
        foreach (var hull in core.HullTypes.Values.Where(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod)))
            Assert.False(ShipEffectRules.UprightHullTexture(true, true, hull.Length), $"Normal hull {hull.HullId} would change UVs");

        static int Sample(MdlTexture texture, float u, float v)
        {
            int x = Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1);
            int y = Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1);
            int p = 4 * (y * texture.Width + x);
            return (texture.Rgba[p] + texture.Rgba[p + 1] + texture.Rgba[p + 2]) / 3;
        }
    }

    private sealed class MidpointRandom : ITrailRandom
    {
        public float Range(float min, float max) => (min + max) / 2;
        public Vector3 Direction() => new Vector3(1, 0, 0);
    }
}
