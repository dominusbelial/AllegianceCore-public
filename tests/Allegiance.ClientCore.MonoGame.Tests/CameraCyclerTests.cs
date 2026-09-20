using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P295 <c>V</c>-key camera cycle
/// (<see cref="CameraCycler"/>): the cycle must walk the fixed mode order,
/// offer the external-turret view only while actually gunning (skipping it
/// otherwise), wrap around, and recover when the current mode is no longer
/// available (e.g. the pilot left the turret while in the turret view).
/// </summary>
public class CameraCyclerTests
{
    [Fact]
    public void NotGunning_SkipsExternalTurret_AndWalksTheRest()
    {
        Assert.Equal(CameraMode.Chase, CameraCycler.Next(CameraMode.Cockpit, isTurretGunner: false));
        Assert.Equal(CameraMode.ExternalMissile, CameraCycler.Next(CameraMode.Chase, isTurretGunner: false));
        // Wraps back to the first available mode (Turret was skipped).
        Assert.Equal(CameraMode.Cockpit, CameraCycler.Next(CameraMode.ExternalMissile, isTurretGunner: false));
    }

    [Fact]
    public void Gunning_IncludesExternalTurret_InTheCycle()
    {
        Assert.Equal(CameraMode.Chase, CameraCycler.Next(CameraMode.Cockpit, isTurretGunner: true));
        Assert.Equal(CameraMode.ExternalTurret, CameraCycler.Next(CameraMode.Chase, isTurretGunner: true));
        Assert.Equal(CameraMode.ExternalMissile, CameraCycler.Next(CameraMode.ExternalTurret, isTurretGunner: true));
        Assert.Equal(CameraMode.Cockpit, CameraCycler.Next(CameraMode.ExternalMissile, isTurretGunner: true));
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
        // From Cockpit, four presses (gunning) visit all four modes and return.
        var seen = new List<CameraMode>();
        var cur = CameraMode.Cockpit;
        for (int i = 0; i < 4; i++)
        {
            cur = CameraCycler.Next(cur, isTurretGunner: true);
            seen.Add(cur);
        }
        Assert.Equal(
            new[]
            {
                CameraMode.Chase,
                CameraMode.ExternalTurret,
                CameraMode.ExternalMissile,
                CameraMode.Cockpit,
            },
            seen);
    }
}
