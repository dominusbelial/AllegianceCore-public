using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// HUD-20 — the client half of the native per-ship experience mechanism:
/// PLAYERINFO seeds <c>fExperience</c> (<c>appmsg.cpp:2280</c>), KILL_SHIP zeroes
/// the victim on a real death (<c>:2769</c>) and adds one to a cross-side,
/// non-allied killer (<c>:2807-2811</c>). Also pins the passenger turret id the
/// observer count needs (<c>appmsg.cpp:2073</c>).
/// </summary>
public class ClientWorldHud20Tests
{
    private const short Victim = 801, Killer = 802;

    private static PlayerInfo Info(short shipId, short side, float experience) => new()
    {
        ShipId = shipId,
        ISide = side,
        CharacterName = $"ship {shipId}",
        FExperience = experience,
        DtidDrone = PlayerInfo.NA,
    };

    private static KillShip Kill(short victim, short killer, short killerSide,
        bool killCredit, bool deathCredit) => new()
    {
        ShipId = victim,
        TypeCredit = (short)IgcObjectType.Ship,
        IdCredit = killer,
        SideIdKiller = killerSide,
        BKillCredit = killCredit,
        BDeathCredit = deathCredit,
    };

    [Fact]
    public void PlayerInfo_seeds_the_ship_experience()
    {
        var world = new ClientWorld();
        world.Apply(Info(Victim, 0, 3f));
        Assert.Equal(3f, world.Ships[Victim].Experience);
    }

    [Fact]
    public void KillShip_with_death_credit_zeroes_the_victim()
    {
        var world = new ClientWorld();
        world.Apply(Info(Victim, 0, 3f));
        world.Apply(Info(Killer, 1, 1f));
        world.Apply(Kill(Victim, Killer, 1, killCredit: true, deathCredit: true));
        Assert.Equal(0f, world.Ships[Victim].Experience);
    }

    [Fact]
    public void Eject_without_death_credit_keeps_the_victim_experience()
    {
        var world = new ClientWorld();
        world.Apply(Info(Victim, 0, 4f));
        world.Apply(Info(Killer, 1, 1f));
        world.Apply(Kill(Victim, Killer, 1, killCredit: true, deathCredit: false));
        Assert.Equal(4f, world.Ships[Victim].Experience);
    }

    [Fact]
    public void Cross_side_non_allied_kill_credit_adds_one_to_the_killer()
    {
        var world = new ClientWorld();
        world.Apply(Info(Victim, 0, 0f));
        world.Apply(Info(Killer, 1, 2f));
        world.Apply(Kill(Victim, Killer, 1, killCredit: true, deathCredit: true));
        Assert.Equal(3f, world.Ships[Killer].Experience);
    }

    [Fact]
    public void Same_side_kill_credit_adds_nothing()
    {
        var world = new ClientWorld();
        world.Apply(Info(Victim, 0, 0f));
        world.Apply(Info(Killer, 0, 2f));
        world.Apply(Kill(Victim, Killer, 0, killCredit: true, deathCredit: true));
        Assert.Equal(2f, world.Ships[Killer].Experience);
    }

    [Fact]
    public void Allied_sides_earn_no_experience_and_non_credited_kills_add_nothing()
    {
        var world = new ClientWorld();
        // CHANGE_ALLIANCES: sides 0 and 1 share alliance group 0
        // (fsmission.cpp:5750-5807; clintlib/appmsg.cpp:2554-2569).
        world.Apply(new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] });
        world.Apply(Info(Victim, 0, 0f));
        world.Apply(Info(Killer, 1, 2f));
        world.Apply(Kill(Victim, Killer, 1, killCredit: true, deathCredit: true));
        Assert.Equal(2f, world.Ships[Killer].Experience);

        world.Apply(Info(Victim, 0, 0f));
        world.Apply(Info(Killer, 2, 2f));
        world.Apply(Kill(Victim, Killer, 2, killCredit: false, deathCredit: true));
        Assert.Equal(2f, world.Ships[Killer].Experience);
    }

    [Fact]
    public void Passenger_loop_stores_the_turret_mount_and_unboarding_clears_it()
    {
        const short host = 900, gunner = 901, observer = 902;
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = host, HullId = 15, SideId = 2, CharacterName = "host" });
        world.Apply(new ExportShip { ShipId = gunner, HullId = 445, SideId = 2, CharacterName = "gunner" });
        world.Apply(new ExportShip { ShipId = observer, HullId = 445, SideId = 2, CharacterName = "observer" });

        world.Apply(new LoadoutChange
        {
            SidShip = host,
            HullId = 16,
            Parts = [],
            Passengers =
            [
                new PassengerData(gunner, TurretId: 2),
                new PassengerData(observer, TurretId: (byte)ShipEntity.BoardTurretObserver),
            ],
        });

        Assert.Equal((short)2, world.Ships[gunner].BoardTurretId);
        Assert.Equal(ShipEntity.BoardTurretObserver, world.Ships[observer].BoardTurretId);
        Assert.Equal((short)host, world.Ships[observer].ParentId);

        // LEAVESHIP detaches the child (appmsg.cpp:1914-1945) — the mount latch
        // clears with the parent link.
        world.Apply(new LeaveShip { SidChild = observer });
        Assert.Equal((short)-1, world.Ships[observer].ParentId);
        Assert.Equal((short)-1, world.Ships[observer].BoardTurretId);
    }
}
