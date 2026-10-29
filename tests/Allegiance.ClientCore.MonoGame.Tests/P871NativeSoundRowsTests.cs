using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core;
using Allegiance.Core.Audio;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P871 — the client-side native sound rows from the n/a audit:
/// <list type="bullet">
/// <item>SIM-0314 — numeric <c>SoundID</c> → template dispatch and invalid-id
/// suppression (<c>WinTrek/WinTrek.cpp:11271-11280</c>);</item>
/// <item>SIM-0346 — <c>PlaySoundIf</c>'s retained instance
/// (<c>WinTrek/trekigc.cpp:930-951</c>);</item>
/// <item>SIM-0371 — the gunner interior loop id
/// (<c>WinTrek/trekigc.cpp:1096-1112,1300-1314</c>);</item>
/// <item>SIM-0407/0408/0409 — the <c>PlaySoundEffect</c>/offset/
/// <c>PlayNotificationSound</c> gates (<c>WinTrek/trekigc.cpp:4955-4983</c>);</item>
/// <item>SIM-0412 — the MDL button sink's immediate audio flush
/// (<c>WinTrek/trekmdl.cpp:452-478</c>).</item>
/// </list>
/// </summary>
public class P871NativeSoundRowsTests
{
    // ── SIM-0314: numeric SoundID dispatch ─────────────────────────────────

    [Fact]
    public void The_native_sound_table_is_the_soundlist_index_space()
    {
        // Igc/igc.h:459-464: enum { lastDynamicSound = 1000, #include "sounds.h" }
        // so DEFSOUND names start at 1001; soundList numeric keys are literals and
        // the vector length is maxIndex+1 (engine/value.h:686-722).
        Assert.Equal(1694, NativeSoundCatalog.Count);
        Assert.Equal((short)1040, NativeSoundCatalog.IdOf("collisionSound"));
        Assert.Equal((short)1386, NativeSoundCatalog.IdOf("salRepairedAtCarrierSound"));
        Assert.Equal((short)1058, NativeSoundCatalog.IdOf("mouseoverSound"));
        Assert.Equal((short)1057, NativeSoundCatalog.IdOf("mouseclickSound"));
        Assert.Equal(-1, NativeSoundCatalog.IdOf("noSuchSound"));
        Assert.Equal("colisionlarge", Assert.Single(NativeSoundCatalog.Resolve(1040)!.Waves));
    }

    [Fact]
    public void The_dead_nyi_turret_block_does_not_shift_the_ids_after_it()
    {
        // P871b: Igc/sounds.h:443-459 is a `/* NYI no turrets ... */` block whose
        // 13 DEFSOUND names the native preprocessor never sees. P871's generator
        // stripped only `//` lines, so it kept them and every id after the block
        // came out +13. Values below are `gcc -E` of
        //   enum { #include "Igc/sounds.h" }   with DEFSOUND(n) -> n,
        // which yields 693 names, ids 1001..1693.
        Assert.Equal(693, NativeSoundCatalog.IdsByName.Count);
        // Last name before the block (and itself a `//DEFSOUND` line: line
        // comments are still stripped), then the last live name before it.
        Assert.Equal((short)1366, NativeSoundCatalog.IdOf("noTurretsSound"));
        Assert.Equal((short)1368, NativeSoundCatalog.IdOf("droneMiningSound"));
        // First name after the block, and the row the port actually plays.
        Assert.Equal((short)1369, NativeSoundCatalog.IdOf("salCommenceScanSound"));
        Assert.Equal((short)1386, NativeSoundCatalog.IdOf("salRepairedAtCarrierSound"));
        // Last name in sounds.h: the whole tail shifted by 13 in P871.
        Assert.Equal((short)1693, NativeSoundCatalog.IdOf("tm_0_blankSound"));

        // The 13 names inside the block must not exist at all (`turretLimit`,
        // which IS live, is deliberately not in this list).
        foreach (var dead in new[]
                 {
                     "turretReadySound", "turretEnemyRixSound", "turretEnemyCrimsonSound",
                     "turretEnemyIronSound", "turretReady2Sound", "turretWatchFireSound",
                     "turretFiringSound", "turretRogerThatSound", "turretCantShootSound",
                     "turretContactSightedSound", "turretTargetObscuredSound",
                     "turretTakingDamageSound", "turretArghSound",
                 })
            Assert.Equal(-1, NativeSoundCatalog.IdOf(dead));

        // The voice table the carrier repair path resolves through agrees with
        // the numeric table (same sounds.h strip, P871b).
        Assert.Equal(NativeSoundCatalog.IdOf("salRepairedAtCarrierSound"),
            Assert.Single(NativeVoiceCatalog.Sounds, p => p.Value.Name == "salRepairedAtCarrierSound").Key);
    }

