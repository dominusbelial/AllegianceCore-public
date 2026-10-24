using System.Numerics;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Training;

/// <summary>One entry of the respawn list (<c>trainingmission.h:24-32</c>): a <see cref="CreateDroneAction"/>
/// plus its downtime and optional spawn group.</summary>
public sealed class RespawnData
{
    public CreateDroneAction? SpawnAction;
    public float Downtime;
    public int SpawnGroup; // char spawnGroup; NA = -1
    public float DestroyedTime;
}

/// <summary>
/// The abstract base of every training mission — the C# port of <c>TrainingMission</c>
/// (<c>trainingmission.h:36-99</c>, <c>trainingmission.cpp:62-935</c>). It owns the goal-list condition,
/// the wait/key conditions, the respawn machinery, the death/kill/station hooks and the kill counter.
/// Concrete missions (e.g. <see cref="Mission2"/>) implement <c>GetMissionID</c>, <c>GetStartSectorID</c>,
/// <c>CreateUniverse</c> and <c>CreateMission</c>. The C# version is decoupled from <c>trekClient</c> by
/// talking to an injected <see cref="ITrainingSite"/>.
/// </summary>
public abstract class TrainingMission
{
    private readonly List<Condition> _waitConditions = new();
    private readonly List<Condition> _keyConditions = new();
    private readonly List<RespawnData> _respawns = new();

    private Condition? _condition;
    private Condition? _deadCondition;
    private int _killCount;

    /// <summary>The injected game bridge (TR-2 binds the host+client; tests bind a fake).</summary>
    protected ITrainingSite Site { get; }

    protected TrainingMission(ITrainingSite site) => Site = site;

    public abstract int GetMissionID();
    public abstract int GetStartSectorId();
    protected abstract void CreateUniverse();
    protected abstract Condition CreateMission();

    /// <summary>Lifecycle entry — mirrors <c>TrainingMission::Start</c> (<c>trainingmission.cpp:153-164</c>).</summary>
    public void Start()
    {
        CreateUniverse();
        _condition = CreateMission();
        _condition.Start();
    }

    /// <summary>
    /// Per-frame hook — mirrors <c>TrainingMission::Frame</c> (<c>trainingmission.cpp:167-266</c>).
    /// Returns <c>true</c> when the mission has terminated (so the caller deletes it), <c>false</c> while
    /// running. When a wait condition is pending it is serviced instead of the goal condition; the goal
    /// list only advances when no wait condition remains.
    /// </summary>
    public bool Frame()
    {
        if (_condition is { } cond)
        {
            ProcessRespawns();

            if (_waitConditions.Count > 0)
            {
                for (int i = _waitConditions.Count - 1; i >= 0; i--)
                {
                    if (_waitConditions[i].Evaluate())
                    {
                        _waitConditions.RemoveAt(i);
                    }
                }
            }
            else
            {
                if (cond.Evaluate())
                    Terminate();
            }
            return false;
        }
        // no play condition -> complete (native path after Terminate, lines 260-265)
        return true;
    }

    /// <summary>Mirrors <c>TrainingMission::Terminate</c> (<c>trainingmission.cpp:269-278</c>): parks the
    /// condition in dead space so <see cref="Frame"/> returns true on the next call.</summary>
    public void Terminate()
    {
        _deadCondition = _condition;
        _condition = null;
    }

    /// <summary><c>AddWaitCondition</c> (<c>trainingmission.cpp:281-285</c>) — the native pushes to the
    /// front; order matters for <c>ResetAction</c>.</summary>
    public void AddWaitCondition(Condition c) => _waitConditions.Insert(0, c);

    /// <summary><c>AddKeyCondition</c> (<c>trainingmission.cpp:416-419</c>).</summary>
    public void AddKeyCondition(Condition c) => _keyConditions.Insert(0, c);

