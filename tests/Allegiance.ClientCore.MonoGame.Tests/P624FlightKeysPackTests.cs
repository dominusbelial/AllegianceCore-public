using System.Linq;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P624 — parity wave 1: flight keys pack. Unit tests for the five command
/// families ported with native semantics (match speed, vector lock, vertical
/// strafe, look-around cameras, radar mode cycle) plus the two P625b/P623
/// leftovers (the OemPipe→ThrottleFull native alternate and the live-generated
/// help-overlay header). Each behavioural assertion is pure (no graphics
/// device), matching the file that drove it.
/// </summary>
public class P624FlightKeysPackTests
{
    // ── vertical strafe: TK_ThrustUp/Down → UpButton/DownButton ────────────
    [Fact]
    public void VerticalStrafe_ThrustUp_SetsUpButtonBit()
    {
        var cmd = FlightInput.Translate(new FlightInputState { StrafeUp = true });
        Assert.NotEqual(0, cmd.StateM & ShipControlState.UpButton);
        Assert.Equal(0, cmd.StateM & ShipControlState.DownButton);
    }

    [Fact]
    public void VerticalStrafe_ThrustDown_SetsDownButtonBit()
    {
        var cmd = FlightInput.Translate(new FlightInputState { StrafeDown = true });
        Assert.NotEqual(0, cmd.StateM & ShipControlState.DownButton);
        Assert.Equal(0, cmd.StateM & ShipControlState.UpButton);
    }

    [Fact]
    public void VerticalStrafe_OpposingPresses_MutuallyExclusiveBits()
    {
        var up = FlightInput.Translate(new FlightInputState { StrafeUp = true });
        var down = FlightInput.Translate(new FlightInputState { StrafeDown = true });
        // Up sets the Up bit only, Down the Down bit only — the sim treats
        // them as independent movement buttons (ShipKinematics.cs:157-162).
        Assert.Equal(ShipControlState.UpButton, up.StateM & ShipControlState.MoveButtonsMask);
        Assert.Equal(ShipControlState.DownButton, down.StateM & ShipControlState.MoveButtonsMask);
    }

    // ── vector lock: TK_LockVector → CoastButton (native coastButtonIGC) ───
    [Fact]
    public void VectorLock_WhenLatched_SetsCoastButtonBit()
    {
        var cmd = FlightInput.Translate(new FlightInputState { VectorLock = true });
        Assert.NotEqual(0, cmd.StateM & ShipControlState.CoastButton);
    }

    [Fact]
    public void VectorLock_WhenNotLatched_NoCoastBit()
    {
        var cmd = FlightInput.Translate(new FlightInputState { VectorLock = false });
        Assert.Equal(0, cmd.StateM & ShipControlState.CoastButton);
    }

    // ── match speed: native GetThrottle (WinTrek.cpp:122-134) ──────────────
    [Theory]
    [InlineData(0f, 100f, -1f)]    // stationary target → stop
    [InlineData(50f, 100f, 0f)]    // half max → mid throttle
    [InlineData(100f, 100f, 1f)]   // at max → full
    [InlineData(150f, 100f, 1f)]   // above max → full (clamped)
    [InlineData(30f, 120f, -0.5f)] // 2*30/120 - 1 = -0.5
    public void MatchSpeedThrottle_MatchesTargetSpeed(float targetSpeed, float myMax, float expected)
    {
        Assert.Equal(expected, FlightInput.MatchSpeedThrottle(targetSpeed, myMax), 3);
    }

    [Fact]
    public void MatchSpeedThrottle_MonotonicUpward()
    {
        // Faster target → greater-or-equal throttle, never decreasing.
        float prev = float.MinValue;
        for (float s = 0f; s <= 200f; s += 10f)
        {
            float t = FlightInput.MatchSpeedThrottle(s, 100f);
            Assert.True(t >= prev, $"throttle decreased at target speed {s}");
            prev = t;
        }
    }

