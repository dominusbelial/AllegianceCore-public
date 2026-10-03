using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P373 — ClientWorld's wiring of the one-shot "new contact" (first-sighting)
/// sound cue. Native fires the cue exactly once per real not-visible→visible
/// edge (<c>UpdateSideVisibility</c>, trekigc.cpp:2080-2094) with two DISTINCT
/// assets depending on whether the newly-visible ship is hostile or friendly.
/// This port's pure-renderer client has no local visibility sim, so the edge is
/// the moment a ship first establishes itself in the viewer's view with a real
/// identity (EXPORT) or a P370-gone ship returns (re-EXPORT or 176 telemetry).
/// The enemy/friendly split reads the SAME source P372's side colour uses —
/// <see cref="ShipEntity.SideId"/> vs <see cref="ClientWorld.MySideId"/>
/// (no alliance concept in this port: "allied" = own side only).
/// </summary>
public class ClientWorldNewContactCueTests
{
    private const short MySide = 0;   // Alpha in the 2-team maps
    private const short EnemySide = 1;

    /// <summary>Seed the client's own side id via the own-ship dummy EXPORT
    /// (ShipId = NA(-1)), the only wire source of the player's own team.</summary>
    private static void SetMySide(ClientWorld world, short side) =>
        world.Apply(new ExportShip { ShipId = -1, SideId = side });

    private static ServerShipUpdate PoseFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static SetCluster SetClusterFor(short myId) => new()
    {
        SectorId = 1,
        Ship = PoseFor(myId),
        Cookie = 0x1,
    };

    private static SingleShipUpdate U(short id) => new() { OtTarget = 0, OidTarget = id, Ship = PoseFor(id) };

    /// <summary>A HEAVY_SHIPS_UPDATE(176) carrying a single ship — the
    /// sector-correct "this ship is in our view" telemetry (P370 clears IsGone
    /// on it; it carries no SideId/HullId — identity comes only from EXPORT).</summary>
    private static HeavyShipsUpdate HeavyFor(short shipId)
    {
        uint timeRef = 100u;
        var posRef = new Vector3(0f, 0f, 0f);
        return new HeavyShipsUpdate
        {
            TimeReference = timeRef,
            PositionReference = posRef,
            HeavyShipEntries =
            [
                HeavyShipUpdateEntry.From(timeRef, posRef, shipId, 0,
                    new ControlData(0f, 0f, 0f, 0f), timeRef, posRef,
                    new Orientation(), new Vector3(0f, 0f, 0f), new[] { 0f, 0f, 0f },
                    1f, 1f, 1f),
            ],
        };
    }

