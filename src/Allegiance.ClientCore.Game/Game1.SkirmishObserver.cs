using System.Text.Json;
using Allegiance.ClientCore.State;
using Allegiance.Core.Model;
using Allegiance.SinglePlayer;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private bool _localSpectating;
    private bool LocalSpectating => _skirmish is not null && _localSpectating;
    private readonly SkirmishCamera _skirmishCamera = new();
    private SkirmishObservation? _observation;
    private Task? _seatChange;
    private short? _requestedSeat;
    private KeyboardState _skirmishLastKeys;
    private double _observerSampleAt;
    private long _observerStarted;
    private bool _observerProofDone;
    private bool _observerAutoStarted;
    private short _lastObserverSubject = -1, _lastObserverStation = -1;
    private short? RenderShipId(ClientWorld world) => LocalSpectating
        ? (_skirmishCamera.ShipId is { } id && world.Ships.ContainsKey(id) ? id : world.Ships.Keys.Select(key => (short?)key).FirstOrDefault()) : world.MyShipId;

    // A camera can draw an empty sector. Its reference pose does not create an entity.
    private bool TrySceneReference(ClientWorld world, out short id, out Flight.PredictedPose pose)
    {
        id = RenderShipId(world) ?? -1;
        if (_poses.TryGetValue(id, out pose)) return true;
        pose = new Flight.PredictedPose(default, Allegiance.Core.Sim.Orientation.Identity);
        return LocalSpectating && _skirmishCamera.SectorId >= 0;
    }

    private bool TrySceneSector(ClientWorld world, short id, out short sector)
    {
        sector = LocalSpectating ? _skirmishCamera.SectorId : (short)-1;
        if (LocalSpectating) return sector >= 0;
        if (!world.Ships.TryGetValue(id, out var ship)) return false;
        sector = RenderSector(ship.SectorId);
        return true;
    }

    private void EnterLocalSpectator()
    {
        DisengageAutopilot("spectating");
        _autoTarget = null;
        _directorActive = false;
        _spectatorFollowId = -1;
        _session.Dispose();
        lock (_session.Gate) _session.World.Reset();
        _localSpectating = true;
        _dockedActive = false;
        _ownShipSendArmed = false;
        _chatActive = false;
        _mainMenuVisible = false;
        _state = ClientState.Flying;
        _skirmishCamera.SelectMode(SkirmishCameraMode.HottestFight);
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_SP5_PROOF") is { Length: > 0 }
            && Enum.TryParse<SkirmishCameraMode>(Environment.GetEnvironmentVariable("ALLEGIANCE_SP5_CAMERA"), true, out var proofCamera))
            _skirmishCamera.SelectMode(proofCamera);
        _observerSampleAt = double.NegativeInfinity;
        _observerStarted = Environment.TickCount64;
        _freeCamActive = false;
        _orbitCamActive = true;
        _cameraMode = CameraMode.OrbitCam;
        _orbitAuto = true;
        Console.WriteLine("[skirmish] shipless observer active");
    }

    private void HandleSkirmishSeatKeys(KeyboardState keys)
    {
        if (_skirmish is null) return;
        bool Edge(string action) => ActionDown(keys, action) && !ActionDown(_skirmishLastKeys, action);
        if (!_chatActive && !_keyBindVisible && _seatChange is null)
        {
            short? side = Edge("SkirmishJoinAlpha") ? (short)0 : Edge("SkirmishJoinBravo") ? (short)1 : null;
            if (side is not null || Edge("SkirmishSpectate"))
            {
                RequestSkirmishSeat(side);
            }
        }
        _skirmishLastKeys = keys;
    }

    private void RequestSkirmishSeat(short? side)
    {
        if (_skirmish is null || _seatChange is not null || side == _skirmish.PlayerSide) return;
        _requestedSeat = side;
        EnterLocalSpectator();
        _seatChange = _skirmish.SetPlayerSideAsync(side, _skirmishCancel?.Token ?? default);
    }

    private void UpdateSeatChange()
    {
        if (_seatChange is not { IsCompleted: true } change) return;
        _seatChange = null;
        try
        {
            change.GetAwaiter().GetResult();
            if (_requestedSeat is null) return;
            _localSpectating = false;
            _freeCamActive = _orbitCamActive = false;
            _cameraMode = CameraMode.Chase;
            _skirmishSideRequested = false;
            _ownReady = true;
            _readyConfirmedShip = null;
            _disconnectTransitioned = false;
            BeginConnect("127.0.0.1", _skirmish!.Port);
        }
        catch (Exception ex) { _skirmishError = "Seat change failed: " + ex.Message; }
    }

    private KeyboardState _observerKeys;
    private void UpdateLocalSpectator(GameTime time, KeyboardState keys)
    {
        _nowSeconds += time.ElapsedGameTime.TotalSeconds;
        _gameSeconds += time.ElapsedGameTime.TotalSeconds * (_skirmish!.Paused ? 0 : 1);
        if (_observerProofWaitExit)
        {
            if (!_autoShotScheduled) { Exit(); return; }
        }
        // SP-6 — one-shot status-bar proof: ALLEGIANCE_OBSERVER_AUTOSHOT captures the real
        // backbuffer (status line shows the chosen speed) and then exits.
        if (!_autoShotDone && Environment.GetEnvironmentVariable("ALLEGIANCE_OBSERVER_AUTOSHOT") is { Length: > 0 } spShot)
        {
            if (_autoShotStartMs <= 0) _autoShotStartMs = Environment.TickCount64;
            int delay = int.TryParse(spShot, out var sd) && sd > 0 ? sd : 8000;
            if (Environment.TickCount64 - _autoShotStartMs >= delay)
            {
                _autoShotName = "skirmish_status_" + _skirmish.Options.Speed;
                _autoShotScheduled = true;
                _autoShotDone = true;
                _observerProofWaitExit = true;
            }
        }
        bool Edge(string action) => ActionDown(keys, action) && !ActionDown(_observerKeys, action);
        if (Edge("Quit")) Exit();
        if (Edge("OpenKeyBindings")) ToggleKeyBindings();
        bool controlsOpen = _keyBindVisible;
        if (_keyBindVisible) UpdateKeyBindingPopup(keys);
        if (Edge("Help") && !controlsOpen) _helpVisible = !_helpVisible;
        if (Edge("Credits") && !controlsOpen) _creditsVisible = !_creditsVisible;
        if (Edge("MainMenu") && !controlsOpen)
        {
            if (_helpVisible) _helpVisible = false;
            else if (_creditsVisible) _creditsVisible = false;
            else ToggleMainMenu();
        }
        if (_creditsVisible)
        {
            if (ActionDown(keys, "CreditsScrollDown")) _creditsOverlayRenderer.Scroll(42);
            if (ActionDown(keys, "CreditsScrollUp")) _creditsOverlayRenderer.Scroll(-42);
        }
        UpdateMainMenu(keys);
        if (!LocalSpectating) return;
        int direction = Edge("OrbitSubjectNext") ? 1 : Edge("SkirmishPrevious") ? -1 : 0;
        bool modal = _mainMenuVisible || _helpVisible || _keyBindVisible || _creditsVisible;
        if (!modal)
        {
            if (Edge("SkirmishCamera")) _skirmishCamera.CycleMode();
            if (Edge("FreeCam")) _skirmishCamera.SelectMode(SkirmishCameraMode.Free);
            if (Edge("OrbitCam")) _skirmishCamera.SelectMode(SkirmishCameraMode.HottestFight);
        }
        else direction = 0;
        bool wasFree = _freeCamActive;
        _freeCamActive = _skirmishCamera.Mode == SkirmishCameraMode.Free;
        _orbitCamActive = !_freeCamActive;
        _cameraMode = _freeCamActive ? CameraMode.FreeCam : CameraMode.OrbitCam;
        if (_nowSeconds >= _observerSampleAt || direction != 0)
        {
            var frame = _skirmish.Observe(_skirmishCamera.SectorId);
            _skirmishCamera.Update(frame, direction);
            lock (_session.Gate)
            {
                if (_observation?.Epoch != frame.Epoch) _session.World.Reset();
                _session.World.ApplyLocalObservation(frame.Objects, frame.Projectiles, frame.Missiles);
            }
            _observation = frame;
            _observerSampleAt = _nowSeconds + .1;
        }
        GatherRenderPoses(time);
        _motionTrail.PruneUntouched();
        _orbitSubjectId = _skirmishCamera.ShipId ?? -1;
        if (_skirmishCamera.Mode == SkirmishCameraMode.Station && _skirmishCamera.StationId is { } station
            && _session.World.Stations.TryGetValue(station, out var st))
        {
            if (_lastObserverStation != station) _orbitCam.FrameRadius(450, MathHelper.ToRadians(50));
            _orbitCam.Focus(new Vector3(st.Position[0], st.Position[1], st.Position[2]));
            _lastObserverStation = station;
            _lastObserverSubject = -1;
        }
        else if (_orbitSubjectId >= 0)
        {
            CenterOrbitOnSubject(_orbitSubjectId, _lastObserverSubject != _orbitSubjectId);
            _lastObserverSubject = _orbitSubjectId;
            _lastObserverStation = -1;
        }
        _orbitCam.AutoOrbit = true;
        _orbitCam.AutoOrbitSpeedRadPerSec = .12f;
        if (_freeCamActive && !wasFree) _freeCam.Teleport(_orbitCam.Eye);
        if (_freeCamActive && !modal)
        {
            var mouse = Mouse.GetState();
            if (_specMouseInit) _freeCam.Look(mouse.X - _specPrevMouseX, mouse.Y - _specPrevMouseY);
            _specMouseInit = true;
            RecenterMouse();
            _freeCam.SpeedModifier = ActionDown(keys, "FreeCamSpeedUp") ? 4 : ActionDown(keys, "FreeCamSpeedSlow") ? .25f : 1;
            _freeCam.Move((ActionDown(keys, "FreeCamForward") ? 1 : 0) - (ActionDown(keys, "FreeCamBack") ? 1 : 0),
                (ActionDown(keys, "FreeCamRight") ? 1 : 0) - (ActionDown(keys, "FreeCamLeft") ? 1 : 0),
                (ActionDown(keys, "FreeCamUp") ? 1 : 0) - (ActionDown(keys, "FreeCamDown") ? 1 : 0), (float)time.ElapsedGameTime.TotalSeconds);
        }
        else { _specMouseInit = false; _orbitCam.Step(time.ElapsedGameTime.TotalSeconds); }
        _observerKeys = keys;
    }

    private static string SkirmishSkillCounters(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object || !stats.TryGetProperty("combat", out var combat)) return "";
        string Value(string name) => combat.TryGetProperty(name, out var value) ? value.ToString() : "-";
        string rate = combat.TryGetProperty("hitRate", out var hitRate) ? hitRate.GetDouble().ToString("P1", System.Globalization.CultureInfo.InvariantCulture) : "-";
        return $"Combat: shots {Value("shotsFired")}   hits {Value("shipHits")}   kills {Value("kills")}   hit rate {rate}";
    }

    private static bool SkirmishHasCombat(JsonElement stats) => stats.ValueKind == JsonValueKind.Object
        && stats.TryGetProperty("combat", out var combat) && combat.TryGetProperty("shipHits", out var hits) && hits.GetDouble() > 0;

    // Deterministic real-client proof entry. Inert during ordinary play.
    private void CaptureSkirmishProof()
    {
        if (!LocalSpectating || _observerProofDone || _camTransActive || Environment.GetEnvironmentVariable("ALLEGIANCE_SP5_PROOF") is not { Length: > 0 } path
            || (Environment.TickCount64 - _observerStarted < 30000 ||
                (Environment.TickCount64 - _observerStarted < 180000 && !SkirmishHasCombat(_skirmish!.Stats)))) return;
        using var texture = new Microsoft.Xna.Framework.Graphics.Texture2D(GraphicsDevice, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        var pixels = new Color[texture.Width * texture.Height];
        GraphicsDevice.GetBackBufferData(pixels);
        texture.SetData(pixels);
        using (var file = File.Create(path)) texture.SaveAsPng(file, texture.Width, texture.Height);
        using (var metadata = new Utf8JsonWriter(File.Create(Path.ChangeExtension(path, ".json"))))
        {
            metadata.WriteStartObject();
            metadata.WriteNumber("width", texture.Width);
            metadata.WriteNumber("height", texture.Height);
            metadata.WriteString("camera", _skirmishCamera.Mode.ToString());
            metadata.WriteNumber("sector", _skirmishCamera.SectorId);
            lock (_session.Gate)
            {
                if (_session.World.MyShipId is { } id) metadata.WriteNumber("playerShipId", id);
                else metadata.WriteNull("playerShipId");
                if (_session.World.MySideId is { } side) metadata.WriteNumber("playerSideId", side);
                else metadata.WriteNull("playerSideId");
                metadata.WriteNumber("flyingShipsInView", _session.World.Ships.Values.Count(s => s.CanDrawInSector(_skirmishCamera.SectorId)));
            }
            metadata.WriteEndObject();
        }
        _observerProofDone = true;
        Console.WriteLine($"[skirmish] spectator proof saved: {path}");
        Exit();
    }
}