    // ── look-around cameras: HeadLook angles → correct directions ──────────
    [Fact]
    public void HeadLook_Right_LooksAlongPositiveX()
    {
        var f = HeadLook.LookForward(Matrix.Identity, HeadLook.Right);
        Assert.True(f.X > 0.99f && System.MathF.Abs(f.Y) < 0.01f, $"expected +X, got {f}");
    }

    [Fact]
    public void HeadLook_Rear_LooksAlongPositiveZ()
    {
        var f = HeadLook.LookForward(Matrix.Identity, HeadLook.Rear);
        Assert.True(f.Z > 0.99f, $"expected +Z, got {f}");
    }

    [Fact]
    public void HeadLook_Left_LooksAlongNegativeX()
    {
        var f = HeadLook.LookForward(Matrix.Identity, HeadLook.Left);
        Assert.True(f.X < -0.99f, $"expected -X, got {f}");
    }

    [Fact]
    public void HeadLook_FrontLeft_LooksDiagonalLeftForward()
    {
        var f = HeadLook.LookForward(Matrix.Identity, HeadLook.FrontLeft);
        Assert.True(f.X < -0.6f && f.Z < -0.6f, $"expected left+forward, got {f}");
    }

    [Fact]
    public void HeadLook_Forward_UnchangedAtZero()
    {
        var f = HeadLook.LookForward(Matrix.Identity, 0f);
        Assert.Equal(Vector3.Forward, f);
    }

    [Theory]
    [InlineData("ViewRear", "Rear")]
    [InlineData("ViewFrontLeft", "FrontLeft")]
    [InlineData("ViewFrontRight", "FrontRight")]
    [InlineData("ViewLeft", "Left")]
    [InlineData("ViewRight", "Right")]
    [InlineData("ViewRearLeft", "RearLeft")]
    [InlineData("ViewRearRight", "RearRight")]
    [InlineData("NotALookAction", "0")]
    public void HeadLook_AngleForAction_MapsEachCommand(string action, string constantName)
    {
        float expected = constantName switch
        {
            "Rear" => HeadLook.Rear, "FrontLeft" => HeadLook.FrontLeft,
            "FrontRight" => HeadLook.FrontRight, "Left" => HeadLook.Left,
            "Right" => HeadLook.Right, "RearLeft" => HeadLook.RearLeft,
            "RearRight" => HeadLook.RearRight, _ => 0f,
        };
        Assert.Equal(expected, HeadLook.AngleForAction(action));
    }

    // ── radar mode cycle: TK_CycleRadar → All→Default→Target→Minimal ───────
    [Fact]
    public void RadarLod_CyclesInNativeOrder()
    {
        Assert.Equal(RadarMode.Default, RadarLod.Next(RadarMode.All));
        Assert.Equal(RadarMode.Target, RadarLod.Next(RadarMode.Default));
        Assert.Equal(RadarMode.Minimal, RadarLod.Next(RadarMode.Target));
        Assert.Equal(RadarMode.All, RadarLod.Next(RadarMode.Minimal));
    }

    [Theory]
    [InlineData(RadarMode.All, true)]
    [InlineData(RadarMode.Default, true)]
    [InlineData(RadarMode.Target, false)]    // friendly ship hidden in Target
    [InlineData(RadarMode.Minimal, false)]   // friendly ship hidden in Minimal
    public void RadarLod_ShowShip_FriendlyHiddenInTargetMinimal(RadarMode mode, bool expected)
    {
        Assert.Equal(expected, RadarLod.ShowShip(mode, ownOrAllied: true, isSelf: false, isSelected: false));
    }

    [Fact]
    public void RadarLod_ShowShip_EnemyOrSelectedAlwaysShown()
    {
        Assert.True(RadarLod.ShowShip(RadarMode.Target, ownOrAllied: false, isSelf: false, isSelected: false));
        Assert.True(RadarLod.ShowShip(RadarMode.Minimal, ownOrAllied: true, isSelf: false, isSelected: true));
        // HUD-4 — native Minimal keeps only self + important, NOT ordinary enemies.
        Assert.False(RadarLod.ShowShip(RadarMode.Minimal, ownOrAllied: false, isSelf: false, isSelected: false));
        Assert.True(RadarLod.ShowShip(RadarMode.Minimal, ownOrAllied: true, isSelf: true, isSelected: false));
    }

