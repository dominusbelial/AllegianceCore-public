using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P758 — the client half of <c>FM_S_ACQUIRE_TREASURE(162)</c> beyond the
/// P710 sound cues: native's <c>c_tcCash</c> branch posts the reward/donation
/// text (<c>clintlib/appmsg.cpp:289-299</c>):
/// <list type="bullet">
/// <item>no autodonate target → <c>"You received a reward of $%d."</c></item>
/// <item>autodonate target set → <c>"You donated your reward of $%d to %s."</c>
/// (the target's name).</item>
/// </list>
/// The wallets themselves already moved server-side (<c>MONEY_CHANGE</c>); this
/// is the player-facing notice. Both texts land in the world event log via
/// <see cref="ClientWorld.PostSystemLine"/> (sender NA, no player prefix).
/// </summary>
public class P758AcquireTreasureClientTests
{
    private static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(
        id, stateM: 0, controls: default, time: 1u,
        position: Vector3.Zero, orientation: Orientation.Identity, velocity: Vector3.Zero,
        turnRates: [0f, 0f, 0f],
        hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
        energyFrac: 1f, oreFrac: 0f, power: 0f);

    /// <summary>Seed the local ship and enter the game (the native handler's
    /// <c>IsInGame()</c> gate, appmsg.cpp:264).</summary>
    private static ClientWorld SeatedWorld(short me)
    {
        var w = new ClientWorld();
        w.Apply(new EnterGame());
        w.Apply(new SetCluster { SectorId = 1, Ship = Pose(me), Cookie = 1 });
        return w;
    }

    [Fact]
    public void Cash_reward_without_autodonate_posts_received_line()
    {
        var w = SeatedWorld(me: 710);

        w.Apply(new AcquireTreasure { TreasureCode = (byte)TreasureCode.Cash, TreasureId = 500, Amount = 500 });

        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("You received a reward of $500.", line.Message);
        Assert.Equal((short)-1, line.SidSender);   // PostSystemLine — no player prefix
    }

    [Fact]
    public void Cash_reward_with_autodonate_posts_donation_line_naming_target()
    {
        var w = SeatedWorld(me: 711);
        w.Apply(new ExportShip { ShipId = 802, HullId = 1, SideId = 0, CharacterName = "Benefactor" });
        w.Apply(new AutoDonateUpdate { SidDonateBy = 711, SidDonateTo = 802 });

        w.Apply(new AcquireTreasure { TreasureCode = (byte)TreasureCode.Cash, TreasureId = 900, Amount = 900 });

        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("You donated your reward of $900 to Benefactor.", line.Message);
    }

    [Fact]
    public void Cash_reward_with_autodonate_to_unknown_target_falls_back_to_received_line()
    {
        // The autodonate relationship points at a ship that is not (yet) in the
        // entity table: native's %s would have no name to print, so the port
        // falls back to the plain reward line instead of an empty name.
        var w = SeatedWorld(me: 712);
        w.Apply(new AutoDonateUpdate { SidDonateBy = 712, SidDonateTo = 999 });

        w.Apply(new AcquireTreasure { TreasureCode = (byte)TreasureCode.Cash, TreasureId = 600, Amount = 600 });

        var line = Assert.Single(w.Events.Chats);
        Assert.Equal("You received a reward of $600.", line.Message);
    }

    [Fact]
    public void Non_cash_acquire_posts_no_reward_line()
    {
        var w = SeatedWorld(me: 713);
        w.TakeSoundEvents();   // discard any seating-pass emissions

        w.Apply(new AcquireTreasure { TreasureCode = (byte)TreasureCode.Powerup, TreasureId = 0x08, Amount = -1 });

        Assert.Empty(w.Events.Chats);
        Assert.Equal(ClientSoundEventKind.PickupPowerup, Assert.Single(w.TakeSoundEvents()).Kind);
    }
}
