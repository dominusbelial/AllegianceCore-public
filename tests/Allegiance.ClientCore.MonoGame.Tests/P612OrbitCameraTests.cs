using System;
using Microsoft.Xna.Framework;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P612 — pure tests for the spectator ORBIT camera (reuses
/// <see cref="InspectorOrbitCamera"/>): locking a subject (a ship/station) and
/// auto-orbit. Verifies the orbit rig frames the locked target — the eye stays
/// one <see cref="InspectorOrbitCamera.Distance"/> from the target and the view
/// points AT it — and that auto-orbit advances smoothly.
/// </summary>
public class P612OrbitCameraTests
{
    [Fact]
    public void Orbit_locked_subject_frames_it_in_view()
    {
        // Centre the rig on a subject (e.g. a ship at (100, 0, -40)) at a sized
        // distance, as Game1.CenterOrbitOnSubject does via FrameRadius.
        var orbit = new InspectorOrbitCamera();
        var subject = new Vector3(100f, 0f, -40f);
        orbit.FrameRadius(30f, MathHelper.ToRadians(50f));   // reset + size distance
        orbit.Focus(subject);                                // then lock the subject

        // Eye is `Distance` from the target ...
        Assert.Equal(orbit.Distance, (orbit.Eye - subject).Length(), precision: 3);
        // ... and the view looks at the subject (projecting it to screen centre).
        var view = orbit.View();
        var eye = CameraPose.EyeFromView(view);
        Assert.True((eye - orbit.Eye).Length() < 1e-2f);
        // forward points from the eye toward the subject
        var fwd = Vector3.Normalize(subject - eye);
        var poseFwd = CameraPose.FromViewProjection(view, Matrix.Identity, eye).Forward;
        poseFwd.Normalize();
        Assert.True(Vector3.Dot(fwd, poseFwd) > 0.999f, $"orbit view should aim at the subject, dot={Vector3.Dot(fwd, poseFwd)}");
    }

    [Fact]
    public void Auto_orbit_keeps_distance_and_revolves()
    {
        var orbit = new InspectorOrbitCamera { AutoOrbit = true, AutoOrbitSpeedRadPerSec = 1f };
        orbit.Focus(new Vector3(5f, 0f, 5f));
        orbit.Distance = 500f;
        var e0 = orbit.Eye;
        float d0 = (orbit.Eye - orbit.Target).Length();
        orbit.Step(1.0);      // 1 rad
        Assert.Equal(1f, orbit.Yaw, precision: 4);
        Assert.Equal(d0, (orbit.Eye - orbit.Target).Length(), precision: 4);   // distance preserved
        Assert.True((orbit.Eye - e0).Length() > 0f, "auto-orbit should move the eye around the target");
    }
}
