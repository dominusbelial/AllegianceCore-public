using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P521 — the death latch is NOT permanent. <see cref="ShipEntity.IsDead"/> is set
/// by <see cref="ClientWorld.Apply(KillShip)"/> (and SHIP_DELETE SDR_KILLED) to hide
/// a corpse (the P344/P370 ghost-ship exclusion), but nothing used to clear it, so
/// after a death the ship stayed hidden forever: the OWN ship's P508 local sim froze
/// at "dead" and never resumed after respawn + undock, and a REMOTE ship that died
/// and respawned with the same id stayed invisible on every other client forever.
///
/// The fix (scope 2 of the brief): clear <see cref="ShipEntity.IsDead"/> at the
/// NATIVE-MATCHING real respawn / re-presence signal —
///   • OWN ship: a fresh SET_CLUSTER (native re-homes <c>m_ship</c> into the
///     cluster and sets the cookie there, appmsg.cpp:1042-1087) — sent on
///     respawn/launch/undock/warp/join; plus SHIP_RESET (server actively re-engaged
///     the ship's live pose);
///   • REMOTE ship: an in-cluster HeavyShipsUpdate(176) (the server only carries
///     ships genuinely flying in the recipient's own cluster, P375) or an identity
///     ExportShip (the server never re-exports a corpse; TCP in-order ⇒ a post-death
///     export is a respawn re-export).
/// These mirror the exact sites that already clear <see cref="ShipEntity.IsGone"/>.
///
/// P370's protection is preserved: SingleShipUpdate is deliberately NOT a clear
/// site — it also reaches same-side teammates in OTHER sectors (P85), so it is
/// semantically weaker than real presence and must not revive a corpse from a
/// merely-refreshed (possibly out-of-view) pose.
/// </summary>
public sealed class DeadLatchRespawnTests
{
    private static ServerShipUpdate Pose(short id, uint time = 700u) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: time,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static SetCluster OwnSetCluster(short myId, short sectorId = 7) => new()
    {
        SectorId = sectorId,
        Ship = Pose(myId),
        Cookie = 0xA1,
    };

    private static SingleShipUpdate U(short id) => new()
    {
        OtTarget = 0,
        OidTarget = id,
        Ship = Pose(id),
    };

    private static void See(ClientWorld w, short id) => w.Apply(U(id));

    private static HeavyShipsUpdate HeavyFor(short sectorShipId, uint time = 701u)
    {
        var entry = HeavyShipUpdateEntry.From(
            time, Vector3.Zero,
            sectorShipId, stateM: 0, new ControlData(0f, 0f, 0f, 0f),
            time, new Vector3(1f, 0f, 0f), Orientation.Identity, Vector3.Zero,
            new[] { 0f, 0f, 0f }, hullFrac: 1f, shieldFrac: 1f, oreFrac: 0f);
        return new HeavyShipsUpdate
        {
            TimeReference = time,
            PositionReference = Vector3.Zero,
            HeavyShipEntries = [entry],
            BpTargetHull = 255,
            BpTargetShield = 255,
        };
    }

    /// <summary>⭐ OWN ship: death latches IsDead; a later SET_CLUSTER (the
    /// respawn/launch/undock signal) clears it so the P508 sim resumes.</summary>
    [Fact]
    public void Own_ship_set_cluster_after_death_clears_the_death_latch()
    {
        var w = new ClientWorld();
        w.Apply(OwnSetCluster(myId: 7));               // own ship seated, sector 7
        Assert.False(w.Ships[7].IsDead);

        w.Apply(KillShip.FromVictim(7, IgcObjectType.Ship, 5));   // dies
        Assert.True(w.Ships[7].IsDead);
        Assert.True(w.Ships[7].IsNotVisible, "a corpse is hidden");

        // Respawn/undock: the server sends a fresh SET_CLUSTER for the OWN ship.
        w.Apply(OwnSetCluster(myId: 7, sectorId: 8));

        Assert.False(w.Ships[7].IsDead, "SET_CLUSTER is the own-ship respawn signal");
        Assert.False(w.Ships[7].IsNotVisible, "the respawned own ship is visible and its sim can resume");
    }

