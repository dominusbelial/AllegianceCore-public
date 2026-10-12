using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="VoiceBudget"/> — the P614 voice-budget /
/// priority-stealing / mutex-group decision engine. No audio device involved;
/// the budget is deliberately free of MonoGame types (same discipline as the
/// other pure audio classes). Before P614 there was NO cap at all: every
/// shot/hit/explosion/ambient of ~390 ships started its own overlapping
/// instance (the ~67k projectile-ambient start/stop rattle in Domingo's client
/// log), so these rules are net-new behaviour.
/// </summary>
public class VoiceBudgetTests
{
    private static BudgetSound S(long id, SoundCategory cat, float loudness,
        bool audible = true, float age = 0f, bool playing = false) =>
        new(id, cat, loudness, audible, age, playing);

    [Fact]
    public void GlobalBudget_NeverExceedsDesiredCap()
    {
        // Far more candidates than the budget, spread across kinds whose
        // COMBINED group caps exceed the global budget — so the global
        // DesiredVoices cap, not a single kind's group cap, is what binds.
        var categories = new[]
        {
            SoundCategory.ExplosionDetonation, SoundCategory.MissileAmbient,
            SoundCategory.WorldAmbient, SoundCategory.HitMine,
            SoundCategory.WeaponFire, SoundCategory.ExplosionShip,
        };
        var sounds = Enumerable.Range(1, 60)
            .Select(i => S(i, categories[i % categories.Length], 1f))
            .ToList();
        var cfg = new VoiceBudgetConfig(DesiredVoices: 16, MaxVoices: 24);

        var decision = VoiceBudget.Decide(sounds, cfg);

        Assert.True(decision.AdmittedIds.Count <= cfg.DesiredVoices,
            $"admitted {decision.AdmittedIds.Count} > desired {cfg.DesiredVoices}");
        Assert.Equal(cfg.DesiredVoices, decision.AdmittedIds.Count); // fully subscribed (global cap binds)
    }

    [Fact]
    public void PriorityStealing_KeepsOwnShipSoundsUnderFlood()
    {
        // A battle flood: every projectile ambient slot saturated.
        var sounds = Enumerable.Range(1, 30)
            .Select(i => S(i, SoundCategory.ProjectileAmbient, 1f))
            .ToList();
        // Now a single HIGH-priority own-ship warning joins the flood.
        sounds.Add(S(9000, SoundCategory.Warning, 1f));

        var decision = VoiceBudget.Decide(sounds, new VoiceBudgetConfig(DesiredVoices: 16));

        Assert.Contains(9000L, decision.AdmittedIds);      // own-ship warning never starved
        Assert.True(decision.AdmittedIds.Count <= 16,      // global cap still respected
            $"admitted {decision.AdmittedIds.Count} exceeded budget");
        // The flood is group-capped to its nearest-6 projectile voices, so the
        // own-ship warning displaces ambient budget rather than the reverse.
        Assert.True(decision.AdmittedIds.Count(s => s >= 1 && s <= 30) <= 6);
    }

    [Fact]
    public void GroupLimit_DropsLowestPriorityMemberOfSameKind()
    {
        // Projectile ambients capped at 6 (native er_beam, sounddef.mdl:14).
        // Give them all the same static priority, varying loudness (≈distance):
        // the admitted six must be the SIX LOUDEST (nearest), not the first six.
        var sounds = new List<BudgetSound>();
        for (int i = 1; i <= 12; i++)
            sounds.Add(S(i, SoundCategory.ProjectileAmbient, loudness: i / 13f, age: 0f));

        var decision = VoiceBudget.Decide(sounds, new VoiceBudgetConfig(DesiredVoices: 16));

        Assert.Equal(6, decision.AdmittedIds.Count);               // the nearest-6 cap
        // Six loudest = ids 7..12 (loudness i/13 ascending). Faintest dropped.
        for (int i = 7; i <= 12; i++) Assert.Contains((long)i, decision.AdmittedIds);
        for (int i = 1; i <= 6; i++) Assert.DoesNotContain((long)i, decision.AdmittedIds);
    }

