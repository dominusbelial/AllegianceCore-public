namespace Allegiance.SinglePlayer;

/// <summary>Shared Classic/Modern LAN setup. Secrets live only for this launch.</summary>
public sealed class HostLanScreenModel
{
    public SkirmishOptions Options { get; private set; } = new() { HostLan = true, StartAsSpectator = false };
    public string PortText { get; private set; } = SkirmishOptions.DefaultHostPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public int Selected { get; private set; }
    public const int StartRow = 7, BackRow = 8, RowCount = 9;
    public const string NetworkHelp = "Firewall blocked? Friends see no LAN listing or connection times out. Allow the game on your private network.";
    public const string InternetHelp = "Internet friends: forward TCP/UDP game port, or use a Tailscale address in Direct Connect. Host quitting ends the game.";
    public void Move(int delta) => Selected = (Selected + delta + RowCount) % RowCount;
    public void Type(char c)
    {
        if (c < 32 || c > 126) return;
        if (Selected == 0 && Options.GameName.Length < 63) Options = Options with { GameName = Options.GameName + c };
        if (Selected == 1 && char.IsAsciiDigit(c) && PortText.Length < 5) PortText += c;
        if (Selected == 2 && Options.Password.Length < 15) Options = Options with { Password = Options.Password + c };
    }
    public void Backspace()
    {
        if (Selected == 0 && Options.GameName.Length > 0) Options = Options with { GameName = Options.GameName[..^1] };
        if (Selected == 1 && PortText.Length > 0) PortText = PortText[..^1];
        if (Selected == 2 && Options.Password.Length > 0) Options = Options with { Password = Options.Password[..^1] };
    }
    public void Change(int delta)
    {
        int Race(int id) => SkirmishOptions.Civilizations[(Array.FindIndex(SkirmishOptions.Civilizations, c => c.Id == id)
            + delta + SkirmishOptions.Civilizations.Length) % SkirmishOptions.Civilizations.Length].Id;
        Options = Selected switch
        {
            3 => Options with { Size = Options.Size == SkirmishSize.Small ? SkirmishSize.Medium : SkirmishSize.Small },
            4 => Options with { Difficulty = (SkirmishDifficulty)(((int)Options.Difficulty + delta + 3) % 3) },
            5 => Options with { PlayerCiv = Race(Options.PlayerCiv) },
            6 => Options with { EnemyCiv = Race(Options.EnemyCiv) },
            _ => Options
        };
    }
    public SkirmishOptions CreateOptions()
    {
        if (!int.TryParse(PortText, out int port)) throw new ArgumentException("Enter a game port of 1024..65534.");
        var options = Options with { HostPort = port };
        _ = options.CreateConfig();
        return options;
    }
    public string[] Rows =>
    [
        "Game name: " + Options.GameName,
        "Game port (TCP + UDP): " + PortText,
        "Password (optional): " + new string('*', Options.Password.Length),
        $"Bots + host: {Options.Size} {Options.TeamSize}v{Options.TeamSize}; friends join beside bots",
        "Bot difficulty: " + Options.Difficulty,
        "Your race: " + SkirmishOptions.Civilizations.First(c => c.Id == Options.PlayerCiv).Name,
        "Enemy race: " + SkirmishOptions.Civilizations.First(c => c.Id == Options.EnemyCiv).Name,
        "Host LAN game (research tech, normal speed)", "Back"
    ];
}
