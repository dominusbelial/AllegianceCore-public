namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// The accumulated (yaw, pitch) deflection of the mouse-driven "virtual
/// joystick", after the current frame's cursor delta has been applied.
/// </summary>
public readonly record struct MouseDeflection(float Yaw, float Pitch);

/// <summary>
/// P506 — a pure view of the virtual-joystick indicator for the HUD: the raw
/// integrated deflection (where the stick currently sits — still in pre-curve
/// units so the dot can wander inside the deadzone visibly), the active
/// deadzone (drawn as a circle), and whether the response curve is linear
/// (labelled LIN vs QUAD). <c>null</c> on the HUD frame while mouse flight is
/// off, so no indicator is drawn.
/// </summary>
public readonly record struct MouseStickView(
    float Yaw, float Pitch, float Deadzone, bool LinearControls);

/// <summary>
/// Pure-logic port of Allegiance's "virtual joystick" mouse-flight — the
/// <c>JoystickImageImpl::MouseMove</c> / <c>SetEnabled</c> pair from the
/// original C++ (<c>src/effect/efimage.cpp:182-209</c> and
/// <c>efimage.cpp:49-72</c>, reference checkout at
/// <c>$ALLEGIANCE_NATIVE_SRC_PATH</c>). Deliberately free of any
/// MonoGame / <c>Microsoft.Xna.Framework</c> type in its public surface, so
/// this translation is unit-testable without a graphics device (same
/// discipline as <see cref="FlightInput"/>).
/// </summary>
/// <remarks>
/// <para><b>It is a relative-delta, integrating joystick, not an absolute
/// cursor map.</b> Each frame the caller hands in the raw pixel offset of the
/// OS cursor from the window centre (the original warps the cursor back to the
/// centre every frame, so every subsequent reading is the delta since last
/// frame). This class multiplies that delta by the sensitivity, then
/// <em>subtracts</em> it from the previously stored deflection — the deflection
/// is <em>integrated</em>, not replaced, exactly as in the original:</para>
/// <code>point.X = m_ppnumber[0] - dx;  point.Y = m_ppnumber[1] - dy;</code>
///
/// <para><b>It does NOT auto-centre.</b> With no further mouse movement the
/// deflection holds exactly where it is; the player must physically move the
/// mouse back the other way to reduce it. Do not add any centring/decay that
/// is not in the original.</para>
///
/// <para><b>Unit-circle clamp.</b> The accumulated point is clamped to the
/// unit circle (<c>if (length &gt; 1) point /= length</c>) — a circular,
/// deadzone-free clamp matching the same yaw²+pitch²+roll² sphere-clamp the
/// server already applies in <c>Ship.ExecuteMove</c>
/// (<c>src/Allegiance.Core/Sim/Ship.cs:2902-2903</c>: <c>l = l &gt; 1f ?
/// 1f/MathF.Sqrt(l) : 1f</c>).</para>
///
/// <para><b>First-delta-after-enable skip.</b> Mirroring <c>m_bJustEnabled</c>,
/// the very first <see cref="Update"/> after <see cref="Enable"/> is discarded,
/// so toggling mouse-flight on does not produce one huge jump from wherever the
/// OS cursor happened to be. This class owns that flag (rather than
/// <see cref="Game1"/>) to keep the MonoGame glue thin and the behaviour
/// unit-testable.</para>
///
/// <para>This drives only yaw and pitch (the original's
/// <c>m_ppnumber[0]/[1]</c> feeding <c>jsValues[c_axisYaw]/[c_axisPitch]</c> in
/// <c>SenseJoystick</c>). Roll and throttle are not part of this and the caller
/// keeps them exactly as it does today.</para>
/// </remarks>
public sealed class MouseFlightController
{
    /// <summary>Default sensitivity — the original's <c>CreateJoystickImage(1.0f /
    /// 512.0f)</c> (<c>src/WinTrek/WinTrek.cpp:2982</c>), ≈0.001953.</summary>
    public const float DefaultSensitivity = 1f / 512f;

    /// <summary>Native's default joystick deadzone (<c>g_fJoystickDeadZoneSmall</c>,
    /// <c>WinTrek/WinTrek.cpp:94</c>) in normalized-axis units — the same unit
    /// space this controller's integrated deflection lives in (clamped to the
    /// unit circle).</summary>
    public const float DeadZone = 0.1f;

