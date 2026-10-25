using System;
using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-12 — the data paths and native overlay rules behind the eleven rows that
/// were blocked on missing client data or invented shapes:
/// <list type="bullet">
/// <item>native radar bracket art quadrant geometry
/// (<c>WinTrek/radarimage.cpp:255-277</c>) and the subject/ripcord/threat
/// masks (<c>radarimage.cpp:572-574,660-671,999-1030</c>);</item>
/// <item>the owner-only continuous cloak fraction
/// (<c>OwnShipCloakUpdate</c>, native <c>GetCloaking()</c>,
/// <c>Igc/cloakigc.cpp:85-122</c>; HUD formulas
/// <c>WinTrek/consoledata.cpp:480-489,563-575</c>);</item>
/// <item>mission lives/kills from MISSIONDEF iLives +
/// PLAYERINFO/KILL_SHIP mission counters
/// (<c>WinTrek/console.cpp:812-821</c>);</item>
/// </list>
/// </summary>
public class Hud12DataAndArtTests
{
    // ── Native DrawExpandedBlip geometry (radarimage.cpp:255-277) ──────────

    [Fact]
    public void Expanded_blip_delta_is_radius_over_sqrt2_floored_at_half_the_quadrant()
    {
        // 32x32 surface, bracket radius 15: 15/√2 = 10.6066 > min(16,16)/2 = 8.
        Assert.Equal(10.6066f, RadarBracketRenderer.ExpandedBlipDelta(32, 32, 15f), 3);
        // A tiny surface / radius takes the native floor m = min(w/2,h/2)/2.
        Assert.Equal(2f, RadarBracketRenderer.ExpandedBlipDelta(8, 8, 2f), 3);
        // The native floor uses only the horizontal midpoint: m = (6/2)/2 = 1.5.
        Assert.Equal(1.5f, RadarBracketRenderer.ExpandedBlipDelta(6, 40, 1f), 3);
    }

    [Fact]
    public void Expanded_blip_quadrants_split_the_bitmap_at_its_midlines()
    {
        Assert.Equal(new Rectangle(0, 0, 16, 16), RadarBracketRenderer.ExpandedBlipQuadrant(0, 32, 32));
        Assert.Equal(new Rectangle(16, 0, 16, 16), RadarBracketRenderer.ExpandedBlipQuadrant(1, 32, 32));
        Assert.Equal(new Rectangle(16, 16, 16, 16), RadarBracketRenderer.ExpandedBlipQuadrant(2, 32, 32));
        Assert.Equal(new Rectangle(0, 16, 16, 16), RadarBracketRenderer.ExpandedBlipQuadrant(3, 32, 32));
    }

    // ── Radar overlay masks (radarimage.cpp:572-574,660-671,999-1030) ──────