    /// <summary>⭐ A brand-new teammate's EXPORT (real id, our side) cues the
    /// FRIENDLY cue exactly once.</summary>
    [Fact]
    public void New_teammate_export_cues_friendly_once()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);

        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = MySide, CharacterName = "mate" });

        var cue = Assert.Single(w.TakeNewContactCues());
        Assert.Equal((short)7, cue.ShipId);
        Assert.False(cue.IsEnemy, "a same-side ship is friendly");
    }

    /// <summary>⭐ A brand-new enemy's cross-side EXPORT (real id, other side)
    /// cues the ENEMY cue exactly once.</summary>
    [Fact]
    public void New_enemy_export_cues_enemy_once()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);

        w.Apply(new ExportShip { ShipId = 8, HullId = 3, SideId = EnemySide, CharacterName = "foe" });

        var cue = Assert.Single(w.TakeNewContactCues());
        Assert.Equal((short)8, cue.ShipId);
        Assert.True(cue.IsEnemy, "a different, non-allied side is an enemy");
    }

    /// <summary>⭐ A routine identity REFRESH of an already-visible, already-
    /// announced ship must NOT re-cue (native fires once per edge, not on every
    /// update while the ship stays visible).</summary>
    [Fact]
    public void Refresh_export_of_visible_ship_does_not_recue()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = MySide });
        Assert.Single(w.TakeNewContactCues());

        // A second export while it's still visible (e.g. a loadout refresh) →
        // no new cue.
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = MySide, CharacterName = "mate" });

        Assert.Empty(w.TakeNewContactCues());
    }

    /// <summary>⭐ The local player's OWN ship never cues (it is not a "new
    /// contact"); likewise the dummy own-ship EXPORT (ShipId = -1) that seeds
    /// MySideId never queues anything.</summary>
    [Fact]
    public void Own_ship_export_never_cues()
    {
        var w = new ClientWorld();

        // Dummy own-ship EXPORT (the MySideId seed) — must not cue nor create an entity.
        w.Apply(new ExportShip { ShipId = -1, SideId = MySide });
        Assert.Empty(w.TakeNewContactCues());

        // The real own ship (assigned by SET_CLUSTER → IsMine) re-exported must not cue.
        w.Apply(SetClusterFor(42));
        Assert.Empty(w.TakeNewContactCues());
        w.Apply(new ExportShip { ShipId = 42, HullId = 3, SideId = MySide });
        Assert.Empty(w.TakeNewContactCues());
    }

    /// <summary>⭐ A P370-gone ship that RETURNS via a fresh EXPORT counts as a
    /// fresh sighting and re-cues (native's cache-and-resurrect visibility).</summary>
    [Fact]
    public void Gone_ship_returns_via_export_and_recues()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = EnemySide });
        Assert.True(Assert.Single(w.TakeNewContactCues()).IsEnemy);

        // Leaves view (warps out) → departs.
        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLeftSector });
        Assert.True(w.Ships[7].IsGone);

        // Returns via identity re-export → a FRESH sighting, enemy cue again.
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = EnemySide });
        var cue = Assert.Single(w.TakeNewContactCues());
        Assert.True(cue.IsEnemy, "a returning enemy re-cues as enemy");
    }

    /// <summary>⭐ The P369-latched cross-side case: a ship returns via 176
    /// telemetry with NO fresh EXPORT (identity latch is one-shot), so the
    /// first-sighting edge must be recognized on the heavy path too — using the
    /// already-established SideId.</summary>
    [Fact]
    public void Returning_identified_ship_cues_via_heavy_ships_update()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = EnemySide });  // establishes identity + cues
        Assert.Single(w.TakeNewContactCues());

        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLeftSector });  // leaves view
        Assert.True(w.Ships[7].IsGone);

        // Telemetry resumes (undock/warp-back) with no fresh EXPORT → cues using latched SideId.
        w.Apply(HeavyFor(7));

        var cue = Assert.Single(w.TakeNewContactCues());
        Assert.Equal((short)7, cue.ShipId);
        Assert.True(cue.IsEnemy, "classified from the latched real SideId (enemy)");
    }

    /// <summary>⭐ A not-yet-identified ship appearing on the heavy path (its
    /// SideId is still the C# default 0) must NOT be cued from that default —
    /// its cue fires only once EXPORT identifies it. Guards against
    /// misclassifying during the brief pre-identity window.</summary>
    [Fact]
    public void Unidentified_ship_on_heavy_path_does_not_cue()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);

        w.Apply(HeavyFor(999));   // telemetry for a ship we've never been EXPORTed

        Assert.Empty(w.TakeNewContactCues());

        // Once identified, the EXPORT cues it correctly.
        w.Apply(new ExportShip { ShipId = 999, HullId = 3, SideId = EnemySide });
        Assert.True(Assert.Single(w.TakeNewContactCues()).IsEnemy);
    }

    /// <summary>⭐ A dead ship does not cue while dead — even a pose for it
    /// (SingleShipUpdate is not real presence, P370) is silent — but once it
    /// GENUINELY respawns (a re-EXPORT is the respawn signal, P521), re-entering
    /// view is a FRESH not-visible→visible edge and it cues again (native
    /// fires the cue once per real transition, P373).</summary>
    [Fact]
    public void Dead_ship_stays_silent_until_it_genuinely_respawns()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = EnemySide });
        Assert.Single(w.TakeNewContactCues());

        w.Apply(KillShip.FromVictim(7, IgcObjectType.Ship, 5));   // dies
        // While dead (IsDead latched), no cue — even from a weak pose.
        w.Apply(U(7));
        Assert.Empty(w.TakeNewContactCues());

        // A genuine respawn re-export clears the death latch → fresh sighting cue.
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = EnemySide });
        Assert.True(Assert.Single(w.TakeNewContactCues()).IsEnemy);
    }

    /// <summary>⭐ Multiple distinct new ships cue in arrival order; a drain
    /// empties the queue (drain-then-forget lifecycle).</summary>
    [Fact]
    public void Multiple_cues_drain_in_order_then_empty()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);
        w.Apply(new ExportShip { ShipId = 1, HullId = 3, SideId = MySide });    // friendly
        w.Apply(new ExportShip { ShipId = 2, HullId = 3, SideId = EnemySide }); // enemy

        var cues = w.TakeNewContactCues();
        Assert.Equal(2, cues.Count);
        Assert.Equal((short)1, cues[0].ShipId);
        Assert.False(cues[0].IsEnemy);
        Assert.Equal((short)2, cues[1].ShipId);
        Assert.True(cues[1].IsEnemy);
        Assert.Empty(w.TakeNewContactCues());
    }

    /// <summary>⭐ Reset (reconnect boundary) drops any pending cues.</summary>
    [Fact]
    public void Reset_clears_pending_cues()
    {
        var w = new ClientWorld();
        SetMySide(w, MySide);
        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = EnemySide });

        w.Reset();

        Assert.Empty(w.TakeNewContactCues());
    }
}
