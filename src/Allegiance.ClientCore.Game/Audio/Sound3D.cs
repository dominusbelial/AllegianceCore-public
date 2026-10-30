using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P893 — the native positional-3D sound model, as pure testable math
/// (no MonoGame audio types, no device): the same discipline as the
/// per-family distance curves this module replaces when 3D is on.
/// </summary>
/// <remarks>
/// <para><b>The native model</b> (all cites re-grepped for P893): every world
/// sound native plays is a DirectSound3D buffer behind a "virtual buffer"
/// (<c>soundengine/ds3dvirtualbuffer.cpp:692-830</c>) that, each frame,
/// recomputes the listener→source vector and applies</para>
/// <list type="bullet">
/// <item><b>Distance rolloff</b> —
/// <c>gainDb = min(0, rolloffFactor · −10 · log10(d² / minDistance²))</c>
/// (<c>ds3dvirtualbuffer.cpp:752-762</c>), i.e. −20 dB per decade of
/// d/minDistance, clamped at 0 dB inside the minimum distance. The engine's
/// rolloff factor is the 1.0 default set in the engine constructor
/// (<c>ds3dengine.cpp:236</c>); nothing ever calls
/// <c>SetRolloffFactor</c>.</item>
/// <item><b>Audibility / priority</b> — a sound whose total gain drops to
/// <see cref="Sound3DModel.MinAudibleDb"/> (−60 dB, <c>ds3dbuffer.h:12</c>)
/// becomes inaudible and is kicked to the lowest dynamic priority
/// (<c>ds3dvirtualbuffer.cpp:750-753, 804-806</c>); the DirectSound max
/// distance is <c>minDistance · 4000</c> (<c>ds3dbuffer.h:15</c>,
/// <c>ds3dbuffer.cpp:297</c>).</item>
/// <item><b>Cones</b> — a source can declare an inner/outer angle pair with an
/// outside gain (dB): full inside the inner angle, the outside gain at/beyond
/// the outer angle, linear in dB between them
/// (<c>ds3dvirtualbuffer.cpp:765-793</c>, the <c>ISoundTweakable3D::SetCone</c>
/// plumbing at <c>soundbase.h:56</c>). <b>No artwork sound uses one</b>: the
/// factories exist (<c>treksound.cpp:93-120</c>, sounddef's
/// <c>ConeSoundTemplateFactory</c>) but zero <c>ConeSound(...)</c> declarations
/// appear in the native artwork tree — so the port implements the math (tested)
/// and applies no cones, exactly like native.</item>
/// <item><b>Doppler</b> — the engine's Doppler factor is the 1.0 default set in
/// the engine constructor (<c>ds3dengine.cpp:237</c>); DirectSound applies it
/// to the projected relative velocity of source and listener with its default
/// 343 m/s speed of sound. The port mirrors that with the classic 1-D radial
/// formula in <see cref="Sound3DModel.DopplerPitchFactor"/>.</item>
/// <item><b>Head-relative</b> — a source whose position equals the listener's
/// skips 3D processing and plays 2D (<c>ds3dvirtualbuffer.cpp:723-726</c>);
/// cockpit/own-ship beds are those sounds in the port.</item>
/// <item><b>Listener</b> — native's listener is the CAMERA: position and
/// orientation from <c>CameraListener</c> (<c>WinTrek.cpp:646-708</c>), while
/// its velocity is the own ship's velocity (native's own REVIEW note
/// acknowledges the mismatch, <c>WinTrek.cpp:686-691</c>). Every view mode uses
/// the same camera listener, so cockpit vs chase differ only in where the
/// camera sits.</item>
/// </list>
/// <para><b>Where the minimum distances come from</b>: the native artwork's
/// <c>ThreeDSound(minDistance, …)</c> templates in
/// <c>~/Development/Artwork/sounddef.mdl</c> (per-role cites in
/// <see cref="Native3DMix"/>). The port's hand-tuned per-loop distance curves
/// (linear falloffs over fixed radii) are replaced by the native model above
/// while the 3D option is on; the curves remain the option-off path so the
/// behaviour equals pre-P893 exactly.</para>
/// </remarks>
public static class Sound3DModel
{
    /// <summary>Native engine rolloff factor — the 1.0 default set in the
    /// DS3D engine constructor (ds3dengine.cpp:236; no caller ever overrides).</summary>
    public const float RolloffFactor = 1.0f;

    /// <summary>Native engine Doppler factor — the 1.0 default set in the
    /// DS3D engine constructor (ds3dengine.cpp:237; no caller ever overrides).</summary>
    public const float DopplerFactor = 1.0f;

