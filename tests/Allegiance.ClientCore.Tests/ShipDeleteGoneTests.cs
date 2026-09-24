using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P370 — the client half of the "ship left your view" fix. Previously the
/// MonoGame client only learned of a ship's departure when it DIED (KILL_SHIP
/// → <see cref="ShipEntity.IsDead"/>); a ship that docked or warped to a
/// different sector was never told to stop rendering it, so it froze at its
/// last-known pose forever (the ghost-ship bug). The server now emits an
/// explicit <c>SHIP_DELETE(20)</c> for those transitions (and already did for
/// death/disconnect); these tests pin the <see cref="ClientWorld"/> reaction:
/// <list type="bullet">
/// <item>SDR_DOCKED / SDR_LEFTSECTOR mark a KNOWN ship transiently "gone"
/// (<see cref="ShipEntity.IsGone"/>, hence <see cref="ShipEntity.IsNotVisible"/>).</item>
/// <item>A delete for a ship we never learned about creates NO phantom entry.</item>
/// <item>KILL_SHIP latches death permanently (IsDead, which keeps IsNotVisible
/// true no matter what).</item>
/// <item>A ship that comes back into view (fresh pose or identity EXPORT)
/// clears IsGone and renders again — the reverse-transition requirement.</item>
/// </list>
/// </summary>
public class ShipDeleteGoneTests
{
    private static ServerShipUpdate PoseFor(short id, uint time = 1u) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: time,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static SingleShipUpdate U(short id) => new()
    {
        OtTarget = 0,
        OidTarget = id,
        Ship = PoseFor(id),
    };

    private static void See(ClientWorld w, short id) => w.Apply(U(id));

    /// <summary>⭐ SDR_DOCKED hides an already-known flying ship (transiently).</summary>
    [Fact]
    public void Docked_delete_marks_known_ship_gone_and_hidden()
    {
        var w = new ClientWorld();
        See(w, 7);
        Assert.False(w.Ships[7].IsGone);

        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrDocked });

        Assert.True(w.Ships[7].IsGone, "docked ship is marked gone");
        Assert.True(w.Ships[7].IsNotVisible, "docked ship is excluded from render/target");
        Assert.False(w.Ships[7].IsDead, "docking is not death");
        Assert.True(w.Ships.ContainsKey(7), "entity stays in the table (queriable, may return)");
    }

    /// <summary>⭐ SDR_LEFTSECTOR hides a ship that warped to another sector.</summary>
    [Fact]
    public void LeftSector_delete_marks_known_ship_gone_and_hidden()
    {
        var w = new ClientWorld();
        See(w, 9);

        w.Apply(new ShipDelete { ShipId = 9, Sdr = ShipDelete.SdrLeftSector });

        Assert.True(w.Ships[9].IsGone);
        Assert.True(w.Ships[9].IsNotVisible);
        Assert.False(w.Ships[9].IsDead);
    }

    /// <summary>⭐ A delete for a ship we never saw must NOT fabricate an entity.</summary>
    [Fact]
    public void Delete_for_unknown_ship_creates_no_phantom()
    {
        var w = new ClientWorld();
        w.Apply(new ShipDelete { ShipId = 998, Sdr = ShipDelete.SdrDocked });
        w.Apply(new ShipDelete { ShipId = 997, Sdr = ShipDelete.SdrLeftSector });
        w.Apply(new ShipDelete { ShipId = 996, Sdr = ShipDelete.SdrLoggedOff });

        Assert.False(w.Ships.ContainsKey(998));
        Assert.False(w.Ships.ContainsKey(997));
        Assert.False(w.Ships.ContainsKey(996));
    }

    /// <summary>⭐ Death (KILL_SHIP) still latches IsDead, and IsGone follows.</summary>
    [Fact]
    public void KillShip_sets_dead_and_gone()
    {
        var w = new ClientWorld();
        See(w, 7);

        w.Apply(KillShip.FromVictim(7, IgcObjectType.Ship, 5));

        Assert.True(w.Ships[7].IsDead);
        Assert.True(w.Ships[7].IsGone);
        Assert.True(w.Ships[7].IsNotVisible);
    }

    /// <summary>⭐ Reverse-transition guard: a SingleShipUpdate can arrive for a
    /// same-side teammate in ANOTHER sector (P85 teammate position is sent to
    /// the whole side regardless of the subject's sector), so it must NOT clear
    /// IsGone — otherwise a warped-away teammate would be un-hidden. Only
    /// HeavyShipsUpdate (176, recipient's own cluster) / SetCluster / ExportShip
    /// re-establish "back in our view".</summary>
    [Fact]
    public void Gone_ship_stays_gone_on_cross_sector_single_ship_update()
    {
        var w = new ClientWorld();
        See(w, 7);
        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLeftSector });
        Assert.True(w.Ships[7].IsGone);

        // A pose for the (still-elsewhere) teammate arrives — it stays gone.
        w.Apply(U(7));

        Assert.True(w.Ships[7].IsGone, "cross-sector pose does not un-hide a departed teammate");
        Assert.True(w.Ships[7].IsNotVisible);
    }

    /// <summary>⭐ Reverse transition via identity — a cross-side ship re-exported
    /// to us (server resumed visibility) clears the transient gone latch.</summary>
    [Fact]
    public void Gone_ship_returns_and_clears_via_export()
    {
        var w = new ClientWorld();
        See(w, 7);
        w.Apply(new ShipDelete { ShipId = 7, Sdr = ShipDelete.SdrLeftSector });
        Assert.True(w.Ships[7].IsGone);

        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = 0, CharacterName = "visitor" });

        Assert.False(w.Ships[7].IsGone);
        Assert.False(w.Ships[7].IsNotVisible);
    }

    /// <summary>⭐ Belt-and-suspenders: even if a stray identity/pose somehow
    /// arrives for a DEAD ship, IsDead stays latched so it never renders again.</summary>
    [Fact]
    public void Dead_ship_stays_hidden_even_if_identity_reappears()
    {
        var w = new ClientWorld();
        See(w, 7);
        w.Apply(KillShip.FromVictim(7, IgcObjectType.Ship, 5));
        Assert.True(w.Ships[7].IsNotVisible);

        w.Apply(new ExportShip { ShipId = 7, HullId = 3, SideId = 0 });   // hypothetical re-export (never sent for a corpse)

        Assert.False(w.Ships[7].IsGone, "identity clears the transient latch...");
        Assert.True(w.Ships[7].IsDead, "...but death is permanent");
        Assert.True(w.Ships[7].IsNotVisible, "so the ship stays excluded");
    }

    /// <summary>⭐ Wire round-trip: the SDR_DOCKED / SDR_LEFTSECTOR delete
    /// survives encode/decode with ShipId + reason intact.</summary>
    [Theory]
    [InlineData(ShipDelete.SdrDocked)]
    [InlineData(ShipDelete.SdrLeftSector)]
    [InlineData(ShipDelete.SdrKilled)]
    [InlineData(ShipDelete.SdrLoggedOff)]
    public void ShipDelete_encodes_decodes_roundtrip(int sdr)
    {
        var m = new ShipDelete { ShipId = 1234, Sdr = sdr };
        var bytes = m.Encode();
        var back = ShipDelete.Decode(bytes);
        Assert.Equal((short)1234, back.ShipId);
        Assert.Equal(sdr, back.Sdr);
    }
}