    [Theory]
    [InlineData((short)-1)]
    [InlineData((short)1694)]
    [InlineData((short)32000)]
    public void An_out_of_range_numeric_id_is_suppressed(short soundId)
    {
        Assert.False(NativeSoundDispatch.IsValidId(soundId));
        Assert.Null(NativeSoundDispatch.Resolve(soundId));
        Assert.False(NativeSoundDispatch.TryPlan(soundId, null, null, out _));
    }

    [Fact]
    public void An_empty_or_wave_less_slot_is_suppressed_like_native_null()
    {
        // 1000 is lastDynamicSound itself: an addressable slot with no template.
        Assert.False(NativeSoundDispatch.TryPlan(1000, null, null, out _));
        // Empty slots further up the numeric block are equally silent.
        short empty = FirstEmptySlot();
        Assert.True(NativeSoundDispatch.IsValidId(empty));
        Assert.False(NativeSoundDispatch.TryPlan(empty, null, null, out _));
    }

    [Fact]
    public void A_valid_id_resolves_its_template_and_forwards_the_requested_source()
    {
        // Unpositioned: native plays at template gain.
        Assert.True(NativeSoundDispatch.TryPlan(1386, null, null, out var bare));
        Assert.Equal("vo_sal_shipisrepaired", bare.Stem);
        Assert.Equal(1f, bare.Volume);

        // Positioned: the requested source places the cue; the listener distance
        // attenuates it (the port's positional convention; the SOURCE is what
        // native forwards to the template).
        var listener = new Vector3(0, 0, 0);
        var near = new NativeSoundSource(new Vector3(0, 0, 0));
        Assert.True(NativeSoundDispatch.TryPlan(1040, near, listener, out var atListener));
        Assert.Equal(1f, atListener.Volume);
        var mid = new NativeSoundSource(new Vector3(NativeSoundDispatch.AudibleRadius / 2, 0, 0));
        Assert.True(NativeSoundDispatch.TryPlan(1040, mid, listener, out var half));
        Assert.Equal(0.5f, half.Volume, 3);
        var far = new NativeSoundSource(new Vector3(NativeSoundDispatch.AudibleRadius + 1, 0, 0));
        Assert.False(NativeSoundDispatch.TryPlan(1040, far, listener, out _));
    }

    // ── SIM-0346: PlaySoundIf retains an instance ──────────────────────────

    [Fact]
    public void Play_sound_if_starts_once_preserves_the_instance_and_restarts_after_stop()
    {
        var slot = new PlaySoundIfSlot();
        Assert.Equal(PlaySoundIfSlot.Action.Start, slot.Update(play: true, requestedId: 1040));
        Assert.Equal((short)1040, slot.PlayingId);
        // A second play request — even for a DIFFERENT id — preserves the instance.
        Assert.Equal(PlaySoundIfSlot.Action.None, slot.Update(play: true, requestedId: 1058));
        Assert.Equal((short)1040, slot.PlayingId);
        // false stops and clears.
        Assert.Equal(PlaySoundIfSlot.Action.Stop, slot.Update(play: false, requestedId: 0));
        Assert.Null(slot.PlayingId);
        // false with nothing held is a no-op.
        Assert.Equal(PlaySoundIfSlot.Action.None, slot.Update(play: false, requestedId: 0));
        // restart after false.
        Assert.Equal(PlaySoundIfSlot.Action.Start, slot.Update(play: true, requestedId: 1058));
        Assert.Equal((short)1058, slot.PlayingId);
    }

