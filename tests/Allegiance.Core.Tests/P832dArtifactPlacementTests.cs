using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed partial class P663TreasureParityTests
{
    [Fact]
    public void P832d_Artifact_placement_uses_native_mine_radius_when_the_base_field_is_uninitialized()
    {
        Vector3 Spawn(float unusedBaseRadius)
        {
            var (mission, _) = Domain();
            var sector = mission.Sectors[1];
            var type = DataMineTypeIGC.Decode(new byte[DataMineTypeIGC.Size]) with
                { Radius = unusedBaseRadius, MineRadius = 100, Lifespan = 600 };
            sector.Add(new Mine(9000, sector, type, Vector3.Zero, 0));
            var artifact = Assert.IsType<Treasure>(mission.GenerateTreasure(0, sector, -2, new Random(832)));
            Assert.Equal(Side.ArtifactSentinelFlag, artifact.Data.TreasureId);
            var p = artifact.Position;
            Assert.InRange(MathF.Sqrt(p.X * p.X + p.Y * p.Y + 25 * p.Z * p.Z), 500, 5000);
            Assert.True(p.LengthSquared() >= 10 * (25 + 100) * (25 + 100), "Native clearance must use the runtime mine radius");
            return p;
        }
        var clean = Spawn(0);
        // 0xCCCCCCCC is present in the artwork core's unused inherited field.
        // Squaring that negative value previously forced a respawn ~1e8 units out.
        Assert.Equal(clean, Spawn(BitConverter.Int32BitsToSingle(unchecked((int)0xCCCCCCCC))));
        Assert.Equal(clean, Spawn(float.PositiveInfinity));
        Assert.Equal(clean, Spawn(float.NaN));
    }
}
