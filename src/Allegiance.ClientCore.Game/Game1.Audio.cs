using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The audio slice of <see cref="Game1"/> (P384 stage-7 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{


    /// <summary>
    /// P232 — drive the weapon-fire one-shot and the per-projectile in-flight
    /// ambient sounds from the local ship's live world state each frame. Under
    /// the Gate lock it prunes expired projectiles, gathers every projectile
    /// within audible range of the local ship (with its listener distance via
    /// <see cref="ProjectileEntity.PositionAt"/>), and hands that to the pure
    /// <see cref="ProjectileSoundPlanner"/>, whose Start/Update/Stop commands
    /// are replayed against <see cref="SoundManager"/>: a newly-spawned bolt
    /// that left OUR muzzle (spawn within
    /// <see cref="Audio.ProjectileAmbientSoundCurve.FireOriginRadius"/> of the
    /// listener — the port's own local-vs-other discrimination, since the P88
    /// wire carries no shooter id) also plays the weapon-fire one-shot,
    /// mirroring native's fFiringShot → single-shot path (trekigc.cpp:1005-1012);
    /// every in-range bolt gets a distance-scaled ambient loop that swells as it
    /// passes the cockpit and stops on expiry/recession (native's per-object
    /// AmbientSoundSite, trekigc.cpp:694-728). The console lines are the live
    /// audio-state surface (the same [monogame] audio convention as P213/P216),
    /// used for the P232 live proof.
    /// </summary>
    private void UpdateProjectileSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        long now = ClientNowMilliseconds;
        List<(ProjectileEntity Entity, float Distance)> inRange = new();

        lock (_session.Gate)
        {
            world.PruneExpiredProjectiles(now);
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                foreach (var pj in world.Projectiles)
                {
                    var pos = pj.PositionAt(now);
                    float dist = (pos - anchor).Length();
                    if (dist > Audio.ProjectileAmbientSoundCurve.AudibleRadius) continue;
                    inRange.Add((pj, dist));
                }
            }
        }

        foreach (var cmd in _projectileSoundPlanner.Plan(inRange))
        {
            switch (cmd.Action)
            {
                case Audio.ProjectileSoundAction.Start:
                    if (cmd.IsLocalFire)
                    {
                        snd.PlayWeaponFire();
                        Console.WriteLine("[monogame] audio: weapon fire");
                    }
                    snd.StartProjectileAmbient(cmd.Projectile, cmd.Volume);
                    Console.WriteLine("[monogame] audio: projectile ambient start");
                    break;
                case Audio.ProjectileSoundAction.Update:
                    snd.UpdateProjectileAmbient(cmd.Projectile, cmd.Volume);
                    break;
                case Audio.ProjectileSoundAction.Stop:
                    snd.StopProjectileAmbient(cmd.Projectile);
                    Console.WriteLine("[monogame] audio: projectile ambient stop");
                    break;
            }
        }
    }

    /// <summary>
    /// P236 — drive the three missile sound cues from the local ship's live world
    /// state each frame, mirroring the P232 projectile-sound wiring (same
    /// pure-planner pattern). Under the Gate lock it gathers every missile in the
    /// LOCAL ship's sector (the draw loop's own visibility filter — a missile in a
    /// neighbouring sector is separated by a warp and inaudible), pairs each with
    /// its listener distance and local-launch attribution, and hands that to the
    /// pure <see cref="Audio.MissileSoundPlanner"/>. Its commands are replayed
    /// against <see cref="Audio.SoundManager"/>: a newly-spawned missile that left
    /// OUR muzzle (spawn within
    /// <see cref="Audio.MissileAmbientSoundCurve.LaunchOriginRadius"/> — the port's
    /// local-vs-other discrimination, since native positions the launch sound at
    /// the firing ship) plays the launch one-shot; every in-sector missile gets a
    /// distance-scaled in-flight ambient loop; a missile that vanishes (impact /
    /// expiry / prune) plays the tiered explosion one-shot at its last-known
    /// detonation site. Console lines are the live audio-state surface (the same
    /// [monogame] audio convention as P213/P216/P232), used for the P236 live proof.
    /// </summary>
    private void UpdateMissileSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inSector = new List<Audio.MissileSoundSample>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                foreach (var (_, ms) in world.Missiles)
                {
                    if (ms.ClusterId != mySector) continue;
                    float dist = (ms.Position - anchor).Length();
                    inSector.Add(new Audio.MissileSoundSample(
                        ms.MissileId, ms.Position, dist,
                        Audio.MissileAmbientSoundCurve.IsLocalLaunch(dist)));
                }
            }
        }

        foreach (var cmd in _missileSoundPlanner.Plan(inSector))
        {
            switch (cmd.Action)
            {
                case Audio.MissileSoundAction.Launch:
                    snd.PlayMissileLaunch();
                    Console.WriteLine("[monogame] audio: missile launch");
                    break;
                case Audio.MissileSoundAction.AmbientStart:
                    snd.StartMissileAmbient(cmd.MissileId, cmd.Volume);
                    Console.WriteLine("[monogame] audio: missile ambient start");
                    break;
                case Audio.MissileSoundAction.AmbientUpdate:
                    snd.UpdateMissileAmbient(cmd.MissileId, cmd.Volume);
                    break;
                case Audio.MissileSoundAction.AmbientStop:
                    snd.StopMissileAmbient(cmd.MissileId);
                    Console.WriteLine("[monogame] audio: missile ambient stop");
                    break;
                case Audio.MissileSoundAction.Explosion:
                    snd.PlayMissileExplosion(cmd.Tier, cmd.Volume);
                    Console.WriteLine($"[monogame] audio: missile explosion (tier {cmd.Tier})");
                    break;
            }
        }
    }

    /// <summary>
    /// P264 — drive the incoming-missile warning loop from the server-streamed
    /// "an enemy missile is tracking YOU" state: each frame the pure
    /// <see cref="Audio.MissileWarningController"/> edge-detects arm / per-frame
    /// pitch / disarm from <c>ClientWorld.MissileTrack</c> (populated by the
    /// owner-only <c>MISSILE_TRACK_WARNING(208)</c>, the port of native's
    /// client-side check at <c>WinTrek/trekigc.cpp:5025-5071</c>), and the command
    /// is replayed against <see cref="Audio.SoundManager"/>: an arm starts the
    /// <c>missilelockonme</c> loop at the best-lock pitch, each armed frame
    /// refreshes the pitch ramp, a disarm stops it. Console lines are the live
    /// audio-state surface (the same [monogame] audio convention as P236).
    /// </summary>
    private void UpdateMissileWarning()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var track = world.MissileTrack;   // plain-data snapshot; no Gate needed
        var cmd = _missileWarningController.Plan(track.IsTracked, track.BestLock);
        if (cmd.Action == Audio.MissileWarningAction.None) return;

        snd.UpdateMissileWarning(cmd);
        switch (cmd.Action)
        {
            case Audio.MissileWarningAction.Arm:
                Console.WriteLine($"[monogame] audio: incoming missile warning ARMED (bestLock {cmd.BestLock:F3})");
                break;
            case Audio.MissileWarningAction.Disarm:
                Console.WriteLine("[monogame] audio: incoming missile warning CLEARED");
                break;
        }
    }

    /// <summary>
    /// P260 — drive the two probe sound cues from the live in-sector probe set
    /// each frame, mirroring the P236 missile wiring (same pure-planner pattern).
    /// Under the Gate lock it gathers every probe in the LOCAL ship's sector,
    /// pairs each with its listener distance and its own-side / own-deploy
    /// attribution, and hands that to the pure <see cref="Audio.ProbeSoundPlanner"/>.
    /// Its commands are replayed against <see cref="Audio.SoundManager"/>: a
    /// freshly-deployed probe that the LOCAL ship dropped (launcherShipId ==
    /// MyShipId) plays the deploy one-shot; every own-side in-sector probe gets a
    /// distance-scaled scanning ambient loop; a probe that vanishes (destroyed /
    /// expired / the player left its sector) stops its ambient. Console lines are
    /// the live audio-state surface (the same [monogame] audio convention as
    /// P232/P236), used for the P260 live proof.
    /// </summary>
    private void UpdateProbeSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inSector = new List<Audio.ProbeSoundSample>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                short mySide = world.MySideId ?? -1;
                foreach (var (_, pe) in world.Probes)
                {
                    if (pe.ClusterId != mySector) continue;
                    float dist = (pe.Position - anchor).Length();
                    inSector.Add(new Audio.ProbeSoundSample(
                        pe.ProbeId, pe.Position, dist,
                        IsOwnSide: pe.SideId == mySide,
                        IsOwnDeploy: pe.LauncherShipId == me));
                }
            }
        }

        foreach (var cmd in _probeSoundPlanner.Plan(inSector))
        {
            switch (cmd.Action)
            {
                case Audio.ProbeSoundAction.Deploy:
                    snd.PlayProbeDeploy();
                    Console.WriteLine("[monogame] audio: probe deploy");
                    break;
                case Audio.ProbeSoundAction.AmbientStart:
                    snd.StartProbeAmbient(cmd.ProbeId, cmd.Volume);
                    Console.WriteLine("[monogame] audio: probe ambient start");
                    break;
                case Audio.ProbeSoundAction.AmbientUpdate:
                    snd.UpdateProbeAmbient(cmd.ProbeId, cmd.Volume);
                    break;
                case Audio.ProbeSoundAction.AmbientStop:
                    snd.StopProbeAmbient(cmd.ProbeId);
                    Console.WriteLine("[monogame] audio: probe ambient stop");
                    break;
            }
        }
    }

    /// <summary>
    /// P237 — drive the warp ambient loop (native's <c>alephSound</c>) from the
    /// local ship's live world state each frame, mirroring the P232/P236
    /// pure-planner wiring. Under the Gate lock it gathers every warp in the
    /// LOCAL ship's sector that is within audible range (the same sector filter
    /// the draw loop uses — a warp in a neighbouring sector is a transit away and
    /// inaudible), pairs each with its listener distance, and hands that to the
    /// pure <see cref="Audio.WarpSoundPlanner"/>. Its commands are replayed
    /// against <see cref="Audio.SoundManager"/>: a warp that just came into range
    /// starts its looped <c>alephloop</c>; a warp still in range refreshes its
    /// volume to the current distance (the "increases as you get close" swell);
    /// a warp that left range (or the player left the sector) stops it. This is
    /// this port's distance-scaled approximation of native's per-warp
    /// <c>AmbientSoundSite</c> (<c>WinTrek/trekigc.cpp:1436-1437</c>), which
    /// starts <c>alephSound</c> (a <c>ThreeDSound(200, LoopingSound(...))</c> loop,
    /// sounddef.mdl:385) at each warp and lets the positional engine attenuate it.
    /// Console lines are the live audio-state surface (the same [monogame] audio
    /// convention as P213/P216/P232/P236), used for the P237 live proof.
    /// </summary>
    private void UpdateWarpSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inRange = new List<(short WarpId, float Distance)>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                foreach (var (warpId, wp) in world.Warps)
                {
                    if (wp.ClusterId != mySector) continue;
                    float dist = (Vec(wp.Position) - anchor).Length();
                    if (dist > Audio.WarpAmbientSoundCurve.AudibleRadius) continue;
                    inRange.Add((warpId, dist));
                }
            }
        }

        foreach (var cmd in _warpSoundPlanner.Plan(inRange))
        {
            switch (cmd.Action)
            {
                case Audio.WarpSoundAction.Start:
                    snd.StartWarpAmbient(cmd.WarpId, cmd.Volume);
                    Console.WriteLine($"[monogame] audio: warp ambient start (warp {cmd.WarpId}, vol {cmd.Volume:F3})");
                    break;
                case Audio.WarpSoundAction.Update:
                    snd.UpdateWarpAmbient(cmd.WarpId, cmd.Volume);
                    break;
                case Audio.WarpSoundAction.Stop:
                    snd.StopWarpAmbient(cmd.WarpId);
                    Console.WriteLine($"[monogame] audio: warp ambient stop (warp {cmd.WarpId})");
                    break;
            }
        }
    }

    /// <summary>P267 — drive the station exterior ambient loops (a looped
    /// <c>shipout</c> per station) from the live in-sector stations within
    /// audible range, mirroring the warp ambient wiring (P237) exactly: under
    /// the Gate lock it gathers every station in the local ship's sector with
    /// its listener distance, hands that to the pure
    /// <see cref="Audio.StationSoundPlanner"/>, and replays its
    /// Start/Update/Stop commands against <see cref="Audio.SoundManager"/>.
    /// Native attaches a per-object <c>AmbientSoundSite</c> to every station
    /// (<c>trekigc.cpp:1465-1468</c>), so this is a distance-based positional
    /// ambient — the real default is <c>shipout</c> (see
    /// <see cref="Audio.StationAmbientSoundCurve"/>). The console lines are the
    /// live audio-state surface.</summary>
    private void UpdateStationSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        var inRange = new List<(short StationId, float Distance)>();
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && own.Latest is { } meUp)
            {
                var anchor = meUp.Position;
                short mySector = own.SectorId;
                foreach (var (stationId, st) in world.Stations)
                {
                    if (st.ClusterId != mySector) continue;
                    float dist = (Vec(st.Position) - anchor).Length();
                    if (dist > Audio.StationAmbientSoundCurve.AudibleRadius) continue;
                    inRange.Add((stationId, dist));
                }
            }
        }

        foreach (var cmd in _stationSoundPlanner.Plan(inRange))
        {
            switch (cmd.Action)
            {
                case Audio.StationSoundAction.Start:
                    snd.StartStationAmbient(cmd.StationId, cmd.Volume);
                    Console.WriteLine($"[monogame] audio: station ambient start (station {cmd.StationId}, vol {cmd.Volume:F3})");
                    break;
                case Audio.StationSoundAction.Update:
                    snd.UpdateStationAmbient(cmd.StationId, cmd.Volume);
                    break;
                case Audio.StationSoundAction.Stop:
                    snd.StopStationAmbient(cmd.StationId);
                    Console.WriteLine($"[monogame] audio: station ambient stop (station {cmd.StationId})");
                    break;
            }
        }
    }

    /// <summary>P267 — drain the world's one-shot sound events (station
    /// destroyed/captured, development completion) into the audio manager,
    /// replaying each exactly once with a console line for the live proof.
    /// Driven by the real <c>STATION_DESTROYED(59)</c>/<c>STATION_CAPTURE(58)</c>/
    /// <c>DEV_COMPLETED(8)</c> wire events (see ClientWorld's Apply handlers).</summary>
    private void UpdateSoundEvents()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        List<ClientSoundEvent> events;
        lock (_session.Gate)
            events = world.TakeSoundEvents();

        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case ClientSoundEventKind.LastEnemyBase:
                    if (!LocalSpectating && !SpectatorCameraActive) snd.PlayVoice("vo_player_foundenemybase");
                    break;
                case ClientSoundEventKind.ChatVoiceOver:
                    if (ev.Voice is { } voice) snd.PlayVoice(voice);
                    break;
                case ClientSoundEventKind.StationDestroyed:
                case ClientSoundEventKind.StationEnemyDestroyed:
                case ClientSoundEventKind.StationCaptured:
                case ClientSoundEventKind.StationEnemyCaptured:
                    snd.PlayStationEvent(ev, _shopCatalog?.StationTypes);
                    Console.WriteLine($"[monogame] audio: {ev.Kind} (station {ev.Id}, type {ev.StationTypeId})");
                    break;
                case ClientSoundEventKind.DevCompleted:
                    snd.PlayDevComplete();
                    Console.WriteLine($"[monogame] audio: development complete chime (dev {ev.Id})");
                    break;
                case ClientSoundEventKind.RipcordAborted:
                    // P414 — the local player's own ripcord countdown was
                    // cancelled; play the single "ripcord aborted" VO cue.
                    snd.PlayRipcordAborted();
                    Console.WriteLine($"[monogame] audio: ripcord aborted (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.SectorHighlighted:
                    snd.PlayCommandMessage();
                    break;
                case ClientSoundEventKind.PlayerWaiting:
                    snd.PlayVoice(Rendering.LobbyStatusLayout.PlayerWaitingWave);
                    break;
                case ClientSoundEventKind.TeleportArrived:
                    // P638 — our teleport dock request was accepted; native
                    // plays the personalJumpSound (appmsg.cpp:1901), mapped to
                    // the jumpin one-shot (native jumpSound, SoundManager.PlayJump).
                    snd.PlayJump();
                    Console.WriteLine($"[monogame] audio: teleport arrived (station {ev.Id})");
                    break;
                // ── P710 — wealth announcements (clintlib/appmsg.cpp) ──────
                case ClientSoundEventKind.PaydayReceived:
                    snd.PlayPayday();
                    Console.WriteLine("[monogame] audio: payday");
                    break;
                case ClientSoundEventKind.PickupPowerup:
                    snd.PlayPickupPowerup();
                    Console.WriteLine("[monogame] audio: powerup pickup");
                    break;
                case ClientSoundEventKind.PickupDevelopment:
                    snd.PlayPickupDevelopment();
                    Console.WriteLine("[monogame] audio: development pickup");
                    break;
                case ClientSoundEventKind.PickupCash:
                    snd.PlayPickupCash();
                    Console.WriteLine("[monogame] audio: cash pickup");
                    break;
                case ClientSoundEventKind.PickupPart:
                    // P735 — SIM-0268: part pickup (native pickUpPartSound,
                    // shipIGC.h:1307; wave hoover, sounddef.mdl:278).
                    snd.PlayPickupPart();
                    Console.WriteLine("[monogame] audio: part pickup");
                    break;
                // ── P710 — incoming-message notification cues
                // (trekigc.cpp:4536-4560) ───────────────────────────────────
                case ClientSoundEventKind.ChatCommand:
                    snd.PlayChatCommand();
                    break;
                case ClientSoundEventKind.ChatCommander:
                    snd.PlayChatCommander();
                    break;
                case ClientSoundEventKind.ChatPersonal:
                    snd.PlayChatPersonal();
                    break;
                case ClientSoundEventKind.ChatRegular:
                    snd.PlayChatRegular();
                    break;
                case ClientSoundEventKind.ChatOffline:
                    snd.PlayChatRegular(offline: true);
                    break;
                // ── P711 — ship low-status / combat feedback one-shots ──────
                case ClientSoundEventKind.HullLow:
                    snd.PlayHullLow();
                    Console.WriteLine("[monogame] audio: hull low");
                    break;
                case ClientSoundEventKind.AmmoLow:
                    snd.PlayAmmoLow();
                    Console.WriteLine("[monogame] audio: ammo low");
                    break;
                case ClientSoundEventKind.FuelLow:
                    snd.PlayFuelLow();
                    Console.WriteLine("[monogame] audio: fuel low");
                    break;
                case ClientSoundEventKind.EnergyLow:
                    snd.PlayEnergyLow();
                    Console.WriteLine("[monogame] audio: energy low");
                    break;
                case ClientSoundEventKind.NoAmmo:
                    snd.PlayNoAmmo();
                    Console.WriteLine("[monogame] audio: no ammo attempt");
                    break;
                case ClientSoundEventKind.NoFuel:
                    snd.PlayNoFuel();
                    Console.WriteLine("[monogame] audio: no fuel attempt");
                    break;
                case ClientSoundEventKind.RipcordDenied:
                    snd.PlayNoRipcord();
                    Console.WriteLine("[monogame] audio: ripcord denied (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.CommanderElected:
                    snd.PlayMadeCommander();
                    Console.WriteLine("[monogame] audio: you became team leader");
                    break;
                case ClientSoundEventKind.InvestorReceived:
                    snd.PlayMadeInvestor();
                    Console.WriteLine("[monogame] audio: first investor");
                    break;
                case ClientSoundEventKind.EnemyFlagStolen:
                    snd.PlayStolenEnemyFlag();
                    Console.WriteLine("[monogame] audio: enemy flag stolen");
                    break;
                case ClientSoundEventKind.ArtifactFound:
                    snd.PlayArtifactFound();
                    Console.WriteLine("[monogame] audio: artifact found");
                    break;
                case ClientSoundEventKind.OurFlagStolen:
                    snd.PlayStolenFlag();
                    Console.WriteLine("[monogame] audio: our flag stolen");
                    break;
                case ClientSoundEventKind.SectorJump:
                    // P716 — the own ship crossed a warp; native's jumpSound
                    // (trekigc.cpp:3998,4415) is the same wavein as the teleport
                    // cue, so it rides SoundManager.PlayJump.
                    snd.PlayJump();
                    Console.WriteLine($"[monogame] audio: sector jump (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.MineDeployed:
                    // P717 — the own ship deployed a mine (native deployMineSound,
                    // clintlib.cpp:3253; wave dropmine, sounddef.mdl:368).
                    snd.PlayMineDeploy();
                    break;
                case ClientSoundEventKind.DeployChaff:
                    // P755 — the own ship deployed chaff (native deployChaffSound,
                    // clintlib.cpp:3249; wave dropobject, sounddef.mdl:367).
                    snd.PlayChaffDeploy();
                    Console.WriteLine($"[monogame] audio: chaff deploy (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.ShieldsOffline:
                    // P717 — the own shield was knocked offline (native
                    // salShieldsOfflineSound, shieldIGC.cpp:186; vo_sal_shieldsoffline).
                    snd.PlayShieldsOffline();
                    break;
                case ClientSoundEventKind.ShieldActivate:
                    // P741 — the own shield powered back up past 5% (native
                    // m_typeData->activateSound, shieldIGC.cpp:94; shieldup).
                    snd.PlayShieldActivate();
                    Console.WriteLine("[monogame] audio: shield power up");
                    break;
                case ClientSoundEventKind.ShieldDeactivate:
                    // P741 — the own shield went down to overload damage (native
                    // m_typeData->deactivateSound, shieldIGC.cpp:185; noshld),
                    // alongside the shields-offline VO.
                    snd.PlayShieldDeactivate();
                    Console.WriteLine("[monogame] audio: shield power down");
                    break;
                case ClientSoundEventKind.CloakEngaged:
                    // P735 — the own ship's cloak engaged (native
                    // salCloakEngageSound, shipIGC.cpp:1608; vo_sal_cloakengage,
                    // sounddef.mdl:807).
                    snd.PlayCloakEngage();
                    Console.WriteLine($"[monogame] audio: cloak engage (ship {ev.Id})");
                    break;
                // ── P719 — station/economy + loadout one-shots ─────────────
                case ClientSoundEventKind.Docked:
                    snd.PlayDocked();
                    Console.WriteLine("[monogame] audio: docked");
                    break;
                case ClientSoundEventKind.WelcomeHome:
                    snd.PlayWelcomeHome();
                    Console.WriteLine("[monogame] audio: welcome home");
                    break;
                case ClientSoundEventKind.Boarded:
                    snd.PlayBoarded();
                    Console.WriteLine($"[monogame] audio: player boarded your ship (ship {ev.Id})");
                    break;
                // ── P736 — presence / side-change salutations ───────────────
                case ClientSoundEventKind.SalQuit:
                    snd.PlaySalQuit();
                    Console.WriteLine($"[monogame] audio: teammate quit (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.SalChangeSides:
                    snd.PlaySalChangeSides();
                    Console.WriteLine($"[monogame] audio: teammate switching sides (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.SalMia:
                    snd.PlaySalMia();
                    Console.WriteLine($"[monogame] audio: teammate MIA (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.SalEnemyLeaves:
                    snd.PlaySalEnemyLeaves();
                    Console.WriteLine($"[monogame] audio: enemy leaves (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.SalRecruitsArrived:
                    snd.PlaySalRecruitsArrived();
                    Console.WriteLine($"[monogame] audio: recruit joined your team (ship {ev.Id})");
                    break;
                case ClientSoundEventKind.SalEnemyJoiners:
                    snd.PlaySalEnemyJoiners();
                    Console.WriteLine($"[monogame] audio: enemy joined (ship {ev.Id})");
                    break;
                // ── P737 — own-ship reload / loadout feedback ───────────────
                case ClientSoundEventKind.SalReloadingAmmo:
                    snd.PlaySalReloadingAmmo();
                    Console.WriteLine("[monogame] audio: reloading ammo");
                    break;
                case ClientSoundEventKind.SalReloadingFuel:
                    snd.PlaySalReloadingFuel();
                    Console.WriteLine("[monogame] audio: reloading fuel");
                    break;
                case ClientSoundEventKind.SalReloadingMissiles:
                    snd.PlaySalReloadingMissiles();
                    Console.WriteLine("[monogame] audio: reloading missiles");
                    break;
                case ClientSoundEventKind.SalReloadingChaff:
                    snd.PlaySalReloadingChaff();
                    Console.WriteLine("[monogame] audio: reloading chaff");
                    break;
                case ClientSoundEventKind.SalReloadingDispenser:
                    snd.PlaySalReloadingDispenser();
                    Console.WriteLine("[monogame] audio: reloading dispenser");
                    break;
                case ClientSoundEventKind.StartReload:
                    snd.PlayStartReload();
                    Console.WriteLine("[monogame] audio: reload started");
                    break;
            }
        }
    }

    /// <summary>
    /// P711 — the per-frame driver for the ship status alarm EDGE detection
    /// (SIM-0361..0366, trekigc.cpp:1264-1275) and the two loop gates — the
    /// ripcord-active loop (SIM-0368, :1287) and the mining loop (SIM-0370,
    /// :1293). <see cref="ClientWorld.UpdateOwnStatusAlarms"/> runs the pure
    /// <see cref="ShipStatusAlarms.Classify"/> edge detector under the gate and
    /// records the one-shot events (drained by <see cref="UpdateSoundEvents"/>);
    /// the two loop gates are read here from <c>ClientWorld.RipcordActive</c> /
    /// <c>MiningAsteroidId</c> and replayed against SoundManager. Called once
    /// per frame with the audio pass. Must run under the gate lock when reading
    /// world state (same discipline as the other per-frame sound drivers).
    /// </summary>
    private void UpdateShipStatusAlarms()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        bool ripcordActive;
        bool mining;
        lock (_session.Gate)
        {
            world.UpdateOwnStatusAlarms();
            ripcordActive = world.RipcordActive;
            mining = world.MiningAsteroidId != -1;
        }

        snd.UpdateRipcordLoop(ripcordActive);
        snd.UpdateMiningLoop(mining);
    }

    /// <summary>
    /// P463 — drain the world's projectile hit SOUND events (one per real
    /// <c>PROJECTILE_HULL_HIT(213)</c> broadcast; <see cref="HitSoundEntity"/>)
    /// into the audio manager, playing at most one cue per target per frame (the
    /// native <c>m_bPlayedHitSoundThisFrame</c> guard, applied by
    /// <see cref="Audio.HitSoundSelector.Select"/>), classified my/other ×
    /// shield/hull. "My" cues (target == local ship) are cockpit-range one-shots
    /// at full volume; "other" cues are positional and attenuate by listener
    /// distance (<see cref="Audio.HitSoundCurve"/>), the same treatment the other
    /// world sounds get. Filtered to the local ship's own sector like the other
    /// positional layers. Console lines are the live audio-state surface (the
    /// same [monogame] audio convention as P232/P236), used for the P463 live
    /// proof.
    /// </summary>
    private double _hudHitUntil;
    private void UpdateHitSounds()
    {
        var world = _session.World;

        List<HitSoundEntity> events;
        short? myShipId;
        short? mySector;
        SimVec? anchor;
        lock (_session.Gate)
        {
            events = world.TakeHitSoundEvents();
            myShipId = world.MyShipId;
            mySector = null;
            anchor = null;
            if (myShipId is { } me && world.Ships.TryGetValue(me, out var own) && own.Latest is { } up)
            {
                mySector = own.SectorId;
                anchor = up.Position;
            }
        }
        if (events.Count == 0) return;
        if (events.Any(e => e.SourceId == myShipId && e.TargetId != myShipId && e.ClusterId == mySector))
            _hudHitUntil = _nowSeconds + .16;
        if (_sound is not { } snd) return;

        // Only hits in the local ship's own sector are audible (the same cluster
        // gate as the spark/explosion renderers). Without a local anchor there is
        // no listener, so positional "other" cues are skipped and "my" is moot.
        var inSector = mySector is { } sec
            ? events.Where(e => e.ClusterId == sec)
            : Enumerable.Empty<HitSoundEntity>();

        foreach (var (targetId, kind, pos) in Audio.HitSoundSelector.Select(inSector, myShipId))
        {
            float volume;
            if (kind is Audio.HitSoundKind.MyShield or Audio.HitSoundKind.MyHull)
            {
                volume = Audio.HitSoundCurve.MyVolume;          // cockpit-range, full
            }
            else
            {
                if (anchor is not { } a) continue;              // no listener → can't place it
                float dist = (pos - a).Length();
                volume = Audio.HitSoundCurve.OtherVolume(dist);
                if (volume <= 0f) continue;                     // out of audible range
            }
            snd.PlayHitSound(kind, volume);
            Console.WriteLine($"[monogame] audio: hit sound {kind} (target {targetId}, vol {volume:F3})");
        }
    }

    /// <summary>
    /// P463c — drain the world's COLLISION events (one per real
    /// <c>COLLISION_EVENT(216)</c> broadcast; <see cref="CollisionEntity"/>) into
    /// the audio pass: play the positional collision one-shot once <b>per
    /// party</b> (native <c>Igc/shipIGC.cpp:1193</c> and the sibling branch for
    /// the other party each call <c>PlaySoundEffect(collisionSound)</c>; a
    /// ship-ship collision therefore produces two sources at the shared impact
    /// point, selected by <see cref="Audio.CollisionFeedback.SelectSoundSources"/>),
    /// and — when the local ship IS one of the two parties — feed that party's
    /// jiggle intensity into P449b's <see cref="Rendering.FlightJiggle"/> via
    /// <see cref="Rendering.FlightJiggle.SetJiggle"/> (the "own ship only" rule:
    /// <see cref="Audio.CollisionFeedback.JiggleForOwnShip"/> returns 0 for a
    /// witnessed collision, so an observer's camera never shakes). Filtered to
    /// the local ship's own sector like the other positional layers. Console
    /// lines are the live audio-state surface (the same [monogame] convention as
    /// P463), used for the P463c live proof.
    /// </summary>
    private void UpdateCollisionSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        List<CollisionEntity> events;
        short? myShipId;
        short? mySector;
        SimVec? anchor;
        lock (_session.Gate)
        {
            events = world.TakeCollisionEvents();
            myShipId = world.MyShipId;
            mySector = null;
            anchor = null;
            if (myShipId is { } me && world.Ships.TryGetValue(me, out var own) && own.Latest is { } up)
            {
                mySector = own.SectorId;
                anchor = up.Position;
            }
        }
        if (events.Count == 0) return;

        foreach (var c in events)
        {
            // Own-ship-only jiggle feed (P449b). JiggleForOwnShip is 0 when the
            // local ship is not a party, so a witnessed collision never shakes.
            float jiggle = Audio.CollisionFeedback.JiggleForOwnShip(
                c.Party1Id, c.Party2Id, c.Jiggle1, c.Jiggle2, myShipId);
            if (jiggle > 0f)
            {
                _flightJiggle.SetJiggle(jiggle);
                Console.WriteLine($"[monogame] audio: collision jiggle {jiggle:F3} (own ship {myShipId})");
            }
        }

        // Positional collision sound, once per party in the local sector.
        // No local anchor -> no listener -> nothing to place.
        if (anchor is { } a)
        {
            foreach (var src in Audio.CollisionFeedback.SelectSoundSources(events, mySector))
            {
                float dist = (src.Position - a).Length();
                float volume = Audio.CollisionSoundCurve.Volume(dist);
                if (volume <= 0f) continue;
                snd.PlayCollisionSound(volume);
                Console.WriteLine($"[monogame] audio: collision sound (party {src.PartyId}, vol {volume:F3})");
            }
        }
    }

    /// <summary>
    /// P574 — drain the world's WORLD-EXPLOSION SOUND events (one per real
    /// <c>EXPLOSION_EVENT(207)</c> broadcast; <see cref="ExplosionSoundEntity"/>)
    /// into the audio manager, giving every world explosion the native tiered
    /// positional sound native plays for every <c>AddExplosion</c> in the
    /// player's current cluster (<c>trekigc.cpp:86-101</c>): the tier
    /// (large/medium/small) comes from the blast <c>scale</c> via
    /// <see cref="Audio.ExplosionSoundTierMapper.ForScale"/> (so a mine pop,
    /// scale 1.5, is the small <c>ef120</c> cue and a ship death, scale =
    /// 2·radius, usually the medium <c>ef24</c>), attenuated by listener
    /// distance via <see cref="Audio.ExplosionSoundCurve"/> and filtered to the
    /// local ship's own sector (native's cluster gate). <b>Missile</b>
    /// detonations are skipped — they are already voiced by
    /// <c>MissileSoundPlanner</c> (which detects a missile vanishing from
    /// <c>world.Missiles</c>), so voicing the same ExplosionEvent here would
    /// double-play it. Console lines are the live audio-state surface (the same
    /// [monogame] convention as P236/P463).
    /// </summary>
    private void UpdateExplosionSounds()
    {
        var world = _session.World;
        if (_sound is not { } snd) return;

        List<ExplosionSoundEntity> events;
        short? mySector;
        SimVec? anchor;
        lock (_session.Gate)
        {
            events = world.TakeExplosionSoundEvents();
            mySector = null;
            anchor = null;
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own) && own.Latest is { } up)
            {
                mySector = own.SectorId;
                anchor = up.Position;
            }
        }
        if (events.Count == 0) return;

        foreach (var (eType, tier, volume) in Audio.ExplosionSoundRouter.Resolve(events, mySector, anchor))
        {
            snd.PlayWorldExplosion(tier, volume);
            Console.WriteLine($"[monogame] audio: world explosion (type {eType}, tier {tier}, vol {volume:F3})");
        }
    }
}
