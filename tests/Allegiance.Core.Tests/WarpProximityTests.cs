using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verification of the <b>warp proximity trigger geometry</b> — the first half
/// of the P41 warp-transit slice. <see cref="Sector.ResolveWarpCollisions"/>
/// (the C# analogue of the warp arm of <c>CshipIGC::HandleCollision</c>,
/// <c>shipIGC.cpp:629-636</c>) must raise the site's
/// <see cref="IIgcSite.HitWarpEvent"/> exactly when a ship's position lies
/// within a warp's collision volume — <c>|ship − warp| ≤ warp.Radius +
/// ship.Radius</c> — and must stay silent outside it. The actual transit is the
/// host's job (<see cref="ServerHost.HitWarpEvent"/>), exercised separately by
/// the loopback test; here a <see cref="RecordingIgcSite"/> witnesses only the
/// trigger, so the geometry is proven independent of the network host.
/// </summary>
public class WarpProximityTests
{
    // A core and a map drawn from the real corpus, like the other mission-level
    // tests — but deliberately chosen to be one that actually CONTAINS a warp
    // (some maps have none), so the trigger has a target.
    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> MapWithWarp = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Warps.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no warp-bearing map found in corpus");
    });

    private static Sector NewStartedSector(out Mission mission, out RecordingIgcSite site)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        mission = Mission.Assemble(
            Core.Value, MapWithWarp.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId)], site = new RecordingIgcSite());
        mission.Stage = MissionStage.Started;
        return mission.Sectors.First(s => s.Warps.Any(w => w.Destination is not null && w.Warp.Position is { Length: 3 }));
    }

    // The first resolvable, positioned warp in the sector — the trigger target.
    private static ResolvedWarp EntryWarp(Sector sector) =>
        sector.Warps.First(w => w.Destination is not null && w.Warp.Position is { Length: 3 });

    private static Ship NewShip(short id, DataHullTypeIGC hull, Vector3 position) =>
        new(id, hull, position, Vector3.Zero, Orientation.Identity);

    private static int HitWarpEvents(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("HitWarpEvent"));

    private static void TickN(Mission m, int n)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < n; i++) { t += 0.045; m.Update(t); }   // < 0.25 ⇒ one chunk
    }

    /// <summary>⭐ A ship placed just INSIDE a warp's collision radius triggers
    /// HitWarpEvent — the proximity geometry engages.</summary>
    [Fact]
    public void ShipInsideWarpRadius_TriggersHitWarpEvent()
    {
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // Centre the ship a comfortable distance inside the volume (well under
        // radius+shipR), not at the exact edge where float rounding could flip it.
        var inward = new Vector3(0f, 1f, 0f);   // an arbitrary non-parallel offset
        var pos = warpPos + inward * Math.Max(1f, 0.3f * warp.Warp.Radius);

        var ship = NewShip(8101, hull, pos);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) >= 1, "a ship inside a warp's radius must trigger HitWarpEvent");
    }

    /// <summary>⭐ A ship placed just OUTSIDE the (P238-scaled) warp collision
    /// radius never triggers, even after several ticks of dwell.</summary>
    [Fact]
    public void ShipOutsideWarpRadius_NeverTriggersHitWarpEvent()
    {
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // A clear margin beyond the P238-scaled volume (WarpVisualScale·radius +
        // shipR + 10% of radius), on a diagonal so it is unambiguous that the
        // ship is outside the sphere. NB: the distance must clear the NEW scaled
        // radius — the old unscaled radius is now entirely interior (P238).
        float gap = Sector.WarpVisualScale * warp.Warp.Radius + shipRadius + MathF.Max(10f, 0.1f * warp.Warp.Radius);
        var outward = new Vector3(1f, 1f, 1f).Normalize();
        var pos = warpPos + outward * gap;

        var ship = NewShip(8102, hull, pos);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.Equal(0, HitWarpEvents(site));
    }

    /// <summary>⭐ P238 — the real transit trigger was widened to match the
    /// visual funnel scale (<see cref="Sector.WarpVisualScale"/> = 9.5), so a
    /// ship at a distance lying BETWEEN the old real trigger
    /// (<c>warp.Radius + shipRadius</c>, ~100 u) and the new scaled trigger
    /// (<c>WarpVisualScale·warp.Radius + shipRadius</c>, ~950 u) now raises
    /// HitWarpEvent where it previously would not — the exact scenario Domingo
    /// hit live flying into the enlarged visible funnel and crossing nothing.</summary>
    [Fact]
    public void ShipBetweenOldAndScaledRadius_NowTriggersHitWarpEvent()
    {
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        float oldThreshold = warp.Warp.Radius + shipRadius;                       // pre-P238 real trigger
        float newThreshold = Sector.WarpVisualScale * warp.Warp.Radius + shipRadius; // P238 (visual-matched)
        // Halfway between the two: outside the OLD volume, inside the NEW one.
        float dist = 0.5f * (oldThreshold + newThreshold);

        Assert.True(dist > oldThreshold,
            "precondition: the probe must sit strictly beyond the old trigger");
        Assert.True(dist < newThreshold,
            "precondition: the probe must sit strictly inside the new trigger");

        var ship = NewShip(8104, hull, warpPos + new Vector3(0f, 1f, 0f) * dist);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) >= 1,
            $"a ship at dist {dist:F1} (between old {oldThreshold:F1} and scaled {newThreshold:F1}) must now trigger HitWarpEvent");
    }

    /// <summary>⭐ The debounce window (native <c>m_warpState</c>,
    /// fsship.cpp:1185-1191) suppresses a second transit while a ship lingers
    /// inside the volume shortly after warping. The ship's
    /// <see cref="Ship.LastWarpTime"/> set within the cooldown must silence the
    /// trigger.</summary>
    [Fact]
    public void RecentlyWarpedShip_InsideVolume_IsDebounced()
    {
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();

        var ship = NewShip(8103, hull, warpPos + new Vector3(0f, 1f, 0f) * Math.Max(1f, 0.3f * warp.Warp.Radius));
        mission.PlaceShip(ship, sector);

        // Mark it as having just warped this sim-instant (the host does this on
        // transit); now it must NOT re-trigger while inside and still cooling.
        ship.LastWarpTime = mission.LastUpdate;

        TickN(mission, 5);   // covers < 2 s cooldown at 0.045/tick

        Assert.Equal(0, HitWarpEvents(site));
    }
}