    /// <summary>minDistance → maxDistance ratio (ds3dbuffer.h:15,
    /// applied at ds3dbuffer.cpp:297).</summary>
    public const float MinToMaxDistanceRatio = 4000.0f;

    /// <summary>Total gain below which a virtual sound is inaudible, in dB
    /// (ds3dbuffer.h:12, applied at ds3dvirtualbuffer.cpp:804).</summary>
    public const float MinAudibleDb = -60.0f;

    /// <summary>Speed of sound for the Doppler model, m/s (DirectSound3D's
    /// default — the value native's factor-1.0 Doppler was computed with).</summary>
    public const float SpeedOfSound = 343.0f;

    /// <summary>Native max distance for a minimum distance
    /// (ds3dbuffer.cpp:297: <c>flMaxDistance = m_fMinimumDistance · 4000</c>).</summary>
    public static float MaxDistance(float minDistance) => minDistance * MinToMaxDistanceRatio;

    /// <summary>Distance at which the native rolloff reaches
    /// <see cref="MinAudibleDb"/> (−20·log10(d/min) = −60 ⇒ d = 1000·min):
    /// native's audibility horizon for a sound. The port uses it as the
    /// gather/loop-admission radius while 3D is on.</summary>
    public static float AudibleRange(float minDistance) => minDistance * 1000f;

    /// <summary>Native distance gain in dB (ds3dvirtualbuffer.cpp:752-762):
    /// <c>min(0, rolloffFactor · −10 · log10(d²/min²))</c>. Full (0 dB) at or
    /// inside the minimum distance, −20 dB per decade beyond.</summary>
    public static float DistanceGainDb(float minDistance, float distance)
    {
        float min = MathF.Max(minDistance, 1e-4f);
        float d = MathF.Max(distance, 1e-6f);
        float db = RolloffFactor * -10f * MathF.Log10(d * d / (min * min));
        return MathF.Min(0f, db);
    }

    /// <summary>dB → linear amplitude (<c>10^(dB/20)</c>), clamped to [0,1]
    /// — the mapping from the native gain domain (dB, −100..0) to MonoGame's
    /// linear instance Volume.</summary>
    public static float DbToLinear(float db) =>
        Math.Clamp(MathF.Pow(10f, db / 20f), 0f, 1f);

    /// <summary>The angle between a cone's facing and the direction to the
    /// listener, in degrees (native computes it with the acos dot-product
    /// identity, ds3dvirtualbuffer.cpp:780-784).</summary>
    public static float ConeAngleDegrees(Vector3 emitterOrientation, Vector3 directionToListener)
    {
        float o = emitterOrientation.Length();
        float d = directionToListener.Length();
        if (o < 1e-6f || d < 1e-6f) return 0f;
        float cos = Vector3.Dot(emitterOrientation, directionToListener) / (o * d);
        return MathF.Acos(Math.Clamp(cos, -1f, 1f)) * (180f / MathF.PI);
    }

    /// <summary>Native cone attenuation in dB (ds3dvirtualbuffer.cpp:786-792):
    /// 0 inside the inner angle, <paramref name="outsideGainDb"/> at/beyond the
    /// outer angle, a linear dB fade between the two. (Native also skips cones
    /// for listener-relative sources — the TODO-HACK at :775-777.)</summary>
    public static float ConeGainDb(float innerAngleDeg, float outerAngleDeg,
        float outsideGainDb, float angleDeg)
    {
        if (outsideGainDb == 0f || innerAngleDeg >= 360f) return 0f;
        if (angleDeg <= innerAngleDeg) return 0f;
        if (angleDeg >= outerAngleDeg) return outsideGainDb;
        return outsideGainDb * (angleDeg - innerAngleDeg) / (outerAngleDeg - innerAngleDeg);
    }

    /// <summary>Doppler pitch factor (a frequency ratio, 1 = unshifted) for a
    /// source at <paramref name="emitterPos">emitter position</paramref> moving
    /// at <paramref name="emitterVel"/>, heard by a listener moving at
    /// <paramref name="listenerVel"/>: the classic 1-D radial model with the
    /// native engine's Doppler factor (1.0, ds3dengine.cpp:237) and DirectSound's
    /// 343 m/s speed of sound. Approaching sources (&lt;0 radial velocity) shift
    /// up, receding ones shift down; the ratio is clamped to a sane [¼, 4] band
    /// so a pass-by never explodes the pitch.</summary>
    public static float DopplerPitchFactor(Vector3 listenerVel, Vector3 emitterVel, Vector3 toEmitterDir)
    {
        float dirLen = toEmitterDir.Length();
        if (dirLen < 1e-6f) return 1f;
        // Positive radial velocity = the distance is growing (receding).
        float radial = Vector3.Dot(emitterVel - listenerVel, toEmitterDir / dirLen);
        float factor = SpeedOfSound / (SpeedOfSound + DopplerFactor * radial);
        return Math.Clamp(factor, 0.25f, 4f);
    }

