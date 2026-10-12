using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P614 — the category a managed voice belongs to, carrying its static
/// priority (added to loudness when ranking which voices get an audible
/// buffer, exactly native's <c>GetDynamicPriority = gain + priority + …</c>,
/// <c>soundengine/ds3dvirtualbuffer.cpp:384-386</c>) and its per-kind limit
/// group cap (native's mutex/limit groups, <c>CreateSerializedSoundTemplate</c>
/// and the DS3D "get the n most important sounds" budget,
/// <c>soundengine/ds3dengine.cpp:410-414</c>).
/// </summary>
/// <remarks>
/// <para><b>Priority values are anchored to native</b> <c>sounddef.mdl</c>
/// wherever native defines one, and tiered only where the P614 brief
/// explicitly requires an explosion to cut through gunfire that native's flat
/// priorities would not guarantee (native ties the large/medium explosion
/// templates at the default priority, <c>sounddef.mdl:268-269</c>). Rows note
/// their anchor and any documented refinement.</para>
/// </remarks>
public enum SoundCategory
{
    /// <summary>Own-ship warnings and voice cues (missile-warning loop, sector
    /// alerts, autopilot/countdown/contact/ripcord/message voices). Native
    /// voices are <c>SerializedSound(MutexSal,…)</c> (sounddef.mdl:71-79,327),
    /// i.e. they must never be drowned — the highest static priority.</summary>
    Warning,

    /// <summary>Sustained weapon burst loops. Native
    /// <c>PrioritySound(5, …)</c> (sounddef.mdl:18,38,82 …).</summary>
    WeaponBurst,

    /// <summary>Always-on own-ship bed (engine loop, cockpit hum, turn
    /// thruster, exterior loop, sector ambience). Kept by priority so they are
    /// never starved by the world-sound storm.</summary>
    EngineBed,

    /// <summary>Large / station / capital-ship explosions. Native
    /// <c>largeExplosionSound</c> at the default priority (sounddef.mdl:268);
    /// tiered above fighter blasts to honour the P614 cut-through order
    /// (documented refinement — native ties large/medium at default 1.0).</summary>
    ExplosionBig,

    /// <summary>Medium / fighter-ship explosions. Native
    /// <c>mediumExplosionSound</c> at default priority (sounddef.mdl:269,
    /// = <c>explodeShipSound</c>:276); tiered between big and detonation for
    /// the cut-through order (documented refinement).</summary>
    ExplosionShip,

    /// <summary>Small / missile detonations. Native
    /// <c>smallExplosionSound</c> = <c>PrioritySound(-1, …)</c>
    /// (sounddef.mdl:270). Kept above hull-hit / projectile fire so a missile
    /// burst is heard over the gunfire bed (refinement from native -1).</summary>
    ExplosionDetonation,

    /// <summary>MY ship shield/hull hit cues. Native myShieldHitSound /
    /// myHullHitSound at the default priority (sounddef.mdl:250,256).</summary>
    HitMine,

    /// <summary>Missile in-flight ambient loops. Native HornetMissileFlight at
    /// the default priority (sounddef.mdl:1303).</summary>
    MissileAmbient,

    /// <summary>World ambient loops (warp / station / probe). Native per-object
    /// <c>AmbientSoundSite</c> loops (trekigc.cpp:1436-1468).</summary>
    WorldAmbient,

    /// <summary>OTHER ships' shield/hull hit cues. Native
    /// <c>PrioritySound(-1.6, …)</c> (sounddef.mdl:263-264).</summary>
    HitOther,

    /// <summary>Projectile single-shot fire. Native
    /// <c>PrioritySound(-1.5, …)</c> (sounddef.mdl:11-14,38 …).</summary>
    WeaponFire,

    /// <summary>Projectile in-flight ambient loops — the P614 rattle source.
    /// Native <c>PrioritySound(-1.5, …)</c> (er_beam, sounddef.mdl:14), capped
    /// to the nearest few by <see cref="SoundCategories.GroupCap"/> so hundreds
    /// of bolts can never sum into a constant wall of sound.</summary>
    ProjectileAmbient,
}

/// <summary>The static priority and per-kind limit-group cap for each
/// <see cref="SoundCategory"/>. Priorities are relative (only their ordering
/// matters — native's "only meaning … is in relation to other priorities",
/// <c>soundengine/ds3dvirtualbuffer.h:252-256</c>).</summary>
public static class SoundCategories
{
    public static float Priority(SoundCategory c) => c switch
    {
        SoundCategory.Warning              => 9.0f,
        SoundCategory.WeaponBurst          => 5.0f,
        SoundCategory.ExplosionBig         => 4.0f,
        SoundCategory.ExplosionShip        => 3.0f,
        SoundCategory.ExplosionDetonation  => 2.0f,
        SoundCategory.EngineBed            => 2.0f,
        SoundCategory.HitMine              => 1.0f,
        SoundCategory.MissileAmbient       => 1.0f,
        SoundCategory.WorldAmbient         => 1.0f,
        SoundCategory.HitOther             => -1.6f,
        SoundCategory.WeaponFire           => -1.5f,
        SoundCategory.ProjectileAmbient    => -1.5f,
        _ => 0f,
    };