    [Fact]
    public void GroupLimit_TightCapCannotMaskAnExplosion()
    {
        // Projectile ambients fill their own group cap (6) but must not eat
        // the global budget slots an explosion needs. An explosion (big,
        // prio 4) arriving must displace the lowest-priority projectile
        // ambients rather than being dropped.
        var sounds = Enumerable.Range(1, 12)
            .Select(i => S(i, SoundCategory.ProjectileAmbient, 0.2f))
            .ToList();
        sounds.Add(S(500, SoundCategory.ExplosionShip, 0.9f, age: 0f));

        var decision = VoiceBudget.Decide(sounds, new VoiceBudgetConfig(DesiredVoices: 16));

        Assert.Contains(500L, decision.AdmittedIds);  // explosion cuts through
        Assert.True(decision.AdmittedIds.Count(s => s >= 1 && s <= 12) <= 6,
            "projectile ambients exceeded their 6-voice group cap");
    }

    [Fact]
    public void OutOfRangeSounds_AreNeverStarted()
    {
        // Inaudible (out of range / silent) candidates must never be admitted.
        var sounds = new List<BudgetSound>
        {
            S(1, SoundCategory.ExplosionShip, 0.01f, audible: false),
            S(2, SoundCategory.WeaponFire, 0f, audible: false),
            S(3, SoundCategory.ProjectileAmbient, 0f, audible: false),
            S(4, SoundCategory.HitOther, 0f, audible: false),
            S(5, SoundCategory.Warning, 1f, audible: true),   // only audible one
        };

        var decision = VoiceBudget.Decide(sounds, new VoiceBudgetConfig(DesiredVoices: 16));

        Assert.Equal(new long[] { 5 }, decision.AdmittedIds);
        Assert.DoesNotContain(1L, decision.AdmittedIds);
        Assert.DoesNotContain(2L, decision.AdmittedIds);
        Assert.DoesNotContain(3L, decision.AdmittedIds);
        Assert.DoesNotContain(4L, decision.AdmittedIds);
    }

    [Fact]
    public void AudibleWithinBudget_AllAdmitted_WhenUndersubscribed()
    {
        // Normal small-skirmish load: well under the cap → everything audible plays.
        var sounds = new List<BudgetSound>
        {
            S(1, SoundCategory.Warning, 0.9f),
            S(2, SoundCategory.EngineBed, 0.5f),
            S(3, SoundCategory.WeaponFire, 0.6f),
            S(4, SoundCategory.ExplosionShip, 0.9f),
            S(5, SoundCategory.ProjectileAmbient, 0.4f),
        };
        var decision = VoiceBudget.Decide(sounds, new VoiceBudgetConfig(DesiredVoices: 16));
        Assert.Equal(5, decision.AdmittedIds.Count);
    }

    [Fact]
    public void LouderWins_AtEqualPriority()
    {
        // Same category, same priority: the LOUDER (closer) sound must outrank
        // the quieter one when only one budget slot remains.
        var sounds = new List<BudgetSound>
        {
            S(1, SoundCategory.HitOther, 0.2f),
            S(2, SoundCategory.HitOther, 0.9f),
        };
        var cfg = new VoiceBudgetConfig(DesiredVoices: 1);
        var decision = VoiceBudget.Decide(sounds, cfg);
        Assert.Equal(new long[] { 2 }, decision.AdmittedIds);   // louder survives
    }

    [Fact]
    public void PlayingContinuityBoost_KeepsCurrentlyPlayingVoice()
    {
        // Native's 0.1 dB continuity boost (ds3dvirtualbuffer.cpp:385): a
        // currently-playing voice is retained over an identical, equally-young
        // newcomer when only one slot remains.
        var sounds = new List<BudgetSound>
        {
            S(1, SoundCategory.WeaponFire, 0.5f, age: 0f, playing: true),
            S(2, SoundCategory.WeaponFire, 0.5f, age: 0f, playing: false),
        };
        var decision = VoiceBudget.Decide(sounds, new VoiceBudgetConfig(DesiredVoices: 1));
        Assert.Equal(new long[] { 1 }, decision.AdmittedIds);
    }

    [Fact]
    public void YoungSoundBeatsIdenticalOlderSound()
    {
        // Native's age boost max(0, 2−age): a younger sound replaces an
        // identical, older one (repeating-weapon behaviour, ds3dvirtualbuffer.cpp:386).
        var sounds = new List<BudgetSound>
        {
            S(1, SoundCategory.WeaponFire, 0.5f, age: 1.9f),
            S(2, SoundCategory.WeaponFire, 0.5f, age: 0f),
        };
        var decision = VoiceBudget.Decide(sounds, new VoiceBudgetConfig(DesiredVoices: 1));
        Assert.Equal(new long[] { 2 }, decision.AdmittedIds);
    }
}
