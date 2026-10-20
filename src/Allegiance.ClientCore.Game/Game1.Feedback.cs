using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P578 — the MonoGame-side consumer of the eject/death/rescue feedback the
/// <see cref="ClientWorld"/> queues on <c>KILL_SHIP(105)</c> /
/// <c>PLAYER_RESCUED(16)</c> (see <see cref="EjectFeedback"/>): it drains them
/// each frame, plays the native critical/non-critical message cues, keeps the
/// transient HUD tip lines (expiring after native's <c>fTipTextDuration =
/// 7.5 s</c>, console.cpp:913), and turns each "gloat camera" request into a
/// 5-second override camera aimed at the killer (native
/// <c>WinTrekClient::EjectPlayer</c> → <c>OverrideCamera(now, pcredit, true)</c>,
/// whose override lasts <c>now + 5.0f</c>, WinTrek.cpp:6501). Partial class
/// sharing the <c>Game1</c> type.
/// </summary>
public sealed partial class Game1
{
    /// <summary>P578 — how long a HUD feedback line stays visible before it
    /// fades (native <c>fTipTextDuration = Training ? 30.0f : 7.5f</c>,
    /// console.cpp:913 — the non-training value this client uses).</summary>
    private const double TipDurationSeconds = Rendering.HudConsoleFormat.CriticalTipDurationSeconds;

    /// <summary>P578/HUD-9 — the three ordinary tip slots, newest first,
    /// retained until overwritten (native <c>TipText/TipText1/TipText2</c> are
    /// not cleared by the expiry path, console.cpp:913-931; dialog.mdl:5347-5349).</summary>
    private readonly string[] _ordinaryTips = new string[Rendering.HudConsoleFormat.OrdinaryTipSlots];

    /// <summary>P578/HUD-9 — the up-to-four critical tip slots, oldest first
    /// (the renderer draws the newest at the bottom, matching native slot 0),
    /// each expiring after <see cref="TipDurationSeconds"/> (native
    /// <c>CriticalTipText0..3</c>, console.cpp:726-730, 913-931).</summary>
    private readonly List<(string Text, double ExpiresAt)> _criticalTips = new();

    /// <summary>P578 — the override camera's tracked world point (native
    /// <c>cmExternalOverride</c> tracks a fixed target position,
    /// WinTrek.cpp:7199-7206), set by <see cref="RequestOverrideCamera"/> and
    /// read by <c>TryOverrideTargetPos</c> in <see cref="Game1.Director"/>.
    /// Null when no override is active (the override then falls back to the
    /// nearest explosion).</summary>
    private SimVec? _overrideTarget;
    private float _overrideRadius;
    private double _overrideUntil;

    /// <summary>
    /// P578 — drain the world's pending eject/death/rescue feedback each frame:
    /// play the native cue for each line, add it to the HUD tip list, prune
    /// expired tips, and hand each pending camera-override request to
    /// <see cref="RequestOverrideCamera"/>. Mirrors the audio-drain pattern of
    /// <c>UpdateSoundEvents</c> (Game1.Audio.cs) — called once per frame under
    /// the gate lock so every wire event is voiced/shown exactly once.
    /// </summary>
    private void UpdateFeedback()
    {
        var world = _session.World;

        List<FeedbackLine> lines;
        List<CameraOverrideRequest> overs;
        lock (_session.Gate)
        {
            lines = world.TakeFeedbackLines();
            overs = world.TakeCameraOverrideRequests();
        }

        // P594 — announce a newly-arrived QUEUED order (a teammate's "pick me
        // up" rescue offer, streamed by the server on c_cidPickup) as a HUD tip
        // + the native "new command" chime, exactly once per order. The player
        // then presses Accept to select/store it. P675's distinct Execute action
        // also engages the intercept autopilot when the ship can fly.
        short qid;
        lock (_session.Gate)
            qid = world.MyQueuedCommand is { } qc ? qc.CommandId : Allegiance.Core.Wire.OrderChange.CidNone;
        if (qid != Allegiance.Core.Wire.OrderChange.CidNone && qid != _queuedCmdAnnouncedId)
        {
            _queuedCmdAnnouncedId = qid;
            _sound?.PlayNonCriticalMessage();   // native "new command" cue
            Console.WriteLine($"[monogame] queued order announced: commandID={qid} — press [Home] to accept");
            PushOrdinaryTip(
                qid == Allegiance.Core.Wire.OrderChange.CidPickup
                    ? "NEW ORDER: pick up a teammate — [Home] to accept"
                    : "NEW COMMAND RECEIVED — [Home] to accept");
        }
        else if (qid == Allegiance.Core.Wire.OrderChange.CidNone)
        {
            _queuedCmdAnnouncedId = -1;   // pending order cleared → a new one re-announces
        }

        foreach (var line in lines)
        {
            if (line.IsCritical)
            {
                if (!line.SoundAlreadyQueued) _sound?.PlayCriticalMessage();
                Console.WriteLine($"[eject] CRITICAL: {line.Text}");
            }
            else
            {
                if (!line.SoundAlreadyQueued) _sound?.PlayNonCriticalMessage();
                Console.WriteLine($"[eject] tip: {line.Text}");
            }
            // P578 — rescue lines carry a second native cue (rescuer →
            // rescuePlayerSound, rescuee → jumpSound; appmsg.cpp:325,339).
            switch (line.Extra)
            {
                case FeedbackExtraSound.RescuePlayer:
                    _sound?.PlayRescue();
                    Console.WriteLine("[eject] rescue cue (hoover)");
                    break;
                case FeedbackExtraSound.Jump:
                    _sound?.PlayJump();
                    Console.WriteLine("[eject] jump cue (jumpin)");
                    break;
            }
            PushTip(line.Text, line.IsCritical);
        }

        foreach (var over in overs)
        {
            if (TryResolveKillerPos(over.FocusShipId, out var pos, out var radius))
            {
                RequestOverrideCamera(pos, radius, over.DurationSeconds);
                Console.WriteLine($"[camera] override -> ship {over.FocusShipId} ({over.DurationSeconds} s)");
            }
            else
            {
                Console.WriteLine($"[eject] camera override requested for killer {over.FocusShipId} but unresolvable — skipped");
            }
        }

        // Prune expired critical tips (native expires each critical slot after
        // the duration, console.cpp:913-931). Ordinary tips are retained.
        _criticalTips.RemoveAll(t => _nowSeconds >= t.ExpiresAt);

        // Revert a finished override camera back to the normal chase view.
        UpdateOverrideCameraExpiry();
    }