    // ── SIM-0371: gunner interior sound id ─────────────────────────────────

    [Fact]
    public void The_gunner_interior_sound_is_the_source_hulls_mounted_hardpoint()
    {
        var hull = Hull(hp0: 120, hp1: 121);
        Assert.Equal((short)121, GunnerInteriorSound.Resolve(boarded: true, turretMount: 1, hull));
        Assert.Equal((short)120, GunnerInteriorSound.Resolve(boarded: true, turretMount: 0, hull));
        // Native returns NA for anything that is not a boarded gunner on a real mount.
        Assert.Equal(-1, GunnerInteriorSound.Resolve(boarded: false, turretMount: 1, hull));
        Assert.Equal(-1, GunnerInteriorSound.Resolve(boarded: true, turretMount: null, hull));
        Assert.Equal(-1, GunnerInteriorSound.Resolve(boarded: true, turretMount: 9, hull));
        Assert.Equal(-1, GunnerInteriorSound.Resolve(boarded: true, turretMount: 1, hull: null));
        Assert.True(GunnerInteriorSound.IsGunner(boarded: true, turretMount: 0));
        Assert.False(GunnerInteriorSound.IsGunner(boarded: true, turretMount: null));
        Assert.False(GunnerInteriorSound.IsGunner(boarded: false, turretMount: 0));
    }

    [Fact]
    public void A_turret_gunner_keeps_the_cockpit_mix_but_not_the_pilot_interior_bed()
    {
        // P871b audit of the P871 `cockpitMode: camera == Cockpit && !isGunner`
        // rewrite. Native separates the two gates (WinTrek/trekigc.cpp:1310-1314):
        //   PlaySoundIf(m_pGunnerInteriorSound, gunnerInteriorSoundID, src, bIsGunner);
        //   PlaySoundIf(m_pPilotInteriorSound,   pht->GetInteriorSound(), src, !bIsGunner);
        //   PlaySoundIf(m_pMainThrusterInteriorSound, …, m_fThrustSoundLevel > 0.01f);
        // — the engine/main-thruster interior bed carries NO gunner gate, and the
        // interior mix audibility is the CAMERA view job only
        // (SetInternalVolume(-100) off cockpit, trekigc.cpp:1360-1372).
        var pilot = new CockpitAudio(CockpitView: true, IsGunner: false);
        Assert.True(pilot.EngineAudible);
        Assert.True(pilot.PilotInterior);

        var gunner = new CockpitAudio(CockpitView: true, IsGunner: true);
        Assert.True(gunner.EngineAudible);      // P871 dropped this for gunners
        Assert.False(gunner.PilotInterior);     // native !bIsGunner

        // Exterior/chase: no interior mix at all, gunner or not.
        var chase = new CockpitAudio(CockpitView: false, IsGunner: false);
        Assert.False(chase.EngineAudible);
        Assert.False(chase.PilotInterior);
        var chaseGunner = new CockpitAudio(CockpitView: false, IsGunner: true);
        Assert.False(chaseGunner.EngineAudible);
        Assert.False(chaseGunner.PilotInterior);

        // The gunner own interior loop is the bed a boarded gunner gets, and it
        // is resolved from the SOURCE hull mounted hardpoint (SIM-0371).
        var hull = Hull(hp0: 120, hp1: 121);
        Assert.True(GunnerInteriorSound.IsGunner(boarded: true, turretMount: 1));
        Assert.Equal((short)121, GunnerInteriorSound.Resolve(boarded: true, turretMount: 1, hull));
    }

    // ── SIM-0407: PlaySoundEffect cluster/null-model gate ──────────────────

    [Fact]
    public void Play_sound_effect_plays_unpositioned_for_null_and_only_in_cluster()
    {
        // Native: if (!model || (model->GetCluster() && model->GetCluster() == GetCluster()))
        Assert.True(SoundRouting.AllowsEffect(modelId: null, modelCluster: null, listenerCluster: 7));
        Assert.True(SoundRouting.AllowsEffect(modelId: 11, modelCluster: 7, listenerCluster: 7));
        Assert.False(SoundRouting.AllowsEffect(modelId: 11, modelCluster: 8, listenerCluster: 7));
        // Detached model: GetCluster() == NULL -> suppressed (the && short-circuit).
        Assert.False(SoundRouting.AllowsEffect(modelId: 11, modelCluster: null, listenerCluster: 7));
        Assert.False(SoundRouting.AllowsEffect(modelId: 11, modelCluster: 7, listenerCluster: null));
    }

