using System.Numerics;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// The bridge between the (host-agnostic) training framework and the game — the C# analogue
/// of the native training library's hard dependency on <c>trekClient</c> plus the <c>ImissionIGC</c>
/// world (<c>src/training/*.cpp</c>, e.g. <c>GetAutopilotCondition.cpp:35</c>,
/// <c>CreateWaypointAction.cpp:39</c>). TR-2 binds this to the embedded host + client; tests
/// bind it to a fake. The control masks are deliberately <em>not</em> here: native keeps them in
/// module globals (<c>Training.cpp:28-34</c>) that the input path reads and the actions write,
/// so they live in <see cref="TrainingControlState"/> instead of per-site.
/// </summary>
public interface ITrainingSite
{
    /// <summary>Elapsed training-clock seconds — the C# stand-in for <c>Time::Now()</c> used by the
    /// periodic/elapsed-time conditions (<c>periodiccondition.cpp:38</c>).</summary>
    float TimeSeconds { get; }

    /// <summary>The player's ship, the primary subject of most conditions (native <c>trekClient.GetShip()</c>).</summary>
    ITrainingObject PlayerShip { get; }

    /// <summary>Resolve a lazy <see cref="TargetRef"/> to its live object, or <c>null</c> when it no
    /// longer exists (native <c>*pTarget</c> boolean test in <c>objectwithinradiuscondition.cpp:73</c>).</summary>
    ITrainingObject? Resolve(TargetRef r);

    // ── ship state (native IshipIGC reads) ─────────────────────────────────────────
    /// <summary><c>IshipIGC::GetAutopilot()</c> (<c>getautopilotcondition.cpp:35</c>).</summary>
    bool GetAutopilot(TargetRef ship);
    /// <summary><c>IshipIGC::GetStateM()</c> bitmask (<c>getstatebitscondition.cpp:36</c>).</summary>
    int GetStateM(TargetRef ship);
    /// <summary><c>IshipIGC::GetControls()</c> — the controls actually applied to the ship
    /// (<c>getcontrolactivecondition.cpp:43</c>, default <c>bUseInputControls=false</c>).</summary>
    ControlData GetShipControls(TargetRef ship);
    /// <summary>The player's raw input controls (<c>g_inputControls</c>, <c>getcontrolactivecondition.cpp:21</c>),
    /// used when a condition opts into <c>bUseInputControls=true</c>.</summary>
    ControlData GetInputControls();
    /// <summary><c>IshipIGC::GetCommandTarget(command)</c> (<c>gettargetcondition.cpp:52</c>).</summary>
    TargetRef? GetCommandTarget(TargetRef ship, int command);

    // ── player events (keyboard / chat) ────────────────────────────────────────────
    /// <summary>Consume-and-return whether the given key has been pressed since the last query
    /// (<c>getkeycondition.cpp:49</c> resets <c>m_pressed</c> on every Evaluate).</summary>
    bool ConsumeKey(int key);
    /// <summary>Push a key press onto the site's key queue from <c>TrainingMission.RecordKeyPress</c>.</summary>
    void RecordKeyPress(int key);
    /// <summary>Consume-and-return whether a chat was sent to the given recipient since the last query
    /// (<c>getchatcondition.cpp:47-52</c>).</summary>
    bool ConsumeChat(int recipient);
    /// <summary>Route a chat event to the mission's chat condition (<c>trainingmission.cpp:327</c>).</summary>
    void RecordChat(int recipient);

    // ── world creation (native actions) ────────────────────────────────────────────
    /// <summary><c>CreateWaypointAction::Execute</c> — spawn a waypoint buoy (<c>createwaypointaction.cpp:37</c>).</summary>
    void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible);
    /// <summary><c>CreateProbeAction::Execute</c> — spawn a probe (<c>createprobeaction.cpp:38</c>).</summary>
    void CreateProbe(int probeId, int typeId, Vector3 position, int sideId, int sectorId);
    /// <summary><c>TrainingMission::CreateDrone</c> (<c>trainingmission.cpp:588</c>); returns the new ship id.</summary>
    int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType);
    /// <summary>Place and configure a drone after creation: sector/cluster, position, orientation
    /// (forward/up), optional spawn command (<c>CreateDroneAction.cpp:60-211</c>).</summary>
    void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up, TargetRef? commandTarget, int commandId);
    /// <summary><c>IshipIGC::SetCommand(command, target, id)</c> (<c>setcommandaction.cpp:71</c>).</summary>
    void IssueCommand(TargetRef ship, int command, TargetRef? target, int commandId);

    // ── ship / camera / HUD configuration (native actions) ────────────────────────
    /// <summary><c>IshipIGC::SetAutopilot(b)</c> (<c>setautopilotaction.cpp:42</c>).</summary>
    void SetAutopilot(TargetRef ship, bool on);
    /// <summary><c>TrekWindow::SetCameraMode / SetViewMode</c> (<c>setdisplaymodeaction.cpp:32</c>).</summary>
    void SetDisplayMode(int cameraMode);
    /// <summary><c>SetTrainingOverlay</c> (<c>sethudoverlayaction.cpp:32</c>).</summary>
    void SetHudOverlay(int overlay);

    /// <summary>
    /// The native <c>turnToFace(delta, dt, ship, &amp;g_setInputControls)</c> helper
    /// (<c>TurnToAction.cpp:62</c>): returns the yaw/pitch/roll corrective turn inputs that would rotate
    /// <paramref name="ship"/> to face <paramref name="target"/>. TR-2 supplies the ship-simulator
    /// implementation; <see cref="TurnToAction"/> applies the native 0.5 damping afterwards.
    /// </summary>
    (float Yaw, float Pitch, float Roll) TurnToFace(TargetRef ship, TargetRef target);

    // ── output (messages / sound) ──────────────────────────────────────────────────
    /// <summary>Deliver a chat line from the commander (<c>messageaction.cpp:52</c>).</summary>
    void ShowMessage(string text);
    /// <summary>Start a sound; returns a stable instance handle (<c>playsoundaction.cpp:41</c>).</summary>
    int PlaySound(int soundId);
    /// <summary><c>ISoundInstance::IsPlaying() == S_OK</c> (<c>playsoundaction.cpp:60</c>).</summary>
    bool IsSoundPlaying(int instance);
    /// <summary><c>ISoundInstance::Stop(true)</c> (<c>playsoundaction.cpp:49</c>).</summary>
    void StopSound(int instance);
}