    [Fact]
    public void RadarLod_ShowWarp_KeepsSelectedInSparseModes()
    {
        Assert.True(RadarLod.ShowWarp(RadarMode.All, isSelected: false));
        Assert.True(RadarLod.ShowWarp(RadarMode.Target, isSelected: false));
        Assert.True(RadarLod.ShowWarp(RadarMode.Minimal, isSelected: true));
        Assert.False(RadarLod.ShowWarp(RadarMode.Minimal, isSelected: false));
    }

    [Fact]
    public void RadarLod_NewKindsFollowNativeLodPresence()
    {
        // Missiles: outside All only those aimed at the own side survive.
        Assert.True(RadarLod.ShowMissile(RadarMode.All, aimsAtOwnSide: false, isSelected: false));
        Assert.False(RadarLod.ShowMissile(RadarMode.Default, aimsAtOwnSide: false, isSelected: false));
        Assert.True(RadarLod.ShowMissile(RadarMode.Target, aimsAtOwnSide: true, isSelected: false));
        // Probes/mines: Target keeps only enemy within the 1000 m clip.
        Assert.True(RadarLod.ShowProbeOrMine(RadarMode.Default, ownOrAllied: true, separation: 5000f, isSelected: false));
        Assert.False(RadarLod.ShowProbeOrMine(RadarMode.Target, ownOrAllied: true, separation: 50f, isSelected: false));
        Assert.False(RadarLod.ShowProbeOrMine(RadarMode.Target, ownOrAllied: false, separation: 5000f, isSelected: false));
        Assert.True(RadarLod.ShowProbeOrMine(RadarMode.Target, ownOrAllied: false, separation: 50f, isSelected: false));
        // Treasures: not-useful only in All; useful clips in Target.
        Assert.False(RadarLod.ShowTreasure(RadarMode.Default, useful: false, separation: 50f, isSelected: false));
        Assert.True(RadarLod.ShowTreasure(RadarMode.Default, useful: true, separation: 5000f, isSelected: false));
        Assert.False(RadarLod.ShowTreasure(RadarMode.Target, useful: true, separation: 5000f, isSelected: false));
        // Asteroids: generic rocks only within the clip; named special rocks in All/Default.
        Assert.False(RadarLod.ShowAsteroid(RadarMode.Default, namedSpecial: false, rangeZero: false, separation: 5000f, isSelected: false));
        Assert.True(RadarLod.ShowAsteroid(RadarMode.Default, namedSpecial: true, rangeZero: false, separation: 5000f, isSelected: false));
        Assert.False(RadarLod.ShowAsteroid(RadarMode.Target, namedSpecial: true, rangeZero: false, separation: 5000f, isSelected: false));
    }

    // ── help-overlay header: generated from live Help + OpenKeyBindings ─────
    [Fact]
    public void HelpHeader_Line_UsesProvidedBindings()
    {
        Assert.Equal("KEYBINDINGS  (F1 TOGGLE  -  F6 CONTROLS / REBIND)",
            HelpHeader.Line("F1", "F6"));
        // A rebind updates the header (P623 moved Help→F1; this must not drift).
        Assert.Equal("KEYBINDINGS  (H TOGGLE  -  F6 CONTROLS / REBIND)",
            HelpHeader.Line("H", "F6"));
    }