    /// <summary>Doppler shift expressed in MonoGame pitch octaves (MonoGame's
    /// <c>SoundEffectInstance.Pitch</c> is octave-based: −1 = half rate), to add
    /// onto a voice's base pitch.</summary>
    public static float DopplerPitchOctaves(float pitchFactor) => MathF.Log2(pitchFactor);

    /// <summary>Stereo pan (−1 left … +1 right) for a source offset
    /// <paramref name="relativePos"/> heard on a listener whose world-space
    /// right axis is <paramref name="listenerRight"/>: the projection of the
    /// direction onto the right axis. Sources dead ahead/behind pan to centre —
    /// DirectSound's stereo down-mix of a 3D source behaves the same way (no
    /// rear-channel attenuation).</summary>
    public static float Pan(Vector3 relativePos, Vector3 listenerRight)
    {
        float d = relativePos.Length();
        if (d < 1e-6f || listenerRight.Length() < 1e-6f) return 0f;
        return Math.Clamp(Vector3.Dot(relativePos, listenerRight) / d, -1f, 1f);
    }
}

/// <summary>The world-sound roles the port plays, with the native
/// <c>ThreeDSound(minDistance)</c> value each carries. Cites are the native
/// artwork's sounddef.mdl lines (re-grepped for P893).</summary>
public enum Native3DRole
{
    /// <summary>Projectile in-flight ambient (<c>er_beam</c>, sounddef.mdl:14).</summary>
    ProjectileAmbient,
    /// <summary>Missile in-flight ambient (HornetMissileFlight, sounddef.mdl:1303).</summary>
    MissileFlight,
    /// <summary>Missile launch one-shot (HornetMissileLaunch, sounddef.mdl:1302).</summary>
    MissileLaunch,
    /// <summary>largeExplosionSound (sounddef.mdl:268).</summary>
    ExplosionLarge,
    /// <summary>mediumExplosionSound (sounddef.mdl:269).</summary>
    ExplosionMedium,
    /// <summary>smallExplosionSound (sounddef.mdl:270) — also otherHullHitSound (:264).</summary>
    ExplosionSmall,
    /// <summary>myShieldHitSound / myHullHitSound pools (sounddef.mdl:250-259).</summary>
    ShipHit,
    /// <summary>rockHitSound (sounddef.mdl:266).</summary>
    RockHit,
    /// <summary>collisionSound (sounddef.mdl:308).</summary>
    Collision,
    /// <summary>alephSound warp loop (sounddef.mdl:385).</summary>
    WarpAmbient,
    /// <summary>Station exterior loop (ILStationExternalSound, sounddef.mdl:1156).</summary>
    StationAmbient,
    /// <summary>asteroidSound loop (sounddef.mdl:382).</summary>
    AsteroidAmbient,
    /// <summary>Probe scanning loop (the er_beam default, sounddef.mdl:14).</summary>
    ProbeAmbient,
    /// <summary>miningSound loop (sounddef.mdl:379).</summary>
    MiningLoop,
}

/// <summary>The ported per-role native minimum distances (sounddef.mdl
/// <c>ThreeDSound(...)</c> values) plus the derived native audibility radius
/// the 3D path uses to admit world loops.</summary>
public static class Native3DMix
{
    /// <summary>Native minimum distance (world units) for the role — the
    /// <c>ThreeDSound(minDistance)</c> argument from the cited sounddef line.</summary>
    public static float MinDistance(Native3DRole role) => role switch
    {
        Native3DRole.ProjectileAmbient => 200f,   // er_beam, sounddef.mdl:14
        Native3DRole.MissileFlight     => 100f,   // HornetMissileFlight, :1303
        Native3DRole.MissileLaunch     => 20f,    // HornetMissileLaunch, :1302
        Native3DRole.ExplosionLarge    => 1000f,  // largeExplosionSound, :268
        Native3DRole.ExplosionMedium   => 400f,   // mediumExplosionSound, :269
        Native3DRole.ExplosionSmall    => 200f,   // smallExplosionSound, :270
        Native3DRole.ShipHit           => 200f,   // myShield/myHullHitSound, :250,:255
        Native3DRole.RockHit           => 100f,   // rockHitSound, :266
        Native3DRole.Collision         => 100f,   // collisionSound, :308
        Native3DRole.WarpAmbient       => 200f,   // alephSound, :385
        Native3DRole.StationAmbient    => 200f,   // ILStationExternalSound, :1156
        Native3DRole.AsteroidAmbient   => 200f,   // asteroidSound, :382
        Native3DRole.ProbeAmbient      => 200f,   // er_beam default, :14
        Native3DRole.MiningLoop        => 200f,   // miningSound, :379
        _ => 200f,
    };

