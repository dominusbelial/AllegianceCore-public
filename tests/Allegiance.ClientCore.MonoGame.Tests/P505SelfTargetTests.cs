using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P505 — the player's OWN ship must never become an autopilot target. Seen live
/// on 2026-10-02 (P502 build): <c>autopilot ON → domingo1 (Ship#8107)</c> — the
/// G-key engaged the autopilot on the player's own ship, a no-op selection that
/// native excludes from target candidates. Two layers are pinned here:
///
/// <list type="bullet">
/// <item><see cref="Game1.IsUsableAutopilotTarget"/> — the pure engage gate the
/// G-key now checks: a Ship candidate whose id equals the local ship id is NOT
/// usable, so the autopilot can never engage on self even if the selection holds
/// it (via the Y-key TK_TargetSelf). Other kinds and other ships are usable.</item>
/// <item><see cref="Game1.GatherSectorCandidates"/> — the T-cycle / nearest-pick
/// candidate pool never offers the local ship in the first place (the `id == me`
/// exclusion), so neither T cycling nor the G implicit-pick can select self.</item>
/// </list>
/// </summary>
public class P505SelfTargetTests
{
    private const short Sector = 952;

    private static ServerShipUpdate PoseFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 1f), time: 1u,
            new SimVec(id, 0f, 0f), Orientation.Identity, SimVec.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    /// <summary>⭐ The live bug: a Ship target carrying the local ship id must not
    /// be usable for autopilot engagement — otherwise G flies the autopilot at the
    /// player's own hull ("autopilot ON → domingo1 (Ship#8107)").</summary>
    [Fact]
    public void IsUsableAutopilotTarget_RejectsOwnShip()
    {
        const short me = 8107;

        // Self ship -> NOT usable (the live bug; native excludes self).
        var self = new Game1.AutopilotTarget(Game1.TargetKind.Ship, me, "domingo1");
        Assert.False(Game1.IsUsableAutopilotTarget(self, me),
            "the player's own ship must never be an autopilot target");

        // Another ship / warp / station / asteroid ARE usable.
        var otherShip = new Game1.AutopilotTarget(Game1.TargetKind.Ship, (short)42, "enemy");
        Assert.True(Game1.IsUsableAutopilotTarget(otherShip, me));
        var warp = new Game1.AutopilotTarget(Game1.TargetKind.Warp, (short)7, "warp7");
        Assert.True(Game1.IsUsableAutopilotTarget(warp, me));
        var station = new Game1.AutopilotTarget(Game1.TargetKind.Station, (short)3, "base");
        Assert.True(Game1.IsUsableAutopilotTarget(station, me));
        var rock = new Game1.AutopilotTarget(Game1.TargetKind.Asteroid, (short)101, "rock");
        Assert.True(Game1.IsUsableAutopilotTarget(rock, me));
    }

    /// <summary>⭐ The T-cycle / nearest-pick candidate pool never offers the local
    /// ship (the `id == me` exclusion in the gather), so neither T cycling nor the
    /// G implicit-pick can select self in the first place.</summary>
    [Fact]
    public void GatherSectorCandidates_ExcludesOwnShip()
    {
        const short me = 730;
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = PoseFor(me) });   // own ship
        SeedShip(w, 731, Sector);   // wingman
        SeedShip(w, 739, Sector);   // bandit

        var objs = Game1.GatherSectorCandidates(
            w.Ships, w.Warps, w.Stations, w.Asteroids, me, Sector);

        var shipIds = objs.Where(o => o.Kind == Game1.TargetKind.Ship).Select(o => o.Id).ToList();
        Assert.DoesNotContain(me, shipIds);          // self never offered
        Assert.Contains((short)731, shipIds);        // wingman still offered
        Assert.Contains((short)739, shipIds);        // bandit still offered
    }

    private static void SeedShip(ClientWorld w, short id, short sector)
    {
        w.Apply(new SingleShipUpdate { OidTarget = id, Ship = PoseFor(id) });
        w.Apply(new ShipStatusMessage { ShipId = id, SectorId = sector });
    }
}