    // ── SIM-0408: the offset overload moves the source ─────────────────────

    [Fact]
    public void The_offset_overload_places_the_source_at_the_model_plus_offset()
    {
        var model = new Vector3(10, 0, 0);
        var source = SoundRouting.OffsetSource(model, new Vector3(0, 4, 0));
        Assert.Equal(new Vector3(10, 4, 0), source.Position);
        // Same availability gate as the plain overload.
        Assert.True(SoundRouting.AllowsEffectWithOffset(11, 7, 7));
        Assert.False(SoundRouting.AllowsEffectWithOffset(11, 8, 7));
        // Native's asteroid caller passes TimesInverse(position2 - position1):
        // the world delta rotated into the model frame.
        var orientation = new Orientation();
        var local = SoundRouting.LocalOffset(orientation, new Vector3(1, 2, 3));
        Assert.Equal(new Vector3(1, 2, 3), local);
    }

    // ── SIM-0409: PlayNotificationSound own/source-ship gate ───────────────

    [Fact]
    public void Notification_sound_allows_only_the_own_or_source_ship()
    {
        // model == GetShip() || model == GetShip()->GetSourceShip()
        Assert.True(SoundRouting.AllowsNotification(modelId: 10, ownShipId: 10, sourceShipId: 10));
        Assert.True(SoundRouting.AllowsNotification(modelId: 10, ownShipId: 20, sourceShipId: 10));   // boarded gunner
        Assert.False(SoundRouting.AllowsNotification(modelId: 11, ownShipId: 10, sourceShipId: 10));
        Assert.False(SoundRouting.AllowsNotification(modelId: null, ownShipId: 10, sourceShipId: 10));
        // Native quirk preserved: with no local ship, pointer equality makes a
        // null model equal null and it plays unpositioned.
        Assert.True(SoundRouting.AllowsNotification(modelId: null, ownShipId: null, sourceShipId: null));
    }

    // ── SIM-0412: MDL button sink ──────────────────────────────────────────

    [Fact]
    public void The_mdl_button_sink_starts_the_sound_and_flushes_the_engine_immediately()
    {
        var sink = new RecordingSink();
        SoundIdEventSink.Fire(sink, 1057);   // an ordinary mouseclick id
        Assert.Equal(new[] { "start:1057", "flush" }, sink.Events);
        sink.Events.Clear();
        // The number-button pane takes an arbitrary configured SoundID
        // (trekmdl.cpp:616) — no different from the built-in ids.
        SoundIdEventSink.Fire(sink, 1040);
        Assert.Equal(new[] { "start:1040", "flush" }, sink.Events);
    }

    private sealed class RecordingSink : ISoundIdEventSink
    {
        public List<string> Events { get; } = new();
        public void StartSoundId(short soundId) => Events.Add($"start:{soundId}");
        public void FlushAudio() => Events.Add("flush");
    }

    private static short FirstEmptySlot()
    {
        for (short id = 0; id < NativeSoundCatalog.Count; id++)
            if (NativeSoundCatalog.Templates.GetValueOrDefault(id) is null) return id;
        throw new InvalidOperationException("the native sound table has no empty slot");
    }

    private static DataHullTypeIGC Hull(short hp0, short hp1) =>
        default(DataHullTypeIGC) with
        {
            HullId = 1,
            MaxWeapons = 2,
            InteriorSound = 140,
            Hardpoints =
            [
                new DataHullTypeIGC.Hardpoint(hp0, 0, "hp0", "hp0", unchecked((short)0xFFFF), Fixed: false),
                new DataHullTypeIGC.Hardpoint(hp1, 0, "hp1", "hp1", unchecked((short)0xFFFF), Fixed: false),
            ],
        };
}