    /// <summary>P578/HUD-9 — route one feedback line into its native slot: the
    /// three retained ordinary slots or the four expiring critical slots.</summary>
    private void PushTip(string text, bool critical)
    {
        if (critical)
        {
            _criticalTips.Add((text, _nowSeconds + TipDurationSeconds));
            if (_criticalTips.Count > Rendering.HudConsoleFormat.CriticalTipSlots)
                _criticalTips.RemoveAt(0);
        }
        else
        {
            PushOrdinaryTip(text);
        }
    }

    /// <summary>P578/HUD-9 — push an ordinary tip into the newest-first three
    /// slots, shifting older lines down (native PostText(false) → TipText
    /// newest-first shift, console.cpp:723-725).</summary>
    private void PushOrdinaryTip(string text)
    {
        for (int i = Rendering.HudConsoleFormat.OrdinaryTipSlots - 1; i > 0; i--)
            _ordinaryTips[i] = _ordinaryTips[i - 1];
        _ordinaryTips[0] = text;
    }



    /// <summary>P578 — resolve the credited killer's live world position and a
    /// framing radius (half its hull length when known, else a fighter-scale
    /// 25 u) for the override camera. Falls back to native's position-behind-
    /// the-victim behaviour (WinTrek.cpp:6511-6514) when the killer can't be
    /// found (e.g. it already left view) — false only when we can't even locate
    /// the victim. Returns the override anchor (killer position or fallback)
    /// and its radius.</summary>
    private bool TryResolveKillerPos(short killerId, out SimVec pos, out float radius)
    {
        pos = default; radius = 25f;
        lock (_session.Gate)
        {
            if (_session.World.Ships.TryGetValue(killerId, out var killer) && killer.Latest is { } ku)
            {
                pos = ku.Position;
                float len = HullLength(killer.HullId);
                if (len > 0f) radius = 0.5f * len;
                return true;
            }
            // Killer not in the world — frame the victim (our own ship) from
            // behind, native's no-target fallback (WinTrek.cpp:6511-6514).
            if (_session.World.MyShipId is { } me
                && _session.World.Ships.TryGetValue(me, out var own) && own.Latest is { } ou)
            {
                pos = ou.Position;
                return true;
            }
        }
        return false;
    }

    /// <summary>P578 — begin the scripted "gloat camera": track a fixed world
    /// point (the killer) for <paramref name="durationSeconds"/>, then revert
    /// to the normal chase view. Mirrors native's <c>OverrideCamera(now,
    /// pmodelTarget, true)</c> (WinTrek.cpp:6480-6534) — <c>cmExternalOverride</c>
    /// + <c>m_timeOverrideStop</c>. Any manual camera key / director input
    /// supersedes it (via the existing camera toggles).</summary>
    private void RequestOverrideCamera(SimVec targetPos, float radius, double durationSeconds)
    {
        _overrideTarget = targetPos;
        _overrideRadius = radius;
        _overrideUntil = _nowSeconds + durationSeconds;
        _cameraMode = CameraMode.ExternalOverride;
        // Fresh framing — reset the remembered external-camera orientation so
        // the override doesn't inherit a stale spin from a prior mode.
        _externalCamAlignedFor = null;
        Console.WriteLine($"[eject] override camera ON -> target ({targetPos.X:0.#},{targetPos.Y:0.#},{targetPos.Z:0.#}) for {durationSeconds:0.#} s");
    }

    /// <summary>P578 — revert an expired override camera to the normal chase
    /// view (native <c>EndOverrideCamera</c> at <c>m_timeOverrideStop</c>,
    /// WinTrek.cpp:6536-6547 / 7673-7678). Called every frame so the swing
    /// always ends.</summary>
    private void UpdateOverrideCameraExpiry()
    {
        if (_cameraMode != CameraMode.ExternalOverride) return;
        if (_nowSeconds >= _overrideUntil)
        {
            _cameraMode = CameraMode.Chase;
            _overrideTarget = null;
            _externalCamAlignedFor = null;
            Console.WriteLine("[eject] override camera END -> chase");
        }
    }
}
