using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P572 — the client-side automation glue that turns the pure
/// <see cref="LiveProof.AutoProofMachine"/> (sequencing + timing) into real
/// game actions in a NORMAL WINDOWED client. Partial class sharing the
/// <c>Game1</c> type. Nothing here is off by default, and everything is
/// client-only: requesting a side, toggling ready, buying a loadout, issuing a
/// chat-goto buoy and engaging the client's own autopilot are exactly the
/// player actions a human would take to frame a live proof.
/// </summary>
public sealed partial class Game1
{
    // ── per-frame feed + drain (called from Update before any handler) ────────

    /// <summary>Feed the world observations into the machine, advance its clock,
    /// and route every action that came due into the per-purpose pending slots
    /// the state handlers consume. Actions are consumed from the machine exactly
    /// once, so the same shot / goto / step never re-fires.</summary>
    private void FeedAutoProofObservations()
    {
        if (_auto is null)
            return;

        double nowSec = Environment.TickCount64 / 1000.0;

        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not null)
                _auto.NoteSeated();
            bool docked = world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var ent) && ent.StationId != -1;
            if (docked) _auto.NoteDocked();
            else _auto.NoteUndocked();
            if (_state == ClientState.Flying)
                _auto.NoteLaunch();
        }

        _auto.Tick(nowSec);

        foreach (var a in _auto.ConsumeDue())
        {
            switch (a.Kind)
            {
                case LiveProof.AutoActionKind.SetCamera:
                case LiveProof.AutoActionKind.HideHud:
                    // Applied once in the constructor (camera + HUD are static
                    // for the run); nothing further to do.
                    break;
                case LiveProof.AutoActionKind.PickSide:
                    // Wired directly from the plan in UpdateSideSelect (roster
                    // readiness is its own gate); nothing to buffer here.
                    break;
                case LiveProof.AutoActionKind.SendReady:
                    // Wired directly from the plan in UpdateReadyCheck (seating
                    // is its own gate); nothing to buffer here.
                    break;
                case LiveProof.AutoActionKind.LaunchHull:
                    // Launched directly from the plan in HandleDockedScreen
                    // (ApplyAutoProofDocked → TryAutoLaunch) once docked; the
                    // machine's emission is the sequencing signal, not the action.
                    break;
                case LiveProof.AutoActionKind.GotoSector:
                    _pendingGotoSector = a.StrArg;
                    break;
                case LiveProof.AutoActionKind.SelectTarget:
                    _pendingTarget = (LiveProof.AutoTargetKind)a.IntArg;
                    break;
                case LiveProof.AutoActionKind.Autoshot:
                    ScheduleAutoshot(a.ShotIndex);
                    break;
                case LiveProof.AutoActionKind.HeadToDock:
                    _pendingHeadToDock = true;
                    break;
                case LiveProof.AutoActionKind.SweepHull:
                    _pendingSweepStep = a.IntArg;
                    break;
                case LiveProof.AutoActionKind.Exit:
                    _pendingExit = true;
                    break;
            }
        }

        if (_pendingExit)
        {
            _pendingExit = false;
            Console.WriteLine("[autoproof] --exit-after reached; exiting");
            Exit();
        }
    }

    private string? _pendingGotoSector;
    private LiveProof.AutoTargetKind? _pendingTarget;
    private bool _pendingHeadToDock;
    private bool _pendingExit;

    // ── side select ───────────────────────────────────────────────────────────

    /// <summary>--auto-side: request the side once the selectable roster is
    /// known (the server only accepts POSITIONREQ against a listed active side).
    /// Fallback: none (the 30 s server auto-assign seats us).</summary>
    private void ApplyAutoProofSideSelect()
    {
        if (_autoPlan.AutoSide is not { } wantSide || _autoSideDone)
            return;
        var selectable = Rendering.SideSelectLayout.SelectableSideIds(
            _session.World.SideRoster, _session.World.IsSideActive);
        if (selectable.Contains((short)wantSide))
        {
            _autoSideDone = true;
            _session.SendPositionReq((short)wantSide);
            Console.WriteLine($"[autoproof] auto-side -> side {wantSide}");
        }
    }

    // ── ready check ───────────────────────────────────────────────────────────

    /// <summary>--auto-ready: send PLAYER_READY(true) once seated.</summary>
    private void ApplyAutoProofReadyCheck()
    {
        if (!_autoPlan.AutoReady || _autoReadyDone)
            return;
        if (_session.World.MyShipId is { } pid)
        {
            _autoReadyDone = true;
            _session.SendPlayerReady(true, pid);
            Console.WriteLine($"[autoproof] auto-ready PLAYER_READY ship {pid}");
        }
    }

    // ── docked (hangar) ───────────────────────────────────────────────────────

    /// <summary>--auto-launch + the dock-preview sweep, applied inside
    /// HandleDockedScreen's shop branch (so the real ShopModel + BuyLoadout are
    /// used). Launch selects the requested hull (blank = keep the current hull)
    /// and sends BUY_LOADOUT(fLaunch=true); the sweep steps through hull
    /// previews with a real-backbuffer shot each.</summary>
    private void ApplyAutoProofDocked(Rendering.ShopModel shop)
    {
        if (!_launchSent)
        {
            if (_autoPlan.AutoLaunchRequested && !_autoLaunchApplied)
            {
                _autoLaunchApplied = true;
                TryAutoLaunch(shop);
                return;
            }
            if (_pendingSweepStep is { } step)
            {
                _pendingSweepStep = null;
                DoDockSweepStep(step, shop);
            }
        }
    }

    private void TryAutoLaunch(Rendering.ShopModel shop)
    {
        // Select the requested hull (empty = keep the current hull + parts).
        if (_autoPlan.LaunchHull.Length > 0)
        {
            short? target = ResolveHullId(_autoPlan.LaunchHull, shop);
            if (target is { } th && th != shop.EffectiveHullId)
            {
                // Cycle through the buyable list until EffectiveHullId == target
                // (CycleHull wraps; the loop is bounded by the list length).
                for (int guard = 0; guard <= shop.BuyableHulls.Count && shop.EffectiveHullId != th; guard++)
                    shop.CycleHull(true);
            }
        }
        if (!shop.TryLaunch())
        {
            Console.WriteLine($"[autoproof] auto-launch BLOCKED: {shop.BlockReason}");
            return;
        }
        _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
        _launchSent = true;
        _throttle = FlightInput.DefaultThrottle;
        Console.WriteLine($"[autoproof] auto-launch BUY_LOADOUT(fLaunch) hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
    }

    private void DoDockSweepStep(int step, Rendering.ShopModel shop)
    {
        // Step 0 = shot the current preview; step > 0 = cycle to the next hull
        // and shot it. Cycled hulls rebase the loadout, exactly like the human
        // Q/E hull-cycle keys.
        if (step > 0)
        {
            shop.CycleHull(true);
            Console.WriteLine($"[autoproof] dock sweep hull {step}: {shop.EffectiveHullId}");
        }
        ScheduleAutoshot(-1, dockSweepStep: step);
    }

    private short? ResolveHullId(string token, Rendering.ShopModel shop)
    {
        if (LiveProof.AutoProofPlan.IsNumericToken(token)
            && short.TryParse(token, out var num))
            return num;
        // Match by name (case-insensitive) against the offered hulls.
        foreach (var h in shop.BuyableHulls)
            if (string.Equals(h.Name, token, StringComparison.OrdinalIgnoreCase))
                return h.HullId;
        Console.WriteLine($"[autoproof] --auto-launch hull '{token}' not found in the offered catalogue — keeping the current hull");
        return null;
    }

    // ── flying ────────────────────────────────────────────────────────────────

    /// <summary>--auto-goto / --auto-target / --auto-dock-after, applied in
    /// UpdateFlying (only meaningful airborne, after the hangar branch).</summary>
    private void ApplyAutoProofFlying()
    {
        if (_pendingGotoSector is { Length: > 0 } gotoName)
        {
            _pendingGotoSector = null;
            if (ResolveSectorId(gotoName) is { } sid)
            {
                IssueStarmapGoto(sid);
                Console.WriteLine($"[autoproof] auto-goto -> sector {sid}");
            }
        }
        if (_pendingTarget is { } tk)
        {
            _pendingTarget = null;
            if (tk != LiveProof.AutoTargetKind.None)
                AutoFrameTarget(tk);
        }
        if (_pendingHeadToDock)
        {
            _pendingHeadToDock = false;
            if (!_autoDockHeading)
            {
                _autoDockHeading = true;
                HeadToHomeStation();
            }
        }
    }

    private short? ResolveSectorId(string token)
    {
        lock (_session.Gate)
        {
            if (LiveProof.AutoProofPlan.IsNumericToken(token)
                && short.TryParse(token, out var num))
                return num;
            foreach (var (cid, dc) in _session.World.Clusters)
                if (string.Equals(dc.Name, token, StringComparison.OrdinalIgnoreCase))
                    return cid;
        }
        Console.WriteLine($"[autoproof] --auto-goto sector '{token}' not found — skipping goto");
        return null;
    }

    /// <summary>--auto-target: frame the nearest object of the requested kind
    /// by reusing the client's own real candidate gather + relation filters
    /// (the same machines T/G use), then engage the autopilot toward it so it
    /// stays framed as the ship drifts.</summary>
    private void AutoFrameTarget(LiveProof.AutoTargetKind kind)
    {
        if (!TryOwnPose(out var pos, out var orient, out _))
            return;

        List<CandidateObject> objs;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } me
                || !world.Ships.TryGetValue(me, out var own))
                return;
            objs = GatherSectorCandidates(world.Ships, world.Warps, world.Stations,
                                          world.Asteroids, me, own.SectorId,
                                          ShipRoleBits, StationRoleBits);
        }

        short? mySide = null;
        lock (_session.Gate) mySide = _session.World.MySideId;

        CandidateObject? best = null;
        float bestDsq = float.MaxValue;
        foreach (var o in objs)
        {
            bool kindOk = kind switch
            {
                LiveProof.AutoTargetKind.Enemy => o.Kind == TargetKind.Ship && o.SideId != mySide,
                LiveProof.AutoTargetKind.Friendly => o.Kind == TargetKind.Ship && o.SideId == mySide,
                LiveProof.AutoTargetKind.Station => o.Kind == TargetKind.Station,
                LiveProof.AutoTargetKind.Aleph => o.Kind == TargetKind.Warp,
                _ => false,
            };
            if (!kindOk) continue;
            var d = o.Position - pos;
            float dsq = d.LengthSquared();
            if (dsq < bestDsq) { bestDsq = dsq; best = o; }
        }

        if (best is { } b)
        {
            _autoTarget = new AutopilotTarget(b.Kind, b.Id, b.Name);
            _autoTargetAtEngage = b.Position;
            EngageAutopilotToward(_autoTarget.Value, "autoproof: frame target");
            Console.WriteLine($"[autoproof] auto-target {kind} -> {b.Name} (kind {b.Kind})");
        }
        else
        {
            Console.WriteLine($"[autoproof] auto-target {kind}: none in sector — leaving default targeting");
        }
    }

    /// <summary>--auto-dock-after: autopilot to the nearest friendly station so
    /// the server's real dock collision (Sector.ResolveDockCollisions) docks us;
    /// the machine's sweep then photographs the hangar previews.</summary>
    private void HeadToHomeStation()
    {
        if (!TryOwnPose(out var pos, out _, out _))
            return;
        short? me = null;
        short mySector = -1;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } m && world.Ships.TryGetValue(m, out var own))
            {
                me = m;
                mySector = own.SectorId;
            }
        }

        SimVec? bestPos = null;
        short bestId = -1; string bestName = "";
        float bestDsq = float.MaxValue;
        lock (_session.Gate)
        {
            foreach (var (sid, st) in _session.World.Stations)
            {
                if (st.ClusterId != mySector) continue;   // prefer our own sector's station
                var spos = Vec(st.Position);
                var d = spos - pos;
                float dsq = d.LengthSquared();
                if (dsq < bestDsq) { bestDsq = dsq; bestId = sid; bestName = st.Name; bestPos = spos; }
            }
        }
        if (bestId < 0)
        {
            Console.WriteLine("[autoproof] --auto-dock-after: no station in my sector to dock at");
            return;
        }
        _autoTarget = new AutopilotTarget(TargetKind.Station, bestId, bestName);
        _autoTargetAtEngage = bestPos;
        EngageAutopilotToward(_autoTarget.Value, "autoproof: head to dock");
        Console.WriteLine($"[autoproof] auto-dock: autopilot to station {bestName} (#{bestId})");
    }

    // ── autoshots (real backbuffer, valid PNG) ────────────────────────────────

    /// <summary>Queue a real-backbuffer screenshot at the next Draw end with a
    /// descriptive name (time + sector + camera). <paramref name="shotIndex"/>
    /// &gt;= 0 = the in-flight schedule; <paramref name="dockSweepStep"/> &gt;= 0
    /// labels a hangar-preview sweep shot.</summary>
    private void ScheduleAutoshot(int shotIndex = -1, int dockSweepStep = -1)
    {
        // P570 — dismiss the briefing popup first so the real-backbuffer proof
        // frame isn't obscured by it (the briefing only auto-dismisses in the
        // offscreen-capture path; mirror what ALLEGIANCE_AUTOSHOT does).
        if (IsBriefingVisible(out _)) DismissBriefing();

        double t = _auto?.SecondsSinceLaunch ?? 0.0;
        string camera = _cameraMode == CameraMode.Cockpit ? "cockpit" : "chase";
        string sector = CurrentSectorName();
        string tag = dockSweepStep >= 0 ? $"sweep{dockSweepStep:00}" : $"t{t:000.0}";
        _autoShotName = $"proof_{tag}_s{sector}_c{camera}";
        _autoShotScheduled = true;
        Console.WriteLine($"[autoproof] autoshot queued: {_autoShotName} (t={t:0.0}s after launch)");
    }

    private string CurrentSectorName()
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && world.Clusters.TryGetValue(own.SectorId, out var dc))
                return SanitizeFileName(dc.Name);
        }
        return "unknown";
    }

    private static string SanitizeFileName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.Length == 0 ? "sector" : sb.ToString();
    }

    /// <summary>Write the queued autoshot. Called from the Draw-end hook where
    /// the real backbuffer holds the completed frame. Writes a VALID PNG via
    /// SaveAsPng (not the P566-bad-CRC encoder) so the file opens in a normal
    /// viewer.</summary>
    private void FlushScheduledAutoshot()
    {
        if (!_autoShotScheduled)
            return;
        _autoShotScheduled = false;
        try
        {
            var gd = GraphicsDevice;
            int w = gd.PresentationParameters.BackBufferWidth;
            int h = gd.PresentationParameters.BackBufferHeight;
            var px = new Color[w * h];
            gd.GetBackBufferData<Color>(px);
            string dir = _autoshotDir.Length > 0 ? _autoshotDir : "screenshots";
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"{_autoShotName}.png");
            using var tex = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
            tex.SetData(px);
            using var fs = System.IO.File.Create(path);
            tex.SaveAsPng(fs, w, h);
            Console.WriteLine($"[autoproof] screenshot saved: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[autoproof] autoshot FAILED: {ex.Message}");
        }
    }
}
