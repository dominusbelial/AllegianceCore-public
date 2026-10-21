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
/// <see cref="IIgcSite.HitWarpEvent"/> exactly when the ship's bounding sphere
/// touches the warp's native hit-test shape — <b>P776: a cone</b>, a base disc
/// of radius <c>warp.Radius</c> through the centre plus a 45° cone behind it
/// (<c>Igc/modelIGC.cpp:127-130</c>; <c>_Utility/HitTest.cpp:254-283</c>;
/// <see cref="AlephConeHitTest"/>) — and must stay silent outside it. The actual
/// transit is the host's job (<see cref="ServerHost.HitWarpEvent"/>), exercised
/// separately by the loopback test; here a <see cref="RecordingIgcSite"/>
/// witnesses only the trigger, so the geometry is proven independent of the
/// network host.
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

    private static Vector3 WarpPosition(ResolvedWarp warp) =>
        new(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

    private static Vector3 WarpForward(ResolvedWarp warp) =>
        warp.Warp.Forward is { Length: 3 } f ? new Vector3(f[0], f[1], f[2]) : new Vector3(0f, 0f, -1f);

    /// <summary>The cone's local +z axis (native orientation backward row).</summary>
    private static Vector3 WarpBackward(ResolvedWarp warp) => AlephConeHitTest.Backward(WarpForward(warp));

    /// <summary>A unit vector on the base disc (perpendicular to forward).</summary>
    private static Vector3 WarpSide(ResolvedWarp warp)
    {
        var side = Vector3.CrossProduct(WarpForward(warp), new Vector3(0f, 1f, 0f));
        if (side.LengthSquared() < 1e-6f) side = Vector3.CrossProduct(WarpForward(warp), new Vector3(1f, 0f, 0f));
        return side.Normalize();
    }

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
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // P776 — the trigger is the cone, so sit ON its axis behind the disc (a
        // point in front of the disc is outside native's shape). Comfortably
        // inside, not at the exact edge where float rounding could flip it.
        var pos = warpPos + WarpBackward(warp) * Math.Max(1f, 0.3f * warp.Warp.Radius);

        var ship = NewShip(8101, hull, pos);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) >= 1, "a ship inside a warp's radius must trigger HitWarpEvent");
    }

    /// <summary>⭐ A ship placed on the base disc just INSIDE the rim
    /// (<c>rho = 0.9 · warp.Radius</c>, <c>z = 0</c>) triggers — the trigger
    /// engages near the cone's boundary, not only dead-centre, so a ship that
    /// actually reaches the warp's real body reliably crosses. P776: the old
    /// sphere probe sat at <c>0.9 · (warp.Radius + shipRadius)</c> on an
    /// arbitrary axis, which the cone owns only if it happens to be behind the
    /// disc; the placement moved onto the base disc.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipJustInsideRawRadius_TriggersHitWarpEvent()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // On the base disc, 10% inside the rim: inside the triangle in
        // (rho, z), and 10% of the radius off the boundary so float rounding
        // cannot flip it.
        float rawThreshold = warp.Warp.Radius + shipRadius;
        float rho = 0.9f * warp.Warp.Radius;
        Assert.True(rho < rawThreshold, "precondition: the probe sits inside the old raw sphere");
        var pos = warpPos + WarpSide(warp) * rho;

        var ship = NewShip(8105, hull, pos);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) >= 1,
            $"a ship on the disc at rho = 0.9·R ({rho:F1}) must trigger HitWarpEvent (native cone, P776)");
    }

    /// <summary>⭐ A ship placed just OUTSIDE the cone along the only axis it
    /// extends (its backward axis, past the apex) never triggers, even after
    /// several ticks of dwell. Together with the inside cases this pins the
    /// cone's extent; the P238 <c>WarpVisualScale·radius</c> volume is still
    /// far larger and never triggers.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShipOutsideWarpRadius_NeverTriggersHitWarpEvent()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // Just beyond the apex along the cone's own axis: radius + shipRadius
        // (+ a margin) is beyond the furthest cone point, so this is outside
        // native's shape while still deep inside the P238-scaled ~9.5x volume.
        float gap = warp.Warp.Radius + shipRadius + MathF.Max(10f, 0.05f * warp.Warp.Radius);
        var pos = warpPos + WarpBackward(warp) * gap;

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
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        float rawThreshold = warp.Warp.Radius + shipRadius;                        // P244 (tight, native-faithful) trigger
        float visualThreshold = Sector.WarpVisualScale * warp.Warp.Radius + shipRadius; // the P238-widened volume (visual-matched)
        // Halfway between the two: INSIDE the old P238 volume, OUTSIDE the raw
        // one — and, since it is beyond the apex, outside native's cone too.
        float dist = 0.5f * (rawThreshold + visualThreshold);

        Assert.True(dist > rawThreshold,
            "precondition: the probe must sit strictly beyond the raw trigger");
        Assert.True(dist < visualThreshold,
            "precondition: the probe must sit strictly inside the P238 visual-matched volume");

        var ship = NewShip(8104, hull, warpPos + WarpBackward(warp) * dist);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.True(HitWarpEvents(site) == 0,
            $"a ship at dist {dist:F1} (between raw {rawThreshold:F1} and visual {visualThreshold:F1}) must NOT trigger under P244's decoupled raw trigger");
    }

    /// <summary>⭐ P776 — a ship in FRONT of the disc, inside the old ~110 u
    /// raw sphere, is outside native's cone and must NOT trigger. This is the
    /// exact spot the host's native-faithful exit places a ship (centre
    /// <c>shipRadius+5</c> in front of the disc, <c>fsship.cpp:129-132</c>), and
    /// the reason the batch-16 rig bounced every 2 s. Fails on the unfixed
    /// sphere code; passes on the cone.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ship_in_front_of_the_disc_inside_the_old_sphere_does_not_trigger()
    {
        Require.Artwork();
        var sector = NewStartedSector(out var mission, out var site);
        var warp = EntryWarp(sector);
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        float shipRadius = 0.5f * hull.Length;

        // Half the old sphere radius in front of the disc: well inside the old
        // sphere (so it fired before P776) but with z well below zero, outside
        // the cone.
        float distance = 0.5f * (warp.Warp.Radius + shipRadius);
        var pos = warpPos + WarpForward(warp).Normalize() * distance;

        var ship = NewShip(8106, hull, pos);
        mission.PlaceShip(ship, sector);

        TickN(mission, 5);

        Assert.Equal(0, HitWarpEvents(site));
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
        var warpPos = WarpPosition(warp);
        var hull = Core.Value.HullTypes.Values.OrderBy(h => h.HullId).First();

        // P776: inside the cone (behind the disc), where the trigger would fire.
        var ship = NewShip(8103, hull, warpPos + WarpBackward(warp) * Math.Max(1f, 0.3f * warp.Warp.Radius));
        mission.PlaceShip(ship, sector);

        // Mark it as having just warped this sim-instant (the host does this on
        // transit); now it must NOT re-trigger while inside and still cooling.
        ship.LastWarpTime = mission.LastUpdate;

        TickN(mission, 5);   // covers < 2 s cooldown at 0.045/tick

        Assert.Equal(0, HitWarpEvents(site));
    }
}