    /// <summary>Native's "Smallest" deadzone preset (<c>g_fJoystickDeadZoneSmallest =
    /// 0.04f</c>, <c>WinTrek.cpp:93</c>), one of the three <see cref="DeadzonePresets"/>.</summary>
    public const float DeadZoneSmallest = 0.04f;

    /// <summary>Native's "Large" deadzone preset (<c>g_fJoystickDeadZoneLarge =
    /// 0.3f</c>, <c>WinTrek.cpp:95</c>), one of the three <see cref="DeadzonePresets"/>.</summary>
    public const float DeadZoneLarge = 0.3f;

    /// <summary>Native's three deadzone presets, in ascending order — Smallest /
    /// Small(default) / Large, exactly the <c>SetDeadzone</c> values
    /// (<c>WinTrek.cpp:5151-5175</c>). The flight toggle cycles through these.</summary>
    public static readonly float[] DeadzonePresets = { DeadZoneSmallest, DeadZone, DeadZoneLarge };

    /// <summary>Native's adjustable mouse-sensitivity range bounds — the clamp of
    /// <c>AdjustMouseSens</c> (<c>WinTrek.cpp:5514</c>: <c>std::min(2.0f,
    /// std::max(0.1f, ...))</c>), default 0.6. Applied here as a MULTIPLIER on
    /// the pixel delta (see <see cref="MouseSensitivity"/>).</summary>
    public const float MinMouseSensitivity = 0.1f;
    /// <summary>See <see cref="MinMouseSensitivity"/>.</summary>
    public const float MaxMouseSensitivity = 2.0f;
    /// <summary>The step <c>AdjustMouseSens</c> applies each toggle
    /// (<c>c_fMouseSensDelta</c>).</summary>
    public const float MouseSensitivityStep = 0.01f;

    /// <summary>Native's autopilot manual-input disengage threshold —
    /// <c>c_fAutopilotDisengage = g_fJoystickDeadZone * 2.0f</c>
    /// (<c>WinTrek/WinTrek.cpp:8292</c>). A virtual-stick move only counts as
    /// manual input when the integrated deflection leaves its engage-time
    /// baseline by more than this on an axis — mirroring native's per-axis
    /// <c>jsValues[axis] - trekJoyStick[axis] &gt; c_fAutopilotDisengage</c>
    /// check — so a 1-pixel hand tremor does not dump the autopilot.</summary>
    public const float AutopilotDisengageThreshold = DeadZone * 2f;

    private readonly float _sensitivity;
    private float _yaw;
    private float _pitch;
    private bool _justEnabled;
    private bool _invertPitch;
    // P506 — the output-stage response-curve settings: the integrated deflection
    // passes through native's deadzone+rescale+quadratic curve (MapResponse)
    // before becoming ControlData yaw/pitch. Defaults are native's (deadzone 0.1
    // = Small, quadratic). _mouseSensitivity is native's adjustable MouseSensitivity
    // preference (WinTrek.cpp:2809) folded in as a pixel-delta multiplier; defaults
    // to 1.0 so the port's existing 1/512 feel is unchanged unless the pilot tunes it.
    private float _deadzone = DeadZone;
    private bool _linearControls;                 // false = quadratic (native default)
    private float _mouseSensitivity = 1f;

    /// <param name="sensitivity">Raw pixel delta → axis multiplier, defaulting to
    /// the original's <see cref="DefaultSensitivity"/>. Must be &gt; 0.</param>
    public MouseFlightController(float sensitivity = DefaultSensitivity)
    {
        if (sensitivity <= 0f)
            throw new ArgumentOutOfRangeException(nameof(sensitivity), "sensitivity must be > 0");
        _sensitivity = sensitivity;
        _justEnabled = false; // constructed active; skip only armed by Enable()
    }

    /// <summary>The current integrated deflection, without applying a delta.</summary>
    public MouseDeflection Current => new(_yaw, _pitch);

    /// <summary>The raw pixel → axis multiplier in use (the original's
    /// <c>m_sensitivity</c>).</summary>
    public float Sensitivity => _sensitivity;

