using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P114 <c>RELAUNCH_SHIP</c> foundation: the new hull capability bits, the
/// landing-bay / launch-slot geometry reconstruction from real <c>.cvh</c>
/// model frames, and the simplified <c>InGarage</c> predicate — all driven by
/// <b>real corpus data</b> (a decoded <c>OT_hullType</c> record for the carrier
/// and a real carrier <c>.cvh</c>), never invented numbers.
/// </summary>
/// <remarks>
/// Finding (P114): landing-bay geometry is <b>not</b> in the <c>OT_hullType</c>
/// wire/static record (native <c>DataHullTypeIGC</c>, <c>src/Igc/igc.h:1767-1820</c>,
/// has no slot members) — it lives in the per-model <c>.cvh</c> collision
/// frames (<c>garage1A</c>…, <c>launch1</c>…), which native
/// <c>HullTypeIGC::Initialize</c> reads via <c>HitTest::Load</c>
/// (<c>hullTypeIGC.cpp:56,158-212</c>) and which this port already parses
/// (<see cref="CvhReader"/>). The reconstructed bay below is real data; the
/// inside/outside expectations are hand-computed from that same geometry.
/// </remarks>
public class LandingBayTests
{
    private static DataHullTypeIGC FindHull(string modelName) =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == modelName).V;

    private static CvhFile LoadCvh(string name)
    {
        var path = CvhCorpus.Files.FirstOrDefault(f => Path.GetFileName(f) == name)
            ?? throw new InvalidOperationException($"corpus lacks {name}");
        return CvhReader.Parse(File.ReadAllText(path));
    }

    // ── capability bits (scope item 1) ─────────────────────────────────────

    [Theory]
    [InlineData(DataHullTypeIGC.HabmRescue, 0x02)]
    [InlineData(DataHullTypeIGC.HabmLandOnCarrier, 0x10)]
    [InlineData(DataHullTypeIGC.HabmCarrier, 0x400)]
    public void New_capability_bits_match_igc_h(short bit, int expected) =>
        Assert.Equal(expected, bit);

    [Fact]
    public void Faohshcar_carrier_hull_carries_the_carrier_bit()
    {
        // faohshcar habmCapabilities = 0x1D02 from the corpus; bit 0x400 (Carrier,
        // igc.h:736) is set, LandOnCarrier (0x10) is not (a carrier does not land
        // on another), Rescue (0x02) is set — decoded via HasCapability.
        var hull = FindHull("faohshcar");
        Assert.Equal(0x1D02, hull.HabmCapabilities);
        Assert.True(hull.HasCapability(DataHullTypeIGC.HabmCarrier));
        Assert.True(hull.HasCapability(DataHullTypeIGC.HabmRescue));
        Assert.False(hull.HasCapability(DataHullTypeIGC.HabmLandOnCarrier));
    }

    // ── slot-geometry reconstruction (real corpus data) ────────────────────

    [Fact]
    public void Faohshcar_bay_reconstructs_from_real_cvh_frames()
    {
        var hull = FindHull("faohshcar");                 // length 300 (real record)
        var cvh = LoadCvh("faohshcar.cvh");               // radius 16.29676056 (real header)
        var bay = HullLandingBay.Reconstruct(cvh, hull.Length);

        // scale = 0.5 * 300 / 16.29676056 = 9.204283 (hullTypeIGC.cpp:62)
        Assert.Equal(9.204283f, bay.Scale, 3);

        // faohshcar has a single landing slot of 5 planes (garage1A..E); no
        // launch1 (its frame is named "launch", so native finds no launch slot).
        Assert.Single(bay.LandSlots);
        Assert.Equal(5, bay.LandSlots[0].Planes.Count);
        Assert.Empty(bay.LaunchSlots);

        // First plane (garage1A): raw pos (0,-2.58092856,3.14835572), raw fwd
        // (0,0.0002605,-0.99999994); native load transform (HitTest.cpp:792-796)
        // → pos (0,-2.5809,3.1484)*scale, fwd (0,-0.00026,1).
        var a = bay.LandSlots[0].Planes[0];
        Assert.Equal(0f, a.Position.X, 3);
        Assert.Equal(-23.7556f, a.Position.Y, 3);
        Assert.Equal(28.9784f, a.Position.Z, 3);
        Assert.Equal(0f, a.Direction.X, 3);
        Assert.Equal(0f, a.Direction.Y, 3);
        Assert.Equal(1f, a.Direction.Z, 3);
    }

    // ── InGarage (simplified predicate) ────────────────────────────────────

    [Fact]
    public void InGarage_is_true_inside_the_bay_and_false_outside()
    {
        // Real carrier at the origin, identity orientation, its real .cvh attached.
        var carrier = new Ship(1, FindHull("faohshcar"), Vector3.Zero, Vector3.Zero, Orientation.Identity)
        {
            Hull = LoadCvh("faohshcar.cvh"),
        };

        // Real, tiny lander (length 2 → sphere radius 1), so it fits the bay.
        var landerHull = FindHull("dreghpod");

        // Hand-derived bay half-spaces (carrier-local, identity orientation;
        // plane positions scaled by 9.204283, dirs after HitTest.cpp:792-796):
        //   A z=28.9784 dir +Z   → R.z  >= 28.9784 + r
        //   B y=-13.1614 dir -Y  → R.y  <= -13.1614 - r
        //   C y=-32.6489 dir +Y  → R.y  >= r - 32.6489
        //   D x=-15.6404 dir +X  → R.x  >= r - 15.6404
        //   E x=+15.7580 dir -X  → R.x  <= 15.7580 - r
        // For r=1 that box is x∈[-14.64,14.76], y∈[-31.65,-14.16], z∈[29.98,∞).
        var inside = new Vector3(0f, -23f, 31f);                 // deep inside the box
        var outLow = new Vector3(0f, -23f, 5f);                  // below the deck plane A
        var outY = new Vector3(0f, 0f, 31f);                     // y=0 outside the y-band
        var outX = new Vector3(100f, -23f, 31f);                 // far outside in x

        Assert.True(InGarage(carrier, landerHull, inside));
        Assert.False(InGarage(carrier, landerHull, outLow));
        Assert.False(InGarage(carrier, landerHull, outY));
        Assert.False(InGarage(carrier, landerHull, outX));
    }

    private static bool InGarage(Ship carrier, DataHullTypeIGC landerHull, Vector3 pos)
        => carrier.InGarage(new Ship(2, landerHull, pos, Vector3.Zero, Orientation.Identity), 0f);
}
