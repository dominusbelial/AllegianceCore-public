using System.Runtime.CompilerServices;

namespace Allegiance.Lobby.Tests;

internal static class LobbyTestHostSettings
{
    /// <summary>Every WebApplicationFactory host otherwise opens config file watchers, and a full run
    /// hits the machine's inotify instance limit (IOException in FileConfigurationProvider).</summary>
    [ModuleInitializer]
    internal static void DisableConfigReload()
        => Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}