    /// <summary>Maximum simultaneous voices of this kind (the mutex/limit
    /// group cap); a new lower-priority member is dropped once the group is
    /// full, mirroring native's serialized "new sound is discarded" template
    /// (soundtemplates.h:101-106). −1 = unlimited. Cap is per-category; the
    /// categories most prone to flooding (projectile ambients, distant hits,
    /// rapid fire) get tight caps so they can never mask an explosion.</summary>
    public static int GroupCap(SoundCategory c) => c switch
    {
        SoundCategory.Warning              => 4,
        SoundCategory.WeaponBurst          => 4,
        SoundCategory.EngineBed            => 6,
        SoundCategory.ExplosionBig         => 4,
        SoundCategory.ExplosionShip        => 6,
        SoundCategory.ExplosionDetonation  => 8,
        SoundCategory.HitMine              => 6,
        SoundCategory.MissileAmbient       => 6,
        SoundCategory.WorldAmbient         => 8,
        SoundCategory.HitOther             => 4,
        SoundCategory.WeaponFire           => 4,
        SoundCategory.ProjectileAmbient    => 6,   // nearest-6: the core rattle cap
        _ => -1,
    };

    /// <summary>Human-readable label for telemetry / the debug overlay.</summary>
    public static string Label(SoundCategory c) => c switch
    {
        SoundCategory.Warning => "warning/voice",
        SoundCategory.WeaponBurst => "weapon-burst",
        SoundCategory.EngineBed => "engine-bed",
        SoundCategory.ExplosionBig => "explosion-large",
        SoundCategory.ExplosionShip => "explosion-ship",
        SoundCategory.ExplosionDetonation => "explosion-detonation",
        SoundCategory.HitMine => "hit-mine",
        SoundCategory.MissileAmbient => "missile-ambient",
        SoundCategory.WorldAmbient => "world-ambient",
        SoundCategory.HitOther => "hit-other",
        SoundCategory.WeaponFire => "weapon-fire",
        SoundCategory.ProjectileAmbient => "projectile-ambient",
        _ => "?",
    };
}

/// <summary>A single sound competing for an audible voice this frame —
/// the pure analogue of native's <c>DSVirtualSoundBuffer</c> priority inputs.</summary>
/// <param name="Id">Stable identity (the voice registry id).</param>
/// <param name="Category">Its <see cref="SoundCategory"/> (priority + group).</param>
/// <param name="Loudness">Effective pre-budget volume (0..1) this frame, from
/// listener distance (the 3D attenuation); inaudible ⇒ 0.</param>
/// <param name="Audible">False when out of audible range / silent — such a
/// sound is never admitted (native skips <c>!IsAudible()</c> sounds and gives
/// them a −100 priority kick, ds3dengine.cpp:424, ds3dvirtualbuffer.cpp:804-806).</param>
/// <param name="AgeSeconds">Age of the sound, feeding native's young-sound
/// boost <c>max(0, 2 − age)</c> (ds3dvirtualbuffer.cpp:385-386).</param>
/// <param name="Playing">True when this voice currently has an audible buffer,
/// giving it native's 0.1 dB continuity boost (ds3dvirtualbuffer.cpp:385).</param>
public readonly record struct BudgetSound(
    long Id,
    SoundCategory Category,
    float Loudness,
    bool Audible,
    float AgeSeconds,
    bool Playing);

/// <summary>The native voice budget: how many simultaneous audible voices to
/// keep and the hard ceiling. Native initialises these per quality tier —
/// software (no hardware buffers) mid quality is desired 16 / max 24 and max
/// quality is desired 24 / max 32 (<c>soundengine/ds3dengine.cpp:513-544</c>).
/// The port's MonoGame software mixer uses the conservative mid-quality
/// numbers by default (desired 16, max 24), overridable in the config.</summary>
public sealed record VoiceBudgetConfig(int DesiredVoices = 16, int MaxVoices = 24);

/// <summary>The result of a <see cref="VoiceBudget.Decide"/> pass: the ids that
/// may hold an audible buffer this frame. Every other audible candidate is
/// dropped (a playing buffer is stopped / a loop muted) so the audible voice
/// count never exceeds <see cref="VoiceBudgetConfig.DesiredVoices"/>.</summary>
public sealed record VoiceBudgetDecision(IReadOnlyList<long> AdmittedIds);

