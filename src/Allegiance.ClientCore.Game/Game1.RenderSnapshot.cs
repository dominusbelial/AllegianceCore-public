using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private ClientWorld.ShipCaches _poseCaches = new();
    private readonly List<(short Id, ServerShipUpdate Update)> _remoteEffectUpdates = [];
    private IReadOnlyList<ScoreboardLayout.Row> _scoreRows = [];
    private IReadOnlyList<TeamPaneLayout.Line> _teamLines = [], _consoleLines = [];
    private PilotInfoLayout.PilotInfo? _framePilot;
    private string? _scoreSelf;
    private string _frameBriefText = "", _fundingStatus = "";
    private int? _briefRemaining, _frameMoney;
    private bool _frameBriefingVisible;
    private MissionBriefingInfo? _frameBriefing;
    private string[] _frameObjectives = [];
    private (int State, string Carrier)? _frameBoard;
    private Color _boardColor;
    private bool _frameDevelopments;
    private short? _previewCarrierHull;
    private string? _previewCiv;

    private void CaptureFlightPanes(ClientWorld world)
    {
        if (_frame.Result is null)
        {
            if (_scoreboardVisible || _pilotInfoVisible)
            {
                _scoreSelf = world.Ships.GetValueOrDefault(world.MyShipId ?? -1)?.CharacterName;
                if (_scoreboardVisible) _scoreRows = ScoreboardLayout.Build(world.ScoreRoster, world.RankTable, world.IsSideActive);
                if (_pilotInfoVisible) _framePilot = PilotInfoLayout.Build(world.ScoreRoster, _scoreSelf, world.RankTable);
            }
            _frameBriefingVisible = IsBriefingVisible(out _briefRemaining);
            _frameBriefing = world.MissionBriefing;
            _frameBriefText = world.BriefingText;
            if (_teamPaneVisible) _teamLines = CaptureTeamLines();
        }
        _consoleLines = CaptureConsoleLines();
        _frameMoney = world.MyMoney;
        if (_investActive && _investDefs is not null)
        {
            _investModel ??= BuildInvestmentModel();
            _fundingStatus = _investmentFunding.Status(world);
        }
        _frameDevelopments = HangarDevelopments;
        if (DockedLoadoutView && _shopModel is { } shop)
        {
            _previewCiv = CivKeyForShipSide(world, world.MySideId ?? -1);
            _previewCarrierHull = world.MyShipId is { } id && world.Ships.TryGetValue(id, out var me)
                && me.ParentId >= 0 && world.Ships.TryGetValue(me.ParentId, out var carrier) && carrier.HullId > 0
                ? carrier.HullId : null;
            if (LoadoutV2Presentation.UseV2(_visuals, true)) CaptureLoadoutV2(shop);
        }
        _frameBoard = (_nativeHangarScreen || DockedLoadoutView) ? LoadoutBoardState() : null;
        if (_frameBoard.HasValue)
            _boardColor = world.MySideId is { } side && world.TryGetSideColor(side, out var r, out var g, out var b)
                ? new Color(r,g,b) : Color.White;
        if ((_dockedActive || _investActive) && !LocalSpectating && !SpectatorCameraActive && !_hideHud && !_hudHidden && !_directorActive)
            _frameObjectives = world.LastBaseObjectives().Select(o => o.Text).ToArray();
    }

    private IReadOnlyList<TeamPaneLayout.Line> CaptureTeamLines()
    {
        IReadOnlyList<TeamPaneLayout.Line> lines;
        string? activeBallotText = _session.World.ActiveBallot?.BallotText;
        var ships = _session.World.Ships;
        var briefs = new Dictionary<string, Rendering.TeamPaneLayout.ShipBrief>();
        foreach (var ent in ships.Values)
            briefs[ent.CharacterName] = Rendering.TeamPaneLayout.BriefOf(_session.World, ent, HullNameForSort);
        // P752 — the pane's selection cursor and the leader's join
        // requests feed the same roster the controls act on.
        var rosterRows = _teamPaneControls.BuildRoster(_session.World, HullNameForSort);
        int sel = _teamPaneControls.Selection;
        string? selectedName = sel >= 0 && sel < rosterRows.Count ? rosterRows[sel].Name : null;
        var joinRequests = rosterRows.Where(r => r.IsJoinRequest).Select(r => r.Name).ToArray();
        lines = Rendering.TeamPaneLayout.Build(
            Rendering.TeamPaneLayout.Entries(_session.World), _session.World.RankTable, briefs,
            _session.World.IsSideActive,
            _session.World.MyMoney,
            activeBallotText,
            _session.World.IsTeamLeader,
            ships.TryGetValue(_session.World.MyShipId ?? -1, out var tpe) ? tpe.CharacterName : null,
            $"{KeyOf("TeamPane").Label} TOGGLE | {KeyOf("TeamPaneNextPilot").Label} SELECT | {KeyOf("TeamPaneSort").Label} SORT | D $100 / E $500 / A AUTO / X STOP | {KeyOf("TeamPaneAct").Label} GOTO/ACCEPT | {KeyOf("Scoreboard").Label} LEADERBOARD | {KeyOf("PilotInfo").Label} SHEET",
            selectedName,
            joinRequests,
            sortKey: _teamPaneControls.SortKey,
            sideSquadName: sideId => _session.World.SideSquadName(sideId),
            sideName: sideId => _session.World.SideNameOverride(sideId));
        lines = lines.Append(new TeamPaneLayout.Line(TeamPaneLayout.LineKind.Hint, ModerationModel.Hint))
            .Append(new TeamPaneLayout.Line(TeamPaneLayout.LineKind.Note, _moderation.Notice ?? "Steam moderator bans unavailable."))
            .ToArray();
        if (_consolePanes.Team == TeamConsoleSize.Mini)
            lines = TeamPaneLayout.Compact(lines, TeamPaneLayout.Entries(_session.World), _session.World.MySideId);
        return lines;
    }

    private readonly FlightRenderSnapshot _frame = new();
    private FlightRenderSnapshot.View _poseView;
    private readonly record struct PoseArt(short Side, short Hull, string? CivKey);
    private readonly Dictionary<short, PoseArt> _shipArt = [];

    private FlightRenderSnapshot.View ReadRenderView(ClientWorld world)
    {
        short? reference = RenderShipId(world);
        short? sector = TrySceneSector(world, reference ?? -1, out short id) ? id : null;
        return FlightRenderSnapshot.View.Read(world, reference, sector);
    }

    private void ResolvePoseArt()
    {
        foreach (var (id, art) in _shipArt)
        {
            string? asset = ResolveShipAsset(art.CivKey, art.Hull);
            var model = asset is null ? null : _shipModels.GetValueOrDefault(asset);
            _shipModelById[id] = model;
            _shipTextureById[id] = asset is null ? null : _shipTextures.GetValueOrDefault(asset);
            if (_shipsNative) _nativeShipRefById[id] = ResolveNativeShip(art.Hull);
            _shipScaleById[id] = ShipRenderScale.ScaleForLength(HullLength(art.Hull), model ?? _fallbackModel);
        }
    }

    private long _frameTimeMs;

    private (Matrix View, Matrix Projection, Vector3 Eye) _frameCamera;
    private FlightHudInputs _frameHud;
    private readonly ChatWindow _flightChatWindow = new();
    private readonly List<string> _stationNotices = [];
    private PreviewSubject? _framePreview;
    private Action? _drawFrameMap;
    private readonly List<short> _arrivalWarps = [];
    private readonly List<AlephTransitSignal> _frameTransitSignals = [];
    private readonly Dictionary<short, string> _transitActors = [];
    private bool _haveHudTransits;
    private (SimVec Position, SimVec Velocity, SimVec Forward, SimVec Right)? _nextHudBurst;
    private readonly record struct BoltInput(SimVec Position, SimVec Velocity, SimVec Forward,
        short ProjectileTypeId, long ReceivedLocalMs);
    private readonly List<BoltInput> _frameProjectiles = [];

    private bool CaptureRenderSnapshot(GameTime gameTime)
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            var view = ReadRenderView(world);
            _frame.CaptureHeader(view);
            if (_state == ClientState.ReadyCheck)
            {
                CaptureReadyCheck();
                return false;
            }
            if (!_directorActive && !_nativeHangarScreen) RefreshInvestState();
            CaptureFlightPanes(world);
            _drawFrameMap = !_directorActive && !_nativeHangarScreen ? CaptureStarmap(gameTime) : null;
            _arrivalWarps.Clear();
            _transitActors.Clear();
            _haveHudTransits = false;
            _nextHudBurst = null;
            if (view.Side != _poseView.Side) ResetEjectBlasts();
            if (view != _poseView || view.Sector is not { } sector || !TrySceneReference(out var me, out var pose))
            {
                if (view.Sector is { } ejectSector && view.Mission == _poseView.Mission
                    && view.Generation == _poseView.Generation && view.Side == _poseView.Side)
                    PrepareEjectBlasts(world, ejectSector);
                else ResetEjectBlasts();
                if (!LocalSpectating && !_nativeHangarScreen) _frameHud = CaptureFlightHud();
                return false;
            }

            long now = ClientNowMilliseconds;
            world.PruneExpiredMissiles(now);
            _frame.CaptureSector(_alephGlimpse);
            _frame.DrawShips.Clear();
            _frame.Ships.Clear();
            _frame.OwnLoadout.Clear();
            short? hidden = !LocalSpectating && _cameraMode == CameraMode.DroneCockpit && _commandee is { } target
                ? CommandCamera.FollowSubject(world, target.Id, sector)?.ShipId : null;
            short side = view.Side ?? -1;
            foreach (var (id, ship) in world.Ships)
            {
                bool radarVisible = !ship.IsNotVisible && !ship.Unknown && ship.ParentId < 0 && ship.SectorId == sector;
                if ((!radarVisible && id != me) || ship.Latest is not { } up) continue;
                if (id == me && ship.Loadout is { } loadout) _frame.OwnLoadout.AddRange(loadout);
                int? rip = world.TryGetRemoteRipcord(id, out long start)
                    ? RadarOverlayRules.FriendlyRipcordSeconds(false, ship.SideId == side, true, start,
                        (long)(world.NowSeconds() * 1000), RipcordDurationSecondsFor(world, ship)) : null;
                _frame.Ships.Add(id, new(ship.HullId, ship.SideId, ship.WingId, ship.CharacterName,
                    ship.CarriedFlagSideId, up, RadarOverlayRules.PilotTypeOf(ship.Player),
                    RadarLabelModel.ShowDetectionEyeFor(ship, side, false), radarVisible, rip));
                if (hidden != id && _shipArt.TryGetValue(id, out var art) && art.Hull == ship.HullId && art.Side == ship.SideId
                    && (LocalSpectating ? ship.CanDrawInSector(sector) : ship.ParentId < 0 && ship.SectorId == sector && !ship.Unknown))
                    _frame.DrawShips.Add(id);
            }
            _frameTimeMs = now;
            PrepareEjectBlasts(world, sector);
            CaptureFrameCamera(gameTime, pose);
            _framePreview = CapturePreviewSubject(me);
            CaptureTransientGeometry(world, me, sector, now);
            _frameProjectiles.Clear();
            world.PruneExpiredProjectiles(now);
            if (_frame.Ships.TryGetValue(me, out var own))
            {
                var cameraEye = _frameCamera.Eye;
                var anchorPos = own.Latest.Position;
                // P542 — count how many received rounds survive to DRAW with
                // the exact renderer cull rules (feed the [proj-diag] line).
                world.DiagnoseDraw(anchorPos, new SimVec(cameraEye.X, cameraEye.Y, cameraEye.Z),
                    now, _projectileTypes);
                if (Environment.GetEnvironmentVariable("ALLEGIANCE_PROJ_DIAG") == "1"
                    && world.Diag.TotalReceived > 0
                    && (LastProjDiagEmitMs <= 0 || now - LastProjDiagEmitMs >= 1000))
                {
                    LastProjDiagEmitMs = now;
                    Console.WriteLine($"[proj-diag] {world.Diag.FormatLine()}");
                }
                foreach (var bolt in world.Projectiles)
                {
                    var pos = bolt.PositionAt(now);
                    if ((pos - anchorPos).LengthSquared() <= ProjectileVisibilityRadiusSqr)
                        _frameProjectiles.Add(new(pos, bolt.Velocity, bolt.Forward, bolt.ProjectileTypeId, bolt.ReceivedLocalMs));
                }
            }
            if (!LocalSpectating && !_nativeHangarScreen) _frameHud = CaptureFlightHud();
            return true;
        }
    }

    private void CaptureTransientGeometry(ClientWorld world, short me, short sector, long now)
    {
        _drawChaff.Clear();
        if (world.Ships.TryGetValue(me, out var own) && own.Latest is { } meChaffUp)
        {
            long chaffNow = ClientNowMilliseconds;
            var chaffTypes = _shopCatalog?.ChaffTypes;
            world.PruneExpiredChaff(chaffNow, chaffTypes);
            var anchorChaff = meChaffUp.Position;
            foreach (var c in world.Chaff)
            {
                var pos = c.PositionAt(chaffNow);
                var toMe = pos - anchorChaff;
                if (toMe.LengthSquared() > ChaffVisibilityRadiusSqr) continue;
                float age = Math.Max(0f, (float)c.ElapsedSeconds(chaffNow));
                float lifespan = Rendering.ChaffModelSource.ResolveLifespan(c.Etid, chaffTypes);
                float radius = Rendering.ChaffAnimation.CurrentRadius(
                    age, lifespan, Rendering.ChaffModelSource.ResolveRadiusCeiling(c.Etid, chaffTypes));
                string stem = Rendering.ChaffModelSource.ResolveStem(c.Etid, chaffTypes);
                _drawChaff.Add((PoseConverter.ToXna(pos), radius, stem, Rendering.ChaffAnimation.RollAngle(age)));
            }
        }
        if (_effectLights.Detail != EffectsDetail.Low)
        {
            long lightNow = now;
            _lightImpacts.RemoveAll(hit => lightNow - hit.ReceivedLocalMs >= 160);
            foreach (var hit in world.HullHits)
                if (hit.ClusterId == sector && _lightImpacts.Count < 64) _lightImpacts.Add(hit);
        }
        _pendingHullHits.Clear();
        foreach (var hit in world.TakeNewHullHits())
            if (hit.ClusterId == sector)
                _pendingHullHits.Add(hit);
        if (System.Environment.GetEnvironmentVariable("ALLEGIANCE_DEBUG_FLARE") == "1" && _pendingHullHits.Count > 0)
            Console.WriteLine($"[flare] ingest {_pendingHullHits.Count} local hits (ownSector={sector}) firstCluster={_pendingHullHits[0].ClusterId} abs={_pendingHullHits.Count(h => h.AbsorbedByShield)}");
        _warpBombRemaining.Clear();
        double serverNow = world.ServerClockSeconds;
        foreach (var (id, bomb) in world.WarpBombs)
            _warpBombRemaining[id] = bomb.ExplosionServerSeconds - serverNow;
        bool instantHandoff = _visuals.IsOn(VisualFlags.ConstructionHandoffKey);
        _drawBuilds.Clear();
        world.PruneExpiredBuildingEffects(now);
        foreach (var effect in (instantHandoff ? world.ConstructionVisuals : world.BuildingEffects).Values)
            if (effect.ClusterId == sector) _drawBuilds.Add(effect.Phase(now));
    }

    private void CaptureFrameCamera(GameTime gameTime, Flight.PredictedPose myPose)
    {
        var myPos = PoseConverter.ToXna(myPose.Position);
        var myRot = PoseConverter.ToXnaRotation(myPose.Orientation);
        var aspect = GraphicsDevice.Viewport.AspectRatio;
        // Frame delta for the render-phase cameras (P508 chase easing,
        // P252's flight jiggle). P1005 — the asteroid spin is NOT advanced
        // from this: a Draw-time step is a render clock, and native rotates
        // rocks inside its model update on the MISSION clock. The spin is
        // stepped from _gameSeconds in PrepareSceneGeometry instead.
        float dtSec = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Camera selection: P211's chase (third-person) / cockpit
        // (first-person, full orientation with roll), plus P295's two new
        // external views (ExternalTurret follows the carrier you're
        // gunning on; ExternalMissile watches the latest live missile in
        // your sector). All honour the same camera-relative convention
        // and all supply a cameraEye that every draw subtracts from
        // object positions.
        Matrix view; Matrix proj; Vector3 cameraEye;
        float chaseDt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        // P979 — resolve this frame's radial-visibility state BEFORE the
        // geometry gather: the per-object gate (OutsideRadialView), the
        // depth pass and the projection all read the same decision.
        ResolveRadialView();
        // P306 — capture mode: default is a fixed tracking overview of the
        // whole fleet (video shows N ships in frame at once). P338 — a
        // `chase` capture instead drives the local ship's REAL chase
        // camera so the live-proof frames exercise the production
        // framing path (radius-adaptive; see ChaseCamera).
        // P580 — otherwise the flight camera is resolved centrally by
        // ComputeFlightCamera, which handles every native external mode,
        // the director b-roll camera and spectator follow (see
        // Game1.Director.cs).
        if (_benchmark is not null && (_benchMeasuring || _benchSuggestion is not null))
        {
            ComputeBenchmarkCamera(myPos, myRot, myPose.Orientation, aspect, chaseDt, out view, out proj, out cameraEye);
        }
        else if (_captureSpec is { } cap && !cap.Chase)
        {
            ComputeCaptureCamera(aspect, out view, out proj, out cameraEye);
        }
        else
        {
            (view, proj, cameraEye) = ComputeFlightCamera(
                myPos, myRot, myPose.Orientation, aspect, chaseDt);
            // P612 — smooth camera transitions: when the framing changes
            // (target switch, missile-cam start/end, mode change) ease the
            // eye/target/FOV from the previous framing over ~0.7 s instead
            // of cutting. Instant when ALLEGIANCE_INSTANT_CAMERA=1. Passes
            // the pose through unchanged when nothing changed (native
            // camera behaviour preserved). Applied AFTER the raw camera so
            // every mode benefits; jiggle below perturbs the smoothed view.
            ApplyCameraSmoothing(ref view, ref proj, ref cameraEye, aspect, chaseDt);
        }
        if ((_benchmark is not null || _benchmarkApplied) && _cameraMode != CameraMode.Command)
            proj = WithBenchmarkViewDistance(proj, _benchmarkViewDistance);
        // P979 — radial visibility ON: push the far plane past the range so
        // the plane can never clip an object the sphere rule keeps (an
        // object at the limit still has its bulk inside the frustum). The
        // per-object gate below does the actual hiding; OFF leaves native's
        // 10000 m planar clip exactly as it was.
        if (_radialViewActive)
            proj = Rendering.RadialVisibility.WithFarPlane(proj, _flightDepthFar);
        // P449b — native camera jiggle (WinTrek.cpp:374-399). Native sets
        // bJiggle = InternalCamera(m_cm), and InternalCamera is true only for
        // cmCockpit (Treki.h:145) — so native shakes the COCKPIT view, not the
        // external chase/missile views. Perturbing the shared view matrix means
        // the world AND the world-projected HUD elements (lead indicator,
        // lock/selection brackets) shake together, while the screen-anchored
        // centre reticle stays put — exactly how native projects the HUD
        // against the jiggled camera. Visual only: the target-picking ray in
        // Game1.Targeting.cs uses the un-jiggled camera, so aiming is stable.
        if (_cameraMode == CameraMode.Cockpit
            && TryOwnAfterburnerForJiggle(out bool abMounted, out bool abActive))
        {
            // ServerShipUpdate.Power is a hardcoded flat 1.0 in this port (no
            // server-side afterburner-power ramp exists; the simulator treats the
            // afterburner as binary boost), so the native GetPower() term is
            // approximated as the AfterburnerButton bit — see the P449b report.
            var (jYaw, jPitch) = _flightJiggle.Step(dtSec, abActive ? 1f : 0f, abMounted);
            if (jYaw != 0f || jPitch != 0f)
                view = Rendering.FlightJiggle.PerturbView(view, jYaw, jPitch);
        }
        // P230 — project the server's lock target into this frame's view and
        // stage the target-box descriptor for DrawHud (null when no target or
        // it is out of view). Uses the same camera-relative projection as the
        // P212 target-pick.
        _lockBracket = ComputeLockBracket(view, proj, cameraEye);
        // P804 — solve the lead on the CLIENT (native indicator.cpp:183-405)
        // and stage the cockpit lead marker / aim predictor, or the
        // chase/missile no-target grey marker, for DrawHud. Null when
        // nothing applies (no target, gate failed, no solution).
        ComputeLeadIndicators(view, proj, cameraEye, out _leadIndicator, out _chaseAim);
        // HUD-8 — stage the velocity-direction marker (cockpit gate is
        // applied in HudModel) and the accepted/current order turn
        // arrows for this frame's snapshot.
        _velocityMarker = ComputeVelocityMarker(view, proj);
        // P1036 — the Jet flight reference from the same source pose the
        // velocity marker reads (attitude quaternion + velocity + the
        // native sector plane). Presentation-only; Classic/Modern ignore it.
        _flightReference = ComputeFlightReference();
        _orderArrows = ComputeOrderArrows(view, proj, cameraEye);
        // P345 — project the player's OWN T-selected autopilot target into
        // this frame's view and stage the selection-bracket descriptor for
        // DrawHud (null when no target is selected or it is out of view).
        // Uses the same shared camera-relative projection (ProjectToScreen)
        // as ComputeLockBracket and P266's lead, but is a separate method
        // because the two concepts are simultaneously active and visually
        // distinct (server missile-lock vs the player's own target).
        _targetSelectionBracket = ComputeTargetSelectionBracket(view, proj, cameraEye);
        // P359 — the bearing arrow is computed EVEN when the target is
        // off-screen (it is exactly what you need then), so it lives apart
        // from the bracket rather than piggy-backing its in-view gate.
        _targetBearingScreenDir = ComputeTargetBearing(view, proj, cameraEye);
        ComputeLastBaseHud(view, proj, cameraEye);

        _frameCamera = (view, proj, cameraEye);
    }
}