    /// <summary>⭐ OWN ship: a SHIP_RESET (server re-engaged the live pose) also
    /// clears the death latch.</summary>
    [Fact]
    public void Own_ship_ship_reset_after_death_clears_the_death_latch()
    {
        var w = new ClientWorld();
        w.Apply(OwnSetCluster(myId: 7));
        w.Apply(KillShip.FromVictim(7, IgcObjectType.Ship, 5));
        Assert.True(w.Ships[7].IsDead);

        w.Apply(new ShipReset { Ship = Pose(7, time: 801u), Cookie = 0xBB });

        Assert.False(w.Ships[7].IsDead, "SHIP_RESET is presence-establishing for the own ship");
        Assert.False(w.Ships[7].IsNotVisible);
    }

    /// <summary>⭐ REMOTE ship: an in-cluster HeavyShipsUpdate(176) after a death
    /// is the respawn/re-entry signal — the server only carries ships genuinely
    /// flying in the recipient's own cluster.</summary>
    [Fact]
    public void Remote_ship_heavy_ships_update_after_death_clears_the_death_latch()
    {
        var w = new ClientWorld();
        w.Apply(OwnSetCluster(myId: 7));               // I am in sector 7 (176 derives sector)
        See(w, 9);                                     // remote ship 9 known to me
        w.Apply(KillShip.FromVictim(9, IgcObjectType.Ship, 8));
        Assert.True(w.Ships[9].IsDead);

        // The dead ship respawns and re-enters MY sector → 176 carries it.
        w.Apply(HeavyFor(sectorShipId: 9));

        Assert.False(w.Ships[9].IsDead, "in-cluster 176 is the remote respawn signal");
        Assert.False(w.Ships[9].IsNotVisible);
    }

    /// <summary>⭐ REMOTE ship: an identity EXPORT after death is the respawn
    /// re-export signal (see ShipDeleteGoneTests too).</summary>
    [Fact]
    public void Remote_ship_export_after_death_clears_the_death_latch()
    {
        var w = new ClientWorld();
        w.Apply(OwnSetCluster(myId: 7));
        See(w, 9);
        w.Apply(KillShip.FromVictim(9, IgcObjectType.Ship, 8));
        Assert.True(w.Ships[9].IsDead);

        w.Apply(new ExportShip { ShipId = 9, HullId = 3, SideId = 1, CharacterName = "revived" });

        Assert.False(w.Ships[9].IsDead);
        Assert.False(w.Ships[9].IsNotVisible);
    }

    /// <summary>⭐ P370 protection: SingleShipUpdate must NOT revive a corpse — it
    /// also reaches same-side teammates in other sectors, so it is not real
    /// presence.</summary>
    [Fact]
    public void Remote_ship_stays_dead_on_single_ship_update()
    {
        var w = new ClientWorld();
        w.Apply(OwnSetCluster(myId: 7));
        See(w, 9);
        w.Apply(KillShip.FromVictim(9, IgcObjectType.Ship, 8));
        Assert.True(w.Ships[9].IsDead);

        w.Apply(U(9));   // a pose for the dead ship — NOT presence

        Assert.True(w.Ships[9].IsDead, "SingleShipUpdate must not revive a corpse");
        Assert.True(w.Ships[9].IsNotVisible);
    }

    /// <summary>⭐ A fresh death re-latches IsDead after it was cleared by a
    /// respawn — the latch cycles correctly across repeated deaths.</summary>
    [Fact]
    public void Death_relatches_after_a_respawn_clear()
    {
        var w = new ClientWorld();
        w.Apply(OwnSetCluster(myId: 7));                 // seat
        w.Apply(KillShip.FromVictim(7, IgcObjectType.Ship, 5));   // death #1
        Assert.True(w.Ships[7].IsDead);
        w.Apply(OwnSetCluster(myId: 7, sectorId: 8));    // respawn #1
        Assert.False(w.Ships[7].IsDead);

        w.Apply(KillShip.FromVictim(7, IgcObjectType.Ship, 5));   // death #2
        Assert.True(w.Ships[7].IsDead, "a second death re-latches IsDead");
        Assert.True(w.Ships[7].IsNotVisible);

        w.Apply(OwnSetCluster(myId: 7, sectorId: 7));    // respawn #2
        Assert.False(w.Ships[7].IsDead);
        Assert.False(w.Ships[7].IsNotVisible);
    }
}