/// <summary>P614 live audio telemetry (for the debug overlay / periodic log):
/// how many voices are audible under the budget, how many are essential
/// (force-admitted own-ship bed), the instantaneous drop rate, and active
/// counts broken out per <see cref="SoundCategory"/>.</summary>
/// <param name="Timestamp">UTC when sampled.</param>
/// <param name="AudibleVoices">Voices holding an audible buffer this frame.</param>
/// <param name="EssentialVoices">Of those, the force-admitted own-ship bed.</param>
/// <param name="DropsPerSecond">Budget drops (voices stolen/culled) per second.</param>
/// <param name="ByCategory">Audible voice count per category label.</param>
public sealed record AudioTelemetry(
    DateTime Timestamp,
    int AudibleVoices,
    int EssentialVoices,
    float DropsPerSecond,
    IReadOnlyList<(string Category, int Active)> ByCategory);

/// <summary>
/// P614 — the pure voice-budget / priority-stealing / mutex-group decision
/// engine, the port of native's DS3D engine voice management
/// (<c>soundengine/ds3dengine.cpp:360-466</c>): each frame rank every audible
/// sound by dynamic priority, admit the <c>Desired</c> most important subject
/// to per-kind group caps, and drop the rest. Deliberately free of any
/// MonoGame type so the budget, priority-stealing and group-limit rules are
/// unit-testable without an audio device (the same discipline as the other
/// pure audio classes).
/// </summary>
/// <remarks>
/// <para><b>Dynamic priority</b> mirrors native exactly
/// (<c>ds3dvirtualbuffer.cpp:384-386</c>): <c>gain(dB) + staticPriority +
/// (playing ? 0.1) + max(0, 2 − age)</c>. Loudness (0..1) is converted to an
/// approximate gain in dB with the standard power ratio formula
/// <c>20·log10(loudness)</c>, floored at the native audibility threshold
/// (<c>c_fMinAudible = −60</c> dB, ds3dbuffer.h:12), so a louder sound ranks
/// above a quieter one at equal priority and a nearby explosion steals the
/// buffer of a distant projectile ambient.</para>
/// <para><b>Group caps</b> are applied per <see cref="SoundCategory"/>
/// (the mutex/limit groups). Admission walks candidates in descending dynamic
/// priority; a candidate whose group is already full is dropped even if it
/// would otherwise fit the global budget — so a group can never exceed its cap,
/// and its cap members are always the <i>highest</i> priority members (e.g.
/// the nearest projectile ambients).</para>
/// </remarks>
public static class VoiceBudget
{
    /// <summary>The audibility floor in dB (native <c>c_fMinAudible = -60</c>,
    /// ds3dbuffer.h:12). Loudness below this converts to the floor so a
    /// barely-audible sound never outranks a closer one.</summary>
    public const float MinAudibleDb = -60f;

    /// <summary>Decide which of the given sounds may be audible this frame.
    /// <paramref name="cfg"/> supplies the global budget. Returns the ids to
    /// keep audible; any other audible candidate is dropped.</summary>
    public static VoiceBudgetDecision Decide(IReadOnlyList<BudgetSound> sounds, VoiceBudgetConfig cfg)
    {
        // Only audible sounds compete; an out-of-range / silent sound is never
        // admitted (native skips !IsAudible, ds3dengine.cpp:424).
        var candidates = sounds.Where(s => s.Audible).ToList();

        // Rank by dynamic priority descending (native priority heap,
        // ds3dengine.cpp:404-414), tie-broken by id for determinism.
        var ranked = candidates
            .OrderByDescending(DynamicPriority)
            .ThenBy(s => s.Id)
            .ToList();

        var admitted = new List<long>(Math.Min(cfg.DesiredVoices, ranked.Count));
        var groupCounts = new Dictionary<SoundCategory, int>();
        int total = 0;

        foreach (var s in ranked)
        {
            int cap = SoundCategories.GroupCap(s.Category);
            groupCounts.TryGetValue(s.Category, out int cur);
            if (cap >= 0 && cur >= cap) continue;          // group full → drop
            if (total >= cfg.DesiredVoices) continue;      // global budget full → drop
            admitted.Add(s.Id);
            groupCounts[s.Category] = cur + 1;
            total++;
        }

        return new VoiceBudgetDecision(admitted);
    }

    /// <summary>The dynamic priority used for ranking — native's
    /// <c>gain + priority + (playing?0.1) + max(0, 2−age)</c>
    /// (ds3dvirtualbuffer.cpp:384-386), with loudness→gain in dB.</summary>
    public static float DynamicPriority(BudgetSound s)
    {
        float gainDb = s.Loudness <= 0f ? MinAudibleDb
            : Math.Max(MinAudibleDb, 20f * (float)Math.Log10(s.Loudness));
        return gainDb
            + SoundCategories.Priority(s.Category)
            + (s.Playing ? 0.1f : 0f)
            + Math.Max(0f, 2f - s.AgeSeconds);
    }
}