    /// <summary>Native audibility radius for the role
    /// (<see cref="Sound3DModel.AudibleRange"/>, the −60 dB distance).</summary>
    public static float AudibleRange(Native3DRole role) =>
        Sound3DModel.AudibleRange(MinDistance(role));
}

/// <summary>The listener for a 3D mix pass — native's <c>CameraListener</c>
/// (WinTrek.cpp:646-708): camera position, own-ship velocity, and the camera's
/// world-space right axis (extracted from the view matrix for panning).</summary>
public readonly record struct Listener3D(
    Vector3 Position,
    Vector3 Velocity,
    Vector3 Right,
    bool HasBasis);

/// <summary>A world sound emitter — native's <c>ModelPointSoundSource</c>
/// (trekigc.cpp:453-541): a world position plus the model's velocity.</summary>
public readonly record struct World3DEmitter(Vector3 Position, Vector3 Velocity);

/// <summary>One tracked world voice's inputs for a 3D mix pass.</summary>
/// <param name="BaseLoudness">The pre-3D loudness the planner/curve produced
/// (used verbatim when 3D is off — today's behaviour).</param>
/// <param name="BasePitch">The voice's template pitch in MonoGame octaves
/// (e.g. the asteroid loop's −0.35), so the Doppler delta adds on top.</param>
/// <param name="Emitter">Current emitter position/velocity.</param>
/// <param name="Role">Which native template family this voice belongs to.</param>
public readonly record struct Voice3DState(
    float BaseLoudness,
    float BasePitch,
    World3DEmitter Emitter,
    Native3DRole Role);

/// <summary>One world voice's 3D mix outputs for the frame.</summary>
/// <param name="Loudness">Effective pre-budget loudness 0..1 (native gain:
/// template gain 0 dB × distance dB × cone dB, linearised).</param>
/// <param name="Audible">False once the native gain hits −60 dB — such a voice
/// is kicked to the lowest budget priority (ds3dvirtualbuffer.cpp:750-753,804-806).</param>
/// <param name="Pan">Stereo pan −1..+1 (0 when the listener basis is unknown).</param>
/// <param name="PitchDelta">Doppler shift in octaves to add onto the base pitch.</param>
public readonly record struct Voice3DResult(
    float Loudness,
    bool Audible,
    float Pan,
    float PitchDelta);

/// <summary>The per-frame per-voice 3D mix: native's
/// <c>DS3DVirtualSoundBuffer::Update</c> (ds3dvirtualbuffer.cpp:692-830) as a
/// pure function. <paramref name="enabled3D"/> == false returns the identity of
/// the base state — exactly today's (option-off) behaviour — so the option-off
/// path is structurally incapable of changing the mix.</summary>
public static class Sound3DMix
{
    public static Voice3DResult Solve(Voice3DState voice, Listener3D listener, bool enabled3D)
    {
        if (!enabled3D)
            return new Voice3DResult(voice.BaseLoudness, voice.BaseLoudness > 0f, 0f, 0f);

        Vector3 rel = voice.Emitter.Position - listener.Position;
        float dist = rel.Length();
        float db = Sound3DModel.DistanceGainDb(Native3DMix.MinDistance(voice.Role), dist);
        // Cone attenuation: native applies cones only to non-listener-relative
        // sources (ds3dvirtualbuffer.cpp:775-777); no artwork sound declares one
        // (see Sound3DModel remarks), so no cone gain is added here — the math
        // (Sound3DModel.ConeGainDb) is ported + tested for future use.
        bool audible = db > Sound3DModel.MinAudibleDb;
        float pan = listener.HasBasis ? Sound3DModel.Pan(rel, listener.Right) : 0f;
        float pitchFactor = dist > 1e-4f
            ? Sound3DModel.DopplerPitchFactor(listener.Velocity, voice.Emitter.Velocity, rel)
            : 1f;
        // The emitter sits at the listener (own ship at the camera in cockpit
        // view): native skips 3D processing entirely for that voice
        // (ds3dvirtualbuffer.cpp:723-726) — full volume, centred, unshifted.
        if (dist <= 1e-4f)
            return new Voice3DResult(1f, true, 0f, 0f);
        return new Voice3DResult(Sound3DModel.DbToLinear(db), audible, pan,
            Sound3DModel.DopplerPitchOctaves(pitchFactor));
    }
}