    /// <summary>
    /// <c>RecordKeyPress</c> (<c>trainingmission.cpp:342-413</c>) — the key gate the client's
    /// flight-input path routes presses through while a training mission is running. Faithful
    /// to native: a set of keys is hard-disabled except in Free Flight, and the command-view
    /// keys are gated on <see cref="CommandViewEnabled"/>. Keys that pass are forwarded to the
    /// site's key queue so <c>GetKeyCondition</c> can consume them. Returns whether the press
    /// was accepted (native returns <c>false</c> to swallow a disabled key).
    /// </summary>
    public bool RecordKeyPress(int key)
    {
        // trainingmission.cpp:342-413
        if (key == KeyPauseTm)
        {
            // TK_PauseTM (trainingmission.cpp:347-358): pause/resume the training game
            // clock. Native toggles Time::Pause()/Time::Continue() (ztime.cpp:15-49) and
            // returns without iterating the key conditions — no mission key ever sees it.
            if (TrainingClock.IsPaused)
                TrainingClock.Continue();
            else
                TrainingClock.Pause();
            return true;
        }

        if (key is KeySkipTmGoal or KeyIncreaseTMClockSpeed or KeyDecreaseTMClockSpeed or KeyResetTMClockSpeed)
        {
            // trainingmission.cpp:359-384 — these four handlers are compiled only under
            // #ifdef _DEBUG_TRAINING, a define that appears in NO project file, so in
            // every shipped build they fall through to the default branch (key
            // conditions; inert for every real mission). The port reproduces both
            // halves: shipping mode forwards them like any other key; debug mode runs
            // the native debug bodies (skip-goal is native "XXX NYI", an empty case).
            if (TrainingClock.DebugTrainingKeys)
            {
                switch (key)
                {
                    case KeySkipTmGoal:
                        // native "XXX NYI" (trainingmission.cpp:361-364)
                        break;
                    case KeyIncreaseTMClockSpeed:
                        TrainingClock.SetShift(TrainingClock.GetShift() + 1); // ztime.cpp:58-67 clamp ±3
                        break;
                    case KeyDecreaseTMClockSpeed:
                        TrainingClock.SetShift(TrainingClock.GetShift() - 1);
                        break;
                    case KeyResetTMClockSpeed:
                        TrainingClock.SetShift(0);
                        break;
                }
                return true; // handled in the switch — never reaches the key conditions
            }
            // shipping behaviour: fall through to the key-condition branch below
        }
        // "XXX hack to disable some keys in training" (trainingmission.cpp:388-396): these are
        // swallowed outright except in Free Flight (mission 10).
        bool hardDisabled = key is KeyConModeInvest or KeyTargetSelf or KeySuicide
            or KeyConModeGameState or KeyConModeTeleport or KeyRejectCommand;
        if (hardDisabled && GetMissionID() != Tm10FreeFlight)
            return false;

        // Command-view keys (trainingmission.cpp:397-401): gated on the mission having enabled
        // command view. Free Flight's hard-disabled keys fall through to this same gate (native
        // case fallthrough), so only a command-view-enabled mission lets them through.
        bool commandViewKey = key is KeyViewCommand or KeyConModeCommand or KeyToggleAutoPilot;
        if (commandViewKey && !CommandViewEnabled)
            return false;

        // default branch (trainingmission.cpp:402-410): every remaining key — including
        // TK_CommandFinishTM (207), which has no case of its own — is delivered to the
        // mission's key conditions (GetKeyCondition::KeyPressed, GetKeyCondition.cpp:55-60
        // via the site key queue the port's GetKeyCondition polls).
        Site.RecordKeyPress(key);
        return true;
    }

    /// <summary>Whether this mission enables the command view (native <c>m_commandViewEnabled</c>;
    /// missions that use the command map set it, <c>Training.h:44</c> <c>CommandViewEnabled()</c>).
    /// Mission 2 (Basic Flight) does not, so the command-view keys stay disabled.</summary>
    public virtual bool CommandViewEnabled => false;

    /// <summary><c>RecordChat</c> (<c>trainingmission.cpp:327-331</c>).</summary>
    public void RecordChat(int recipient) => Site.RecordChat(recipient);

    /// <summary><c>ShipLanded</c> (<c>trainingmission.cpp:422-425</c>); missions override to terminate.</summary>
    public virtual bool ShipLanded() => true;

    /// <summary><c>ShipDied</c> (<c>trainingmission.cpp:428-475</c>) — reposition the player ship clear of
    /// the killer and eject. Repositioning is a host concern; TR-1 exposes the hook for TR-2.</summary>
    public virtual void ShipDied()
    {
    }

    /// <summary><c>RestoreShip</c> (<c>trainingmission.cpp:478-512</c>) — restore the ship and add a 5s
    /// override-camera wait condition. Host physics deferred to TR-2.</summary>
    public virtual bool RestoreShip()
    {
        AddWaitCondition(ElapsedTimeCondition.Create(5.0f, Site));
        return true;
    }

    /// <summary><c>ShipKilled</c> (<c>trainingmission.cpp:515-524</c>) — count a wingman the player's side
    /// killed.</summary>
    public virtual void ShipKilled(bool playerSideKilledWingman)
    {
        if (playerSideKilledWingman)
            _killCount++;
    }

    /// <summary><c>KillStationEvent</c> (<c>trainingmission.cpp:533-563</c>) — host responsibility for
    /// "all stations destroyed" detection; deferred to TR-2.</summary>
    public virtual void KillStationEvent()
    {
    }

    public int GetKillCount() => _killCount;

