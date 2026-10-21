using System.Buffers.Binary;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P773 — the client <b>derives</b> the local player's own Kill Bonus readout
/// from the own <see cref="ShipEntity"/>'s experience, exactly as native:
/// <c>100·(GetExperienceMultiplier()−1) = 50·e/(e+4)</c>
/// (<c>WinTrek/console.cpp:824-828</c>, <c>Igc/shipIGC.h:1812-1818</c>).
/// The experience itself is seeded by PLAYERINFO's <c>fExperience</c>
/// (<c>appmsg.cpp:2280</c>, HUD-20) and kept current by KILL_SHIP
/// (<c>appmsg.cpp:2769</c> victim reset, <c>appmsg.cpp:2807-2811</c> killer
/// +1). No wire message is involved — the port-only
/// fmid-212 kill-bonus stream is retired (P773). Pure
/// <see cref="ClientWorld.Apply"/> tests, no server.
/// </summary>
public class ClientWorldKillBonusTests
{
    private const short MyId = 701;
    private const short EnemyId = 702;

    /// <summary>Minimal decode-body pose (the shared BotFixRegression pattern):
    /// identity compact orientation, only the ship id and one pose coordinate
    /// are meaningful for these tests.</summary>
    private static ServerShipUpdate Pose(short id)
    {
        var bytes = new byte[ServerShipUpdate.Width];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, id);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 0f);
        var reader = new WireReader(bytes);
        return ServerShipUpdate.DecodeBody(ref reader);
    }

    /// <summary>A fresh world with the own ship seated in sector 1 (SET_CLUSTER
    /// assigns <see cref="ClientWorld.MyShipId"/>).</summary>
    private static ClientWorld SeatedWorld()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(MyId), Cookie = 0x1 });
        return world;
    }

    /// <summary>⭐ The own kill bonus is seeded from PLAYERINFO's
    /// <c>fExperience</c> (appmsg.cpp:2280): experience 2 →
    /// <c>50·2/6 ≈ 16.667%</c>, the same readout native's console shows from
    /// <c>GetExperienceMultiplier()</c>.</summary>
    [Fact]
    public void Own_kill_bonus_is_seeded_from_PLAYERINFO_experience()
    {
        var world = SeatedWorld();
        world.Apply(new PlayerInfo { ShipId = MyId, ISide = 0, CharacterName = "ME", FExperience = 2f });

        Assert.Equal(50f * 2f / 6f, world.KillBonusPercent, 3);
    }

    /// <summary>⭐ A cross-side kill credit on an enemy raises the own ship's
    /// experience by one (appmsg.cpp:2807-2811): experience 2 → 3 →
    /// <c>50·3/7 ≈ 21.429%</c>.</summary>
    [Fact]
    public void Own_kill_credit_on_an_enemy_raises_the_own_kill_bonus()
    {
        var world = SeatedWorld();
        world.Apply(new PlayerInfo { ShipId = MyId, ISide = 0, CharacterName = "ME", FExperience = 2f });
        world.Apply(new PlayerInfo { ShipId = EnemyId, ISide = 1, CharacterName = "FOE" });

        world.Apply(new KillShip
        {
            ShipId = EnemyId,
            TypeCredit = (short)IgcObjectType.Ship,
            IdCredit = MyId,
            SideIdKiller = 0,
            BKillCredit = true,
            BDeathCredit = true,
        });

        Assert.Equal(50f * 3f / 7f, world.KillBonusPercent, 3);
    }

    /// <summary>⭐ The own ship as victim: an eject (no death credit) keeps the
    /// experience (appmsg.cpp:2763-2770, the <c>AddEjection</c> branch), while a
    /// real death resets it to 0 (<c>SetExperience(0.0f)</c>,
    /// appmsg.cpp:2766-2769) — so the readout drops to 0%.</summary>
    [Fact]
    public void Own_death_resets_and_eject_keeps_the_own_kill_bonus()
    {
        var world = SeatedWorld();
        world.Apply(new PlayerInfo { ShipId = MyId, ISide = 0, CharacterName = "ME", FExperience = 2f });
        Assert.Equal(50f * 2f / 6f, world.KillBonusPercent, 3);

        // Eject: no death credit — the streak survives.
        world.Apply(new KillShip { ShipId = MyId, TypeCredit = -1, IdCredit = -1, BKillCredit = false, BDeathCredit = false });
        Assert.Equal(50f * 2f / 6f, world.KillBonusPercent, 3);

        // Real death: native zeroes the victim ship's experience.
        world.Apply(new KillShip { ShipId = MyId, TypeCredit = -1, IdCredit = -1, BKillCredit = false, BDeathCredit = true });
        Assert.Equal(0f, world.KillBonusPercent, 3);
    }

    /// <summary>⭐ A fresh world has no own ship and therefore no kill bonus —
    /// the readout is an honest 0 (passes on both the old streamed and the new
    /// derived implementations; kept as the no-own-ship guard).</summary>
    [Fact]
    public void Fresh_world_has_no_kill_bonus()
    {
        var world = new ClientWorld();
        Assert.Equal(0f, world.KillBonusPercent);
    }
}
