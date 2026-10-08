using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the <c>V</c>-key camera cycle (<see cref="CameraCycler"/>,
/// P295 + P580): the cycle must walk the fixed mode order, offer the
/// external-turret view only while actually gunning (skipping it otherwise), wrap
/// around, and recover when the current mode is no longer available (e.g. the
/// pilot left the turret while in the turret view). P580 extends the order with
/// the seven ported native external modes (after the P295 missile view); the
/// scripted ExternalOverride is deliberately NOT in the cycle.
/// </summary>
public class CameraCyclerTests
{
    [Fact]
    public void NotGunning_SkipsExternalTurret_AndWalksTheRest()
    {
        Assert.Equal(CameraMode.Chase, CameraCycler.Next(CameraMode.Cockpit, isTurretGunner: false));
        Assert.Equal(CameraMode.ExternalMissile, CameraCycler.Next(CameraMode.Chase, isTurretGunner: false));
        // Walk through the P580 native external modes.
        Assert.Equal(CameraMode.ExternalFlyBy, CameraCycler.Next(CameraMode.ExternalMissile, isTurretGunner: false));
        Assert.Equal(CameraMode.ExternalFlyByTarget, CameraCycler.Next(CameraMode.ExternalFlyBy, isTurretGunner: false));
        Assert.Equal(CameraMode.ExternalOrbit, CameraCycler.Next(CameraMode.ExternalFlyByTarget, isTurretGunner: false));
        Assert.Equal(CameraMode.ExternalTarget, CameraCycler.Next(CameraMode.ExternalOrbit, isTurretGunner: false));
        Assert.Equal(CameraMode.ExternalBase, CameraCycler.Next(CameraMode.ExternalTarget, isTurretGunner: false));
        Assert.Equal(CameraMode.ExternalStation, CameraCycler.Next(CameraMode.ExternalBase, isTurretGunner: false));
        // Wraps back to the first available mode (Turret was skipped).
        Assert.Equal(CameraMode.Cockpit, CameraCycler.Next(CameraMode.ExternalStation, isTurretGunner: false));
    }

    [Fact]
    public void Gunning_IncludesExternalTurret_InTheCycle()
    {
        Assert.Equal(CameraMode.Chase, CameraCycler.Next(CameraMode.Cockpit, isTurretGunner: true));
        Assert.Equal(CameraMode.ExternalTurret, CameraCycler.Next(CameraMode.Chase, isTurretGunner: true));
        Assert.Equal(CameraMode.ExternalMissile, CameraCycler.Next(CameraMode.ExternalTurret, isTurretGunner: true));
        Assert.Equal(CameraMode.ExternalFlyBy, CameraCycler.Next(CameraMode.ExternalMissile, isTurretGunner: true));
        Assert.Equal(CameraMode.Cockpit, CameraCycler.Next(CameraMode.ExternalStation, isTurretGunner: true));
    }

    [Fact]
    public void LeavingTurretWhileInTurretView_RestartsAtFirstAvailable()
    {
        // Pilot is stuck in the ExternalTurret view but has just left the turret
        // (isTurretGunner now false): the cycle must not offer Turret again, and
        // must not hang on the stale mode — it restarts at the first available.
        Assert.Equal(CameraMode.Cockpit, CameraCycler.Next(CameraMode.ExternalTurret, isTurretGunner: false));
    }

    [Fact]
    public void CycleCoversEveryMode_ExactlyOnceWhenGunning()
    {
        var expected = new[]
        {
            CameraMode.Chase,
            CameraMode.ExternalTurret,
            CameraMode.ExternalMissile,
            CameraMode.ExternalFlyBy,
            CameraMode.ExternalFlyByTarget,
            CameraMode.ExternalOrbit,
            CameraMode.ExternalTarget,
            CameraMode.ExternalBase,
            CameraMode.ExternalStation,
            CameraMode.Cockpit,
        };
        var seen = new List<CameraMode>();
        var cur = CameraMode.Cockpit;
        for (int i = 0; i < expected.Length; i++)
        {
            cur = CameraCycler.Next(cur, isTurretGunner: true);
            seen.Add(cur);
        }
        Assert.Equal(expected, seen);
    }

    [Fact]
    public void Override_IsNotInTheVCycle()
    {
        // ExternalOverride is a scripted, time-boxed camera native drops the pilot
        // into (WinTrek.cpp:6480-6534, 7673-7678), never a user-toggled cycle view.
        var cur = CameraMode.Cockpit;
        for (int i = 0; i < 20; i++)
        {
            cur = CameraCycler.Next(cur, isTurretGunner: false);
            Assert.NotEqual(CameraMode.ExternalOverride, cur);
        }
    }
}
