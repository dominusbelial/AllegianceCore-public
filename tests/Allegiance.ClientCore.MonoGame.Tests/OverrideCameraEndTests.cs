using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>Regression: after a death the gloat (override) camera ended in CHASE, so the
/// player stayed in chase view for the rest of the session. Native EndOverrideCamera
/// (WinTrek.cpp:6536-6568) goes to vmCombat → cmCockpit.</summary>
public sealed class OverrideCameraEndTests
{
    [Fact]
    public void Expired_override_camera_returns_to_the_cockpit()
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        Set(game, "_cameraMode", CameraMode.ExternalOverride);
        Set(game, "_overrideUntil", 3d);
        Set(game, "_nowSeconds", 5d);
        typeof(Game1).GetMethod("UpdateOverrideCameraExpiry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, null);
        Assert.Equal(CameraMode.Cockpit, (CameraMode)typeof(Game1).GetField("_cameraMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!);
        Assert.Equal(CameraMode.Cockpit, Game1.OverrideCameraEndMode);
    }

    [Fact]
    public void Unexpired_override_camera_keeps_tracking()
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        Set(game, "_cameraMode", CameraMode.ExternalOverride);
        Set(game, "_overrideUntil", 10d);
        Set(game, "_nowSeconds", 5d);
        typeof(Game1).GetMethod("UpdateOverrideCameraExpiry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, null);
        Assert.Equal(CameraMode.ExternalOverride, (CameraMode)typeof(Game1).GetField("_cameraMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!);
    }

    private static void Set(Game1 game, string name, object value)
        => typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
}