    [Fact]
    public void Command_subjects_follow_the_console_chat_recipient_rules()
    {
        // GetSubjects() is NULL unless a chat is being composed (console.cpp:2327-2334).
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Team, composing: false, true, true, 0, 0));
        // CHAT_TEAM / CHAT_ALLIES: every own-side ship (console.cpp:336-346).
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Team, true, true, true, 3, 0));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Team, true, sameSide: false, true, 0, 0));
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Allies, true, true, true, 0, 0));
        // CHAT_EVERYONE: every ship (console.cpp:368-371).
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Everyone, true, sameSide: false, false, 0, 0));
        // CHAT_WING: selected wing on our side (console.cpp:359-366,405-413).
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Wing, true, true, true, 2, 2));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Wing, true, true, true, 1, 2));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Wing, true, sameSide: false, true, 2, 2));
        // CHAT_FRIENDLY_SECTOR: own side, last seen in the chat cluster.
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.FriendlySector, true, true, true, 0, 0));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.FriendlySector, true, true, sameSector: false, 0, 0));
        // P818 — CHAT_INDIVIDUAL / CHAT_GROUP are membership in the compose's
        // ship list (radarimage.cpp:651); no list ⇒ no fabricated subject.
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Individual, true, true, true, 0, 0));
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Individual, true, true, true, 0, 0,
            shipId: 4, subjectShips: new short[] { 4 }));
        Assert.True(RadarOverlayRules.IsSubject(ChatTarget.Group, true, sameSide: false, true, 0, 0,
            shipId: 4, subjectShips: new short[] { 4, 5 }));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Group, true, true, true, 0, 0,
            shipId: 6, subjectShips: new short[] { 4, 5 }));
        Assert.False(RadarOverlayRules.IsSubject(ChatTarget.Admin, true, true, true, 0, 0));
        // Native assigns psubjects only when bRangeLabels is OFF (command camera).
        Assert.False(RadarOverlayRules.SubjectsApply(commandCamera: false, composing: true));
        Assert.True(RadarOverlayRules.SubjectsApply(commandCamera: true, composing: true));
        Assert.False(RadarOverlayRules.SubjectsApply(commandCamera: true, composing: false));
    }

    [Fact]
    public void Carried_flag_and_artifact_masks_use_the_native_GetFlag_sentinels()
    {
        // radarimage.cpp:643-645 — NA (-1) = none, side id = flag,
        // SIDE_TEAMLOBBY (-2) = artifact.
        Assert.True(RadarOverlayRules.CarriedFlag(3));
        Assert.False(RadarOverlayRules.CarriedFlag(-1));
        Assert.False(RadarOverlayRules.CarriedFlag(-2));
        Assert.True(RadarOverlayRules.CarriedArtifact(-2));
        Assert.False(RadarOverlayRules.CarriedArtifact(3));
        Assert.False(RadarOverlayRules.CarriedArtifact(-1));
        // A ship with no PLAYERINFO is an NPC (pilot type below c_ptPlayer);
        // a player's PLAYERINFO carries c_ptPlayer = 10.
        Assert.Equal((byte)0, RadarOverlayRules.PilotTypeOf(null));
        Assert.Equal(PlayerInfo.PilotTypePlayer, RadarOverlayRules.PilotTypeOf(
            new PlayerInfo { PilotType = PlayerInfo.PilotTypePlayer }));
    }

    [Fact]
    public void Drone_ripcord_needs_the_state_bit_same_side_and_a_non_player_pilot()
    {
        // droneRipMaskIGC = 0x1000 (igc.h:821); GetPilotType() < c_ptPlayer (0..9).
        Assert.True(RadarOverlayRules.DroneRipcord(0x1000, sameSide: true, pilotType: 0));
        Assert.True(RadarOverlayRules.DroneRipcord(0x9000, sameSide: true, pilotType: Ship.PilotTypeBuilder));
        Assert.False(RadarOverlayRules.DroneRipcord(0x1000, sameSide: true, pilotType: 10));
        Assert.False(RadarOverlayRules.DroneRipcord(0x1000, sameSide: false, pilotType: 0));
        Assert.False(RadarOverlayRules.DroneRipcord(0x0800, sameSide: true, pilotType: 0));
        Assert.Equal(ShipControlState.DroneRip, RadarOverlayRules.DroneRipMask);
    }

    [Fact]
    public void Friendly_ripcord_seconds_are_int_left_plus_one_and_cap_at_99999()
    {
        // (int)GetRipcordTimeLeft() + 1 (radarimage.cpp:1015-1030).
        Assert.Equal(7, RadarOverlayRules.FriendlyRipcordSeconds(
            false, true, true, startedLocalMs: 0, nowLocalMs: 4_000, durationSeconds: 10f));
        Assert.Equal(1, RadarOverlayRules.FriendlyRipcordSeconds(
            false, true, true, 0, 10_000, 10f));
        // Countdown has elapsed (or no ripcord / self / other side): no label.
        Assert.Null(RadarOverlayRules.FriendlyRipcordSeconds(false, true, true, 0, 10_001, 10f));
        Assert.Null(RadarOverlayRules.FriendlyRipcordSeconds(false, true, false, 0, 1_000, 10f));
        Assert.Null(RadarOverlayRules.FriendlyRipcordSeconds(true, true, true, 0, 1_000, 10f));
        Assert.Null(RadarOverlayRules.FriendlyRipcordSeconds(false, false, true, 0, 1_000, 10f));
        Assert.Equal(99999, RadarOverlayRules.FriendlyRipcordSeconds(
            false, true, true, 0, 0, 200_000f));
    }

    // ── DamageBucket threat/flash (radarimage.cpp:660-671, igc.h:4926-5057) ─

    [Fact]
    public void Damage_bucket_threat_lives_for_the_40s_window_and_flashes_250ms()
    {
        var tracker = new Game1.HostileDamageTracker();
        const long t0 = 10_000;
        tracker.Record(7, 25f, t0);
        // Bucket exists (totalDamage >= .5) and the first hit is in the bright phase.
        Assert.True(tracker.Threat(7, t0));
        Assert.True(tracker.Flash(7, t0));
        Assert.True(tracker.Flash(7, t0 + 249));
        Assert.False(tracker.Flash(7, t0 + 250));
        // An unrelated model has no bucket.
        Assert.False(tracker.Threat(8, t0));
        // 8 × 5 s slots: the damage is out of the window after 40 s
        // (DamageTrack::SwitchSlots deletes a bucket whose total drops below .5).
        Assert.False(tracker.Threat(7, t0 + 41_000));
    }

    [Fact]
    public void Damage_bucket_flash_cycles_while_damage_keeps_arriving()
    {
        var tracker = new Game1.HostileDamageTracker();
        const long t0 = 100_000;
        tracker.Record(4, 1f, t0);              // bright [100000,100250)
        tracker.Record(4, 1f, t0 + 400);        // dim phase → anchor moves to 100500
        Assert.False(tracker.Flash(4, t0 + 400));
        Assert.True(tracker.Flash(4, t0 + 500));
        Assert.False(tracker.Flash(4, t0 + 750));
        Assert.True(tracker.Threat(4, t0 + 500));   // bucket total is still in-window
    }

    // ── Own-ship mission stats (console.cpp:812-821) ───────────────────────

    private static ClientWorld WorldWithOwnAndKiller()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                5, 0, ControlData.Neutral, 1u, SimVec.Zero, Orientation.Identity,
                SimVec.Zero, new[] { 0f, 0f, 0f },
                hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 1f),
        });
        // Own PlayerInfo, seeded at join (native FedSrv.CPP:2085-2086 →
        // appmsg.cpp:2254-2256) with 4 kills and 2 deaths.
        world.Apply(new PlayerInfo
        {
            CharacterName = "Me", ISide = 1, ShipId = 5, PilotType = PlayerInfo.PilotTypePlayer,
            NKills = 4, NDeaths = 2,
        });
        world.Apply(new PlayerInfo
        {
            CharacterName = "Killer", ISide = 2, ShipId = 6, PilotType = PlayerInfo.PilotTypePlayer,
            NKills = 0, NDeaths = 0,
        });
        return world;
    }

    [Fact]
    public void PlayerInfo_carries_mission_kill_and_death_counters_through_the_wire()
    {
        var msg = new PlayerInfo
        {
            CharacterName = "pilot", ISide = 1, ShipId = 5,
            NKills = 12, NEjections = 2, NDeaths = 3,
        };
        var decoded = PlayerInfo.Decode(msg.Encode());
        Assert.Equal((short)12, decoded.NKills);
        Assert.Equal((short)2, decoded.NEjections);
        Assert.Equal((short)3, decoded.NDeaths);
    }

    [Fact]
    public void KillShip_credits_the_victims_death_and_the_launchers_kill()
    {
        var world = WorldWithOwnAndKiller();
        Assert.True(world.TryGetOwnMissionStats(out var kills, out var deaths));
        Assert.Equal((short)4, kills);
        Assert.Equal((short)2, deaths);

        // A credited kill of ship 5 by ship 6 (native appmsg.cpp:2766-2773,2807-2810).
        world.Apply(new KillShip
        {
            ShipId = 5, TypeCredit = (short)IgcObjectType.Ship, IdCredit = 6,
            SideIdKiller = 2, BKillCredit = true, BDeathCredit = true,
        });

        Assert.True(world.TryGetOwnMissionStats(out kills, out deaths));
        Assert.Equal((short)4, kills);       // our kills unchanged
        Assert.Equal((short)3, deaths);      // our mission deaths + 1
        Assert.True(world.Ships.TryGetValue(6, out var killer));
        Assert.Equal((short)1, killer.Player!.NKills);
        Assert.Equal((short)0, killer.Player.NDeaths);
    }

    [Fact]
    public void Lives_are_iLives_minus_deaths_and_badges_follow_the_native_gates()
    {
        // console.cpp:812 — iLives − MissionDeaths().
        Assert.Equal(3, HudConsoleFormat.RemainingLives(missionLives: 5, missionDeaths: 2));
        // console.cpp:814-817 — the lives badge only while remaining < 10.
        Assert.True(HudConsoleFormat.LivesVisible(9));
        Assert.False(HudConsoleFormat.LivesVisible(10));
        Assert.False(HudConsoleFormat.LivesVisible(MissionDef.UnlimitedLives - 2));
        // console.cpp:818-821 — kills only in a deathmatch (IsDeathMatchGame()).
        Assert.True(HudConsoleFormat.KillsVisible(deathmatch: true, kills: 4));
        Assert.False(HudConsoleFormat.KillsVisible(deathmatch: false, kills: 4));
        Assert.False(HudConsoleFormat.KillsVisible(deathmatch: true, kills: null));
    }

    // ── Continuous cloak (Igc/cloakigc.cpp:85-122; consoledata.cpp:480-489) ─

    [Fact]
    public void Own_ship_cloak_update_roundtrips_and_derives_native_values()
    {
        var msg = new OwnShipCloakUpdate { Cloaking = 0.4f };
        var decoded = OwnShipCloakUpdate.Decode(msg.Encode());
        Assert.Equal(0.4f, decoded.Cloaking, 5);
        Assert.Equal(60f, OwnShipCloakUpdate.StrengthPercent(decoded.Cloaking), 4);
        Assert.True(OwnShipCloakUpdate.IsCloaked(decoded.Cloaking));
        Assert.False(OwnShipCloakUpdate.IsCloaked(1f));
        Assert.Equal(0f, OwnShipCloakUpdate.StrengthPercent(1f), 4);
        Assert.Equal(100f, OwnShipCloakUpdate.StrengthPercent(0f), 4);
        // Clamp defensive reads of a corrupt value.
        Assert.Equal(100f, OwnShipCloakUpdate.StrengthPercent(-1f), 4);
        Assert.Equal(0f, OwnShipCloakUpdate.StrengthPercent(2f), 4);
    }

    private static HudFrame FrameWithCloak(float? strength, bool engagedBit) =>
        new(1f, 1f, 1f, 1f, 1f, 1f, 100f, 0f, false, false, false, 0,
            Flashing: false, WarpFlash: 0f, Mining: false, Cloaked: engagedBit,
            KillBonusPercent: 0f, RollDegrees: 0f,
            WeaponSystems: Array.Empty<WeaponStatusRow>(), IsAdriftLifepod: false,
            OxygenSecondsRemaining: 0, CloakStrengthPercent: strength);

    [Fact]
    public void Cloak_icon_persists_through_the_decloak_ramp_until_cloaking_reaches_one()
    {
        // Native IsCloaked = GetCloaking() < 1 (consoledata.cpp:563-575): the
        // state bit has already dropped, but the visible fraction is still < 1,
        // so the icon stays lit while the emitter ramps down.
        var dec = HudModel.Create(FrameWithCloak(strength: 30f, engagedBit: false));
        Assert.True(dec.Status.CloakActive);
        Assert.Equal(30f, dec.Status.CloakStrengthPercent);
        var fullyVisible = HudModel.Create(FrameWithCloak(strength: 0f, engagedBit: true));
        Assert.False(fullyVisible.Status.CloakActive);
        // Before the first 220 message the engaged bit remains the fallback.
        var fallback = HudModel.Create(FrameWithCloak(strength: null, engagedBit: true));
        Assert.True(fallback.Status.CloakActive);
        Assert.Null(fallback.Status.CloakStrengthPercent);
    }

    [Fact]
    public void Cloak_drained_marker_needs_an_active_ramp_and_low_energy()
    {
        var frame = FrameWithCloak(strength: 45f, engagedBit: true) with { EnergyFraction = 0.15f };
        var model = HudModel.Create(frame);
        Assert.True(model.Status.CloakActive);
        Assert.True(model.Status.CloakDrained);
        var healthy = HudModel.Create(frame with { EnergyFraction = 0.8f });
        Assert.False(healthy.Status.CloakDrained);
    }
}
