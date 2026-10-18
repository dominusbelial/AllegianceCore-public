using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    public readonly record struct SideCivilization(short CivId, bool Random);
    public readonly record struct SideGameState(byte Conquest, byte Territory, byte Flags, byte Artifacts);
    public readonly record struct Departure(short ShipId, QuitSideReason Reason, string Message, bool FromMission);
    private readonly Dictionary<short, SideCivilization> _sideCivilizations = new();
    private readonly Dictionary<short, SideGameState> _sideGameStates = new();
    public IReadOnlyDictionary<short, SideCivilization> SideCivilizations => _sideCivilizations;
    public IReadOnlyDictionary<short, SideGameState> SideGameStates => _sideGameStates;
    public Departure? LastDeparture { get; private set; }
    /// <summary>ENTER_GAME is the sync-complete barrier, not a mission-stage change
    /// (Messages.h:831-835; clintlib.cpp:3851-3857).</summary>
    public bool InGame { get; private set; }

    private void ResetInterfaceState()
    {
        InGame = false;
        _sideCivilizations.Clear();
        _sideGameStates.Clear();
        LastDeparture = null;
    }

    // appmsg.cpp:2530-2539: civilization and random-faction choice are independent.
    private void Apply(ChangeTeamCiv message)
    {
        if (message.SideId is < 0 or >= GameState.MaxSides) return;
        _sideCivilizations[message.SideId] = new(message.CivId, message.Random);
        if (SideRoster is { } roster)
            SideRoster = new SideRoster { Sides = roster.Sides.Select(s => s.SideId == message.SideId
                ? s with { CivId = message.CivId } : s).ToArray() };
    }

    // appmsg.cpp:2392-2407: replace all four counters for each side, in side-id order.
    private void Apply(GameState message)
    {
        for (short side = 0; side < GameState.MaxSides; side++)
            _sideGameStates[side] = new(message.Conquest[side], message.Territory[side],
                message.NFlags[side], message.NArtifacts[side]);
    }

    // appmsg.cpp:3040-3045; clintlib.cpp:3851-3857. Saved loadout-file handling
    // belongs to the native local preference system; live ship equipment survives.
    private void Apply(EnterGame _) => InGame = true;

    // appmsg.cpp:584-606: current flight cluster only, reverse iteration, no creation.
    private void Apply(ProbesUpdate message)
    {
        if (!InGame || MyShipId is not { } me || !_ships.TryGetValue(me, out var own)) return;
        for (int i = message.Entries.Length - 1; i >= 0; i--)
        {
            var entry = message.Entries[i];
            if (_probes.TryGetValue(entry.ProbeId, out var probe) && probe.ClusterId == own.PoseSectorId)
                probe.Fraction = entry.BpFraction / 240f;
        }
    }

    // appmsg.cpp:1692-1718: ignore a stale/mismatched sector; a conversion keeps
    // the building effect alive, an explosive termination removes it with the rock.
    private void Apply(AsteroidDestroyed message)
    {
        if (!InGame || !_asteroids.TryGetValue(message.AsteroidId, out var rock)
            || rock.ClusterId != message.ClusterId) return;
        if (message.ExplodeF)
        {
            Apply(new ExplosionEvent { Type = ExplosionType.Asteroid, ClusterId = rock.ClusterId,
                Position = new(rock.Position[0], rock.Position[1], rock.Position[2]), Scale = rock.Radius });
            _buildingEffects.Remove(message.AsteroidId);
        }
        _asteroids.Remove(message.AsteroidId);
        _asteroidLive.Remove(message.AsteroidId);
        MarkSnapshotDirty();
    }

    // appmsg.cpp:2993-3002; clintlib.cpp:3889-4034. Keep player/ready identity,
    // clear leadership and team status, and move the player to SIDE_TEAMLOBBY (-2).
    private void Apply(QuitSide message)
    {
        if (!_ships.TryGetValue(message.ShipId, out var ship) || ship.SideId < 0) return;
        ApplySideLeave(message.ShipId, message.Reason);
        short oldSide = ship.SideId;
        bool own = message.ShipId == (JoinedShipId ?? MyShipId);
        if (_leaderShipIdBySideId.GetValueOrDefault(oldSide, (short)-1) == message.ShipId)
            _leaderShipIdBySideId.Remove(oldSide);
        if (MissionOwnerBroadcast?.ShipId == message.ShipId) MissionOwnerBroadcast = null;
        _moneyByShipId[message.ShipId] = 0;
        _autoDonateTargetByShipId.Remove(message.ShipId);
        ship.SideId = -2;
        if (ship.Player is { } player) { player.ISide = -2; player.Money = 0; }
        ship.IsGone = true;
        ship.PoseSectorId = null;
        ship.SectorId = ship.StationId = ship.ParentId = -1;
        ship.Loadout = ship.Cargo = null;
        if (own)
        {
            ClearTeamWorld();
            MySideId = -2;
            MyAcceptedCommand = null;
            _myQueuedCommand = null;
        }
        else if (InGame && _mission?.Stage == MissionStageMessage.StageStarted)
        {
            string? suffix = oldSide != MySideId ? $" has left {SideRoster?.Sides.FirstOrDefault(s => s.SideId == oldSide).Name ?? $"side {oldSide}"}."
                : (int)message.Reason switch
                {
                    0 => " was booted from the team by the team leader.",
                    1 => " was booted from the game by the mission owner.",
                    2 => " was booted from the team by an admin.",
                    4 => " was booted because of a squad change.",
                    6 => " was booted because he/she did not meet the new rank limits.",
                    7 => " was booted to reduce the team size within it's new limits.",
                    8 => " has quit.", 9 => " is MIA.",
                    10 => " has been booted due to a duplicate logon.",
                    13 => " is switching sides.", _ => null,
                };
            if (suffix is not null) PostSystemLine(ship.CharacterName + suffix);
        }
        LastDeparture = new(message.ShipId, message.Reason, message.Message, false);
        MarkSnapshotDirty();
    }

    // OnQuitSide (clintlib.cpp:3723-3787) + CmissionIGC::ResetMission
    // (Igc/missionigc.cpp:3804-3831): clear sector contents, keep mission/player roster.
    private void ClearTeamWorld()
    {
        InGame = false;
        SectorGeneration++;
        foreach (var ship in _ships.Values)
        {
            ship.PoseSectorId = null;
            ship.SectorId = ship.StationId = -1;
            ship.Unknown = true;
            ship.Latest = null;
        }
        Graph = null;
        ViewedClusterId = null;
        ViewedClusterPosition = null;
        _clusters.Clear(); _stations.Clear(); _warps.Clear();
        _asteroids.Clear(); _asteroidLive.Clear(); _stationLive.Clear();
        _probes.Clear(); _mines.Clear(); _missiles.Clear(); _projectiles.Clear();
        _ownProjectileCount = 0; _projectileDrainCursor = 0;
        _treasures.Clear(); _buildingEffects.Clear(); _chaff.Clear();
        _explosions.Clear(); _hullHits.Clear(); _pendingBallots.Clear();
        _newContactCues.Clear(); _highlightedClusters.Clear();
        _clusterWarningBySectorId.Clear(); _clusterWarningCues.Clear();
        _lastMissileFiredByShip.Clear();
        ClearRipcord();
        _missileLock = MissileLockState.None;
        _missileTrack = MissileTrackState.Clear;
        _leadIndicator = LeadIndicatorState.None;
        _miningAsteroidId = -1;
        ShipWarnings.Reset();
    }

    // appmsg.cpp:2501-2526; clintlib.cpp:4037-4077. Departure from a real
    // side first follows QUIT_SIDE; a remote mission departure deletes the player.
    private void Apply(QuitMission message)
    {
        if (!_ships.TryGetValue(message.ShipId, out var ship)) return;
        if (message.ShipId == (JoinedShipId ?? MyShipId))
        {
            Reset(); // OnQuitMission clears mission, player list and chats
            OwnMissionDeparture = message;
        }
        else
        {
            if (ship.SideId >= 0)
                Apply(new QuitSide { ShipId = message.ShipId, Reason = message.Reason, Message = message.Message });
            _ships.Remove(message.ShipId);
            _mutedPilots.Remove(message.ShipId);
            _roster.Remove(message.ShipId);
            _moneyByShipId.Remove(message.ShipId);
            _autoDonateTargetByShipId.Remove(message.ShipId);
            _positionRequests.RemoveWhere(r => r.ShipId == message.ShipId);
            MarkSnapshotDirty();
        }
        LastDeparture = new(message.ShipId, message.Reason, message.Message, true);
    }
}