    /// <summary>
    /// When true, each incoming <em>pitch delta</em> is negated before being
    /// integrated — the ubiquitous "invert mouse Y" preference. Default
    /// <c>false</c>, preserving native's mouse-pitch convention exactly.
    /// <b>This negates the incoming <c>dy</c> delta only, never the stored
    /// deflection <c>_pitch</c>.</b> That is the load-bearing choice: flipping
    /// the already-integrated deflection on toggle would instantly swing the
    /// virtual stick to the opposite position (a violent attitude jump
    /// mid-flight); flipping only new deltas leaves the current deflection
    /// exactly where it is and lets subsequent mouse motion simply integrate
    /// the other direction — how every shipped invert-Y option behaves. The
    /// unit-circle clamp, the <c>_justEnabled</c> first-delta skip, and
    /// <see cref="DeflectionMovedPast"/>'s abs-based comparison all compose
    /// unchanged with a sign flip (magnitude is preserved). Session-scoped:
    /// there is no settings-persistence system in this client, so this resets
    /// to <c>false</c> on every restart.
    /// </summary>
    public bool InvertPitch { get => _invertPitch; set => _invertPitch = value; }

    /// <summary>
    /// P506 — the output-stage deadzone (in integrated-deflection units, native's
    /// <c>g_fJoystickDeadZone</c>). Any integrated deflection with magnitude inside
    /// ±this is zeroed by <see cref="MapResponse"/> before it becomes yaw/pitch,
    /// so small hand movements around centre do not turn the ship. Defaults to
    /// <see cref="DeadZone"/> (0.1 = the "Small" preset); cycle via
    /// <see cref="DeadzonePresets"/>.
    /// </summary>
    public float Deadzone { get => _deadzone; set => _deadzone = value; }

    /// <summary>
    /// P506 — whether the response curve is linear instead of native's default
    /// quadratic (<c>m_bLinearControls</c>, <c>WinTrek.cpp:10792</c>). When
    /// <c>false</c> (native default) <see cref="MapResponse"/> squares the mapped
    /// value (<c>value *= fabsf(value)</c>) so small deflections steer gently and
    /// large ones bite; when <c>true</c> the deadzone-rescaled value is used
    /// directly.
    /// </summary>
    public bool LinearControls { get => _linearControls; set => _linearControls = value; }

    /// <summary>
    /// P506 — native's adjustable mouse-sensitivity preference
    /// (<c>m_pnumMouseSens</c>, default 0.6, adjustable ±0.01 within [0.1, 2.0],
    /// <c>WinTrek.cpp:2809</c> / <c>AdjustMouseSens</c> :5512-5531), folded into
    /// <see cref="Update"/> as an extra multiplier on the pixel delta on top of
    /// the fixed <see cref="Sensitivity"/> image factor. Clamped to
    /// [<see cref="MinMouseSensitivity"/>, <see cref="MaxMouseSensitivity"/>].
    /// Defaults to 1.0 so the port's existing 1/512 flight feel is unchanged
    /// until the pilot tunes it.
    /// </summary>
    public float MouseSensitivity
    {
        get => _mouseSensitivity;
        set => _mouseSensitivity = Math.Clamp(value, MinMouseSensitivity, MaxMouseSensitivity);
    }

    /// <summary>
    /// P506 — map an integrated deflection through native's joystick response
    /// curve into the <c>ControlData</c> yaw/pitch axes: deadzone zeroing,
    /// rescale to preserve the full ±1 range past the deadzone edge, then the
    /// quadratic curve unless <paramref name="linear"/>. Reuses
    /// <see cref="JoystickFlightController.MapJoystick"/> (the P380 physical-stick
    /// deadzone/rescale — a single shared implementation, not a third copy),
    /// compensating for that function's sign inversion: MapJoystick's denominator
    /// <c>dz − 1</c> is negative, which inverts the sign — correct for the
    /// physical stick whose raw axis convention opposes the sim's, but NOT for the
    /// mouse virtual stick, which native feeds straight to <c>jsValues</c> already
    /// in sim-aligned sign (<c>efimage.cpp:193-194</c>, <c>WinTrek.cpp:10827-10828</c>).
    /// Negating MapJoystick's output recovers the sign-preserving deadzone+rescale
    /// while sharing the exact formula. <b>Note:</b> this is a deliberate
    /// improvement over native, which applies NO deadzone/curve to the mouse at
    /// all — Domingo's playtest (2026-10-02) flagged the ship impossible to hold
    /// steady; this is that fix, documented in <c>docs/reports/P506_...</c>.
    /// </summary>
    public static (float Yaw, float Pitch) MapResponse(MouseDeflection defl, float deadzone, bool linear)
    {
        static float Map(float v, float dz, bool lin)
        {
            // Deadzone + rescale via the P380-shared MapJoystick, sign-compensated.
            float mapped = -JoystickFlightController.MapJoystick(v, dz);
            return lin ? mapped : mapped * MathF.Abs(mapped);   // native value *= fabsf(value)
        }
        return (Map(defl.Yaw, deadzone, linear), Map(defl.Pitch, deadzone, linear));
    }

