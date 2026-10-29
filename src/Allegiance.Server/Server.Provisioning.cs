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
/// server and joins the returned cookie. Native hosts several missions per
/// process; the port appends player-created worlds without changing existing
/// missions or the NA/first-mission route (FedSrv.CPP:489-493).</para>
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

    /// <summary>Additional worlds require capacity (native MaxGames,
    /// FedSrv/FedSrv.CPP:9560-9562; WinTrek/CreateGameDialog.h:20).
    /// Research showcases retain their provisioning refusal.</summary>
    internal bool CanHostPlayerGame()
    {
        return _config.Showcase.TechMode != "research" && _missions.Count < _config.MaxGames;
    }

    /// <summary>Drain the claimed game requests the lobby heartbeat handed over
    /// (one per tick is enough — each one assembles a whole world).</summary>
    private void DrainClaimedGames()
    {
        if (_heartbeat is null) return;
        while (_heartbeat.TryTakeClaimedGame(out var claim))
        {
            var mission = ProvisionPlayerGame(claim);
            _heartbeat.CompleteGame(claim.Id, mission?.Cookie);
        }
    }

    /// <summary>
    /// Create the mission a claimed request asked for, addressed by its cookie.
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
        // Recheck on the sim thread: a claim may have raced another creation
        // since CanHostPlayerGame (native full test, CreateGameDialog.h:20).
        if (_missions.Count >= _config.MaxGames)
        {
            Console.WriteLine($"[provision] '{claim.GameName}' refused: server game limit reached ({_missions.Count}/{_config.MaxGames})");
            return null; // DrainClaimedGames reports Failed via the ready acknowledgement.
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

        // Native identifies the created mission by cookie (lobby/client.cpp:390-465).
        // Append so NA and existing peers keep their configured worlds.
        _config.Games.Add(config);
        _missions.Add(mission);
        mission.MissionId = checked((short)_missions.Count);
        ParamsOf(mission).StrGamePassword = parameters.StrGamePassword;
        mission.PlayerCreated = true;                            // SIM-0146: the lobby-directed lifecycle
        SpawnInitialMiners(mission);
        MarkLobbyDirty("player-game-created");
        Console.WriteLine($"[provision] mission created for '{claim.GameName}' (request {claim.Id}): "
            + $"map {config.Map}, {mission.Sectors.Count} sectors, {mission.Sides.Count} side(s), "
            + $"{mission.Warps.Count} warp(s), {mission.Sectors.Sum(s => s.Stations.Count)} station(s) — "
            + $"player-created (SIM-0146 reap armed, FedSrv.CPP:7871-7910), join by cookie {mission.Cookie}");
        return mission;
    }
}
