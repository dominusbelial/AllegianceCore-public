using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Server;

/// <summary>
/// P871b / lobby::Create game — the SERVER half of the new-game wizard: a
/// registered game server picks up the request it claimed from the lobby, creates
/// the mission from the wizard's parameters, and makes it the world a joining
/// client lands in.
///
/// <para><b>Native shape.</b> <c>CREATE_MISSION_REQ</c> → the chosen server
/// creates the mission (<c>lobby/client.cpp:390-465</c>) → the client connects to
/// that server and joins the mission it just created. Native hosts one mission per
/// process; the port may host several (<c>ServerConfig.Games</c>), so a
/// player-created mission is inserted at index 0 — the slot every join path uses
/// (<c>AssignShip</c>, the join burst, the lobby advertisement) — and the
/// configured worlds stay live behind it.</para>
///
/// <para><b>SIM-0146.</b> A mission created for a player sets
/// <see cref="Mission.PlayerCreated"/>, which is what native's
/// <c>CheckForNoShows</c> reaps after ten empty seconds
/// (<c>FedSrv.CPP:7871-7910</c>). Configured, admin, showcase and skirmish worlds
/// never set it, so they persist.</para>
/// </summary>
public sealed partial class Server
{
    /// <summary>P871b test seam (InternalsVisibleTo): supplies the lobby heartbeat,
    /// so a test can drive the claim poll against an in-process lobby.</summary>
    internal Func<LobbyConfig, LobbyHeartbeat>? HeartbeatFactory { get; set; }

    /// <summary>P871b test seam (InternalsVisibleTo): run one heartbeat/claim poll.</summary>
    internal Task PumpLobbyAsync() => _heartbeat?.PumpAsync() ?? Task.CompletedTask;

    /// <summary>The idle-slot predicate the lobby says CREATE_MISSION_REQ needs:
    /// the join target must exist, be pre-game and have no players. Native's lobby
    /// only picks a server that is not in a game.</summary>
    internal bool CanHostPlayerGame()
    {
        if (_missions is not { Count: > 0 }) return true;      // bare transport: the first request creates the world
        var target = _missions[0];
        return target.Stage == MissionStage.NotStarted && !HasMissionPlayers(target);
    }

    /// <summary>Drain the claimed game requests the lobby heartbeat handed over
    /// (one per tick is enough — each one assembles a whole world).</summary>
    private void DrainClaimedGames()
    {
        if (_heartbeat is null) return;
        while (_heartbeat.TryTakeClaimedGame(out var claim))
            ProvisionPlayerGame(claim);
    }

    /// <summary>
    /// Create the mission a claimed request asked for and make it the join target.
    /// Never throws: a bad request is logged and dropped (the lobby's request is
    /// already claimed, and native has no un-claim message).
    /// </summary>
    internal Mission? ProvisionPlayerGame(ClaimedGame claim)
    {
        if (_config.Showcase.TechMode == "research")
        {
            Console.WriteLine($"[provision] '{claim.GameName}' refused: this server runs a research showcase world");
            return null;
        }
        if (!PlayerGameProvisioning.TryDecodeParams(claim.ParamsWire, out var parameters, out var decodeError))
        {
            Console.WriteLine($"[provision] '{claim.GameName}' refused: {decodeError}");
            return null;
        }
        var config = PlayerGameProvisioning.TryCreate(claim, parameters, _config.StaticCore, out var createError);
        if (config is null)
        {
            Console.WriteLine($"[provision] '{claim.GameName}' refused: {createError}");
            return null;
        }
        // Native InitSide falls back to an unused civilization when the params'
        // rgCivID slot names one this core does not have (fsmission.cpp:33-62);
        // the port's CivIds contract uses -1 for "random", so an unknown id from
        // a stale client becomes random rather than a refused game.
        for (int i = 0; i < config.CivIds.Length; i++)
            if (config.CivIds[i] >= 0 && !_core.Civilizations.ContainsKey((short)config.CivIds[i]))
                config.CivIds[i] = -1;
        string mapPath = Path.Combine(_config.ArtPath, config.Map);
        if (!File.Exists(mapPath))
        {
            Console.WriteLine($"[provision] '{claim.GameName}' refused: map '{config.Map}' not found under artPath");
            return null;
        }

        Mission mission;
        try
        {
            mission = AssembleGameMission(config, _core, _landingBays, _weaponMuzzles,
                _stationLandingBays, _collisionHulls, playerCreated: true);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            Console.WriteLine($"[provision] '{claim.GameName}' refused: {e.Message}");
            return null;
        }

        // The wizard's world becomes slot 0 — the slot every join path uses
        // (AssignShip, the join burst, the lobby advertisement). The configured
        // worlds keep running behind it, and both lists stay index-aligned so the
        // game-over rotation and GameSettings keep working.
        _config.Games.Insert(0, config);
        _missions.Insert(0, mission);
        mission.PlayerCreated = true;                            // SIM-0146: the lobby-directed lifecycle
        SpawnInitialMiners(mission);
        MarkLobbyDirty("player-game-created");
        Console.WriteLine($"[provision] mission created for '{claim.GameName}' (request {claim.Id}): "
            + $"map {config.Map}, {mission.Sectors.Count} sectors, {mission.Sides.Count} side(s), "
            + $"{mission.Warps.Count} warp(s), {mission.Sectors.Sum(s => s.Stations.Count)} station(s) — "
            + "player-created (SIM-0146 reap armed, FedSrv.CPP:7871-7910), joins now land here");
        return mission;
    }
}
