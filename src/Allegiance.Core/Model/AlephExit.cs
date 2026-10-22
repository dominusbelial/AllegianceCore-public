using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

/// <summary>
/// P777 — the native <b>aleph exit</b>: the exact pose a ship gets when it
/// crosses a warp. A pure, allocation-free port of the body of
/// <c>CFSShip::HitWarp</c> (<c>FedSrv/fsship.cpp:90-132</c>) minus the
/// orchestration (the mass gate, the warp-state handshake, the cluster move and
/// the site events stay in <c>ServerHost.HitWarpEvent</c>, <c>Allegiance.Server</c>).
/// </summary>
/// <remarks>
/// <para><b>Native rule, in order</b> (every line cited):</para>
/// <list type="number">
/// <item><b>Base frame.</b> <c>Orientation alephOrientation = pwarpDest-&gt;GetOrientation()</c>
/// (<c>fsship.cpp:90</c>). The warp's orientation is the single-argument
/// <c>Orientation(dataWarp-&gt;forward)</c> (<c>Igc/warpIGC.cpp:44-46</c>), i.e.
/// <c>Orientation::Set(const Vector&amp; forwardAxis)</c>
/// (<c>zlib/orientation.cpp:314-356</c>; port <see cref="Orientation.Set(Vector3)"/>).
/// Native lets the aleph spin about <c>dataWarp-&gt;rotation</c>
/// (<c>warpIGC.cpp:47</c>, applied in <c>CmodelIGC::Update</c>
/// <c>Igc/modelIGC.cpp:85-100</c>); this port uses the <c>t = 0</c> frame —
/// roll only, and not modelled (out of scope, see the P777 report).</item>
/// <item><b>Entry speed.</b> <c>v = m_pShip-&gt;GetVelocity()</c>,
/// <c>speed2 = v.LengthSquared()</c>, <c>speed = sqrt(speed2)</c>
/// (<c>fsship.cpp:91-93</c>). Wobble happens <b>only</b> when
/// <c>speed2 &gt; 0</c> (<c>fsship.cpp:94</c>): a stationary ship exits with the
/// exact aleph frame and no roll bump.</item>
/// <item><b>Error.</b> <c>fsship.cpp:96-111</c>: <c>dp = pwarp-&gt;GetPosition() −
/// ship position</c> (the <b>source</b> warp — the one that was hit),
/// <c>t = (dp·v)/speed2</c>, <c>d = |dp − t·v|²</c> (the <b>squared</b> distance
/// from the source aleph centre to the ship's line of flight),
/// <c>r = pwarp-&gt;GetRadius()</c>, <c>error = d/(r·r) + 0.125</c> ("Error ranges
/// from 0.125 to 1.125"), then if <c>GetMass() &gt; 300</c>,
/// <c>error *= 300/GetMass()</c> (<c>fsship.cpp:105-108</c>).</item>
/// <item><b>Pitch, then yaw.</b> <c>Pitch(random(−error, error))</c> then
/// <c>Yaw(random(−error, error))</c> (<c>fsship.cpp:113-114</c>) — both
/// pre-multiplied rotations about the frame's own right and up axes, in that
/// order (<c>zlib/orientation.cpp:514-560</c>; ports
/// <see cref="Orientation.Pitch"/>, <see cref="Orientation.Yaw"/>), in radians.</item>
/// <item><b>Roll spin.</b> <c>SetCurrentTurnRate(c_axisRoll,
/// GetCurrentTurnRate(c_axisRoll) + random(π·0.5·error, π·1.5·error))</c>
/// (<c>fsship.cpp:116-118</c>, "Must be less than 2.0 * pi"). Always positive,
/// <b>added</b> to the current roll rate, roll axis only — yaw and pitch rates
/// are untouched. The 2π bound is the wire range of <c>CompactTurnRates</c>
/// (<c>char·2π/127</c>, port <c>Wire/Compact/Quant.cs:43-53</c>). This helper
/// returns the delta; the caller adds it.</item>
/// <item><b>Velocity.</b> <c>backward = alephOrientation.GetBackward()</c> —
/// the <b>perturbed</b> frame (<c>fsship.cpp:120-123</c>) —
/// <c>backward · −(speed + c_fcidExitWarpSpeed)</c>, <c>c_fExitWarpSpeed = 15</c>
/// (<c>fsship.cpp:126-127</c>; <c>Igc/missionigc.cpp:2688</c>).</item>
/// <item><b>Position.</b> <c>destPos + up·random(2,5) + right·random(2,5) −
/// (GetRadius()+5)·backward</c> (<c>fsship.cpp:129-132</c>), all three axes from
/// the perturbed frame.</item>
/// <item><b>Random.</b> <c>random(min,max) = rand()/RAND_MAX·(max·(1−FLT_EPSILON)
/// − min) + min</c> (<c>zlib/zmath.h:114-117</c>). The draw order is pitch,
/// yaw, roll, up offset, right offset — this method draws in exactly that
/// order, so an injected seeded <see cref="Random"/> reproduces a native
/// sequence.</item>
/// </list>
/// <para><b>What is not modelled here:</b> native <c>GetMass()</c> is hull
/// <b>plus parts</b> (<c>Igc/shipIGC.cpp:1402,1468,1496</c>); the caller passes
/// the port's hull-only <see cref="Ship.Mass"/> (<c>Sim/Ship.cs:208</c>), the
/// same gap the P760 mass gate has. Native's <c>char</c> turn-rate wrap is not
/// reproduced; the wire's <c>CompactTurnRates</c> clamps to ±2π instead.</para>
/// </remarks>
internal static class AlephExit
{
    /// <summary>The native exit pose plus the two quantities the caller needs:
    /// the roll turn-rate delta to add (<c>fsship.cpp:116-118</c>) and the
    /// <c>error</c> term (<c>fsship.cpp:111</c>) for logging/telemetry.</summary>
    internal readonly record struct Result(Vector3 Position, Vector3 Velocity, float RollRateDelta, float Error);

