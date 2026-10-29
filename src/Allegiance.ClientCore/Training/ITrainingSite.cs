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

    // ── ship / combat state (M3/M4 seams, native <c>IdamageIGC</c>/<c>IshipIGC</c>) ─────
    /// <summary>TRUE when a ship is destroyed: it is absent <em>or</em> its armor fraction is 0 —
    /// the expansion of <c>GetShipIsDestroyedCondition</c>
    /// (<c>getshipisdestroyedcondition.h:27-28</c> via <c>getshipisdamagedcondition.cpp:44-57</c>).</summary>
    bool IsShipDestroyed(TargetRef ship);
    /// <summary>TRUE when the ship's accepted-or-current command id equals <paramref name="commandId"/>
    /// (<c>getcommandcondition.cpp:44-49</c>); TRUE when the ship is absent.</summary>
    bool HasCommand(TargetRef ship, int commandId);
    /// <summary>The model's current sector id (<c>getsectorcondition.cpp:44-49</c>), or <c>-1</c> when absent.</summary>
    int GetSector(TargetRef model);
    /// <summary>TRUE when <paramref name="ship"/> has <paramref name="target"/> in scanner range
    /// (<c>IshipIGC::InScannerRange</c>, <c>shipseesobjectcondition.cpp:47-50</c>).</summary>
    bool ShipSees(TargetRef ship, TargetRef target);
    /// <summary>The live probe count of the mission's cluster world
    /// (<c>trekClient.GetCore()-&gt;GetProbes()-&gt;n()</c>, <c>getprobecountcondition.cpp:31</c>).
    /// Mission 6's <c>GetProbeCountCondition</c> compares the player's deployed probes to this.</summary>
    int GetProbeCount();
    /// <summary>The current radar LOD mode id (<c>GetWindow()-&gt;GetRadarMode()</c>,
    /// <c>getradarlodcondition.cpp:31</c>) — the <see cref="Rendering.RadarMode"/> integer.</summary>
    int GetRadarLod();
    /// <summary>The live ammunition count of <paramref name="ship"/> (<c>IshipIGC::GetAmmo()</c>,
    /// <c>getshiphasammocondition.cpp:32-34</c>). The training condition compares this against a
    /// threshold; a ship with no mounted ammo magazine reports 0.</summary>
    int GetAmmo(TargetRef ship);

    // ── command-view / escalation state (M5/M6 seams) ─────────────────────────
    /// <summary>TRUE when <paramref name="model"/> is the object the player currently has picked in
    /// the command view (<c>GetPickedCondition.cpp:37</c> — the console's picked object).</summary>
    bool GetPicked(TargetRef model);
    /// <summary>The sector id the player is currently viewing (<c>GetViewSectorCondition.cpp:34</c> —
    /// <c>trekClient.GetViewCluster()</c>).</summary>
    int GetViewSector();
    /// <summary>The model's armour fraction (0 = destroyed/absent, 1 = pristine) — the
    /// <c>IdamageIGC::GetFraction</c> read behind <c>GetShipIsDamagedCondition</c>
    /// (<c>getshipisdamagedcondition.cpp:56</c>).</summary>
    float GetFraction(TargetRef model);
    /// <summary>The last command the player acknowledged (queued-order accept; the static
    /// <c>m_cCommandAcknowledged</c> in <c>CommandAcknowledgedCondition.cpp:21</c>), <c>-3</c>=c_cidDoNothing.</summary>
    int GetCommandAcknowledged();

    // ── combat / equipment / UI actions (native actions) ───────────────────────────
    /// <summary>Damage the <em>player's</em> ship's shield or hull (<c>DoDamageAction.cpp:26-50</c>).</summary>
    void DealDamage(bool toHull);
    /// <summary>Damage a specific ship by a collision amount, then reset its controls and fire all
    /// its weapons (<c>DoDamageAdjustableAction</c>, dodamageadjustableaction.cpp:27-36 —
    /// <c>ReceiveDamage</c> then <c>SetStateBits(weaponsMaskIGC|buttonsMaskIGC, allWeaponsIGC)</c>
    /// and <c>SetControls</c> reset).</summary>
    void DamageShipAdjustable(TargetRef ship, float amount);
    /// <summary>Toggle the player's selected weapon mount (<c>ToggleWeaponAction.cpp:29-47</c>).</summary>
    void ToggleWeapon(int mount);
    /// <summary>Swap a mounted part of an equipment type to a mount (<c>SwapWeaponAction.cpp:28-37</c>).</summary>
    void SwapWeapon(int equipmentType, int mount);
    /// <summary>Heal and re-arm the player's ship (shield/hull to 1, energy/ammo/fuel to max, fill
    /// cargo with missiles+ammo) (<c>RepairRearmAction.cpp:37-74</c>).</summary>
    void RepairAndRearm();
    /// <summary>Show a client pane, turning off the sibling panes (<c>ShowPaneAction.cpp:30-38</c>).</summary>
    void ShowPane(int paneMask);
    /// <summary>Show/hide the enemy-detection eyeball on the player's ship status
    /// (<c>ShowEyeballAction.cpp:28-35</c>).</summary>
    void SetEyeball(bool shown);
    /// <summary>Set an object's scan signature (<c>SetSignatureAction.cpp:46-49</c>).</summary>
    void SetSignature(ObjectType type, int id, float signature);

    // ── M8 nanite / repair / station seams (native mission8.cpp) ──────────────────
    /// <summary>TRUE when a ship has a part of the given <paramref name="partId"/> mounted (a part
    /// with that part-type object id and a mount id &gt;= 0) — port of <c>GetShipHasMountedCondition</c>
    /// (<c>getshiphasmountedcondition.cpp:36-45</c>). Mission 8 gates goal 1 on the player mounting
    /// the Nanite Repair System (part 190).</summary>
    bool HasPartMounted(TargetRef ship, int partId);
    /// <summary>Mount a part onto a ship — the write behind <c>SetDroneNanAction</c>
    /// (<c>setdronenanaction.cpp:36-52</c>, mounts Nanite 190 on a wingman) and the player's own
    /// weapon mount that <c>GetShipHasMountedCondition</c> detects. <paramref name="mount"/> is the
    /// native mount id (0 = front weapon slot).</summary>
    void MountPart(TargetRef ship, int partId, int mount = 0);
    /// <summary>Damage a station's armour (<c>IstationIGC::ReceiveDamage</c>,
    /// <c>stationIGC.cpp:147-156</c>; a negative amount repairs) — the write behind Mission 8
    /// goal 1's start action (<c>mission8.cpp:203</c>), which knocks the home station down so the
    /// player can practise repairing it with the nanite gun.</summary>
    void DamageStation(int stationId, float amount);

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
    /// <summary><c>TrainingMission::CreateDrone</c> (<c>trainingmission.cpp:588</c>); returns the new ship id.
    /// <paramref name="behaviour"/> (wingman <c>c_wbbm*</c> mask), <paramref name="stationType"/> (constructor
    /// station type, <c>NA</c>=-1) and <paramref name="techLevel"/> (negative tech-level loadout) mirror the
    /// native <c>CreateDroneAction</c> fields (<c>createdroneaction.cpp:91-198</c>).</summary>
    int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType,
        int behaviour = 0, int stationType = -1, int techLevel = 1);
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
