using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Games;

/// <summary>
/// P871 / lobby::Create game — the new-game wizard's pure selection model: the
/// port of native <c>NewGameScreen</c>'s create flow
/// (<c>WinTrek/newgamescreen.cpp:1034-1041</c>, which runs
/// <c>Invalid(true)</c> then sends <c>FM_CS_MISSIONPARAMS</c>) combined with the
/// lobby's <c>CREATE_MISSION_REQ</c> server/core/map selection
/// (<c>lobby/messageslc.h:54-59</c>).
/// <para>
/// The wizard selects a LISTED server (the "server" field of native's request),
/// that server's CORE (native's <c>IGCStaticFile</c>), a MAP from the lobby's
/// authoritative catalog, and the game name. Its mission-parameter draft is the
/// SAME <see cref="MissionParamsMsg"/> the owner later edits, validated with the
/// shared <see cref="MissionParamsMsg.Invalid"/> rule before anything is sent.
/// </para>
/// </summary>
public sealed class CreateGameWizard
{
    public sealed record Catalog(IReadOnlyList<ServerChoice> Servers, IReadOnlyList<string> Cores,
        IReadOnlyList<string> Maps)
    {
        public static Catalog Empty { get; } = new([], [], []);
    }

    public sealed record ServerChoice(string Id, string Name, string Address, int Port,
        string Core, string MissionName, int CurrentGames = 0, int MaxGames = 20)
    {
        // Native IsFull and cur/max display, WinTrek/CreateGameDialog.h:20,87.
        public bool IsFull => CurrentGames >= MaxGames;
        public string DisplayName => $"{Name} ({CurrentGames}/{MaxGames})";
    }

    public enum Field { Server, Core, Map, Name, Create }

    /// <summary>The wizard's catalog (empty until the lobby answers).</summary>
    public Catalog Options { get; private set; } = Catalog.Empty;

    /// <summary>Which row the cursor is on.</summary>
    public Field Cursor { get; private set; } = Field.Server;

    public int ServerIndex { get; private set; }
    public int CoreIndex { get; private set; }
    public int MapIndex { get; private set; }

    /// <summary>The typed game name (native's game-name edit box).</summary>
    public string GameName { get; private set; } = "";

    /// <summary>The mission-parameter draft (owner editing; same shape as the
    /// advertised params and the server's MISSIONPARAMS handler adopts). Seeded
    /// with native's <c>MissionParams::Reset</c> winning-condition defaults
    /// (conquest ON at 100, territory OFF at 100) so a fresh form is valid.</summary>
    public MissionParamsMsg Params { get; set; } = new()
    {
        NTems = 2,
        IGoalConquestPercentage = 100,
        IGoalTerritoryPercentage = 100,
    };

    /// <summary>Last validation/relay message shown under the form.</summary>
    public string Status { get; set; } = "";

    public static readonly IReadOnlyList<Field> Fields =
        [Field.Server, Field.Core, Field.Map, Field.Name, Field.Create];

    public void ApplyCatalog(Catalog catalog)
    {
        Options = catalog;
        ServerIndex = catalog.Servers.Count > 0 ? Math.Clamp(ServerIndex, 0, catalog.Servers.Count - 1) : 0;
        CoreIndex = catalog.Cores.Count > 0 ? Math.Clamp(CoreIndex, 0, catalog.Cores.Count - 1) : 0;
        MapIndex = catalog.Maps.Count > 0 ? Math.Clamp(MapIndex, 0, catalog.Maps.Count - 1) : 0;
        // Selecting a server fixes its core (native's request carried the
        // server's own IGC static file, lobby/client.cpp:396).
        if (catalog.Servers.Count > 0 && catalog.Cores.Count > 0)
        {
            int index = IndexOf(catalog.Cores, SelectedServer!.Core);
            if (index >= 0) CoreIndex = index;
        }
    }

    public ServerChoice? SelectedServer =>
        Options.Servers.Count > 0 ? Options.Servers[Math.Clamp(ServerIndex, 0, Options.Servers.Count - 1)] : null;
    public string? SelectedCore => Options.Cores.Count > 0 ? Options.Cores[Math.Clamp(CoreIndex, 0, Options.Cores.Count - 1)] : null;
    public string? SelectedMap => Options.Maps.Count > 0 ? Options.Maps[Math.Clamp(MapIndex, 0, Options.Maps.Count - 1)] : null;

    public void Move(int delta)
    {
        int index = ((int)Cursor + delta) % Fields.Count;
        if (index < 0) index += Fields.Count;
        Cursor = Fields[index];
    }

    /// <summary>LEFT/RIGHT on a combo (server/core/map).</summary>
    public void Adjust(int delta)
    {
        switch (Cursor)
        {
            case Field.Server:
                if (Options.Servers.Count == 0) return;
                ServerIndex = Step(ServerIndex, delta, Options.Servers.Count);
                if (SelectedServer is { } server && Options.Cores.Count > 0)
                {
                    int index = IndexOf(Options.Cores, server.Core);
                    if (index >= 0) CoreIndex = index;
                }
                break;
            case Field.Core:
                if (Options.Cores.Count == 0) return;
                CoreIndex = Step(CoreIndex, delta, Options.Cores.Count);
                break;
            case Field.Map:
                if (Options.Maps.Count == 0) return;
                MapIndex = Step(MapIndex, delta, Options.Maps.Count);
                break;
        }
    }

    /// <summary>Type into the game-name box (native's edit control), capped.</summary>
    public void TypeName(string text)
    {
        foreach (char c in text)
        {
            if (GameName.Length >= 32) break;
            if (c is >= ' ' and not '\u007f') GameName += c;
        }
    }

    public void Backspace() { if (GameName.Length > 0) GameName = GameName[..^1]; }

    /// <summary>The payload the lobby create request carries, or null with the
    /// validation message (native NewGameScreen::OnButtonCreate).
    /// </summary>
    public CreateGameRequest? TryCreate()
    {
        Status = "";
        if (SelectedServer is not { } server) { Status = "No server selected."; return null; }
        if (server.IsFull) { Status = "That server is full. Choose another server."; return null; }
        if (SelectedCore is not { } core) { Status = "No core selected."; return null; }
        if (!string.Equals(core, server.Core, StringComparison.OrdinalIgnoreCase))
        {
            Status = "That core is not the one the server runs.";
            return null;
        }
        if (SelectedMap is not { } map) { Status = "No map selected."; return null; }
        string name = GameName.Trim();
        if (name.Length == 0) { Status = "Type a game name first."; return null; }

        var draft = Params.Clone();
        draft.StrGameName = name;
        draft.SzIgcStaticFile = core;
        if (draft.Invalid(bFromClient: true) is { } why) { Status = why; return null; }
        Status = "Requesting game…";
        return new CreateGameRequest(server.Id, core, map, name, draft);
    }

    /// <summary>The lobby create-request body (same fields as the endpoint).</summary>
    public sealed record CreateGameRequest(string ServerId, string Core, string Map,
        string GameName, MissionParamsMsg Params);

    /// <summary>Record the lobby's answer on the status line.</summary>
    public void Acknowledge(string? requestId, string? error)
        => Status = error is not null ? error
            : requestId is not null ? $"Game requested ({requestId})." : "Game requested.";

    private static int Step(int value, int delta, int count) => count == 0 ? 0 : ((value + delta) % count + count) % count;

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