    // ── P625b leftover: OemPipe folded into ThrottleFull as a native alternate ─
    [Fact]
    public void ThrottleFull_Alternate_OemPipeBackslashStillWorks()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        // The catalogue default is OemBackslash (VK 0xDC, native mdl line 43)…
        Assert.Equal(Keys.OemBackslash, map.BindingOf("ThrottleFull").Key);
        // …and the ISO/US-layout OemPipe scancode is a registered alternate.
        Assert.True(map.IsDown(new KeyboardState(Keys.OemPipe), "ThrottleFull"),
            "OemPipe must resolve ThrottleFull via its native alternate");
        Assert.True(map.IsDown(new KeyboardState(Keys.OemBackslash), "ThrottleFull"),
            "OemBackslash must resolve ThrottleFull");
        Assert.False(map.IsDown(new KeyboardState(Keys.OemPlus), "ThrottleFull"),
            "an unrelated key must not resolve ThrottleFull");
    }

    // ── catalogue: every new command registered + native default where native has one ─
    [Theory]
    [InlineData("ThrustUp", Keys.Insert, "TK_ThrustUp")]
    [InlineData("ThrustDown", Keys.X, "TK_ThrustDown")]
    [InlineData("LockVector", Keys.L, "TK_LockVector")]
    [InlineData("MatchSpeed", Keys.OemQuestion, "TK_MatchSpeed")]
    [InlineData("CycleRadar", Keys.OemPeriod, "TK_CycleRadar")]
    [InlineData("ViewFrontLeft", Keys.D1, "TK_ViewFrontLeft")]
    [InlineData("ViewFrontRight", Keys.D2, "TK_ViewFrontRight")]
    [InlineData("ViewLeft", Keys.D3, "TK_ViewLeft")]
    [InlineData("ViewRight", Keys.D4, "TK_ViewRight")]
    [InlineData("ViewRearLeft", Keys.D6, "TK_ViewRearLeft")]
    [InlineData("ViewRear", Keys.D7, "TK_ViewRear")]
    [InlineData("ViewRearRight", Keys.D8, "TK_ViewRearRight")]
    public void Catalogue_RegistersNewCommand_WithKeyAndNativeName(string action, Keys key, string nativeName)
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var def = map.Definitions.First(d => d.Name == action);
        Assert.Equal(key, map.BindingOf(action).Key);
        Assert.Contains(nativeName, def.Description);   // native TK name in the help text
    }

    [Fact]
    public void Catalogue_NativeWins_MovedBindings()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        // LockVector took native L; Ripcord moved to ITS native Ctrl+R (mdl line 68).
        Assert.Equal(Keys.L, map.BindingOf("LockVector").Key);
        Assert.Equal(Keys.R, map.BindingOf("Ripcord").Key);
        Assert.Equal(InputModifiers.Control, map.BindingOf("Ripcord").Modifiers);
        // CycleRadar took native '.'; SelectSectorNext vacated it for D9.
        Assert.Equal(Keys.OemPeriod, map.BindingOf("CycleRadar").Key);
        Assert.Equal(Keys.D9, map.BindingOf("SelectSectorNext").Key);
        // Promote turrets: native Ctrl+1..4 (mdl lines 73-76).
        Assert.Equal(InputModifiers.Control, map.BindingOf("PromoteTurret1").Modifiers);
        // Help stayed F1 (native mdl line 110); the header reflects it.
        Assert.Equal(Keys.F1, map.BindingOf("Help").Key);
    }

    [Fact]
    public void Catalogue_NewCommandsAppearInHelpOverlay()
    {
        var map = new ActionMap(InputActionCatalog.Definitions);
        var all = map.ToHelpGroups().SelectMany(g => g.Entries).ToList();
        foreach (var desc in new[]
        {
            "Vertical strafe up (held thrust) [TK_ThrustUp]",
            "Vertical strafe down (held thrust) [TK_ThrustDown]",
            "Vector lock (hold heading+speed) [TK_LockVector]",
            "Match speed with target (hold) [TK_MatchSpeed]",
            "Cycle radar modes (All/Default/Target/Minimal) [TK_CycleRadar]",
            "Look rear (hold) [TK_ViewRear]",
        })
            Assert.Contains(all, e => e.Description == desc);
    }
}