    /// <summary>
    /// Apply the native aleph exit. <paramref name="exitOrientation"/> is
    /// mutated in place into the (perturbed) exit frame — native builds the
    /// frame from the destination warp's orientation and then
    /// <c>SetOrientation(alephOrientation)</c> (<c>fsship.cpp:120</c>).
    /// </summary>
    /// <param name="exitOrientation">The ship's orientation object; replaced
    /// with the exit frame (source <c>fsship.cpp:90,113-120</c>).</param>
    /// <param name="sourceWarpPos">The <b>hit</b> warp's centre
    /// (<c>pwarp-&gt;GetPosition()</c>, <c>fsship.cpp:98</c>).</param>
    /// <param name="sourceWarpRadius">That warp's radius
    /// (<c>pwarp-&gt;GetRadius()</c>, <c>fsship.cpp:102</c>).</param>
    /// <param name="destWarpPos">The partner (destination) warp's centre
    /// (<c>pwarpDest-&gt;GetPosition()</c>, <c>fsship.cpp:129</c>).</param>
    /// <param name="destWarpForward">The partner warp's forward
    /// (<c>DataWarpIGC.forward</c> → <c>Orientation o(dataWarp-&gt;forward)</c>,
    /// <c>Igc/warpIGC.cpp:44-46</c>). Zero resets to identity
    /// (<c>orientation.cpp:350</c>).</param>
    /// <param name="shipPos">The ship's position at the collision
    /// (<c>fsship.cpp:98</c>).</param>
    /// <param name="shipVel">The ship's velocity at the collision
    /// (<c>fsship.cpp:91</c>).</param>
    /// <param name="shipMass">Hull mass; drives the &gt;300 scaling
    /// (<c>fsship.cpp:105-108</c>).</param>
    /// <param name="shipRadius">The ship's bounding radius
    /// (<c>0.5·Length</c>, <c>Igc/shipIGC.cpp:1419</c>), used as
    /// <c>GetRadius()+5</c> (<c>fsship.cpp:132</c>).</param>
    /// <param name="exitSpeedBonus"><c>c_fcidExitWarpSpeed</c>
    /// (<c>fsship.cpp:126-127</c>).</param>
    /// <param name="rng">The exit RNG; draws in native order (pitch, yaw, roll,
    /// up, right).</param>
    internal static Result Apply(
        Orientation exitOrientation,
        Vector3 sourceWarpPos, float sourceWarpRadius,
        Vector3 destWarpPos, Vector3 destWarpForward,
        Vector3 shipPos, Vector3 shipVel, float shipMass, float shipRadius,
        float exitSpeedBonus, Random rng)
    {
        // fsship.cpp:90 — the base frame is the destination warp's orientation,
        // built by the single-argument Orientation::Set(forward)
        // (warpIGC.cpp:45, orientation.cpp:314-356).
        exitOrientation.Set(destWarpForward);

        // fsship.cpp:91-94.
        float speed2 = shipVel.LengthSquared();
        float speed = MathF.Sqrt(speed2);
        float error = 0f;
        float rollDelta = 0f;

        if (speed2 > 0f)
        {
            // fsship.cpp:96-111 — how close the ship came to the source aleph's
            // centre, as the squared distance from the centre to its line of
            // flight, normalised by the radius and offset by 0.125.
            Vector3 dp = sourceWarpPos - shipPos;
            float t = (dp * shipVel) / speed2;
            float d = (dp - shipVel * t).LengthSquared();
            float r = sourceWarpRadius;
            error = (d / (r * r)) + 0.125f;

            // fsship.cpp:105-108 — the heavier the ship, the less the aleph
            // should affect its rotational velocity.
            if (shipMass > 300.0f)
                error *= 300.0f / shipMass;

            // fsship.cpp:113-114 — pitch then yaw, each random(-error, error).
            exitOrientation.Pitch(NativeRandom(rng, -error, error));
            exitOrientation.Yaw(NativeRandom(rng, -error, error));

            // fsship.cpp:116-118 — roll-axis spin only, always positive, added
            // to the current rate by the caller.
            rollDelta = NativeRandom(rng, MathF.PI * 0.5f * error, MathF.PI * 1.5f * error);
        }

        // fsship.cpp:120-123 — backward comes from the PERTURBED frame.
        Vector3 backward = exitOrientation.GetBackward();

        // fsship.cpp:126-127 — reverse at entry speed + the exit bonus.
        Vector3 velocity = backward * -(speed + exitSpeedBonus);

        // fsship.cpp:129-132 — up·rand(2,5) + right·rand(2,5) − (r+5)·backward.
        // Draw order stays up then right, after pitch/yaw/roll.
        float upOff = NativeRandom(rng, 2.0f, 5.0f);
        float rightOff = NativeRandom(rng, 2.0f, 5.0f);
        Vector3 position = destWarpPos
            + exitOrientation.GetUp() * upOff
            + exitOrientation.GetRight() * rightOff
            - backward * (shipRadius + 5.0f);

        return new Result(position, velocity, rollDelta, error);
    }

    /// <summary>
    /// <c>random(min,max)</c> (<c>zlib/zmath.h:114-117</c>):
    /// <c>rand()/RAND_MAX·(max·(1−FLT_EPSILON) − min) + min</c>. Ported with
    /// <see cref="Random.NextSingle"/> as <c>rand()/RAND_MAX</c> and
    /// <c>FLT_EPSILON = 1.19209290e-7</c>.
    /// </summary>
    internal static float NativeRandom(Random rng, float min, float max)
        => rng.NextSingle() * (max * (1f - 1.19209290e-7f) - min) + min;
}