    /// <summary>Returns whether the player ship is in range of a "too close" guard rail.</summary>
    public int CommanderId => -1; // m_commanderID; set by LoadUniverse in TR-2

    /// <summary>
    /// <c>CreatePlaySoundGoal</c> (<c>trainingmission.cpp:896-902</c>): a goal that completes when a
    /// voice-over sound finishes, with the sound as its start action.
    /// </summary>
    protected Goal CreatePlaySoundGoal(int soundId)
    {
        var play = new PlaySoundAction(Site, soundId);
        var goal = new Goal(new SoundFinishedCondition(play));
        goal.AddStartAction(play);
        return goal;
    }

    /// <summary><c>CreateTooLongCondition</c> (<c>trainingmission.cpp:927-932</c>): periodically repeat a
    /// prompt sound once the player dawdles.</summary>
    protected Condition CreateTooLongCondition(float delay, int soundId)
    {
        var periodic = new PeriodicCondition(new NTimesCondition(new TrueCondition(), 2, sticky: true), delay, Site);
        return new ConditionalAction(periodic, new PlaySoundAction(Site, soundId));
    }

    /// <summary><c>CreateTooCloseCondition</c> (<c>trainingmission.cpp:905-924</c>): a rotating
    /// "don't get too close" reminder when the player is both within radius and moving towards a place.</summary>
    protected Condition CreateTooCloseCondition(TargetRef place)
    {
        TargetRef ship = Site.PlayerShip.Ref;
        var within = new ObjectWithinRadiusCondition(Site, ship, place, Site.PlayerShip.Radius * 10.0f);
        var moving = new ObjectMovingTowardsCondition(Site, ship, place);
        var and = new AndCondition(within, moving);
        var msg = new MessageAction(Site);
        msg.AddMessage($"Don't get too close to the target!");
        var periodic = new PeriodicCondition(and, 2.0f, Site);
        return new ConditionalAction(periodic, msg, ConditionalAction.DelayedExecution);
    }

    private void ProcessRespawns()
    {
        // trainingmission.cpp:176-221
        int skipGroup = -1; // NA
        for (int idx = 0; idx < _respawns.Count; idx++)
        {
            var rd = _respawns[idx];
            if (rd.SpawnAction is not { } cda)
                continue;
            bool shipExists = Site.Resolve(new TargetRef(ObjectType.Ship, cda.ShipId)) is not null;
            bool readyToSpawn = !shipExists && Site.TimeSeconds > rd.DestroyedTime + rd.Downtime;
            if (!readyToSpawn)
                continue;

            if (rd.DestroyedTime == 0)
            {
                rd.DestroyedTime = Site.TimeSeconds;
            }
            else if (rd.SpawnGroup == -1)
            {
                cda.Execute();
                rd.DestroyedTime = 0;
            }
            else if (rd.SpawnGroup != skipGroup)
            {
                var group = new List<RespawnData> { rd };
                bool waveReady = true;
                foreach (var other in _respawns)
                {
                    if (other.SpawnGroup == rd.SpawnGroup && other.SpawnAction?.ShipId != cda.ShipId)
                    {
                        if (Site.Resolve(new TargetRef(ObjectType.Ship, other.SpawnAction!.ShipId)) is not null)
                        {
                            waveReady = false;
                            break;
                        }
                        if (other.DestroyedTime == 0)
                            other.DestroyedTime = Site.TimeSeconds;
                        if (Site.TimeSeconds > other.DestroyedTime + other.Downtime)
                            group.Add(other);
                        else
                        {
                            waveReady = false;
                            break;
                        }
                    }
                }
                if (waveReady)
                {
                    foreach (var rdg in group)
                    {
                        rdg.SpawnAction!.Execute();
                        rdg.DestroyedTime = 0;
                    }
                    break;
                }
                skipGroup = rd.SpawnGroup;
            }
        }
    }

    /// <summary><c>AddRespawn</c> (<c>trainingmission.cpp:287-316</c>). <paramref name="spawnOnAdd"/>
    /// spawns immediately if not already present, else arms the timer.</summary>
    public void AddRespawn(CreateDroneAction action, float downtime, int spawnGroup = -1, bool spawnOnAdd = false)
    {
        var rd = new RespawnData
        {
            SpawnAction = action,
            Downtime = downtime,
            SpawnGroup = spawnGroup,
        };

        if (spawnOnAdd)
        {
            bool exists = Site.Resolve(new TargetRef(ObjectType.Ship, action.ShipId)) is not null;
            if (!exists)
                action.Execute();
            rd.DestroyedTime = 0;
        }
        else
        {
            rd.DestroyedTime = Site.TimeSeconds;
        }

        _respawns.Insert(0, rd);
    }
}
