using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P623 — the <see cref="VictimDamaged"/>(217) wire codec and the world's
/// per-frame hostile attribution buffer fed from it and drained via
/// <see cref="ClientWorld.TakeNewVictimDamages"/>. The message is a targeted
/// unicast to the struck ship's own peer telling it who hurt it and by how much
/// — the wire analogue of native's same-process DamageBucket population that
/// powers TK_TargetHostile.
/// </summary>
public class ClientWorldVictimDamagedTests
{
    [Fact]
    public void VictimDamaged_EncodesAndDecodes_RoundTripsAllFields()
    {
        var msg = new VictimDamaged { AttackerId = 9088, Damage = 42.5f };
        var decoded = VictimDamaged.Decode(msg.Encode());

        Assert.Equal(VictimDamaged.Fmid, decoded.GetFmid());
        Assert.Equal((ushort)217, VictimDamaged.Fmid);
        Assert.Equal((short)9088, decoded.AttackerId);
        Assert.Equal(42.5f, decoded.Damage);
    }

    [Fact]
    public void VictimDamaged_IsTwelveBytes_HeaderMatches()
    {
        var msg = new VictimDamaged { AttackerId = 1, Damage = 10f };
        byte[] bytes = msg.Encode();
        Assert.Equal(VictimDamaged.FixedSize, bytes.Length);
        Assert.Equal(12, bytes.Length);   // header(4)+attackerId(2)+pad(2)+damage(4)
        Assert.Equal((ushort)12, FedMessageHeader.Read(bytes).CbMsg);
        Assert.Equal((ushort)217, FedMessageHeader.Read(bytes).Fmid);
        Assert.Equal((short)1, VictimDamaged.Decode(bytes).AttackerId);
    }

    [Fact]
    public void VictimDamaged_Decode_RejectsWrongFmid()
    {
        var msg = new MineDestroyed { ClusterId = 1, MineId = 2 };   // fmid 84
        Assert.Throws<System.ArgumentException>(() => VictimDamaged.Decode(msg.Encode()));
    }

    [Fact]
    public void VictimDamaged_AppendsAttribution_AndDrainsExactlyOnce()
    {
        var world = new ClientWorld();
        world.Apply(new VictimDamaged { AttackerId = 5, Damage = 30f });
        world.Apply(new VictimDamaged { AttackerId = 6, Damage = 17.5f });

        var drained = world.TakeNewVictimDamages();
        Assert.Equal(2, drained.Count);
        Assert.Equal((short)5, drained[0].AttackerId);
        Assert.Equal(30f, drained[0].Damage);
        Assert.Equal((short)6, drained[1].AttackerId);
        Assert.Empty(world.TakeNewVictimDamages());   // drained exactly once
    }

    [Fact]
    public void VictimDamaged_NonPositiveDamage_IsIgnored()
    {
        var world = new ClientWorld();
        world.Apply(new VictimDamaged { AttackerId = 9, Damage = 0f });
        world.Apply(new VictimDamaged { AttackerId = 9, Damage = -3f });
        Assert.Empty(world.TakeNewVictimDamages());
    }
}
