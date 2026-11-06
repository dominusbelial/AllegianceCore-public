using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>A missile's explosion is drawn at its blast radius when it carries blast
/// power, else at its model radius (native missileigc.cpp:516-531 queues the blast at
/// blastRadius; clusterIGC.cpp:406-407 shows it at that size). The port sent the model
/// radius for every missile, so a 2-unit missile's blast was invisible at range.</summary>
public sealed class MissileExplosionScaleTests
{
    [Fact]
    public void Blast_missile_explodes_at_its_blast_radius()
        => Assert.Equal(60f, Missile.ExplosionScaleFor(blastPower: 120f, blastRadius: 60f, radius: 2f));

    [Fact]
    public void Missile_without_blast_power_explodes_at_its_model_radius()
        => Assert.Equal(2f, Missile.ExplosionScaleFor(blastPower: 0f, blastRadius: 60f, radius: 2f));
}