    /// <summary>True if the next <see cref="Update"/>'s delta will be discarded
    /// (armed by <see cref="Enable"/>).</summary>
    public bool JustEnabled => _justEnabled;

    /// <summary>
    /// Arm the "skip the next delta" flag — mirrors <c>SetEnabled(true)</c>
    /// setting <c>m_bJustEnabled = true</c>. Call on toggle-on so the first
    /// update after enabling does not jump. Does not alter the stored deflection.
    /// </summary>
    public void Enable() => _justEnabled = true;

    /// <summary>
    /// Zero the stored deflection — mirrors <c>SetEnabled(false)</c> zeroing
    /// <c>m_ppnumber[0..2]</c>. Call on toggle-off. Does not arm the skip flag
    /// (a subsequent <see cref="Enable"/> does that).
    /// </summary>
    public void Reset()
    {
        _yaw = 0f;
        _pitch = 0f;
    }

    /// <summary>
    /// Apply one frame's raw cursor delta (in pixels, relative to the window
    /// centre) and return the updated integrated deflection.
    /// </summary>
    /// <remarks>
    /// Reproduces the original arithmetic exactly: native subtracts both
    /// <c>dx</c> (yaw) and <c>dy</c> (pitch) from the stored value
    /// (<c>efimage.cpp</c> <c>MouseMove</c>: <c>m_ppnumber[0] - dx</c>,
    /// <c>m_ppnumber[1] - dy</c>), and so does this port. This is consistent
    /// with the sim/keyboard convention now that <see cref="FlightInput"/>
    /// matches native (<c>TK_YawRight</c> → <c>-1</c>): mouse-right yields a
    /// negative <c>dx</c>, hence a negative yaw deflection, which the sim turns
    /// into a rightward turn — exactly like the right arrow key. (An earlier
    /// revision "corrected" dx to addition because the port's keyboard yaw was
    /// itself sign-inverted against native at that time; P310 reverted both to
    /// native, so subtraction is now correct on both axes.) A zero delta leaves
    /// the deflection unchanged (no auto-centring).
    /// </remarks>
    public MouseDeflection Update(float dxPixels, float dyPixels)
    {
        if (_justEnabled)
        {
            _justEnabled = false;
            return Current;
        }

        float dx = dxPixels * _sensitivity * _mouseSensitivity;
        float dy = dyPixels * _sensitivity * _mouseSensitivity;
        if (_invertPitch) dy = -dy;   // P342 — invert-mouse-Y toggle; flips the incoming delta, not _pitch

        float yaw = _yaw - dx;
        float pitch = _pitch - dy;

        float length = MathF.Sqrt(yaw * yaw + pitch * pitch);
        if (length > 1f)
        {
            yaw /= length;
            pitch /= length;
        }

        _yaw = yaw;
        _pitch = pitch;
        return Current;
    }

    /// <summary>
    /// Whether the current integrated deflection has left the given
    /// engage-time <paramref name="baseline"/> by more than
    /// <paramref name="threshold"/> on <em>either</em> axis — the exact shape
    /// of native's autopilot manual-input check (<c>WinTrek.cpp:8292-8299</c>),
    /// which compares the current stick value against the value captured when
    /// the autopilot engaged (<c>trekJoyStick</c>, <c>WinTrek.cpp:8283-8289</c>)
    /// and disengages when any axis has moved past <c>2 × deadzone</c>. Used by
    /// <c>Game1</c>'s manual-flight-input detector so a mere 1-pixel cursor
    /// twitch (which perturbs the integrated deflection by ~1/512 ≈ 0.002, far
    /// below the threshold) does not dump an engaged autopilot, while a real
    /// stick move does.
    /// </summary>
    /// <param name="baseline">The deflection captured when the autopilot engaged.</param>
    /// <param name="threshold">Disengage threshold in deflection units
    /// (normally <see cref="AutopilotDisengageThreshold"/>).</param>
    public bool DeflectionMovedPast(MouseDeflection baseline, float threshold)
    {
        return MathF.Abs(_yaw - baseline.Yaw) > threshold
            || MathF.Abs(_pitch - baseline.Pitch) > threshold;
    }
}
