using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipInsideWarpRadius_TriggersHitWarpEvent()
    {
        Require.Artwork();
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

    /// <summary>⭐ A ship placed just INSIDE the tight raw collision radius
    /// (<c>0.9 · (warp.Radius + shipRadius)</c>) triggers — the trigger engages
    /// near the boundary, not only dead-centre, so a ship that actually reaches
    /// the warp's real body reliably crosses (the anti-regression half of P244:
    /// transit must still fire). Together with
    /// <see cref="ShipOutsideWarpRadius_NeverTriggersHitWarpEvent"/> this
    /// brackets the P244 trigger onto <c>warp.Radius + shipRadius</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipJustInsideRawRadius_TriggersHitWarpEvent()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // Comfortably inside the raw threshold (10% margin off the boundary —
        // enough that float rounding can't flip it), on an off-axis offset so
        // it is unambiguously a 3-D sphere test.
        float rawThreshold = warp.Warp.Radius + shipRadius;
        var inward = new Vector3(1f, 1f, 0f).Normalize();
        var pos = warpPos + inward * (0.9f * rawThreshold);

        var ship = NewShip(8105, hull, pos);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) >= 1,
            $"a ship at 0.9·({rawThreshold:F1}) = {0.9f * rawThreshold:F1} from the warp centre must trigger HitWarpEvent");
    }

    /// <summary>⭐ A ship placed just OUTSIDE the real raw warp collision radius
    /// (<c>warp.Radius + shipRadius</c> — P244's tight native-faithful trigger)
    /// never triggers, even after several ticks of dwell. This pins the UPPER
    /// bound of the trigger: it is the raw collision sphere, not the P238-scaled
    /// <c>WarpVisualScale·radius</c> one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipOutsideWarpRadius_NeverTriggersHitWarpEvent()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // A slim margin just beyond the RAW collision radius (warp.Radius +
        // shipRadius + 5% of radius) on a diagonal, so it is unambiguous that
        // the ship is outside the tight P244 trigger sphere yet would be deep
        // inside the P238-scaled ~9.5x volume.
        float gap = warp.Warp.Radius + shipRadius + MathF.Max(10f, 0.05f * warp.Warp.Radius);
        var outward = new Vector3(1f, 1f, 1f).Normalize();
        var pos = warpPos + outward * gap;

        var ship = NewShip(8102, hull, pos);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.Equal(0, HitWarpEvents(site));
    }

    /// <summary>⭐ P244 — the trigger was decoupled from the visual funnel scale,
    /// so a ship sitting BETWEEN the raw real trigger (<c>warp.Radius +
    /// shipRadius</c>, ~100 u) and the P238-widened one (<c>WarpVisualScale·
    /// warp.Radius + shipRadius</c>, ~950 u) must <b>no longer</b> raise
    /// HitWarpEvent. Under P238 this distance lay inside the widened sphere — the
    /// exact "the aleph disappears while you're still approaching its rim" bug
    /// this stage fixes — and it now sits outside the re-tightened raw trigger.
    /// Proving the negative here (with the positive in
    /// <see cref="ShipInsideWarpRadius_TriggersHitWarpEvent"/> and the live test)
    /// brackets the trigger back onto the warp's real raw radius.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipBetweenRawAndVisualRadius_DoesNotTriggerHitWarpEvent()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        float rawThreshold = warp.Warp.Radius + shipRadius;                        // P244 (tight, native-faithful) trigger
        float visualThreshold = Sector.WarpVisualScale * warp.Warp.Radius + shipRadius; // the P238-widened volume (visual-matched)
        // Halfway between the two: INSIDE the old P238 volume, OUTSIDE the raw one.
        float dist = 0.5f * (rawThreshold + visualThreshold);

        Assert.True(dist > rawThreshold,
            "precondition: the probe must sit strictly beyond the raw trigger");
        Assert.True(dist < visualThreshold,
            "precondition: the probe must sit strictly inside the P238 visual-matched volume");

        var ship = NewShip(8104, hull, warpPos + new Vector3(0f, 1f, 0f) * dist);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) == 0,
            $"a ship at dist {dist:F1} (between raw {rawThreshold:F1} and visual {visualThreshold:F1}) must NOT trigger under P244's decoupled raw trigger");
    }

    /// <summary>⭐ The debounce window (native <c>m_warpState</c>,
    /// fsship.cpp:1185-1191) suppresses a second transit while a ship lingers
    /// inside the volume shortly after warping. The ship's
    /// <see cref="Ship.LastWarpTime"/> set within the cooldown must silence the
    /// trigger.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RecentlyWarpedShip_InsideVolume_IsDebounced()
    {
        Require.Artwork();
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
