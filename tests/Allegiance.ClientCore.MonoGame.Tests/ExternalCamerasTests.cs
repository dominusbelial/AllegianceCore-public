using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P580 — native-placement fidelity tests for the ported external cameras
/// (<see cref="ExternalCameras"/>). Each assertion checks the ported formula
/// against a hand-computed value derived from the native code it cites
/// (WinTrek.cpp per-mode switch :7099-7320; helpers :6848-6878, 7137-7164).
/// </summary>
public class ExternalCamerasTests
{
    [Fact]
    public void FlyByOffset_MatchesNativeMidpointFormula()
    {
        // target (100,0,0), me (0,0,0), backward (0,0,-1):
        //   midpoint = (50,0,0); for both objects delta = (±50,0,0), dBackward=0,
        //   dSide = sqrt(2500 + 0) = 50, dTarget = dMe = 50*3 + 0 = 150.
        //   offset = 1.25 * max(150,150) = 187.5.  (WinTrek.cpp:7137-7163)
        float offset = ExternalCameras.FlyByOffset(
            new Vector3(100f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -1f));
        Assert.Equal(187.5f, offset, 3);
    }

    [Fact]
    public void FlyBy_EyeLooksAtMidpoint_BackedOffAlongBackward()
    {
        // Same scenario: eye = midpoint + backward*187.5 = (50,0,-187.5),
        // looking at midpoint (50,0,0).
        var ev = ExternalCameras.FlyBy(
            new Vector3(100f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -1f), 10f, 0f);
        AssertClose(new Vector3(50f, 0f, -187.5f), ev.Eye, 1e-3f);
        AssertClose(new Vector3(50f, 0f, 0f), ev.LookAt, 1e-3f);
    }

    [Fact]
    public void FlyBy_CoincidentPoints_StaysFinite()
    {
        // Degenerate geometry (target exactly at the ship): the midpoint formula
        // yields offset 0 so the eye sits at the midpoint — finite, never NaN
        // (native asserts the two points differ; the caller's no-target fallback
        // is the chase view, WinTrek.cpp:7165-7170).
        var ev = ExternalCameras.FlyBy(
            new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -1f), 10f, 0f);
        Assert.True(float.IsFinite(ev.Eye.X) && float.IsFinite(ev.Eye.Y) && float.IsFinite(ev.Eye.Z));
        AssertClose(new Vector3(0f, 0f, 0f), ev.Eye, 1e-3f);
    }

    [Fact]
    public void Target_ClampsOffsetToNativeMinimum()
    {
        // offset floor = 5 + 2*r (WinTrek.cpp:7099-7101). r=10 -> 25.
        var ev = ExternalCameras.Target(
            new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -1f), targetRadius: 10f, offset: 0f);
        AssertClose(new Vector3(0f, 0f, -25f), ev.Eye, 1e-3f);
        AssertClose(Vector3.Zero, ev.LookAt, 1e-3f);
    }

    [Fact]
    public void OrbitOffset_UsesNativeFivePlusTwoPointFiveTimesLength()
    {
        // WinTrek.cpp:7196: offset = 5.0 + 2.5 * HullLength.
        Assert.Equal(5f + 2.5f * 40f, ExternalCameras.OrbitOffset(40f), 3);
        Assert.Equal(105f, ExternalCameras.OrbitOffset(40f), 3);
    }

    [Fact]
    public void GenericExternal_SitsBehindObjectAtOffset()
    {
        var ev = ExternalCameras.GenericExternal(
            new Vector3(10f, 20f, 30f), new Vector3(0f, 0f, -1f), offset: 40f, objectRadius: 5f);
        AssertClose(new Vector3(10f, 20f, 30f - 40f), ev.Eye, 1e-3f);
        AssertClose(new Vector3(10f, 20f, 30f), ev.LookAt, 1e-3f);
    }

    [Fact]
    public void Track_PlacesEyeBehindFirstObjectLookingAtSecond()
    {
        // TrackCamera (WinTrek.cpp:6848-6878): p1=(0,0,0), p2=(0,0,10), len=3.
        // axis=(0,0,1); world-up (0,1,0) is orthogonal so it stays; side =
        // cross(axis,up) = (−1,0,0). eye = p1 - axis*2L + side*0.25L = (−0.75,0,−6).
        var ev = ExternalCameras.Track(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 10f), length: 3f);
        AssertClose(new Vector3(-0.75f, 0f, -6f), ev.Eye, 1e-3f);
        AssertClose(new Vector3(0f, 0f, 10f), ev.LookAt, 1e-3f);
    }

    [Fact]
    public void Station_And_Override_ReuseTrackLengths()
    {
        // Station offset = 5 + 2.5*shipRadius (WinTrek.cpp:7213).
        Assert.Equal(5f + 2.5f * 20f, ExternalCameras.StationOffset(20f), 3);
        // Override length = shipRadius*3 (WinTrek.cpp:7203).
        Assert.Equal(60f, ExternalCameras.OverrideLength(20f), 3);
        // Both produce a finite camera.
        var st = ExternalCameras.Station(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 100f), 20f);
        Assert.True(st.Eye.LengthSquared() > 0f);
        var ov = ExternalCameras.Override(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 100f), 20f);
        Assert.True(ov.Eye.LengthSquared() > 0f);
    }

    [Fact]
    public void OrbitRotationRates_AreFiniteAndKeepBackwardUnit()
    {
        // Native pitches/yaws/rolls the free orientation by fixed rates each
        // frame (WinTrek.cpp:7187-7189): result must stay a finite unit backward.
        var o = Orientation.Identity;
        ExternalCameras.RotateOrbitRates(o, 0.016f);
        var b = o.GetBackward();
        Assert.True(float.IsFinite(b.X) && float.IsFinite(b.Y) && float.IsFinite(b.Z));
        Assert.Equal(1f, b.Length(), 3);
    }

    [Fact]
    public void View_IsCameraRelative_NonDegenerate()
    {
        var ev = ExternalCameras.Target(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -1f), 10f, 0f);
        var m = ev.View();
        Assert.True(float.IsFinite(m.M11) && float.IsFinite(m.M33) && float.IsFinite(m.M44));
        // Look direction (target - eye) must be a valid finite vector.
        var lookDir = ev.LookAt - ev.Eye;
        Assert.True(lookDir.LengthSquared() > 0f);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tol)
    {
        Assert.True((expected - actual).LengthSquared() <= tol * tol,
            $"expected {expected} but got {actual}");
    }
}
